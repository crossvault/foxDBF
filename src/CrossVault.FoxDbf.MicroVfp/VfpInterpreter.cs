using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P1b — the TREE-WALKING INTERPRETER + runtime.
//
//  Runs the TasTrade sample / real-world customer business functions so their result + table side
//  effects are identical to VFP9. Reuses wholesale: VfpExpression (scalar ops +
//  functions), VfpSession (work areas, USE/SELECT, name resolution, SQL), DbfTable
//  (random-access read) + DbfWriter (REPLACE/DELETE). Adds, on top: a memory store
//  with VFP dynamic scoping, a per-area record pointer, the call mechanism with
//  by-ref / by-value parameter passing, the runtime STATE functions and the
//  statement tree-walk. See analysis/MICROVFP_SEMANTICS.md.
//
//  Char field reads are RIGHT-trimmed (trailing blanks/NULs) — this preserves the
//  leading-justified width VFP relies on (e.g. LEN(setup.value)=6) while matching
//  the library's trimmed convention; combined with SET EXACT ON it reproduces VFP's
//  field-vs-field "=" without the SET EXACT OFF short-pattern over-match.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A tree-walking interpreter for microVFP stored procedures over a <see cref="VfpSession"/>.</summary>
/// <remarks>Not thread-safe: use one instance per session/connection; do not share across threads.</remarks>
public sealed partial class VfpInterpreter
{
    private readonly Dictionary<string, ProcDef> _procs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VfpValue> _defines = new(StringComparer.OrdinalIgnoreCase);
    // Per-DATA-SESSION (5.14): the record-pointer/relation/order/buffering/LOCATE state, swapped by
    // SET DATASESSION. Non-readonly so a session switch can re-point it at the target session's map.
    private Dictionary<int, AreaMeta> _meta = new();
    private readonly List<CallContext> _callStack = new();

    // CANDIDATE tags are byte-identical to plain tags on disk for a FREE table (no DBC index catalog), so
    // microVFP tracks candidacy in-session: full table path → the set of tag names created CANDIDATE. A
    // write that introduces a duplicate key into any of these must RAISE (VFP behaviour), not silently
    // corrupt the tag. Enforced around INSERT/REPLACE/APPEND (the writer itself only honours the on-disk
    // UNIQUE bit). Lost when the session ends (no on-disk candidate signal) — a documented free-table limit.
    private readonly Dictionary<string, HashSet<string>> _candidateTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hard cap on call nesting depth — a runaway recursive UDF throws a catchable
    /// <see cref="MicroVfpRuntimeException"/> at this depth rather than a fatal StackOverflowException.
    /// Each interpreter call spans MANY native frames (tree-walk → compiled-expression invoke →
    /// HostInvoke → back to CallProc), so one logical level costs far more stack than the tiny margin
    /// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack"/>
    /// guarantees — so the numeric cap (not the probe) is the real backstop and must sit safely below the
    /// native limit. RE-MEASURED (Batch 4): in a Debug build the native stack overflows at ≈64 nested UDF
    /// levels — MUCH tighter than an earlier ~110 estimate (that figure did not survive later per-frame
    /// growth: every partial-class addition that widens the hot Exec/ExecBlock/HostInvoke frames lowers the
    /// cliff, and it now sits right around the old 64 cap). 48 restores a real margin under that ≈64 cliff
    /// while still dwarfing any legitimate VFP nesting (RI cascades and SP chains in the corpus are
    /// single-digit deep). No test pins a specific SUCCESSFUL depth — only that unbounded recursion throws a
    /// catchable <see cref="MaxCallDepthErrorNumber"/> — so a lower cap is a pure robustness win.
    /// ponytail: 48 is a fixed ceiling; the only sound way to raise it is to move deep interpretation onto a
    /// large-stack thread (a bigger cap alone just re-arms the StackOverflow it exists to prevent).</summary>
    private const int MaxCallDepth = 48;

    /// <summary>The stable VFP-style error number microVFP raises when <see cref="MaxCallDepth"/> is hit
    /// (project-review 5.3). VFP itself faults UNCATCHABLY on unbounded recursion (verified against the
    /// runtime: it crashes before any trappable error fires), so there is NO oracle-authoritative number
    /// to pin here — this is a microVFP-internal stable code, positive/non-zero for the AERROR/ON ERROR
    /// contract. Its only contract is stability; change it only with the matching TypedError test.</summary>
    internal const int MaxCallDepthErrorNumber = 1809;

    private readonly Row _row;
    // Per-DATA-SESSION (5.14): the ACTIVE session's SET DELETED/EXACT/ANSI/COLLATE… context. Aliases
    // Session.Context; re-pointed on SET DATASESSION. Non-readonly so a switch can mirror the new session.
    private EvaluationContext _ctx;

    // ── transactions (snapshot-and-restore over the live DBC tables; see §Transaktionen) ──
    private sealed class TxnFrame { public readonly Dictionary<string, FileSnapshot> Snaps = new(StringComparer.OrdinalIgnoreCase); }
    // 6.3: a rollback pre-image streams the live .dbf/.cdx/.fpt to TEMP FILES (paths below) instead of holding
    // them as byte[] in RAM — the old whole-file-into-memory capture cost up to ~3×2GB transient on a 2GB table.
    // A field is null when that companion was ABSENT at capture (fail-soft); on restore a null path DELETES any
    // companion that appeared (the .dbf itself is left untouched). Cleanup() deletes the temps and nulls the
    // paths (idempotent) — run after every commit/rollback so the temp dir never grows across a long session.
    private sealed class FileSnapshot
    {
        public string Path = "";
        public string? DbfTemp;
        public string? CdxTemp;
        public string? FptTemp;
        public void Cleanup()
        {
            TryDeleteTemp(DbfTemp); TryDeleteTemp(CdxTemp); TryDeleteTemp(FptTemp);
            DbfTemp = CdxTemp = FptTemp = null;
        }
        private static void TryDeleteTemp(string? temp)
        {
            if (temp is null) return;
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
        }
    }
    // Per-DATA-SESSION (5.14): a PRG transaction stack is scoped to its data session, so a BEGIN TRANSACTION
    // in one session never snapshots/rolls back another session's writes. Non-readonly so a switch re-points it.
    private List<TxnFrame> _txn = new();

    // ── SET NEAR (per data-session; microVFP INDEX/ORDER MODEL) ──
    // When ON, a failed SEEK leaves the pointer on the record just past where the key would sort
    // (EOF if past the end) instead of at EOF. Read back via SET("NEAR"). Scope: SEEK only.
    private bool _setNear;

    // SET AUTOSAVE ON|OFF — VFP flushes header buffers on RETURN to the Command window / read events.
    // microVFP writes through on every REPLACE/DELETE, so this only feeds SET("AUTOSAVE"). Default OFF.
    private bool _setAutosave;

    // SET MEMOWIDTH TO n — the memo word-wrap column consulted by MEMLINES()/MLINE()/ATLINE()/…. VFP default
    // is 50 and the MINIMUM is 8 (a smaller value is silently ignored — verified against the VFP9 runtime). Read back
    // (as a NUMBER) by SET("MEMOWIDTH"). Global (not per-data-session), like the VFP setting.
    private int _memoWidth = 50;

    // SET DATABASE TO [name] — the CURRENT-database designation over the single open DBC. An open DBC is
    // current by default (VFP: OPEN DATABASE makes it current); `SET DATABASE TO` with no name clears the
    // designation (DBC()/SET("DATABASE") = ""); `SET DATABASE TO name` re-selects it. Multi-DBC is out of
    // scope (single-DBC session — see the backlog SET DATABASE entry).
    private bool _currentDbCleared;

    // &macro re-entrancy guard: a self-/mutually-referential macro (pcmd='&pcmd' then &pcmd) survives
    // ExpandMacrosInLine unchanged (its 10-pass loop only bounds textual expansion WITHIN one line), so
    // Exec re-enters ExecMacroSubst unboundedly → StackOverflowException (uncatchable, kills the host).
    // VFP9 instead raises a graceful error, so we bound the runtime re-parse depth and throw.
    private int _macroDepth;

    // ── SET RELATION (multi-table relation model; microVFP P1 gap #2) ──
    // Re-entrancy guard: while a parent move is repositioning its children, the child moves (SEEK/GOTO)
    // must NOT themselves re-fire the reposition hook — chaining is driven explicitly by
    // RepositionChildrenCore's recursion. A hard depth cap makes cyclic relations (a→b→a) terminate.
    private bool _inReposition;
    private const int MaxRelationDepth = 32;

    // ── ON ERROR runtime-error trap state (see §Fehlerbehandlung) ──
    private PrgStatement? _onErrorCmd;    // the installed handler command (executed on a trapped error).
    private string? _onErrorText;         // its raw source — ON("ERROR") reads it; ON ERROR &var re-installs it.
    private bool _inHandler;              // re-entrancy guard: a fault inside the handler is not re-trapped.
    private int _errNo;                   // ERROR()  — the LIVE trapped error number (0 outside a handler).
    private string _errMsg = "";          // MESSAGE() — the LIVE trapped error text (cleared on handler exit).
    private int _errLine;                 // LINENO()  — the offending source line (cleared on handler exit).

    /// <summary>Creates an interpreter bound to <paramref name="session"/>.</summary>
    public VfpInterpreter(VfpSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Memory = new MemoryStore();
        // Surface SELECT … INTO ARRAY / INSERT … FROM ARRAY|MEMVAR at the SQL layer against THIS session's
        // memvar store: the SQL engine (SelectExecutor / DmlExecutor) reaches the store through this bridge,
        // so both the microVFP SP path and the ADO.NET command path write/read the same active-session arrays.
        Session.MemoryBridge = new MemvarBridge(this);
        Runtime = new RuntimeState();
        _row = new Row(this);
        _ctx = session.Context;
        // VFP runtime defaults the SP corpus + oracle harness run under, plus the field-read policy
        // (see header): SET DELETED ON, MACHINE collation, and — paired with RIGHT-trimmed char reads
        // — SET EXACT ON so a field = field comparison is equality (no SET-EXACT-OFF prefix over-match).
        _ctx.Exact = true;
        _ctx.Deleted = true;
        _ctx.Encoding ??= Encoding.Latin1; // CHR()/ASC() byte-consistency for createId/newguid.
        _callStack.Add(new CallContext("(main)", Array.Empty<VfpValue>(), 0));
        // 5.14: register the DEFAULT data session (id 1) — its interpreter-side state (record pointers, locks,
        // cached writers, PRG transactions, per-session SETs) IS the live fields above; SET DATASESSION swaps
        // these in lockstep with the VfpSession work-area bundle. See VfpInterpreter.DataSession.
        _interpSessions[1] = new InterpSessionState
        {
            Meta = _meta, AreaLocks = _areaLocks, CachedWriters = _cachedWriters, Txn = _txn,
        };
        // 5.5: release the per-area cached write handles when the session is disposed (callers dispose the
        // session, not the interpreter), so no .dbf stays locked past the session's life. 5.14: this now
        // covers EVERY data session's writers, not just the active one.
        Session.Disposing += DisposeCachedWritersAllSessions;
        // P3 §C.2: release every open low-level file handle (FOPEN/FCREATE) when the session is disposed —
        // low-level handles are interpreter-global (independent of work areas), so no OS handle outlives the
        // session. VfpInterpreter.Dispose() does the same for a caller that disposes the interpreter directly.
        Session.Disposing += CloseAllLowLevelHandles;
        // 6.3: delete the snapshot temp dir (rollback pre-images stream to files there) when the session is
        // disposed — a catch-all for any temp a crash/abort left behind (each FileSnapshot.Cleanup already
        // removes its own temps after commit/rollback, so this only ever finds leftovers).
        Session.Disposing += DeleteSnapshotTempDir;
        // 5.13: an ADO.NET copy-on-write transaction Commit/Rollback QUIESCES the session (CloseAllHandles)
        // right before it swaps each private copy over the live file. A byte-range lock (RLOCK/FLOCK) an SP
        // took inside the transaction rides a cached writer on the LIVE file (coordination happens there);
        // that open handle + its lock would break the File.Replace/Copy writeback. Release every held lock
        // and dispose the cached writers at the quiesce point — the DEFINED lock disposition for an ADO.NET
        // transaction boundary. Autocommit never calls CloseAllHandles, so this is inert outside a tx.
        Session.HandlesClosing += ReleaseLocksAndWritersOnQuiesce;
    }

    // 5.13/5.14 quiesce hook (see ctor): drop the interpreter's held locks + cached writers so a transaction
    // Commit/Rollback can write back the live files. 5.14 — do it for EVERY data session, not just the ACTIVE
    // one: a NON-active PRIVATE session (CreateDataSession) that took a byte-range lock / cached writer on a
    // .dbf the imminent File.Replace targets would otherwise block the writeback (and keep ISRLOCKED .T.). For
    // each session we release the byte ranges off its cached writers, clear its AreaLocks bookkeeping (so
    // ISRLOCKED reports .F. in that session after the quiesce), then dispose the cached writers themselves
    // (closing the OS handle). The ACTIVE session's InterpSessionState aliases the live _areaLocks/_cachedWriters,
    // so it is covered by the same loop — single-session behaviour is byte-identical to the old two-call form.
    private void ReleaseLocksAndWritersOnQuiesce()
    {
        foreach (var st in _interpSessions.Values)
        {
            foreach (var set in st.AreaLocks.Values)
            {
                if (st.CachedWriters.TryGetValue(set.Path, out var w))
                {
                    foreach (var rec in set.Records) UnlockOne(w, rec);
                    if (set.File) { try { w.UnlockFile(); } catch { /* best-effort byte-range release */ } }
                }
                set.Records.Clear();
                set.File = false;
            }
            st.AreaLocks.Clear();
            foreach (var w in st.CachedWriters.Values)
            {
                try { w.Dispose(); } catch { /* best-effort — Dispose releases any OS lock still on the handle */ }
            }
            st.CachedWriters.Clear();
        }
    }

    /// <summary>The data session the interpreter runs over (work areas, USE/SELECT, resolution, DML).</summary>
    public VfpSession Session { get; }

    /// <summary>The memory-variable store (VFP scoping: LOCAL/PRIVATE/PUBLIC + implicit-private).</summary>
    internal MemoryStore Memory { get; }

    /// <summary>The interpreter runtime state (ON ERROR, SET REPROCESS, the lock fail-fast branch).</summary>
    internal RuntimeState Runtime { get; }

    /// <summary>The loaded program, or <see langword="null"/> before <see cref="Load(PrgProgram)"/>.</summary>
    public PrgProgram? Program { get; private set; }

    internal VfpInsertProfile? InsertProfile { get; set; }

    /// <summary>
    /// P3b (point 4) — load the session DBC's <c>StoredProceduresSource</c> (the RI procs + business
    /// functions) straight from the container, so the caller need not <see cref="LoadFile(string)"/> an
    /// export for auto-fire to find the trigger procs. Reuses the P3a <see cref="DbfDatabase.StoredProcedureSource"/>
    /// reader; a no-op when the session has no open database or the container carries no SP source.
    /// </summary>
    public void LoadStoredProceduresFromDatabase()
    {
        var src = Session.Database?.StoredProcedureSource;
        if (string.IsNullOrEmpty(src)) return;
        var prog = PrgParser.Parse(src);
        foreach (var p in prog.Procedures) _procs[p.Name] = p;
        foreach (var p in prog.Procedures) ScanDefines(p.Body);
        ScanDefines(prog.Main);
    }

    /// <summary>Registers the procedures/functions of <paramref name="program"/>; last duplicate wins.</summary>
    public void Load(PrgProgram program)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        foreach (var p in program.Procedures) _procs[p.Name] = p;
        foreach (var p in program.Procedures) ScanDefines(p.Body);
        ScanDefines(program.Main);
    }

    /// <summary>Parses the PRG at <paramref name="path"/> and <see cref="Load(PrgProgram)"/>s it.</summary>
    public void LoadFile(string path) => Load(PrgParser.ParseFile(path));

    /// <summary>True when a procedure/function named <paramref name="name"/> is loaded.</summary>
    public bool Has(string name) => _procs.ContainsKey(name);

    /// <summary>Parses and runs <paramref name="source"/> as TOP-LEVEL statements (a setup snippet) in
    /// the global frame; state mutates the bound session/memory/runtime.</summary>
    public void Execute(string source)
    {
        var prevProfile = VfpInsertProfile.Current;
        VfpInsertProfile.Current = InsertProfile;
        try
        {
            var prog = ParseProgramCached(source);
            foreach (var p in prog.Procedures) { _procs[p.Name] = p; ScanDefines(p.Body); }
            ScanDefines(prog.Main);
            try { ExecBlock(prog.Main); }
            catch (ReturnSignal) { /* RETURN at top level just ends the snippet */ }
        }
        finally
        {
            VfpInsertProfile.Current = prevProfile;
        }
    }

    /// <summary>Evaluates a single VFP expression string (e.g. a DBC field DEFAULT / RULE, or an ad-hoc
    /// <c>?expr</c> command) over this interpreter's session + memory, returning the typed result. Field
    /// references resolve against the CURRENT work area's record; bare names also resolve to memvars /
    /// <c>#DEFINE</c>s; function calls dispatch to loaded stored procedures (UDFs) before the engine's
    /// built-ins. A parse/eval failure yields <see cref="VfpValue.Null"/> (the same fail-soft contract as
    /// <c>EVALUATE()</c>).</summary>
    public VfpValue EvalExpression(string expression) => EvalText(expression ?? string.Empty);

    /// <summary>Like <see cref="EvalExpression"/> but REPORTS a parse/eval failure instead of the fail-soft
    /// <c>EVALUATE()</c> Null. Returns <see langword="true"/> with the evaluated value, or <see langword="false"/>
    /// (value = <see cref="VfpValue.Null"/>) when parsing or evaluation threw. The enforced write-model uses
    /// this so a DBC RULE whose expression ERRORS (an unloaded UDF, a missing field reference) surfaces as a
    /// violation rather than silently passing — exactly as VFP9 raises the error.</summary>
    public bool TryEvalExpression(string expression, out VfpValue value)
    {
        try
        {
            value = ParseExpressionCached(MicroVfpExprRewrite.Normalize(expression ?? string.Empty)).Evaluate(_row, _ctx);
            return true;
        }
        catch
        {
            value = VfpValue.Null;
            return false;
        }
    }

    /// <summary>Invokes the loaded procedure/function <paramref name="name"/> with <paramref name="args"/>
    /// (BY VALUE) and returns its value (<c>RETURN expr</c>, default <c>.T.</c>).</summary>
    public VfpValue Call(string name, params VfpValue[] args)
    {
        if (!_procs.TryGetValue(name, out var proc))
            throw new MicroVfpRuntimeException($"Procedure '{name}' is not loaded.");
        return CallProc(proc, args ?? Array.Empty<VfpValue>(), (args?.Length ?? 0), null);
    }

    // ─────────────────────────── call mechanism ───────────────────────────

    private sealed class CallContext
    {
        public readonly string Name;
        public readonly VfpValue[] Args;
        public int PassedCount;
        public readonly List<string> ParamNames = new();
        public bool ParamsBound;
        public CallContext(string name, VfpValue[] args, int passed) { Name = name; Args = args; PassedCount = passed; }
    }

    private CallContext CurrentCall => _callStack[^1];

    /// <summary>Run <paramref name="proc"/> over a fresh memory frame, binding params, executing the
    /// body, writing BY-REF results back, and returning <c>RETURN</c>'s value (default <c>.T.</c>).</summary>
    private VfpValue CallProc(ProcDef proc, VfpValue[] args, int passedCount, string?[]? byRefNames)
    {
        // Cap call nesting so a runaway recursive UDF (no base case) throws a NORMAL, catchable runtime
        // error instead of a fatal StackOverflowException that would kill the host process. The numeric
        // limit is far above any legitimate VFP call depth (VFP itself caps at ~128 nested calls); the
        // EnsureSufficientExecutionStack probe additionally trips — throwing a catchable
        // InsufficientExecutionStackException — if a single deep call would actually exhaust the native
        // stack before the numeric cap (each interpreter frame spans many native frames).
        if (_callStack.Count >= MaxCallDepth)
            throw new MicroVfpRuntimeException(
                $"Maximum call nesting depth ({MaxCallDepth}) exceeded calling '{proc.Name}' — " +
                "probable unbounded recursion.", MaxCallDepthErrorNumber);
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

        var cc = new CallContext(proc.Name, args, passedCount);
        Memory.PushFrame();
        _callStack.Add(cc);

        // Header params `Name(a,b)` bind LOCAL; a leading PARAMETERS/LPARAMETERS binds when executed.
        if (proc.Parameters.Count > 0)
        {
            BindParams(proc.Parameters, isLocal: true, cc);
            cc.ParamsBound = true;
        }

        VfpValue result = VfpValue.Logical(true);
        try { ExecBlock(proc.Body); }
        catch (ReturnSignal rs) { result = rs.Value; }

        // Capture callee param finals BEFORE popping (for BY-REF write-back).
        var finals = cc.ParamNames.Select(n => Memory.Get(n)).ToArray();

        _callStack.RemoveAt(_callStack.Count - 1);
        Memory.PopFrame();

        if (byRefNames is not null)
            for (int i = 0; i < byRefNames.Length && i < finals.Length; i++)
                if (byRefNames[i] is { } caller) Memory.Set(caller, finals[i]);

        return result;
    }

    private void BindParams(IReadOnlyList<string> names, bool isLocal, CallContext cc)
    {
        for (int i = 0; i < names.Count; i++)
        {
            var v = i < cc.PassedCount && i < cc.Args.Length ? cc.Args[i] : VfpValue.Logical(false);
            Memory.BindParameter(names[i], v, isLocal ? VarKind.Local : VarKind.Private);
            cc.ParamNames.Add(names[i]);
        }
    }

    // ─────────────────────────── statement tree-walk ───────────────────────────

    private sealed class ReturnSignal : Exception { public VfpValue Value; public ReturnSignal(VfpValue v) => Value = v; }
    private sealed class ExitSignal : Exception { public static readonly ExitSignal I = new(); }
    private sealed class LoopSignal : Exception { public static readonly LoopSignal I = new(); }

    private void ExecBlock(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in body)
        {
            try { Exec(s); }
            catch (ReturnSignal) { throw; }
            catch (ExitSignal) { throw; }
            catch (LoopSignal) { throw; }
            catch (Exception ex)
            {
                // A RUNTIME error: run the installed ON ERROR handler in the error's context, then
                // RESUME after this line (default RETURN semantics). With no handler installed — or while
                // already inside a handler — the error propagates exactly as before P2.
                if (_inHandler || _onErrorCmd is null) throw;
                RunErrorHandler(ex, s.Line);
            }
        }
    }

    // ON ERROR <command>: capture ERROR()/MESSAGE()/LINENO(), run the handler (re-entrancy-guarded),
    // then CLEAR ERROR()/MESSAGE() (the RI handler captures them into pnerror/gaErrors first). RETRY is
    // not modelled — VFP's default on handler exit is RESUME AFTER the offending line, which is exactly
    // "return to ExecBlock and continue with the next statement".
    private void RunErrorHandler(Exception ex, int line)
    {
        SetErrorState(ex, line);
        _inHandler = true;
        try
        {
            if (_onErrorCmd is { } cmd) Exec(cmd);
        }
        catch { /* a fault inside the handler is swallowed (VFP would abort the handler). */ }
        finally
        {
            _inHandler = false;
            // The LIVE ERROR()/MESSAGE()/LINENO() clear on handler exit, but the RETAINED last-error
            // state (Runtime.LastError*) persists so a post-handler AERROR() can still fill its array.
            _errNo = 0; _errMsg = ""; _errLine = 0;
        }
    }

    // Capture a trapped error into BOTH the live trap functions (ERROR()/MESSAGE()/LINENO(), cleared on
    // handler exit) AND the retained last-error state that backs AERROR() (kept until the next error).
    private void SetErrorState(Exception ex, int line)
    {
        int no = ErrorNumberOf(ex);
        string msg = ex.Message ?? string.Empty;
        _errNo = no; _errMsg = msg; _errLine = line;
        Runtime.LastErrorNumber = no;
        Runtime.LastErrorMessage = msg;
        Runtime.LastErrorDetail = null;
        Runtime.LastErrorArea = Session.CurrentArea;
        Runtime.LastErrorTrigger = 0;
        Runtime.LastErrorField = 0;
    }

    // Map a trapped exception onto the VFP9 error number the RI/AERROR/ON ERROR contract branches on
    // (project-review 5.3). Order — MOST authoritative first, message-text sniffing LAST:
    //   (1) TYPED  — the throw site pinned the number on the exception via IVfpErrorCode. The inner chain
    //       is walked too, so a BCL wrapper that re-threw one of ours (e.g. Array.Sort wrapping ASORT's
    //       typed error in an InvalidOperationException) still surfaces the pinned number. No text read.
    //   (2) KNOWN OWN TYPE — a Core exception whose WHOLE class maps 1:1 to a VFP number.
    //   (3) LAST RESORT — an English message-text heuristic, for FOREIGN (BCL / third-party) exceptions
    //       that cannot carry a typed number, plus a few PERIPHERAL own throws whose VFP number is
    //       syntax-dependent (SET ORDER / SET INDEX "not found" — VFP reports 12/1683 depending on form,
    //       so they intentionally stay on the shared "not found" → 1 mapping). Best-effort and English-only;
    //       every RI-critical / oracle-pinned OWN class is typed above, so text never decides THOSE. The
    //       final fallback stays a positive, non-zero number — the RI framework branches require non-zero.
    internal static int ErrorNumberOf(Exception ex)
    {
        // (1) typed number — authoritative. Walk the inner chain: a BCL wrapper may sit on top of ours.
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is IVfpErrorCode { VfpErrorNumber: int typed }) return typed;

        // (2) known OWN exception types with a well-defined 1:1 VFP class.
        if (ex is DbfFileNotFoundException) return 1;   // "File/table does not exist" (VFP err 1).

        // (3) LAST RESORT — foreign exceptions + a few peripheral own throws (see the header note). English-
        // only, best-effort. Most specific patterns first so a generic "rule" message can't shadow the trigger.
        string m = ex.Message ?? string.Empty;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("locate command must be issued")) return 42;     // CONTINUE with no prior LOCATE.
        if (Has("1539") || Has("trigger failed")) return 1539;   // RI trigger returned .F. (RESTRICT abort).
        if (Has("field") && Has("rule")) return 1582;            // field validation rule violated.
        if (Has("record") && Has("rule")) return 1583;           // record (table) validation rule violated.
        if (Has("rule violated") || Has("violates the rule")) return 1582; // bare rule-text → field-rule class.
        if (Has("session number is invalid")) return 1540;       // SET DATASESSION TO <non-existent session>.
        // Lock-related text (a FOREIGN IOException from the Core writer — the interpreter models locks as
        // always-held, single-process, so a genuine multi-user conflict is not reachable/oracle-pinnable
        // headless). These are TWO DISTINCT VFP classes and the branch must not collapse them:
        //   • "not locked" is genuinely err 130 "Record is not locked" — the ABSENCE of a held lock.
        //   • a lock CONFLICT (another user holds it) is a different class: err 109 (record) / 108 (whole
        //     file). The legacy code returned 130 for the conflict case too; corrected here per project-
        //     review 5.3 (a headless oracle pin of a real multi-user conflict is infeasible — see above).
        if (Has("not locked")) return 130;                       // absence of a held lock (err 130).
        if (Has("file") && Has("lock")) return 108;              // whole-file lock conflict.
        if (Has("locked") || Has("lock")) return 109;            // record lock conflict.
        if (Has("not found") || Has("does not exist")) return 1; // "File does not exist".
        if (Has("data type") || Has("type mismatch") || Has("not the same data type")) return 9; // type mismatch.
        return 1;   // unknown cause → a positive, non-zero VFP-style error number (all RI fallbacks need).
    }

    private void Exec(PrgStatement s)
    {
        long dispatchStart = VfpInsertProfile.Start();
        // Batch 4: flush any DEFERRED direct-append read-view refresh before the next NON-INSERT statement, so
        // a run of fast INSERTs reopens the view ONCE (here, at the first following statement) rather than per
        // row. INSERT itself is exempt — it appends through the cached writer and never reads the stale view.
        if (_pendingAppendPaths is { Count: > 0 } && s is not InsertStmt) FlushPendingAppends();
        switch (s)
        {
            case Assignment a: AssignTo(a.Target, Eval(a.Value)); break;
            case StoreStmt st: { var v = Eval(st.Value); foreach (var t in st.Targets) AssignTo(t, v); break; }
            case VarDecl vd: ExecDecl(vd); break;
            case DimensionStmt dm: ExecDimension(dm); break;
            case ReleaseStmt rl: foreach (var n in rl.Names) Memory.Release(n); break;
            case ParametersStmt ps: ExecParameters(ps); break;
            case ReturnStmt r: throw new ReturnSignal(r.Value is null ? VfpValue.Logical(true) : Eval(r.Value));
            case ExitStmt: throw ExitSignal.I;
            case LoopStmt: throw LoopSignal.I;
            case IfStmt iff: ExecIf(iff); break;
            case DoCaseStmt dc: ExecDoCase(dc); break;
            case DoWhileStmt dw: ExecDoWhile(dw); break;
            case ForStmt f: ExecFor(f); break;
            case ForEachStmt fe: ExecForEach(fe); break;
            case ScanStmt sc: ExecScan(sc); break;
            case DoCall dca: ExecDoCall(dca); break;
            case ExprStatement es: Eval(es.Expression); break;
            case UseStmt us: ExecUse(us); break;
            case SelectAreaStmt sa: ExecSelectArea(sa); break;
            case SqlSelectStmt sq: ExecSqlSelect(sq); break;
            case SeekStmt sk: ExecSeek(sk); break;
            case GoStmt go: ExecGo(go); break;
            case SkipStmt sp: ExecSkip(sp); break;
            case SetOrderStmt so: ExecSetOrder(so); break;
            case IndexStmt ix: ExecIndex(ix); break;
            case ReindexStmt rix: ExecReindex(rix); break;
            case DeleteTagStmt dt: ExecDeleteTag(dt); break;
            case SetIndexStmt si: ExecSetIndex(si); break;
            case SetStmt set: ExecSet(set); break;
            case SetRelationStmt sr: ExecSetRelation(sr); break;
            case SetRelationOffStmt sro: ExecSetRelationOff(sro); break;
            case SetSkipStmt ss: ExecSetSkip(ss); break;
            case OnErrorStmt oe: ExecOnError(oe); break;
            case ReplaceStmt rp: ExecReplace(rp); break;
            case DeleteStmt del: ExecDelete(del); break;
            case RecallStmt rc: ExecRecall(rc); break;
            case SumStmt sum: ExecSum(sum); break;
            case UnlockStmt ul: ExecUnlock(ul); break;       // release our REAL byte-range locks (finding 5.13).
            case BeginTxnStmt: BeginTransaction(); break;
            case EndTxnStmt: EndTransaction(); break;
            case RollbackStmt: RollbackTransaction(); break;
            case DirectiveStmt d: ProcessDefine(d.Text); break;
            case InsertStmt ins: VfpInsertProfile.Stop(VfpInsertProfileBucket.StatementFetchDispatch, dispatchStart); ExecInsert(ins); break;
            case GatherStmt g: ExecGather(g); break;
            case ScatterStmt sc2: ExecScatter(sc2); break;
            case AppendFromStmt af: ExecAppendFrom(af); break;
            case CopyStructureStmt cs: ExecCopyStructure(cs); break;
            case CopyToStmt ct: ExecCopyTo(ct); break;
            case CreateFromStmt cf: ExecCreateFrom(cf); break;
            case BlankStmt bl: ExecBlank(bl); break;
            case AppendMemoStmt am: ExecAppendMemo(am); break;
            case CopyIndexesStmt ci: ExecCopyIndexes(ci); break;
            case CopyTagStmt cta: ExecCopyTag(cta); break;
            case ReplaceFromArrayStmt rfa: ExecReplaceFromArray(rfa); break;
            case TotalStmt tot: ExecTotal(tot); break;
            case PackStmt pk: ExecPack(pk); break;
            case RenameTableStmt rt: ExecRenameTable(rt); break;
            case CopyToArrayStmt cta: ExecCopyToArray(cta); break;
            case ZapStmt zp: ExecZap(zp); break;
            case WaitStmt wt: ExecWait(wt); break;
            case SaveToStmt sv: ExecSaveTo(sv); break;
            case RestoreFromStmt rf: ExecRestoreFrom(rf); break;
            case MemoryDumpStmt md: ExecMemoryDump(md); break;
            case ProceduresStmt pr: ExecProcedures(pr); break;
            case PackDatabaseStmt: ExecPackDatabase(); break;
            case ValidateDatabaseStmt vd: ExecValidateDatabase(vd); break;
            case DisplayStructureStmt ds: ExecDisplayStructure(ds); break;
            case DisplayTablesStmt dtb: ExecDisplayTables(dtb); break;
            case SqlPassthroughStmt sp: ExecSqlPassthrough(sp); break;
            case FlushStmt: break;                           // write-through model — persisted already; no-op.
            case ClearStmt cl: ExecClear(cl); break;
            case MacroSubstStmt ms: ExecMacroSubst(ms); break;
            case LocateStmt loc: ExecLocate(loc); break;
            case ContinueStmt: ExecContinue(); break;
            case UnknownCommand uc:
                // CLOSE ALL sweeps open low-level file handles (P3 §C.2); every other unrecognised command
                // (and CLOSE DATABASES/TABLES/…) stays a headless no-op.
                if (string.Equals(uc.Verb, "CLOSE", StringComparison.OrdinalIgnoreCase)) ExecCloseCommand(uc.Arguments);
                break;
            case ProcDef: break;
            default: break;
        }
    }

    private void ExecDecl(VarDecl vd)
    {
        if (vd.All) return; // PRIVATE/LOCAL ALL [LIKE/EXCEPT] — no reservation needed for the targets.
        var kind = vd.Scope switch
        {
            DeclScope.Public => VarKind.Public,
            DeclScope.Local => VarKind.Local,
            _ => VarKind.Private,
        };
        for (int i = 0; i < vd.Names.Count; i++)
        {
            string? dim = i < vd.Dimensions.Count ? vd.Dimensions[i] : null;
            if (dim is not null && TryParseDims(dim, out int rows, out int cols))
                Memory.DeclareArray(vd.Names[i], kind, rows, cols);
            else
                Memory.Declare(vd.Names[i], kind);
        }
    }

    // DIMENSION / REDIMENSION name(r[,c]) — create or resize each named array (preserve + .F.-fill).
    private void ExecDimension(DimensionStmt dm)
    {
        for (int i = 0; i < dm.Names.Count; i++)
        {
            string? dim = i < dm.Dimensions.Count ? dm.Dimensions[i] : null;
            if (dim is not null && TryParseDims(dim, out int rows, out int cols))
                Memory.RedimOrCreateArray(dm.Names[i], rows, cols);
        }
    }

    // Parse the captured bracket text of an array declaration ("(1,12)" / "[5]" / "(lnRows+1,alen(a,2))")
    // into row/col counts; the dimension expressions are evaluated (constants or live expressions). cols=0
    // ⇒ a 1-D array. Returns false when the text is not a valid (positive-row) dimension list.
    private bool TryParseDims(string dimText, out int rows, out int cols)
    {
        rows = 0; cols = 0;
        string inner = dimText.Trim();
        if (inner.Length < 2) return false;
        inner = inner.Substring(1, inner.Length - 2);          // strip the enclosing () or [].
        var parts = PrgScan.SplitTopCommas(inner);
        if (parts.Count == 0 || parts[0].Trim().Length == 0) return false;
        rows = (int)EvalText(parts[0]).AsNumber;
        if (parts.Count > 1 && parts[1].Trim().Length > 0) cols = (int)EvalText(parts[1]).AsNumber;
        return rows > 0;
    }

    private void ExecParameters(ParametersStmt ps)
    {
        var cc = CurrentCall;
        if (cc.ParamsBound) return;
        BindParams(ps.Names, isLocal: ps.IsLocal, cc);
        cc.ParamsBound = true;
    }

    private void ExecIf(IfStmt iff)
    {
        if (Truth(Eval(iff.Condition))) ExecBlock(iff.Then);
        else ExecBlock(iff.Else);
    }

    private void ExecDoCase(DoCaseStmt dc)
    {
        foreach (var c in dc.Cases)
            if (Truth(Eval(c.Condition))) { ExecBlock(c.Body); return; }
        if (dc.Otherwise is not null) ExecBlock(dc.Otherwise);
    }

    private void ExecDoWhile(DoWhileStmt dw)
    {
        while (Truth(Eval(dw.Condition)))
        {
            try { ExecBlock(dw.Body); }
            catch (ExitSignal) { break; }
            catch (LoopSignal) { /* continue */ }
        }
    }

    private void ExecFor(ForStmt f)
    {
        double to = Eval(f.To).AsDouble;                       // burned-in once.
        double step = f.Step is null ? 1d : Eval(f.Step).AsDouble;
        if (step == 0d) step = 1d;
        double cur = Eval(f.From).AsDouble;
        Memory.Set(f.Variable, VfpValue.Number(cur));
        while (step > 0 ? cur <= to : cur >= to)
        {
            try { ExecBlock(f.Body); }
            catch (ExitSignal) { break; }
            catch (LoopSignal) { /* fall through to increment */ }
            cur = Memory.Get(f.Variable).AsDouble + step;       // body may mutate the loop var.
            Memory.Set(f.Variable, VfpValue.Number(cur));
        }
    }

    // FOR EACH uVar IN aArray … ENDFOR — iterate the array's elements in LINEAR (row-major) order, binding
    // uVar to a VALUE COPY of each element (no write-back into the array; hackfox s4g688). The element set is
    // snapshotted once up front so a resize inside the body does not change the count mid-loop. Non-array /
    // unbound collections iterate zero times (no user Collection object model in microVFP).
    private void ExecForEach(ForEachStmt fe)
    {
        var arr = Memory.FindArray(fe.Collection.Text.Trim());
        if (arr is null) return;
        int n = arr.Length;
        var snapshot = new VfpValue[n];
        for (int i = 0; i < n; i++) snapshot[i] = arr.GetLinear(i + 1);

        foreach (var el in snapshot)
        {
            Memory.Set(fe.Variable, el);
            try { ExecBlock(fe.Body); }
            catch (ExitSignal) { break; }
            catch (LoopSignal) { /* next element */ }
        }
    }

    private void ExecScan(ScanStmt sc)
    {
        int area = Session.CurrentArea;
        // SCAN's default scope is ALL (an implicit GO TOP). A WHILE clause with no explicit scope is
        // REST — it processes from the CURRENT record (the canonical `SEEK key` / `SCAN WHILE key=…`
        // RI cascade idiom relies on this; a GO TOP would discard the SEEK). See MICROVFP_SEMANTICS §SCAN.
        var (scope, count) = ResolveLocScope(sc.Scope);
        if (sc.Scope is null && sc.While is not null) scope = LocScope.Rest;

        switch (scope)
        {
            case LocScope.All:
                GoTop(area);
                break;
            case LocScope.Record:
                GoRecord(area, count);
                break;
        }

        int remaining = scope switch
        {
            LocScope.Next => Math.Max(0, count),
            LocScope.Record => 1,
            _ => int.MaxValue,
        };

        while (remaining > 0)
        {
            Session.SelectArea(area);                            // body may have switched areas.
            var m = Meta(area);
            if (m.Eof) break;
            var wa = Session.AreaAt(area);
            if (wa is null) break;
            if (!Visible(wa, m.RecNo))
            {
                if (scope == LocScope.Record) break;
                Skip(area, 1);
                continue;
            }
            if (sc.While is not null && !Truth(Eval(sc.While))) break;
            remaining--;                                        // NEXT counts visited records, not FOR matches.
            bool match = sc.For is null || Truth(Eval(sc.For));
            if (match)
            {
                try { ExecBlock(sc.Body); }
                catch (ExitSignal) { Session.SelectArea(area); break; }
                catch (LoopSignal) { /* implicit skip below */ }
            }
            Session.SelectArea(area);
            Skip(area, 1);
        }
    }

    private void ExecDoCall(DoCall d)
    {
        if (!_procs.TryGetValue(d.Name, out var proc)) return;  // unknown DO target → no-op.
        int n = d.Args.Count;
        var values = new VfpValue[n];
        var byRef = new string?[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = Eval(d.Args[i]);
            byRef[i] = IsBareVariable(d.Args[i].Text) ? d.Args[i].Text.Trim() : null;
        }
        CallProc(proc, values, n, byRef);
    }

    private static bool IsBareVariable(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || !(VfpIdentifier.IsStart(t[0]))) return false;
        foreach (var ch in t) if (!(VfpIdentifier.IsPart(ch))) return false;
        return true; // a single identifier ⇒ BY REFERENCE (a literal/expr/(parens)/a.b is not).
    }

    // ─────────────────────────── data / work-area commands ───────────────────────────

    private void ExecUse(UseStmt u)
    {
        if (u.IsClose)
        {
            int area = u.In is not null ? ResolveAreaRef(u.In) : Session.CurrentArea;
            // 5.13: closing a table releases the locks the session held on it (VFP semantics) — release our
            // byte-range locks FIRST so the writer carries no held lock and the prune below can dispose it.
            if (area > 0) { ReleaseAreaLocks(area); Session.CloseArea(area); _meta.Remove(area); }
            PruneCachedWriters();   // 5.5: releasing the last area on a file must release its cached writer.
            return;
        }

        string table = NameOf(u.Table!);
        if (table.Contains('!')) table = table[(table.IndexOf('!') + 1)..];
        string? alias = u.Alias is null ? null : NameOf(u.Alias);

        int? inArea = null;
        if (u.In is not null)
        {
            if (u.In.IsNameExpr)
            {
                var v = Eval(u.In.Expr!.Expression);
                inArea = IsNumeric(v) ? (int)v.AsNumber : Session.FindAreaByAlias(v.AsString)?.Area;
            }
            else if (int.TryParse(u.In.Name, out var n)) inArea = n;
            else inArea = Session.FindAreaByAlias(u.In.Name!)?.Area;
        }

        int targetArea = inArea switch
        {
            0 => Session.LowestFreeAreaNumber(),
            int n => n,
            _ => Session.CurrentArea,
        };

        Session.Use(table, inArea, alias, again: u.Again,
            exclusive: u.Mode == UseMode.Exclusive, noUpdate: u.NoUpdate);
        ReleaseStaleLocks();    // 5.13: a USE that repurposed/closed an area drops the locks it held (before prune).
        PruneCachedWriters();   // 5.5: repurposing an area off its old table may release that file's cached writer.

        var wa = Session.AreaAt(targetArea);
        if (wa is not null)
        {
            var m = new AreaMeta();
            _meta[wa.Area] = m;

            // USE … INDEX <list> — open+track the named non-structural index files (was a no-op).
            if (u.Index is { Count: > 0 } && wa.Table.SourcePath is string tpath)
                foreach (var f in u.Index)
                {
                    string fp = ResolveExistingIndexPath(tpath, NameOf(f).Trim());
                    if (File.Exists(fp)) AddExtraIndex(wa.Area, fp);
                }

            // Controlling order: an explicit ORDER wins; else the FIRST listed index becomes master.
            string? orderName = u.Order is null ? null : NameOf(u.Order).Trim();
            if (!string.IsNullOrEmpty(orderName))
                m.Order = ResolveOrderName(wa.Area, orderName);
            else if (m.ExtraIndexes is { Count: > 0 })
                m.Order = FirstIndexIdentity(wa.Area);

            GoTop(wa.Area);
        }
    }

    /// <summary>The controlling-order identity of the FIRST opened non-structural index of
    /// <paramref name="area"/> — a standalone <c>.idx</c>'s stem, or the first tag of a <c>.cdx</c>.</summary>
    private string? FirstIndexIdentity(int area)
    {
        var extras = Meta(area).ExtraIndexes;
        if (extras is null || extras.Count == 0) return null;
        string p = extras[0];
        if (IsIdxPath(p)) return IdxOrderName(p);
        try
        {
            using var cdx = CdxFile.Open(p, Session.AreaAt(area)?.Table);
            return TagsInLayoutOrder(cdx).FirstOrDefault()?.Name;
        }
        catch { return null; }
    }

    private void ExecSelectArea(SelectAreaStmt sa)
    {
        var a = sa.Area;
        if (a.IsNameExpr)
        {
            var v = Eval(a.Expr!.Expression);
            if (IsNumeric(v)) Session.SelectArea((int)v.AsNumber);
            else SelectByName(v.AsString);
        }
        else SelectByName(a.Name!);
        Meta(Session.CurrentArea);
    }

    private void SelectByName(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var n)) Session.SelectArea(n);     // SELECT 0 = lowest free; SELECT n.
        else Session.SelectArea(s);                                // SELECT alias.
    }

    private void ExecSqlSelect(SqlSelectStmt sq)
    {
        var parsed = sq.Parsed ?? SafeParseSql(sq.Sql);
        if (parsed is not SelectStatement sel) return;
        var exec = new SelectExecutor(Session, _row);
        exec.Run(sel);
        if (sel.Into is { Kind: IntoKind.Cursor } into)
        {
            Session.SelectArea(into.Name);                         // INTO CURSOR selects the cursor.
            _meta[Session.CurrentArea] = new AreaMeta();
            GoTop(Session.CurrentArea);
        }
    }

    private void ExecSet(SetStmt set)
    {
        string arg = set.Arguments.Trim();
        switch (set.Setting)
        {
            case "REPROCESS": SetReprocess(arg); break;
            case "DELETED": _ctx.Deleted = OnOff(arg); break;
            case "EXACT": _ctx.Exact = OnOff(arg); break;
            case "ANSI": _ctx.Ansi = OnOff(arg); break;
            case "COLLATE": SetCollate(arg); break;             // baked into the next INDEX tag; SET("COLLATE").
            case "UNIQUE": Runtime.Unique = OnOff(arg); break;  // session default for a clause-less INDEX; SET("UNIQUE").
            case "MULTILOCKS": Runtime.Multilocks = OnOff(arg); break; // OFF ⇒ a new RLOCK releases the prior record lock; SET("MULTILOCKS").
            case "KEY": SetKey(arg); break;                     // master-index visible key range; feeds Visible().
            case "FILTER": SetFilter(arg); break;               // per-area record-visibility predicate; feeds Visible(); FILTER()/SET("FILTER").
            case "NEAR": _setNear = OnOff(arg); break;          // failed-SEEK pointer parking; SET("NEAR").
            case "NULL": _ctx.NullSetting = OnOff(arg); break;  // CREATE/ALTER TABLE default nullability; SET("NULL").
            case "AUTOSAVE": _setAutosave = OnOff(arg); break;  // header-buffer flush policy; SET("AUTOSAVE").
            case "MEMOWIDTH": SetMemoWidth(arg); break;         // memo word-wrap column for MEMLINES/MLINE; SET("MEMOWIDTH").
            case "DATASESSION": SetDataSession(arg); break;     // 5.14: SWITCH the active data session; TO current no-op, TO 0/non-existent → err 1540.
            case "DATABASE": SetDatabase(arg); break;           // current-DBC designation; feeds DBC()/SET("DATABASE").
            case "BLOCKSIZE": SetBlockSize(arg); break;         // memo (.fpt) block size for the NEXT created table; SET("BLOCKSIZE").
            case "TEXTMERGE": SetTextMerge(arg); break;         // ON/OFF + DELIMITERS TO; SET("TEXTMERGE"); default delims for TEXTMERGE().
            case "FIELDS": SetFields(arg); break;               // field-list restriction tracking; FLDLIST()/SET("FIELDS").
            // SET NOCPTRANS TO [FieldList] [ADDITIVE] — a FoxPro-2.x runtime relic. The real (persistent)
            // NOCPTRANS semantics live on the column 0x04 flag (already fully honoured on the read path);
            // this runtime override is accepted as a no-op. FLAG: no runtime per-field code-page override.
            case "NOCPTRANS": break;
            default: break; // TALK / COMPATIBLE / DATA / PROCEDURE / … — irrelevant to results.
        }
    }

    private static bool OnOff(string s) => s.Trim().StartsWith("ON", StringComparison.OrdinalIgnoreCase);

    // SET DATASESSION TO n — SWITCH the active data session (5.14). VFP only ever CREATES a private session
    // from a form (DataSession=2); microVFP's headless equivalent is the host API CreateDataSession(). This
    // handler only SWITCHES among sessions that already exist: TO the current id is a no-op (so TO 1 while on
    // session 1 stays a no-op — the oracle-pinned fact); TO an existing id switches; TO 0 / a non-existent id
    // raises VFP error 1540 "Session number is invalid." (also oracle-pinned). SET("DATASESSION") returns the
    // numeric current id via FnSet. Isolation is real: work areas, record pointers, orders, relations,
    // buffering, LOCATE state, byte-range locks and the session-scoped SETs all swap with the session.
    private void SetDataSession(string arg)
    {
        string s = arg.Trim();
        if (s.StartsWith("TO", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2).Trim();
        var v = EvalText(s);
        int n = IsNumeric(v) ? (int)v.AsNumber : int.TryParse(v.AsString, out var p) ? p : -1;
        if (n == Session.CurrentDataSessionId) return;   // TO the current session → no-op.
        if (n < 1 || !Session.HasDataSession(n))
            throw new MicroVfpRuntimeException("Session number is invalid.", 1540);   // VFP error 1540.
        SwitchDataSession(n);
    }

    // SET DATABASE TO [name]. With NO name → clear the current-database designation (DBC()/SET("DATABASE")
    // return ""). WITH a name → (re)select the single open DBC as current. Multi-DBC selection is out of
    // scope (single-DBC session), so any name simply restores the current designation.
    private void SetDatabase(string arg)
    {
        string s = arg.Trim();
        if (s.StartsWith("TO", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2).Trim();
        if (s.Length == 0) { _currentDbCleared = true; return; }   // TO (no name) clears the designation.
        // A NAME must reference the actually-open DBC (VFP raises "Database 'X' is not open." otherwise —
        // verified against the VFP9 runtime). Compare path/extension-insensitively (bare DBC name) case-insensitively.
        s = s.Trim('\'', '"', ' ');
        string requested = Path.GetFileNameWithoutExtension(s);
        string open = Session.Database is not null ? Path.GetFileNameWithoutExtension(Session.DatabasePath ?? string.Empty) : string.Empty;
        if (open.Length == 0 || !string.Equals(requested, open, StringComparison.OrdinalIgnoreCase))
            throw new MicroVfpRuntimeException($"Database '{requested}' is not open.");
        _currentDbCleared = false;   // re-select the open DBC as current.
    }

    // The current DBC's name (no path/extension) when one is open AND not cleared, else "". Backs both
    // DBC() (full path) and SET("DATABASE") (bare name) — see DbcPath / FnSet.
    private string CurrentDbPath()
        => (!_currentDbCleared && Session.Database is not null) ? (Session.DatabasePath ?? string.Empty) : string.Empty;

    // SET MEMOWIDTH TO n. VFP silently IGNORES a value below the minimum of 8 (leaves the width unchanged —
    // verified against the VFP9 runtime: after SET MEMOWIDTH TO 5, SET("MEMOWIDTH") still reports the prior value).
    private void SetMemoWidth(string arg)
    {
        var parts = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && parts[0].Equals("TO", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (idx >= parts.Length) return;
        var v = EvalText(parts[idx]);
        int n = IsNumeric(v) ? (int)v.AsNumber : int.TryParse(v.AsString, out var p) ? p : -1;
        if (n >= 8) _memoWidth = n;   // below 8 ⇒ ignored (width unchanged).
    }

    private void SetReprocess(string arg)
    {
        var parts = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && parts[0].Equals("TO", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (idx >= parts.Length) return;
        string val = parts[idx];
        if (val.Equals("AUTOMATIC", StringComparison.OrdinalIgnoreCase)) { Runtime.Reprocess = -2; return; }
        var v = EvalText(val);
        Runtime.Reprocess = IsNumeric(v) ? (int)v.AsNumber
                           : int.TryParse(v.AsString, out var n) ? n : 0;
    }

    private void ExecOnError(OnErrorStmt oe)
    {
        switch (oe.Kind)
        {
            case OnErrorKind.Clear:
                InstallOnError(null, null);
                break;
            case OnErrorKind.Macro:
                // ON ERROR &var — EXPAND the macro NOW (definition-time): the variable's string content
                // becomes the installed handler (re-parsed as a command). The RI save/restore idiom
                // `ON ERROR &pcOldError` re-installs a previously saved handler; an EMPTY string clears it.
                InstallOnError(ExpandMacro(oe.Macro?.Text ?? string.Empty), null);
                break;
            default:
                // ON ERROR <command> — install the already-parsed command + retain its raw source text so
                // ON("ERROR") reads it back and a later `ON ERROR &var` can re-install it verbatim.
                InstallOnError(oe.CommandText, oe.Command);
                break;
        }
    }

    // Install (or clear) the ON ERROR handler. An empty/blank command clears it (Runtime.OnError = null →
    // ON("ERROR") = "" and the lock fail-fast branch sees no handler). A non-empty command text with no
    // pre-parsed statement (the macro path) is parsed here into a single executable command.
    private void InstallOnError(string? commandText, PrgStatement? parsed)
    {
        commandText = commandText?.Trim();
        if (string.IsNullOrEmpty(commandText))
        {
            _onErrorCmd = null; _onErrorText = null; Runtime.OnError = null;
            return;
        }
        _onErrorCmd = parsed ?? ParseSingle(commandText);
        _onErrorText = commandText;
        Runtime.OnError = commandText;
    }

    // Expand an `ON ERROR &var[.]` macro to the variable's CURRENT string content (the handler command).
    // The dot terminator and leading '&' are stripped; an undefined variable yields "" (⇒ clear).
    private string ExpandMacro(string macroText)
    {
        string t = macroText.Trim();
        if (t.StartsWith('&')) t = t.Substring(1);
        t = t.Trim();
        if (t.EndsWith('.')) t = t.Substring(0, t.Length - 1);
        t = t.Trim();
        return t.Length == 0 ? string.Empty : Memory.Get(t).AsString;
    }

    // Generalised &macro execution: expand EVERY &var[.] in the captured line to the variable's CURRENT
    // string content (textual substitution BEFORE execution), then re-parse the result and run it as an
    // embedded statement. Reuses ExpandMacro's single-token rule + ParseSingle. An empty/unparseable
    // expansion is a no-op (an undefined macro expands to "" — VFP treats a blank macro line as nothing).
    private void ExecMacroSubst(MacroSubstStmt ms)
    {
        string expanded = ExpandMacrosInLine(ms.Macro.Text).Trim();
        if (expanded.Length == 0) return;
        // Depth guard: ExpandMacrosInLine bounds textual expansion within one line only, so a self-
        // referential macro (&pcmd where pcmd='&pcmd') re-parses to another MacroSubstStmt and re-enters
        // here forever → StackOverflowException (uncatchable). Bound the nesting and raise VFP-style.
        if (_macroDepth >= 16)
            // 1206 is a microVFP-INTERNAL stable code, NOT oracle-pinned: real VFP9 faults UNCATCHABLY on
            // recursive macro expansion (no trappable ERROR() to read a number from), so there is no
            // authoritative runtime value — same honest labeling as MaxCallDepthErrorNumber. Its only
            // contract is stability; change it only with the matching TypedError test.
            throw new MicroVfpRuntimeException("Nesting level too deep.", 1206);
        var stmt = ParseSingle(expanded);
        if (stmt is null) return;
        _macroDepth++;
        try { Exec(stmt); }
        finally { _macroDepth--; }
    }

    // Replace every top-level (outside string literals) &ident[.] occurrence with the memvar's string value.
    // The optional trailing '.' terminator is consumed. Bounded iteration expands a macro whose expansion
    // itself contains a further macro reference.
    private string ExpandMacrosInLine(string line)
    {
        for (int pass = 0; pass < 10; pass++)
        {
            var sb = new StringBuilder(line.Length);
            bool changed = false;
            bool inStr = false; char q = '\0';
            for (int i = 0; i < line.Length;)
            {
                char c = line[i];
                if (inStr) { sb.Append(c); if (c == q) inStr = false; i++; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; sb.Append(c); i++; continue; }
                if (c == '[' && !IsSubscriptBracket(line, i))
                {
                    // A value-position [...] is a VFP string literal. Copy it verbatim; `name[...]`
                    // remains an array subscript whose expression can contain a runtime macro.
                    do { sb.Append(line[i]); } while (++i < line.Length && line[i - 1] != ']');
                    continue;
                }
                if (c == '&' && i + 1 < line.Length && (VfpIdentifier.IsStart(line[i + 1])))
                {
                    int j = i + 1;
                    while (j < line.Length && (VfpIdentifier.IsPart(line[j]))) j++;
                    string name = line.Substring(i + 1, j - (i + 1));
                    if (j < line.Length && line[j] == '.') j++;   // consume the '.' terminator.
                    sb.Append(Memory.Get(name).AsString);
                    changed = true;
                    i = j;
                    continue;
                }
                sb.Append(c); i++;
            }
            line = sb.ToString();
            if (!changed) break;
        }
        return line;
    }

    // Keep this local context rule aligned with MicroVfpExprRewrite.ConvertSubscripts: '[' immediately
    // after an identifier character or a close delimiter is an array subscript; otherwise it starts a
    // bracket string literal.
    private static bool IsSubscriptBracket(string text, int index)
    {
        char prev = index > 0 ? text[index - 1] : '\0';
        return prev == ')' || prev == ']' || VfpIdentifier.IsPart(prev);
    }

    // CLEAR MEMORY / CLEAR ALL. MEMORY releases every memvar + array; ALL additionally closes every open
    // work area (a program-start reset). The bare/UI forms (ClearKind.Ui) have no headless effect.
    private void ExecClear(ClearStmt cl)
    {
        switch (cl.Kind)
        {
            case ClearKind.Memory:
                Memory.ClearAll();
                break;
            case ClearKind.All:
                Memory.ClearAll();
                ReleaseAllLocks();            // 5.13: CLEAR ALL = UNLOCK ALL + close everything (release byte-range locks).
                DisposeAllCachedWriters();    // 5.5: CLEAR ALL closes every file — release cached writers too.
                Session.CloseActiveAreas();  // 5.14: CLEAR ALL is scoped to the CURRENT data session (NOT the
                                             // all-sessions transaction-quiesce CloseAllHandles); keeps the binding.
                _meta.Clear();               // drop the per-area record pointers/relations too.
                _currentDbCleared = true;    // CLEAR ALL closes all files incl. databases ⇒ DBC()/SET("DATABASE")="".
                break;
            default:
                break;   // CLEAR / CLEAR WINDOWS / GETS / … — no UI model; flagged no-op.
        }
    }

    // Parse a single command line into one PrgStatement (the ON ERROR handler body); null when it does not
    // yield exactly one top-level statement.
    private static PrgStatement? ParseSingle(string command)
    {
        try
        {
            var prog = PrgParser.Parse(command);
            return prog.Main.Count > 0 ? prog.Main[0] : null;
        }
        catch { return null; }
    }

    // ─────────────────────────── name / field resolution ───────────────────────────

    /// <summary>Resolve a name for the expression engine: qualified <c>alias.field</c> → that area's
    /// current record; <c>m.var</c> → memvar; bare → CURRENT area field, else memvar, else #DEFINE.</summary>
    internal object? ResolveName(string name)
    {
        if (name.Equals("_triggerlevel", StringComparison.OrdinalIgnoreCase))
            return (decimal)TriggerLevel;     // P2 RI seam — the trigger gate (see TriggerLevel).

        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            string head = name[..dot], rest = name[(dot + 1)..];
            if (head.Equals("m", StringComparison.OrdinalIgnoreCase))
                return Memory.Get(rest).ToClr();
            var wa = Session.FindAreaByAlias(head);
            if (wa is not null)
            {
                // Batch 4: an alias.field read observes this area — flush any deferred fast-append refresh
                // first (and re-fetch, since the reopen replaces the WorkArea). No-op when nothing pending.
                if (_pendingAppendPaths is { Count: > 0 }) { FlushPendingAppends(wa.Area); wa = Session.FindAreaByAlias(head); }
                return wa is not null ? ReadField(wa, rest) : null;
            }
            return null;                                  // object property / DBC!table.field → undefined.
        }

        var cur = Session.AreaAt(Session.CurrentArea);
        if (cur is not null && ColumnIndex(cur.Table, name) >= 0)
        {
            // Batch 4: a bare field read of the current area — flush any deferred fast-append refresh first.
            if (_pendingAppendPaths is { Count: > 0 }) { FlushPendingAppends(cur.Area); cur = Session.AreaAt(Session.CurrentArea); }
            return cur is not null ? ReadField(cur, name) : null;
        }
        // A bare ARRAY name in scalar context = element (1,1)/(1), per VFP.
        if (Memory.FindArray(name) is { } arr) return arr.First.ToClr();
        if (Memory.IsDefined(name)) return Memory.Get(name).ToClr();
        if (_defines.TryGetValue(name, out var d)) return d.ToClr();
        return null;
    }

    // Field read WITH the buffer overlay: a buffered edit to the current record shadows the on-disk value,
    // and a read on a buffered APPENDED row (recno past the physical count) comes from the append buffer.
    // Falls through to the raw on-disk read when the area is unbuffered / the field is unedited.
    private object? ReadField(VfpSession.WorkArea wa, string field)
    {
        if (_meta.TryGetValue(wa.Area, out var mm) && mm.Buffering > 1 && mm.Buf is { } buf)
        {
            int idx = ColumnIndex(wa.Table, field);
            if (idx < 0) return null;
            int rcTable = wa.Table.RecordCount;
            if (mm.RecNo > rcTable)                                // a buffered appended row.
            {
                int ai = mm.RecNo - rcTable - 1;
                if (ai < 0 || ai >= buf.Appends.Count) return null;
                return buf.Appends[ai].Fields.TryGetValue(idx, out var av) ? av : BlankFor(wa.Table.Columns[idx]);
            }
            if (mm.RecNo >= 1 && mm.RecNo <= rcTable && buf.Rows.TryGetValue(mm.RecNo, out var e)
                && e.Fields.TryGetValue(idx, out var bv))
                return bv;                                          // buffered edit shadows the disk value.
        }
        return ReadFieldOnDisk(wa, field);
    }

    // The typed blank an unset appended-row field reads as / is written to disk with: the column type's real
    // EMPTY() value — "" for character, 0 for numerics, .F. for logical, an empty date for D/T — NEVER .NULL.
    // for a non-character column (matching what DbfWriter writes for a column the INSERT omitted, so the live
    // pre-commit view and the committed on-disk row agree).
    private static object? BlankFor(DbfColumn col) => col.Type switch
    {
        'C' or 'V' or 'M' => string.Empty,
        'I' => 0,
        'N' or 'F' or 'B' or 'Y' => 0m,
        'L' => false,
        _ => null,   // D/T → empty date (DbfWriter writes a blank date field); exotic types → null.
    };

    // Field read straight from the on-disk record (ignores the buffer) — the pre-change value OLDVAL is
    // seeded from and the value CURVAL() reports.
    private object? ReadFieldOnDisk(VfpSession.WorkArea wa, string field)
    {
        int idx = ColumnIndex(wa.Table, field);
        if (idx < 0) return null;
        var m = Meta(wa.Area);
        int rc = wa.Table.RecordCount;
        if (m.RecNo < 1 || m.RecNo > rc) return null;

        DbfRecord rec;
        if (m.Cached is { } c && m.CachedRec == m.RecNo) rec = c;
        else
        {
            var got = wa.Table.GetRecord(m.RecNo - 1);
            if (got is not DbfRecord dr) return null;     // deleted / unreadable.
            rec = dr; m.Cached = dr; m.CachedRec = m.RecNo;
        }

        var col = wa.Table.Columns[idx];
        if (col.Type is 'C' or 'V')
        {
            // Honour the _NullFlags bitmap FIRST: a NULL-able character field with its null bit set is a
            // genuine .NULL. (ISNULL() true), not the blank string the raw bytes would decode to.
            if (rec[idx] is null) return null;
            var raw = rec.GetRawField(col);
            return wa.Table.Encoding.GetString(raw).TrimEnd(' ', '\0');   // RIGHT-trim only (see header).
        }
        return rec[idx];
    }

    private static int ColumnIndex(DbfTable t, string name)
    {
        var cols = t.Columns;
        for (int i = 0; i < cols.Count; i++)
            if (string.Equals(cols[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string StripQualifier(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private string TypeOf(string expr)
    {
        string e = expr.Trim();
        if (IsBareVariable(e) && !IsDefinedName(e)) return "U";
        // An array-element reference `name(…)` / `name[…]` on an UNDEFINED array is "U" (the corpus uses
        // TYPE('gaErrors(1)')<>"U" to test existence). A DEFINED array falls through to report the element's
        // own type (TYPE('gaErrors[r,1]')<>"L" tests whether a row slot is still an unfilled .F.).
        string lead = LeadingIdentOf(e);
        if (lead.Length > 0 && lead.Length < e.Length)
        {
            char after = e[lead.Length..].TrimStart() is { Length: > 0 } rest ? rest[0] : '\0';
            if ((after == '(' || after == '[') &&
                Memory.FindArray(lead) is null && !_procs.ContainsKey(lead))
                return "U";
        }
        VfpValue v;
        try { v = EvalText(e); } catch { return "U"; }
        return v.Type switch
        {
            VfpType.Character => "C",
            VfpType.Numeric or VfpType.Integer => "N",
            VfpType.Currency => "Y",
            VfpType.Date => "D",
            VfpType.DateTime => "T",
            VfpType.Logical => "L",
            VfpType.Null => "X",
            _ => "U",
        };
    }

    private bool IsDefinedName(string name)
    {
        if (Memory.IsDefined(name)) return true;
        if (_defines.ContainsKey(name)) return true;
        var cur = Session.AreaAt(Session.CurrentArea);
        return cur is not null && ColumnIndex(cur.Table, name) >= 0;
    }

    // ─────────────────────────── helpers ───────────────────────────

    private VfpSession.WorkArea? AreaArg(VfpValue[] a, int i)
    {
        int area = a.Length > i ? AreaNumber(a[i]) : Session.CurrentArea;
        // Batch 4: a record-state function (RECCOUNT/RECNO/EOF/BOF/FOUND/DELETED/DBF/…) observes this area —
        // flush any deferred fast-append refresh so it sees the just-appended rows (no-op when nothing pending).
        if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area);
        return Session.AreaAt(area);
    }

    private AreaMeta? MetaArg(VfpValue[] a, int i)
    {
        var wa = AreaArg(a, i);
        return wa is null ? null : Meta(wa.Area);
    }

    private int AreaNumber(VfpValue v)
        => IsNumeric(v) ? (int)v.AsNumber : (Session.FindAreaByAlias(v.AsString)?.Area ?? 0);

    private int ResolveAreaRef(NameRef nr)
    {
        if (nr.IsNameExpr)
        {
            var v = Eval(nr.Expr!.Expression);
            return IsNumeric(v) ? (int)v.AsNumber : (Session.FindAreaByAlias(v.AsString)?.Area ?? 0);
        }
        if (int.TryParse(nr.Name, out var n)) return n;
        return Session.FindAreaByAlias(nr.Name!)?.Area ?? 0;
    }

    private string NameOf(NameRef nr) => nr.IsNameExpr ? Eval(nr.Expr!.Expression).AsString : (nr.Name ?? string.Empty);

    private void AssignTo(string target, VfpValue value)
    {
        string t = target.Trim();
        if (t.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) t = t[2..];

        // An array-element target `a(i)` / `a(i,j)` / `a[i]` / `a[i,j]`: write the element of the named
        // array (1-based). The subscripts are live expressions (e.g. gaErrors[lnErrorRows,4]).
        int open = t.IndexOfAny(new[] { '(', '[' });
        if (open > 0)
        {
            string name = t.Substring(0, open).Trim();
            if (Memory.FindArray(name) is { } arr)
            {
                char close = t[open] == '(' ? ')' : ']';
                int end = t.LastIndexOf(close);
                if (end > open)
                {
                    var subs = PrgScan.SplitTopCommas(t.Substring(open + 1, end - open - 1));
                    int s1 = subs.Count > 0 ? (int)EvalText(subs[0]).AsNumber : 1;
                    int? s2 = subs.Count > 1 ? (int)EvalText(subs[1]).AsNumber : (int?)null;
                    arr.Set(s1, s2, value);
                }
            }
            return;   // a non-array subscript target is not a memvar path — ignore (as before).
        }
        Memory.Set(t, value);
    }

    private VfpValue Eval(PrgExpr e)
    {
        long start = VfpInsertProfile.Start();
        try
        {
            if (e.IsParsed) return e.Parsed!.Evaluate(_row, _ctx);
            return EvalText(e.Text);
        }
        finally
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.ExpressionEval, start);
        }
    }

    private VfpValue EvalText(string text)
    {
        // Normalise VFP array syntax (bracket subscripts → parens; ALEN/AERROR array-name → quoted name)
        // so dynamically-evaluated strings (TYPE/EVAL/macro/DIMENSION dimension expressions) resolve arrays.
        try
        {
            if (text.IndexOf('&') >= 0) text = ExpandMacrosInLine(text);
            return ParseExpressionCached(MicroVfpExprRewrite.Normalize(text)).Evaluate(_row, _ctx);
        }
        catch { return VfpValue.Null; }
    }

    // The leading identifier of an expression (letters/digits/underscore after an optional leading letter),
    // or "" when it does not start with one.
    private static string LeadingIdentOf(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0 || !(VfpIdentifier.IsStart(s[0]))) return string.Empty;
        int j = 1;
        while (j < s.Length && (VfpIdentifier.IsPart(s[j]))) j++;
        return s.Substring(0, j);
    }

    private static bool Truth(VfpValue v) => v.Type == VfpType.Logical && v.AsLogical;
    private static bool IsNumeric(VfpValue v) => v.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency;

    private static SqlStatement? SafeParseSql(string sql)
    {
        try { return SqlParser.Parse(sql); } catch { return null; }
    }

    private void ScanDefines(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in Walk(body))
            if (s is DirectiveStmt d) ProcessDefine(d.Text);
    }

    private static IEnumerable<PrgStatement> Walk(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in body)
        {
            yield return s;
            switch (s)
            {
                case IfStmt i: foreach (var x in Walk(i.Then)) yield return x; foreach (var x in Walk(i.Else)) yield return x; break;
                case DoWhileStmt w: foreach (var x in Walk(w.Body)) yield return x; break;
                case ForStmt f: foreach (var x in Walk(f.Body)) yield return x; break;
                case ForEachStmt fe: foreach (var x in Walk(fe.Body)) yield return x; break;
                case ScanStmt sc: foreach (var x in Walk(sc.Body)) yield return x; break;
                case DoCaseStmt dc:
                    foreach (var c in dc.Cases) foreach (var x in Walk(c.Body)) yield return x;
                    if (dc.Otherwise is not null) foreach (var x in Walk(dc.Otherwise)) yield return x;
                    break;
            }
        }
    }

    private void ProcessDefine(string text)
    {
        // #DEFINE NAME value  →  register NAME = the value expression (string/number literal).
        string t = text.TrimStart();
        if (t.Length == 0 || t[0] != '#') return;
        t = t[1..].TrimStart();
        if (!t.StartsWith("define", StringComparison.OrdinalIgnoreCase)) return;
        string rest = t[6..].TrimStart();
        int j = 0;
        while (j < rest.Length && (VfpIdentifier.IsPart(rest[j]))) j++;
        if (j == 0) return;
        string name = rest[..j];
        string valText = rest[j..].Trim();
        if (valText.Length == 0) { _defines[name] = VfpValue.Logical(true); return; }
        _defines[name] = EvalText(valText);
    }

    private static int JulianDay(DateTime d)
    {
        int a = (14 - d.Month) / 12, y = d.Year + 4800 - a, m = d.Month + 12 * a - 3;
        return d.Day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    }

    /// <summary>VFP <c>SYS(2007)</c> is CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflect/xorout)
    /// over the CP1252 bytes (verified: "ABC"→62728, ""→65535).</summary>
    private static int Crc16Ccitt(string s)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in Encoding.Latin1.GetBytes(s))
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    // ─────────────────────────── the engine row/host bridge ───────────────────────────

    private sealed class Row : IRowContext, IVfpFunctionHost
    {
        private readonly VfpInterpreter _it;
        public Row(VfpInterpreter it) => _it = it;

        public object? GetField(string name) => _it.ResolveName(name);

        public int RecNo
        {
            get { var m = _it.Meta(_it.Session.CurrentArea); return m.RecNo; }
        }

        public bool Deleted => _it.FnDeleted(Array.Empty<VfpValue>());

        public int RecCount => _it.Session.AreaAt(_it.Session.CurrentArea)?.Table.RecordCount ?? 0;

        public bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result)
            => _it.HostInvoke(upperName, args, ctx, out result);
    }
}
