using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// The per-connection VFP DATA SESSION: the work-area (1..N) model an ADO.NET connection wraps.
/// A session opens a data source — a <c>.dbc</c> (VFP database container, long table names), a
/// DIRECTORY of free <c>.dbf</c> tables, or a single <c>.dbf</c> — and then runs VFP-SQL plus the
/// work-area commands (USE / SELECT &lt;area&gt;).
/// <para>
/// Each work area holds an open <see cref="CrossVault.FoxDbf.DbfTable"/> + its
/// <see cref="CrossVault.FoxDbf.Index.CdxFile"/>, an alias, and a lock/share mode. The session
/// tracks the current area, resolves <c>alias</c> → table and <c>alias.field</c>, applies
/// <c>SELECT 0</c> / <c>IN 0</c> semantics, and AUTO-OPENS a table named in a SELECT that is not
/// already in a work area (in a scratch area, closed after the query — VFP behaviour). A bare table
/// name resolves against the open DBC (long names) FIRST, then a free <c>.dbf</c> in the data
/// directory.
/// </para>
/// <para>
/// The session carries an <see cref="EvaluationContext"/> (collation, SET DELETED, SET ANSI) that
/// the SELECT executor evaluates predicates and ORDER BY with.
/// </para>
/// <para>
/// Not thread-safe: use one instance per session/connection; do not share across threads.
/// </para>
/// </summary>
public sealed class VfpSession : IDisposable
{
    // ── DATA SESSIONS (finding 5.14) ──────────────────────────────────────────────────────
    // A DATA SESSION is an isolated set of work areas PLUS its own session-scoped SET/evaluation state.
    // Session 1 (the default public session) is created in the ctor; a form-equivalent PRIVATE session is
    // added by CreateDataSession, and SET DATASESSION TO n switches _active. Every existing work-area code
    // path operates on _active unchanged (the maps/current-area are surfaced below under the old field names).
    private sealed class DataSessionState
    {
        public readonly Dictionary<int, WorkArea> Areas = new();
        /// <summary>Cursor alias → its backing temp-table <c>.dbf</c> path (each in its own temp directory).
        /// A cursor created by <c>SELECT … INTO CURSOR</c> is a real temp table registered as a work area; its
        /// files are deleted when the cursor is dropped (name reused) or the session is disposed.</summary>
        public readonly Dictionary<string, string> CursorPaths = new(StringComparer.OrdinalIgnoreCase);
        public int CurrentArea = 1; // VFP selects work area 1 by default.
        public EvaluationContext Context; // this session's SET DELETED/EXACT/ANSI/COLLATE… state.
        public DataSessionState(EvaluationContext context) => Context = context;
    }

    private readonly Dictionary<int, DataSessionState> _sessions = new();
    private DataSessionState _active;
    private int _currentSessionId = 1;
    private int _nextSessionId = 2;

    // The DATA SOURCE binding (open .dbc / directory) is SHARED across data sessions — a deliberate microVFP
    // simplification (VFP scopes open databases per session; microVFP binds ONE data source per VfpSession and
    // isolates at the work-area level, which is exactly what the per-session isolation model requires).
    private DbfDatabase? _db;
    private string? _dataDir;
    private string? _dbcPath;
    private bool _disposed;

    // The ACTIVE session's swappable maps/current-area, surfaced under the ORIGINAL field names so every
    // existing work-area code path (USE/SELECT/DML/SQL/buffering/locks) keeps operating on the active session
    // untouched — SET DATASESSION just re-points _active.
    private Dictionary<int, WorkArea> _areas => _active.Areas;
    private Dictionary<string, string> _cursorPaths => _active.CursorPaths;
    private int _currentArea { get => _active.CurrentArea; set => _active.CurrentArea = value; }

    /// <summary>Creates a session with VFP-default evaluation settings.</summary>
    public VfpSession() : this(null) { }

    /// <summary>Creates a session carrying <paramref name="context"/> (or fresh VFP defaults).</summary>
    public VfpSession(EvaluationContext? context)
    {
        _active = new DataSessionState(context ?? new EvaluationContext());
        _sessions[1] = _active;
    }

    /// <summary>The ambient evaluation context (collation, SET DELETED, SET ANSI) of the ACTIVE data session
    /// — the executor and the interpreter read the current session's SET state through this.</summary>
    public EvaluationContext Context => _active.Context;

    /// <summary>
    /// Raised at the START of <see cref="Dispose"/>, before any work-area handle is closed. A consumer that
    /// holds its own file handles keyed to this session's lifetime (the microVFP interpreter's 5.5 cached
    /// write handles) subscribes here so they are released when the session is disposed — even when the
    /// caller disposes only the session and never the interpreter. Best-effort: a throwing subscriber does
    /// not abort the rest of disposal.
    /// </summary>
    internal event Action? Disposing;

    /// <summary>
    /// Raised at the START of <see cref="CloseAllHandles"/> — the QUIESCE point an ADO.NET transaction
    /// Commit/Rollback runs BEFORE it swaps each private copy over the live file (or restores a DDL
    /// snapshot). A consumer holding LIVE-file handles keyed to a coordination lock (the microVFP
    /// interpreter's 5.13 RLOCK/FLOCK byte-range locks, which ride a cached writer on the LIVE file even
    /// inside a copy-on-write transaction) subscribes here to release them, so the imminent
    /// <see cref="File.Replace(string, string, string?)"/> / <see cref="File.Copy(string, string, bool)"/>
    /// writeback is not blocked by an open handle or a held byte range. Best-effort: a throwing subscriber
    /// never aborts the quiesce.
    /// </summary>
    internal event Action? HandlesClosing;

    /// <summary>
    /// Bridge to the ACTIVE session's microVFP memory-variable store, registered by the bound
    /// <c>VfpInterpreter</c> in its constructor. It is the seam <c>SELECT … INTO ARRAY</c> writes through and
    /// <c>INSERT … FROM ARRAY|MEMVAR</c> reads through, so both the microVFP SP path and the ADO.NET command
    /// path surface those idioms against the same memvar store. <see langword="null"/> when no interpreter is
    /// bound (pure-SQL use), in which case those forms raise a clear error.
    /// </summary>
    internal IVfpMemoryBridge? MemoryBridge { get; set; }

    /// <summary>
    /// The optional query ACCELERATOR (e.g. the Highlike engine) that SELECT / DML candidate-set
    /// discovery routes through instead of the plain <see cref="QueryOptimizer"/>. When <see langword="null"/>
    /// (the default) the Core optimizer runs. An accelerator must return the SAME record set the Core
    /// path would (it only changes the plan, never the answer) — see <see cref="IQueryAccelerator"/>.
    /// </summary>
    public IQueryAccelerator? Accelerator { get; set; }

    /// <summary>When <see langword="true"/>, tables auto-opened by SELECT / resolved for a bare USE
    /// default to EXCLUSIVE (the connection-wide <c>Exclusive=true</c> setting). Explicit
    /// <c>USE … EXCLUSIVE</c> always opens exclusive regardless.</summary>
    public bool DefaultExclusive { get; set; }

    /// <summary>When <see langword="true"/>, the session is read-only: tables open NOUPDATE and DML
    /// (<see cref="OpenWritableTarget"/>) is rejected. Mirrors the connection-wide <c>ReadOnly=true</c>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// COPY-ON-WRITE read redirect installed by an active ADO.NET transaction. Given a RESOLVED live
    /// <c>.dbf</c> path it returns the path THIS session should actually open for READ: the table's
    /// PRIVATE working copy when the transaction has already taken one (read-your-writes), else the
    /// live path unchanged. <see langword="null"/> in autocommit (reads always hit the live file).
    /// </summary>
    internal Func<string, string>? TxRedirectReadPath { get; set; }

    /// <summary>
    /// COPY-ON-WRITE write redirect installed by an active ADO.NET transaction. Invoked from
    /// <see cref="OpenWritableTarget"/> with the RESOLVED live <c>.dbf</c> path just before a writer is
    /// opened (any open read handle on the table already released). It lazily creates the table's PRIVATE
    /// working copy on first write — recording the live file's change-token for optimistic commit — and
    /// returns the COPY path the writer should open, so the live file is never touched for the duration
    /// of the transaction (ISOLATION). <see langword="null"/> in autocommit (writes hit the live file).
    /// The hook is idempotent per table.
    /// </summary>
    internal Func<string, string>? TxBeginWritePath { get; set; }

    /// <summary>
    /// COPY-ON-WRITE canonicalization hook installed by an active ADO.NET transaction: given a resolved
    /// <c>.dbf</c> path that may be one of the transaction's PRIVATE working copies, return the LIVE path it
    /// copies (else the path unchanged). Lets <see cref="ReopenAreaTable"/> recover a redirected work area's
    /// live identity so it can re-resolve the DBC member (long field names) and re-apply the read redirect
    /// when refreshing the handle. <see langword="null"/> in autocommit.
    /// </summary>
    internal Func<string, string>? TxLivePath { get; set; }

    /// <summary>
    /// WRITE redirect for a DIRECT writer (the microVFP interpreter opens its own <c>DbfWriter</c> on a
    /// table's <c>SourcePath</c> rather than going through <see cref="OpenWritableTarget"/>). Inside a
    /// transaction this engages the SAME <see cref="TxBeginWritePath"/> seam the raw DML path uses — lazily
    /// taking the table's private working copy on first write and returning the COPY path so the write lands
    /// there (ISOLATION / rollback-able). Idempotent when handed a path that is already a private copy.
    /// Returns the path unchanged in autocommit, so EnforceRules=off / non-transactional writes are
    /// byte-identical.
    /// </summary>
    internal string RedirectWritePath(string dbfPath)
        => TxBeginWritePath is { } beginWrite ? beginWrite(dbfPath) : dbfPath;

    /// <summary>
    /// DDL snapshot-on-live hook installed by an active ADO.NET transaction. CREATE / ALTER / DROP
    /// operate on the LIVE files (so the transacting connection sees the table created / dropped / altered
    /// within the transaction); this captures the live files first so a Rollback restores them (or, for
    /// CREATE, records the to-be-created table so a Rollback deletes it). <see langword="null"/> in
    /// autocommit.
    /// </summary>
    internal Action<string>? TxDdlSnapshot { get; set; }

    /// <summary>
    /// Snapshot the table backing <paramref name="dbfPath"/> through the active transaction (if any)
    /// BEFORE a DDL op (CREATE / ALTER / DROP) touches its LIVE files. For ALTER/DROP this captures the
    /// live files so a Rollback restores them (DROP would otherwise be permanent data loss); for CREATE
    /// the path does not exist yet, so nothing is copied but the table is RECORDED so a Rollback deletes
    /// the newly created files. A no-op in autocommit.
    /// </summary>
    internal void SnapshotForDdl(string dbfPath) => TxDdlSnapshot?.Invoke(dbfPath);

    /// <summary>
    /// Release every open work-area table/index handle (without disturbing the data-source binding) so
    /// the underlying files can be overwritten — used by a transaction <c>Rollback</c> before it
    /// restores snapshotted files over the live ones. Tables re-open lazily on the next access.
    /// </summary>
    internal void CloseAllHandles()
    {
        // Let subscribers (the microVFP interpreter's 5.5 cached writers + 5.13 held byte-range locks)
        // release their LIVE-file handles first — a transaction Commit/Rollback quiesces via this BEFORE it
        // swaps/restores the live files, and a lingering interpreter lock handle on a live file would block
        // that writeback. Best-effort — a throwing subscriber never aborts the quiesce.
        try { HandlesClosing?.Invoke(); } catch { /* best-effort */ }
        // 5.14: tear down EVERY data session's work-area handles, not just the ACTIVE one. An ADO.NET COW
        // transaction Commit/Rollback quiesces the WHOLE VfpSession here before it swaps each private copy over
        // its live file; a NON-active PRIVATE session (CreateDataSession) holding an open handle on that .dbf
        // would otherwise block the File.Replace writeback (or leave that session reading torn content). This
        // mirrors Dispose()'s per-session teardown, minus the data-source binding + cursor temp tables, which a
        // quiesce keeps — tables re-open lazily on the next access, in whichever session accesses them.
        foreach (var s in _sessions.Values)
        {
            foreach (var w in s.Areas.Values) w.Dispose();
            s.Areas.Clear();
        }
    }

    /// <summary>
    /// Close (dispose) only the CURRENT data session's work-area handles — the microVFP <c>CLEAR ALL</c> /
    /// <c>CLOSE ALL</c> teardown, which in VFP is scoped to the CURRENT data session (a form's PRIVATE session
    /// keeps its tables open when the default session issues CLEAR ALL). Unlike <see cref="CloseAllHandles"/>
    /// (the whole-VfpSession transaction-quiesce boundary) this touches neither other data sessions nor the
    /// <see cref="HandlesClosing"/> subscribers; the data-source binding is kept.
    /// </summary>
    internal void CloseActiveAreas()
    {
        foreach (var w in _areas.Values) w.Dispose();
        _areas.Clear();
    }

    /// <summary>Find the records matching <paramref name="filter"/> through the attached
    /// <see cref="Accelerator"/> when one is set, else the Core <see cref="QueryOptimizer"/>. The result
    /// set is identical either way; only the plan differs.</summary>
    internal QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? ctx)
        => Accelerator is { } acc
            ? acc.FindRecords(table, cdx, filter, ctx)
            : QueryOptimizer.FindRecords(table, cdx, filter, ctx);

    /// <summary>The session's resolved data directory (free-table root, or the open DBC's directory),
    /// or <see langword="null"/> when no data source is open. The DDL executor creates / drops table
    /// files here. Exposed for schema enumeration by the ADO.NET provider.</summary>
    public string? DataDirectory => _dataDir;

    /// <summary>The open VFP database container (<c>.dbc</c>) backing this session, or
    /// <see langword="null"/> in free-table (directory) mode. The DDL executor consults it for
    /// DBC-member CREATE / DROP semantics. Exposed for schema enumeration by the ADO.NET provider.</summary>
    public DbfDatabase? Database => _db;

    /// <summary>The full path of the open VFP database container (<c>.dbc</c>), or <see langword="null"/>
    /// in free-table (directory) mode. Backs the microVFP <c>ADATABASES()</c> array-filler.</summary>
    internal string? DatabasePath => _dbcPath;

    // ---- data sessions (finding 5.14) -----------------------------------------------------

    /// <summary>The id of the CURRENT (active) data session — the numeric <c>SET("DATASESSION")</c>.
    /// 1 is the default public session.</summary>
    public int CurrentDataSessionId => _currentSessionId;

    /// <summary>Every existing data-session id, ascending (the <c>ASESSIONS()</c> list). Always includes 1.</summary>
    public IReadOnlyList<int> DataSessionIds
    {
        get { var ids = new List<int>(_sessions.Keys); ids.Sort(); return ids; }
    }

    /// <summary>True when data session <paramref name="id"/> exists (<c>SET DATASESSION TO n</c> is valid iff so).</summary>
    public bool HasDataSession(int id) => _sessions.ContainsKey(id);

    /// <summary>
    /// Create a new (form-equivalent PRIVATE) data session with its OWN empty work-area set and the supplied
    /// fresh SET/evaluation <paramref name="context"/>; returns the new id. Does NOT switch to it — the caller
    /// (the microVFP host API / <c>SET DATASESSION TO</c>) switches separately. VFP itself only ever creates a
    /// private session from a form with <c>DataSession=2</c>; this is microVFP's headless equivalent.
    /// </summary>
    public int CreateDataSession(EvaluationContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        int id = _nextSessionId++;
        _sessions[id] = new DataSessionState(context);
        return id;
    }

    /// <summary>Switch the ACTIVE data session to <paramref name="id"/> (<c>SET DATASESSION TO n</c>). Throws
    /// when the id does not exist — the caller maps that to VFP error 1540 "Session number is invalid."</summary>
    public void SwitchDataSession(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessions.TryGetValue(id, out var s))
            throw new FoxDbfSqlException($"Data session {id} does not exist.") { VfpErrorNumber = 1540 };
        _active = s;
        _currentSessionId = id;
    }

    /// <summary>
    /// Release data session <paramref name="id"/>: close (dispose) its work-area handles — releasing the
    /// byte-range locks those areas took (5.13 lifetime rules extend naturally) — and delete its cursor temp
    /// tables, then drop it. Session 1 is permanent; the caller must switch away from a session before
    /// releasing it. A no-op when the id does not exist.
    /// </summary>
    public void ReleaseDataSession(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (id == 1)
            throw new InvalidOperationException("The default data session (1) cannot be released.");
        if (id == _currentSessionId)
            throw new InvalidOperationException($"Cannot release the CURRENT data session {id}; switch away first.");
        if (!_sessions.Remove(id, out var s)) return;
        foreach (var w in s.Areas.Values) w.Dispose();
        s.Areas.Clear();
        foreach (var p in s.CursorPaths.Values) TryDeleteCursorFiles(p);
        s.CursorPaths.Clear();
    }

    /// <summary>Every open work area of data session <paramref name="id"/> (backs <c>AUSED(arr, n)</c>);
    /// empty when the session does not exist.</summary>
    internal IReadOnlyCollection<WorkArea> OpenAreasOf(int id)
        => _sessions.TryGetValue(id, out var s) ? s.Areas.Values : Array.Empty<WorkArea>();

    // ---- data sources ---------------------------------------------------------------------

    /// <summary>Opens a VFP database container (<c>.dbc</c>) as the session's data source: bare
    /// table names then resolve against its long table names first, else a free <c>.dbf</c> sibling.</summary>
    public void OpenDatabase(string dbcPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(dbcPath);
        _db = DbfDatabase.OpenFoxpro(dbcPath);
        _dbcPath = Path.GetFullPath(dbcPath);
        // free-table fallback resolves against the .dbc's own directory.
        _dataDir = Path.GetDirectoryName(_dbcPath);
    }

    /// <summary>Close the current DBC handle, run <paramref name="writeToDbc"/> (which mutates the container
    /// file directly — e.g. APPEND PROCEDURES rewriting the stored-procedure memo) while nothing holds it
    /// open, then RE-OPEN it so subsequent reads see the change. No-op when no DBC is open.</summary>
    internal void RewriteDatabase(Action<string> writeToDbc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dbcPath is null) return;
        _db?.Dispose();
        _db = null;
        try { writeToDbc(_dbcPath); }
        finally { _db = DbfDatabase.OpenFoxpro(_dbcPath); }
    }

    /// <summary>Opens a directory of free <c>.dbf</c> tables as the session's data source: bare
    /// table names resolve to <c>&lt;dir&gt;/&lt;name&gt;.dbf</c>.</summary>
    public void OpenDirectory(string directory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(directory);
        _dataDir = directory;
    }

    // ---- work areas -----------------------------------------------------------------------

    /// <summary>The current work-area number (0 when none is selected).</summary>
    public int CurrentArea => _currentArea;

    /// <summary>The alias of the table in the current work area, or null when none.</summary>
    public string? CurrentAlias
        => _areas.TryGetValue(_currentArea, out var w) ? w.Alias : null;

    /// <summary>
    /// Opens <paramref name="table"/> in a work area (USE). <paramref name="inArea"/> selects the
    /// target area (0 = the lowest free area); <paramref name="alias"/> overrides the default alias;
    /// <paramref name="again"/> re-opens an already-open table; <paramref name="exclusive"/> requests
    /// EXCLUSIVE (else SHARED) and <paramref name="noUpdate"/> a read-only open.
    /// </summary>
    public void Use(string table, int? inArea = null, string? alias = null,
                    bool again = false, bool exclusive = false, bool noUpdate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(table);

        // Target area + whether USE also SELECTs it: IN 0 opens in the lowest free area WITHOUT
        // changing the current area; IN n targets n and selects it; no IN uses the current area.
        int targetArea;
        bool selectAfter;
        if (inArea is 0) { targetArea = LowestFreeArea(); selectAfter = false; }
        else if (inArea is int n) { targetArea = n; selectAfter = true; }
        else { targetArea = _currentArea; selectAfter = true; }

        var (dbf, cdx) = OpenNamedTable(table, exclusive, noUpdate);

        // USE replaces whatever occupied the target area.
        if (_areas.Remove(targetArea, out var prev)) prev.Dispose();

        string a;
        if (alias is not null)
        {
            a = NormalizeAlias(alias);
        }
        else
        {
            string defaultAlias = NormalizeAlias(DefaultAlias(table));
            bool collides = _areas.Values.Any(w =>
                string.Equals(w.Alias, defaultAlias, StringComparison.OrdinalIgnoreCase));
            a = collides
                ? targetArea is >= 1 and <= 10
                    ? ((char)('A' + targetArea - 1)).ToString()
                    : "W" + targetArea.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : defaultAlias;
        }
        _areas[targetArea] = new WorkArea(targetArea, dbf, cdx, a, exclusive, noUpdate);
        if (selectAfter) _currentArea = targetArea;
    }

    /// <summary>Switches the current work area by NUMBER (<c>SELECT n</c>; 0 selects the lowest free area).</summary>
    public void SelectArea(int area)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _currentArea = area == 0 ? LowestFreeArea() : area;
    }

    /// <summary>Switches the current work area by ALIAS (<c>SELECT alias</c>).</summary>
    public void SelectArea(string alias)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(alias);
        foreach (var (num, w) in _areas)
            if (string.Equals(w.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                _currentArea = num;
                return;
            }
        throw new FoxDbfSqlException($"Alias '{alias}' is not open in any work area.");
    }

    // ---- execution ------------------------------------------------------------------------

    /// <summary>
    /// Parses and executes a single VFP-SQL statement or work-area command. Returns a
    /// <see cref="SqlResult"/> for a SELECT; returns <see langword="null"/> for a command
    /// (USE / SELECT &lt;area&gt;). Throws <see cref="NotSupportedException"/> for forms not yet
    /// supported in Phase 1b (JOINs, comma multi-table, INTO materialization, TOP … PERCENT).
    /// </summary>
    public SqlResult? Execute(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stmt = SqlParser.Parse(sql);
        switch (stmt)
        {
            case SelectAreaCommand sa:
                if (sa.Area is int n) SelectArea(n);
                else SelectArea(sa.Alias!);
                return null;

            case UseCommand u:
                ExecuteUse(u);
                return null;

            case SelectStatement sel:
                return new SelectExecutor(this).Run(sel);

            case InsertStatement:
            case UpdateStatement:
            case DeleteStatement:
                return new DmlExecutor(this).Run(stmt);

            case CreateTableStatement:
            case AlterTableStatement:
            case DropTableStatement:
                return new DdlExecutor(this).Run(stmt);

            default:
                throw new NotSupportedException($"Statement '{stmt.GetType().Name}' is not supported.");
        }
    }

    private void ExecuteUse(UseCommand u)
    {
        // bare USE (no table, no prompt) → close the current work area.
        if (u.Table is null && !u.Prompt)
        {
            if (_areas.Remove(_currentArea, out var w)) w.Dispose();
            return;
        }
        if (u.Table is null)
            throw new NotSupportedException("USE ? (interactive table picker) is not supported.");

        int? inArea = u.InArea;
        if (inArea is null && u.InAlias is not null) inArea = AreaOfAlias(u.InAlias);

        Use(u.Table, inArea, u.Alias, u.Again,
            exclusive: u.Mode == UseMode.Exclusive, noUpdate: u.NoUpdate);
    }

    // ---- table / alias resolution (also used by the executor) -----------------------------

    /// <summary>Build a fresh SQL-evaluation context: a clone of <see cref="Context"/> with SQL
    /// (ANSI-governed) <c>=</c> semantics enabled for WHERE / HAVING predicates.</summary>
    internal EvaluationContext SqlContext() => new()
    {
        Exact = Context.Exact,
        Deleted = Context.Deleted,
        Optimize = Context.Optimize,
        Ansi = Context.Ansi,
        SqlSemantics = true,
        Collation = Context.Collation,
        Culture = Context.Culture,
        Encoding = Context.Encoding,
    };

    // ---- SELECT … INTO TABLE | CURSOR materialization -------------------------------------

    /// <summary>Resolves the on-disk path a <c>SELECT … INTO TABLE name</c> writes to: a free
    /// <c>&lt;name&gt;.dbf</c> in the session's data directory. Throws when no data directory is open.</summary>
    internal string ResolveIntoTablePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_dataDir is null)
            throw new FoxDbfSqlException(
                $"No data directory is open; cannot create table '{name}' from SELECT … INTO TABLE.");
        string file = name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? name : name + ".dbf";
        return Path.Combine(_dataDir, file);
    }

    /// <summary>Allocates a fresh temp <c>.dbf</c> path (in its own unique temp directory) for a
    /// <c>SELECT … INTO CURSOR</c> backing table. The directory is removed when the cursor is dropped.</summary>
    internal string NewCursorTempPath(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem)) stem = "cursor";
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_cursor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, stem + ".dbf");
    }

    /// <summary>Registers the temp table at <paramref name="tempDbfPath"/> as an open CURSOR work area
    /// under <paramref name="alias"/>, so a follow-up <c>SELECT … FROM alias</c> resolves it. Any prior
    /// area/cursor with the same alias is dropped first (its temp files deleted), so reusing a cursor name
    /// replaces the previous cursor.</summary>
    internal void RegisterCursor(string alias, string tempDbfPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(tempDbfPath);

        string a = NormalizeAlias(alias);

        // Drop any existing area with this alias (cursor or plain table) before re-registering.
        var prev = FindAreaByAlias(a);
        if (prev is not null && _areas.Remove(prev.Area, out var pw)) pw.Dispose();
        if (_cursorPaths.Remove(a, out var oldPath)) TryDeleteCursorFiles(oldPath);

        var dbf = DbfTable.Open(tempDbfPath, new DbfOptions { LockMode = Write.LockMode.Shared });
        int area = LowestFreeArea();
        _areas[area] = new WorkArea(area, dbf, null, a, exclusive: false, noUpdate: false);
        _cursorPaths[a] = tempDbfPath;
    }

    /// <summary>Best-effort deletion of a cursor's backing temp directory (its <c>.dbf</c>/<c>.fpt</c>
    /// live in a unique throwaway dir).</summary>
    private static void TryDeleteCursorFiles(string dbfPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(dbfPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>The open work area numbered <paramref name="area"/>, or null. (microVFP record-pointer
    /// model: it reads the area's <see cref="WorkArea.Table"/> / <see cref="WorkArea.Cdx"/> / alias.)</summary>
    internal WorkArea? AreaAt(int area) => _areas.TryGetValue(area, out var w) ? w : null;

    /// <summary>Every open work area (microVFP: ALIAS()/USED()/SELECT(1)-highest-unused enumeration).</summary>
    internal IReadOnlyCollection<WorkArea> OpenAreas => _areas.Values;

    /// <summary>The lowest free (unused) work-area number — the <c>SELECT 0</c> / <c>IN 0</c> target.</summary>
    internal int LowestFreeAreaNumber() => LowestFreeArea();

    /// <summary>The highest UNUSED work-area number — the <c>SELECT(1)</c> function's answer (VFP caps
    /// work areas at 32767; the highest gap below that which is free).</summary>
    internal int HighestUnusedAreaNumber()
    {
        int a = 32767;
        while (a > 1 && _areas.ContainsKey(a)) a--;
        return a;
    }

    /// <summary>Force-select <paramref name="area"/> as current WITHOUT the <c>SELECT 0</c> remapping
    /// (microVFP positions the cursor model on an area it just opened/created, e.g. an INTO CURSOR).</summary>
    internal void SetCurrentArea(int area) => _currentArea = area;

    /// <summary>Close (release) the table in work area <paramref name="area"/> (microVFP <c>USE IN n</c>
    /// / <c>USE IN alias</c>). A no-op when the area is not open.</summary>
    internal void CloseArea(int area)
    {
        if (_areas.Remove(area, out var w)) w.Dispose();
    }

    /// <summary>
    /// Re-open the table in work area <paramref name="area"/> from disk (same alias / mode / source),
    /// re-applying the DBC long field names when it is a member and re-attaching its structural
    /// <c>.cdx</c>. microVFP calls this after an out-of-band REPLACE/DELETE so a fresh read handle sees
    /// the just-written bytes (the prior handle's buffered FileStream would otherwise serve stale data).
    /// </summary>
    internal void ReopenAreaTable(int area)
    {
        if (!_areas.TryGetValue(area, out var w)) return;
        string? path = w.Table.SourcePath;
        if (path is null) return;

        // COPY-ON-WRITE: inside a transaction the area may already be riding a private working copy. Recover
        // the LIVE path first so the DBC-member match (below) and the free-table resolution work off the real
        // identity, then re-apply the READ redirect so the refreshed handle opens the COPY (read-your-writes).
        // Both hooks are null in autocommit, leaving the reopen byte-identical.
        string livePath = TxLivePath is { } toLive ? toLive(path) : path;

        DbfTable dbf;
        string? objectName = null;
        if (_db is not null)
        {
            string full = Path.GetFullPath(livePath);
            foreach (var t in _db.TableNames)
            {
                var p = _db.GetTablePath(t);
                if (p is not null && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
                { objectName = t; break; }
            }
        }
        string readPath = TxRedirectReadPath is { } redirect ? redirect(livePath) : livePath;
        dbf = objectName is not null
            ? _db!.OpenTableAt(objectName, readPath)
            : DbfTable.Open(readPath, new DbfOptions { LockMode = w.Exclusive ? Write.LockMode.Exclusive : Write.LockMode.Shared });

        CdxFile? cdx = null;
        string cdxPath = SidecarFile.ChangeExtension(readPath, ".cdx");
        if (File.Exists(cdxPath))
        {
            try { cdx = CdxFile.Open(cdxPath, dbf); }
            catch { cdx = null; }
        }

        w.Dispose();
        _areas[area] = new WorkArea(area, dbf, cdx, w.Alias, w.Exclusive, w.NoUpdate);
    }

    /// <summary>An already-open work area whose alias matches <paramref name="name"/>, else null.</summary>
    internal WorkArea? FindAreaByAlias(string name)
    {
        foreach (var w in _areas.Values)
            if (string.Equals(w.Alias, name, StringComparison.OrdinalIgnoreCase))
                return w;
        return null;
    }

    /// <summary>Find an open work area whose table resolves to the same on-disk file as
    /// <paramref name="fullPath"/> (a fully-qualified path). Used to release a stale read handle held
    /// under a CUSTOM alias that does not equal the target name, before a write opens the same file.</summary>
    internal WorkArea? FindAreaByPath(string fullPath)
    {
        foreach (var w in _areas.Values)
        {
            string? src = w.Table.SourcePath;
            if (src is not null &&
                string.Equals(Path.GetFullPath(src), fullPath, StringComparison.OrdinalIgnoreCase))
                return w;
        }
        return null;
    }

    /// <summary>
    /// Open the table named <paramref name="name"/> as a NEW handle: the open DBC's long table names
    /// FIRST, else a free <c>&lt;name&gt;.dbf</c> in the data directory. Also opens its structural
    /// <c>.cdx</c> when present (best-effort). The caller owns and disposes the returned handles.
    /// </summary>
    internal (DbfTable table, CdxFile? cdx) OpenNamedTable(string name, bool exclusive = false, bool noUpdate = false)
    {
        // Connection-wide Exclusive / ReadOnly defaults apply on top of any explicit request.
        exclusive |= DefaultExclusive;
        noUpdate |= ReadOnly;

        DbfTable dbf;
        if (_db is not null && TryDbcName(name, out var objectName))
        {
            // COPY-ON-WRITE for a DBC member: resolve the member's LIVE .dbf path (without opening it),
            // run it through the read redirect, and — when the transaction has already taken a private
            // working copy of this member — open that COPY (read-your-writes) with the DBC long field
            // names applied. Outside a transaction (or before this member's first write) the live path is
            // returned unchanged and the table opens live, exactly as before.
            string? livePath = _db.GetTablePath(objectName);
            if (livePath is not null && TxRedirectReadPath is { } dbcRedirect)
            {
                string readPath = dbcRedirect(livePath);
                dbf = _db.OpenTableAt(objectName, readPath);
            }
            else
            {
                dbf = _db.OpenTable(objectName);
            }
        }
        else
        {
            string? path = ResolveFreePath(name);
            if (path is null)
                throw new FoxDbfSqlException(
                    $"Table '{name}' was not found in the open database or data directory.") { VfpErrorNumber = 1 }; // VFP err 1 "does not exist".
            // COPY-ON-WRITE: inside a transaction that has already written this table, reads go to its
            // private working copy (read-your-writes); otherwise the live path is returned unchanged.
            if (TxRedirectReadPath is { } redirect) path = redirect(path);
            dbf = DbfTable.Open(path, new DbfOptions
            {
                LockMode = exclusive ? Write.LockMode.Exclusive : Write.LockMode.Shared,
            });
        }

        CdxFile? cdx = null;
        var src = dbf.SourcePath;
        if (src is not null)
        {
            string cdxPath = SidecarFile.ChangeExtension(src, ".cdx");
            if (File.Exists(cdxPath))
            {
                try { cdx = CdxFile.Open(cdxPath, dbf); }
                catch { cdx = null; }
            }
        }
        return (dbf, cdx);
    }

    /// <summary>
    /// Open the table named <paramref name="name"/> for WRITING (the DML counterpart to
    /// <see cref="OpenNamedTable"/>). If the table is already open in a work area, that area's read
    /// handle is released for the duration of the write (and re-opened when the returned
    /// <see cref="WritableTarget"/> is disposed) so there is no double-open / sharing conflict; the
    /// write honours the area's EXCLUSIVE/SHARED mode. Otherwise the table is auto-opened SHARED.
    /// The caller reads the live, just-written state through <see cref="WritableTarget.Table"/>
    /// (a <see cref="DbfTable"/> view riding the writer's own stream).
    /// </summary>
    internal WritableTarget OpenWritableTarget(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        if (ReadOnly)
            throw new FoxDbfSqlException($"The connection is read-only; cannot write to table '{name}'.");

        var existing = FindAreaByAlias(name)
                       ?? FindAreaByAlias(NormalizeAlias(DefaultAlias(name)));
        bool exclusive = existing?.Exclusive ?? false;

        string path;
        ReopenInfo? reopen = null;
        if (existing is not null)
        {
            path = existing.Table.SourcePath
                   ?? throw new FoxDbfSqlException($"Open table '{name}' has no source path on disk to write to.");
            reopen = new ReopenInfo(existing.Area, existing.Table.SourcePath!, existing.Alias, existing.Exclusive, existing.NoUpdate);
            _areas.Remove(existing.Area);
            existing.Dispose(); // release the read handle so the writer can open the file.
        }
        else
        {
            path = Path.GetFullPath(ResolveWritablePath(name));
            // The table may be open under a CUSTOM alias (alias != name): match by the RESOLVED file
            // path so its stale read handle is released for the write and re-opened on dispose —
            // otherwise the write opens a second handle and the aliased area keeps a stale view.
            if (FindAreaByPath(path) is { } aliased)
            {
                exclusive = aliased.Exclusive;
                reopen = new ReopenInfo(aliased.Area, path, aliased.Alias, aliased.Exclusive, aliased.NoUpdate);
                _areas.Remove(aliased.Area);
                aliased.Dispose();
            }
        }

        DbfWriter writer;
        try
        {
            // COPY-ON-WRITE redirect: at this point any open read handle on the target has been released
            // and no writer is open yet, so the on-disk files are a consistent copy of the table's
            // last-flushed state. Inside a transaction the hook lazily takes the table's private working
            // copy on FIRST write (recording the live change-token) and returns the COPY path, so the live
            // file is never touched (ISOLATION) and subsequent writes reuse the same copy. It runs INSIDE
            // the try so a copy failure restores the work area we just closed (and does not leave a
            // half-open work area behind) — exactly like a writer-open failure. No-op in autocommit.
            if (TxBeginWritePath is { } beginWrite) path = beginWrite(path);

            writer = DbfWriter.Open(path, new DbfOptions
            {
                LockMode = exclusive ? Write.LockMode.Exclusive : Write.LockMode.Shared,
            });
        }
        catch
        {
            if (reopen is ReopenInfo r0) ReopenArea(r0); // a failed open/snapshot must not lose the work area.
            throw;
        }
        return new WritableTarget(this, writer, reopen);
    }

    /// <summary>Resolve the on-disk path of a writable table: the open DBC's long table names FIRST,
    /// else a free <c>&lt;name&gt;.dbf</c> in the data directory.</summary>
    private string ResolveWritablePath(string name)
    {
        if (_db is not null && TryDbcName(name, out var objectName))
        {
            using var t = _db.OpenTable(objectName);
            return t.SourcePath
                   ?? throw new FoxDbfSqlException($"DBC table '{name}' has no source path on disk.");
        }
        string? path = ResolveFreePath(name);
        if (path is null)
            throw new FoxDbfSqlException(
                $"Table '{name}' was not found in the open database or data directory.") { VfpErrorNumber = 1 }; // VFP err 1 "does not exist".
        return path;
    }

    /// <summary>Re-open a work area that <see cref="OpenWritableTarget"/> temporarily closed, in the
    /// SAME area / alias / mode, without disturbing the current-area selection.</summary>
    private void ReopenArea(ReopenInfo r)
    {
        int saved = _currentArea;
        Use(r.Table, r.Area, r.Alias, again: false, exclusive: r.Exclusive, noUpdate: r.NoUpdate);
        _currentArea = saved;
    }

    internal readonly record struct ReopenInfo(int Area, string Table, string Alias, bool Exclusive, bool NoUpdate);

    /// <summary>Close (release the handle of) every open work area whose table resolves to
    /// <paramref name="fullPath"/>, returning the reopen descriptors so an ALTER can re-open them after
    /// the rewrite. A DROP simply discards the returned list (the file is gone). Used by DDL so an
    /// ALTER/DROP never collides with a stale handle a work area still holds on the same file.</summary>
    internal List<ReopenInfo> CloseAreasForPath(string fullPath)
    {
        var closed = new List<ReopenInfo>();
        WorkArea? wa;
        while ((wa = FindAreaByPath(fullPath)) is not null)
        {
            closed.Add(new ReopenInfo(wa.Area, fullPath, wa.Alias, wa.Exclusive, wa.NoUpdate));
            _areas.Remove(wa.Area);
            wa.Dispose();
        }
        return closed;
    }

    /// <summary>Re-open work areas that <see cref="CloseAreasForPath"/> closed (used by ALTER after the
    /// in-place rewrite, so the session keeps the same areas/aliases it had before the DDL).</summary>
    internal void ReopenAreas(IEnumerable<ReopenInfo> infos)
    {
        foreach (var r in infos) ReopenArea(r);
    }

    /// <summary>A writable handle on a target table for the duration of one DML statement: the
    /// <see cref="DbfWriter"/> plus a <see cref="DbfTable"/> read view over the same stream. Disposing
    /// it flushes + closes the writer and re-opens any work area that was temporarily closed.</summary>
    internal sealed class WritableTarget : IDisposable
    {
        private readonly VfpSession _session;
        private readonly ReopenInfo? _reopen;
        private bool _disposed;

        internal WritableTarget(VfpSession session, DbfWriter writer, ReopenInfo? reopen)
        {
            _session = session; Writer = writer; _reopen = reopen;
        }

        /// <summary>The open writer for the target table.</summary>
        public DbfWriter Writer { get; }

        /// <summary>A <see cref="DbfTable"/> view over the writer's own stream — reads see the live,
        /// just-written state, so <see cref="QueryOptimizer.FindRecords"/> matches against it.</summary>
        public DbfTable Table => Writer.Schema;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // A flush failure here means the DML's rows may NOT have persisted — but the caller has
            // already computed the affected-count. Capture the failure, finish best-effort cleanup
            // (close the writer, re-open any work area), then RETHROW so the statement cannot report a
            // phantom success on data that never reached disk.
            Exception? flushError = null;
            try { Writer.Flush(); }
            catch (Exception ex) { flushError = ex; }

            Writer.Dispose();
            if (_reopen is ReopenInfo r) _session.ReopenArea(r);

            if (flushError is not null)
                throw new FoxDbfSqlException(
                    "The write could not be flushed to disk; the operation did not persist.", flushError);
        }
    }

    private bool TryDbcName(string name, out string objectName)
    {
        objectName = name;
        if (_db is null) return false;
        foreach (var t in _db.TableNames)
            if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
            {
                objectName = t;
                return true;
            }
        // Also resolve a full .dbf PATH to its DBC member (e.g. a ROLLBACK re-opening an area BY PATH), so
        // the member's long field names are re-applied instead of opening the bare table.
        if (Path.IsPathRooted(name))
        {
            string full = Path.GetFullPath(name);
            foreach (var t in _db.TableNames)
            {
                var p = _db.GetTablePath(t);
                if (p is not null && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
                {
                    objectName = t;
                    return true;
                }
            }
        }
        return false;
    }

    private string? ResolveFreePath(string name)
    {
        if (_dataDir is null) return null;
        string direct = Path.Combine(_dataDir, name);
        if (File.Exists(direct)) return direct;
        string withExt = Path.Combine(_dataDir, name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? name : name + ".dbf");
        return File.Exists(withExt) ? withExt : null;
    }

    private int LowestFreeArea()
    {
        int a = 1;
        while (_areas.ContainsKey(a)) a++;
        return a;
    }

    private int? AreaOfAlias(string alias)
    {
        foreach (var (num, w) in _areas)
            if (string.Equals(w.Alias, alias, StringComparison.OrdinalIgnoreCase))
                return num;
        return null;
    }

    private static string DefaultAlias(string table)
    {
        // The default alias is the bare table name without directory / extension.
        string name = Path.GetFileNameWithoutExtension(table);
        return string.IsNullOrEmpty(name) ? table : name;
    }

    private static string NormalizeAlias(string alias) => alias.ToUpperInvariant();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Let subscribers (the microVFP interpreter's cached write handles) release their own handles first,
        // while the session is otherwise intact. Best-effort — never let a subscriber abort teardown.
        try { Disposing?.Invoke(); } catch { /* best-effort */ }
        // Tear down EVERY data session (5.14): the active one plus any private sessions still open.
        foreach (var s in _sessions.Values)
        {
            foreach (var w in s.Areas.Values) w.Dispose();
            s.Areas.Clear();
            // Drop every cursor's backing temp table (close handle above, then delete the temp dirs).
            foreach (var p in s.CursorPaths.Values) TryDeleteCursorFiles(p);
            s.CursorPaths.Clear();
        }
        _sessions.Clear();
        _db?.Dispose();
    }

    // ---- work area ------------------------------------------------------------------------

    /// <summary>One open work area: a table handle + its (optional) CDX + alias + open mode.</summary>
    internal sealed class WorkArea : IDisposable
    {
        public int Area { get; }
        public DbfTable Table { get; }
        public CdxFile? Cdx { get; }
        public string Alias { get; }
        public bool Exclusive { get; }
        public bool NoUpdate { get; }

        public WorkArea(int area, DbfTable table, CdxFile? cdx, string alias, bool exclusive, bool noUpdate)
        {
            Area = area; Table = table; Cdx = cdx; Alias = alias;
            Exclusive = exclusive; NoUpdate = noUpdate;
        }

        public void Dispose()
        {
            Cdx?.Dispose();
            Table.Dispose();
        }
    }
}
