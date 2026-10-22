using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// microVFP P3 batch 4 (the FINAL backlog batch) — the remaining array / variable / DB-lifecycle / table
/// items: ADIR() / COPY TO ARRAY / AFONT (§C.1); SAVE TO / RESTORE FROM / WAIT / LIST|DISPLAY MEMORY (§C.4);
/// APPEND|COPY PROCEDURES / PACK DATABASE / VALIDATE DATABASE (§C.7); DISPLAY STRUCTURE|TABLES (§C.14);
/// SET FIELDS (§C.15); ZAP (§C.16). Behaviours pinned against the VFP9 runtime (see the P3Misc oracle tests).
/// EXPORT / IMPORT (office/Lotus/SYLK/DIF formats) are FLAGGED — see the completeness ledger.
/// </summary>
public sealed partial class VfpInterpreter
{
    // ── SET FIELDS state (§C.15): the field-list restriction is TRACKED for FLDLIST()/SET("FIELDS")
    // introspection only; the actual field-visibility restriction is FLAGGED (not enforced — hackfox
    // s4g091: views supersede it, and the corpus never relies on it). ──
    private readonly List<string> _setFields = new();
    private bool _setFieldsOn;
    private string _setFieldsAlias = string.Empty;
    // TRUE once the list was established via SET FIELDS TO ALL: SET("FIELDS",1) then reports the LITERAL
    // "ALL" while FLDLIST() still enumerates every field (hackfox s4g091). Oracle-pinned vs the VFP9 runtime: the
    // flag PERSISTS across SET FIELDS ON/OFF (SET("FIELDS",1) stays "ALL" after either) and is cleared only
    // by an explicit field list or an empty SET FIELDS TO.
    private bool _setFieldsAll;

    // ─────────────────────────── §C.15 SET FIELDS ───────────────────────────

    /// <summary>SET FIELDS TO [list | ALL] | ON | OFF. TO with a list records it (+ turns the restriction ON,
    /// alias-qualified against the current work area for FLDLIST()); TO with no list clears it; ON/OFF toggle
    /// the flag without discarding the list.</summary>
    private void SetFields(string arg)
    {
        string a = arg.Trim();
        if (a.Equals("ON", StringComparison.OrdinalIgnoreCase)) { _setFieldsOn = _setFields.Count > 0; return; }
        if (a.Equals("OFF", StringComparison.OrdinalIgnoreCase)) { _setFieldsOn = false; return; }
        if (a.StartsWith("TO", StringComparison.OrdinalIgnoreCase)) a = a.Substring(2).Trim();

        if (a.Length == 0) { _setFields.Clear(); _setFieldsOn = false; _setFieldsAlias = string.Empty; _setFieldsAll = false; return; }
        if (a.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var wa = Session.AreaAt(Session.CurrentArea);
            _setFields.Clear();
            if (wa is not null) _setFields.AddRange(wa.Table.Columns.Where(c => !c.IsSystem).Select(c => c.Name));
            _setFieldsAlias = (Session.CurrentAlias ?? string.Empty).ToUpperInvariant();
            _setFieldsOn = _setFields.Count > 0;
            _setFieldsAll = true;                                // SET("FIELDS",1) now reads back the literal "ALL".
            return;
        }
        _setFields.Clear();
        _setFieldsAll = false;                                  // an explicit list supersedes a prior TO ALL.
        foreach (var piece in PrgScan.SplitTopCommas(a))
        {
            string name = piece.Trim();
            int dot = name.IndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];              // drop any alias. qualifier from the list item.
            name = name.Trim();
            if (name.Length > 0) _setFields.Add(name);
        }
        _setFieldsAlias = (Session.CurrentAlias ?? string.Empty).ToUpperInvariant();
        _setFieldsOn = _setFields.Count > 0;
    }

    // ─────────────────────────── §C.1 COPY TO ARRAY ───────────────────────────

    /// <summary>COPY TO ARRAY aName [FIELDS …] [scope] [FOR][WHILE] — the read counterpart of APPEND FROM
    /// ARRAY. Records copy from the TOP of the (default ALL) scope. An UNDEFINED array auto-dimensions to
    /// (records × fields) 2-D; an EXISTING 2-D array is filled capped by its dimensions (never redimensioned);
    /// an EXISTING 1-D array takes the FIRST scoped record's fields (error when too small). Memo/general/blob
    /// /picture cells hold a <c>.F.</c> placeholder (hackfox s4g386). Oracle-pinned vs the VFP9 runtime.</summary>
    private void ExecCopyToArray(CopyToArrayStmt s)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        var cols = ScatterFields(wa.Table, s.Fields, memo: true);
        int nFields = cols.Count;
        if (nFields == 0) return;

        int savRec = m.RecNo; bool savEof = m.Eof, savBof = m.Bof;
        var rows = new List<VfpValue[]>();
        try
        {
            int rc = wa.Table.RecordCount;
            for (int rec = 1; rec <= rc; rec++)
            {
                if (!Visible(wa, rec)) continue;
                m.RecNo = rec; m.Cached = null; m.Eof = false; m.Bof = false;
                if (s.While is not null && !Truth(Eval(s.While))) break;
                if (s.For is not null && !Truth(Eval(s.For))) continue;
                var vals = new VfpValue[nFields];
                for (int i = 0; i < nFields; i++)
                    vals[i] = cols[i].Type is 'M' or 'G' or 'P' or 'W'
                        ? VfpValue.Logical(false)
                        : VfpValue.FromClr(ReadField(wa, cols[i].Name));
                rows.Add(vals);
            }
        }
        finally { m.RecNo = savRec; m.Eof = savEof; m.Bof = savBof; m.Cached = null; }

        var existing = Memory.FindArray(s.ArrayName);
        if (existing is null)
        {
            int nrec = Math.Max(rows.Count, 1);
            var arr = Memory.RedimOrCreateArray(s.ArrayName, nrec, nFields);
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < nFields; c++)
                    arr.Set(r + 1, c + 1, rows[r][c]);
            return;
        }
        if (!existing.Is2D)
        {
            if (rows.Count == 0) return;
            if (existing.Length < nFields)
                throw new MicroVfpRuntimeException("Subscript is outside defined range.", 31);
            for (int i = 0; i < nFields; i++) existing.SetLinear(i + 1, rows[0][i]);
            return;
        }
        int maxRows = Math.Min(existing.Rows, rows.Count);
        int maxCols = Math.Min(existing.Cols, nFields);
        for (int r = 0; r < maxRows; r++)
            for (int c = 0; c < maxCols; c++)
                existing.Set(r + 1, c + 1, rows[r][c]);
    }

    // ─────────────────────────── §C.16 ZAP ───────────────────────────

    /// <summary>ZAP [IN area|alias] — remove ALL records (structure + indexes kept). Requires the target
    /// table opened EXCLUSIVE (else VFP error 110 "File must be opened exclusively." — oracle-verified against
    /// vfp9.exe 2026-07-05; the earlier 1705 was the FOREIGN-holder "File access is denied" number, a
    /// different class). The DBC delete-trigger is intentionally NOT fired (VFP bug-compatible — hackfox
    /// s4g096). Wires to the fully-built <c>DbfWriter.Zap()</c>.</summary>
    private void ExecZap(ZapStmt s)
    {
        int area = s.In is not null ? ResolveAreaRef(s.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        if (!wa.Exclusive && !Session.DefaultExclusive)
            throw new MicroVfpRuntimeException("File must be opened exclusively.", 110);
        RewriteTableInPlace(area, w => w.Zap());
    }

    /// <summary>Shared close/rewrite/reopen dance for a destructive in-place table rewrite (PACK / ZAP):
    /// release cached writer + read handles so the writer can truncate, run <paramref name="op"/>, reopen,
    /// then invalidate the affected areas' cached records and reposition at top.</summary>
    private void RewriteTableInPlace(int area, Action<DbfWriter> op)
    {
        var wa = Session.AreaAt(area);
        string? path = wa?.Table.SourcePath;
        if (wa is null || path is null) return;
        path = BeginTxWrite(path);
        string full = Path.GetFullPath(path);
        SnapshotForTxn(path);
        InvalidateCachedWriter(full);
        var reopen = Session.CloseAreasForPath(full);
        try
        {
            // Exclusive open: every read handle for this path was released above, and the Core Pack/Zap
            // guard requires LockMode.Exclusive. A foreign SHARED holder makes this open throw cleanly.
            using var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Exclusive });
            op(writer);
        }
        finally { Session.ReopenAreas(reopen); }
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path) && _meta.TryGetValue(w.Area, out var mm))
            { mm.Cached = null; mm.Ordered = null; }
        GoTop(area);
    }

    // ─────────────────────────── §C.4 WAIT (headless — never blocks) ───────────────────────────

    /// <summary>WAIT — headless: never blocks; a TO target receives "" (no keypress). The message / WINDOW /
    /// TIMEOUT / NOWAIT / CLEAR clauses are accepted no-ops (analogous to the MESSAGEBOX() stub).</summary>
    private void ExecWait(WaitStmt s)
    {
        if (s.ToVar is { Length: > 0 } v) Memory.Set(v, VfpValue.Character(string.Empty));
    }

    // ─────────────────────────── §C.4 SAVE TO / RESTORE FROM ───────────────────────────
    // microVFP's OWN round-trip format (magic "MVFPMEM1"). FLAG: the proprietary VFP .mem binary is NOT
    // interop-compatible — files written by VFP cannot be RESTOREd here and vice versa; only self round-trips.

    private const string MemMagic = "MVFPMEM1";

    private void ExecSaveTo(SaveToStmt s)
    {
        string path = ResolveMemPath(NameOf(s.Target));
        var bindings = Memory.EnumerateVisible()
            .Where(b => MemFilter(b.Name, s.Like, s.Except))
            .ToList();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);
        bw.Write(MemMagic);
        bw.Write(bindings.Count);
        foreach (var b in bindings)
        {
            bw.Write(b.Name);
            if (b.Array is { } arr)
            {
                bw.Write((byte)1);
                bw.Write(arr.Rows);
                bw.Write(arr.Cols);
                if (arr.Is2D)
                    for (int r = 1; r <= arr.Rows; r++)
                        for (int c = 1; c <= arr.Cols; c++) WriteValue(bw, arr.Get(r, c));
                else
                    for (int r = 1; r <= arr.Rows; r++) WriteValue(bw, arr.Get(r, null));
            }
            else
            {
                bw.Write((byte)0);
                WriteValue(bw, b.Scalar ?? VfpValue.Null);
            }
        }
    }

    private void ExecRestoreFrom(RestoreFromStmt s)
    {
        string path = ResolveMemPath(NameOf(s.Source));
        if (!File.Exists(path)) return;
        if (!s.Additive) Memory.ClearAll();   // RESTORE without ADDITIVE = implicit CLEAR MEMORY (hackfox s4g222).

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8);
        if (br.ReadString() != MemMagic) return;          // not our format (VFP .mem interop is FLAGGED).
        int count = br.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = br.ReadString();
            byte kind = br.ReadByte();
            if (kind == 1)
            {
                int rows = br.ReadInt32(), cols = br.ReadInt32();
                var arr = new VfpArray(rows, cols);
                if (arr.Is2D)
                    for (int r = 1; r <= arr.Rows; r++)
                        for (int c = 1; c <= arr.Cols; c++) arr.Set(r, c, ReadValue(br));
                else
                    for (int r = 1; r <= arr.Rows; r++) arr.Set(r, null, ReadValue(br));
                Memory.SetArray(name, arr);
            }
            else
            {
                // Every restored var comes back PRIVATE — even over a same-named PUBLIC (hackfox s4g222), so use
                // SetPrivate (unconditional private re-bind), NOT Set (which would keep an existing cell's kind).
                Memory.SetPrivate(name, ReadValue(br));
            }
        }
    }

    private static void WriteValue(BinaryWriter bw, VfpValue v)
    {
        switch (v.Type)
        {
            case VfpType.Character: bw.Write((byte)'C'); bw.Write(v.AsString); break;
            case VfpType.Numeric: bw.Write((byte)'N'); bw.Write(v.AsNumber); break;
            case VfpType.Currency: bw.Write((byte)'Y'); bw.Write(v.AsNumber); break;
            case VfpType.Integer: bw.Write((byte)'I'); bw.Write((int)v.AsNumber); break;
            case VfpType.Logical: bw.Write((byte)'L'); bw.Write(v.AsLogical); break;
            case VfpType.Date: bw.Write((byte)'D'); bw.Write(v.AsDate.DayNumber); break;
            case VfpType.DateTime: bw.Write((byte)'T'); bw.Write(v.AsDateTime.ToBinary()); break;
            default: bw.Write((byte)'X'); break;   // .NULL. / Unknown.
        }
    }

    private static VfpValue ReadValue(BinaryReader br) => (char)br.ReadByte() switch
    {
        'C' => VfpValue.Character(br.ReadString()),
        'N' => VfpValue.Number(br.ReadDecimal()),
        'Y' => VfpValue.Currency(br.ReadDecimal()),
        'I' => VfpValue.Integer(br.ReadInt32()),
        'L' => VfpValue.Logical(br.ReadBoolean()),
        'D' => VfpValue.Date(DateOnly.FromDayNumber(br.ReadInt32())),
        'T' => VfpValue.DateTime(System.DateTime.FromBinary(br.ReadInt64())),
        _ => VfpValue.Null,
    };

    // ─────────────────────────── §C.4 LIST / DISPLAY MEMORY ───────────────────────────

    /// <summary>LIST | DISPLAY MEMORY [LIKE skel] [TO FILE cFile] — a headless text dump (Name / scope / type
    /// / value) of the visible memvars to the file (no console output without a TO FILE target). FLAG: the
    /// exact VFP column layout is not reproduced (no hackfox spec — our own readable format).</summary>
    private void ExecMemoryDump(MemoryDumpStmt s)
    {
        if (s.ToFile is null) return;   // headless: no console; only the TO FILE target produces output.
        var sb = new StringBuilder();
        foreach (var b in Memory.EnumerateVisible().Where(b => s.Like is null || MatchSkeleton(b.Name, s.Like)))
        {
            string scope = b.Kind switch { VarKind.Public => "Pub", VarKind.Local => "Loc", _ => "Priv" };
            if (b.Array is { } arr)
                sb.Append(b.Name.ToUpperInvariant().PadRight(18)).Append(scope).Append("  A  ")
                  .Append($"Array[{arr.Rows}{(arr.Is2D ? "," + arr.Cols : string.Empty)}]").AppendLine();
            else
            {
                var v = b.Scalar ?? VfpValue.Null;
                sb.Append(b.Name.ToUpperInvariant().PadRight(18)).Append(scope).Append("  ")
                  .Append(TypeChar(v)).Append("  ").Append(DumpValue(v)).AppendLine();
            }
        }
        // Memory variables are byte-per-char (Latin-1) strings in microVFP, so Latin-1 writes their bytes back.
        File.WriteAllBytes(ResolveDataPath(NameOf(s.ToFile)), Encoding.Latin1.GetBytes(sb.ToString()));
    }

    private static char TypeChar(VfpValue v) => v.Type switch
    {
        VfpType.Character => 'C', VfpType.Numeric => 'N', VfpType.Currency => 'Y', VfpType.Integer => 'N',
        VfpType.Logical => 'L', VfpType.Date => 'D', VfpType.DateTime => 'T', VfpType.Null => 'X', _ => 'U',
    };

    private static string DumpValue(VfpValue v) => v.Type switch
    {
        VfpType.Character => "\"" + v.AsString + "\"",
        VfpType.Logical => v.AsLogical ? ".T." : ".F.",
        VfpType.Numeric or VfpType.Integer or VfpType.Currency => v.AsNumber.ToString(CultureInfo.InvariantCulture),
        VfpType.Date => v.AsDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        VfpType.Null => ".NULL.",
        _ => v.AsString,
    };

    // ─────────────────────────── §C.7 APPEND / COPY PROCEDURES ───────────────────────────

    /// <summary>COPY PROCEDURES TO cFile — write the current DBC's stored-procedure SOURCE to a file.
    /// APPEND PROCEDURES FROM cFile — append that file's text to the DBC's stored-procedure source (persisted
    /// into the container's CODE memo) AND merge its procedures so they are callable immediately. FLAG: the
    /// APPEND form requires an existing StoredProceduresSource record (VFP creates one when absent — the empty
    /// -container case is not modelled here).</summary>
    private void ExecProcedures(ProceduresStmt s)
    {
        var db = _currentDbCleared ? null : Session.Database;
        if (db is null) return;

        if (!s.Append)
        {
            string outPath = ResolveDataPath(NameOf(s.File));
            // The CODE memo is NOCPTRANS and decodes byte-per-char (Latin-1); write those bytes back as stored.
            File.WriteAllBytes(outPath, Encoding.Latin1.GetBytes(db.StoredProcedureSource ?? string.Empty));
            return;
        }

        string srcPath = ResolveDataPath(NameOf(s.File));
        if (!File.Exists(srcPath)) return;
        // Byte-per-char like the .prg reader and the CODE memo: no UTF-8 decoding, no BOM detection.
        string appended = Encoding.Latin1.GetString(File.ReadAllBytes(srcPath));
        string existing = db.StoredProcedureSource ?? string.Empty;
        string combined = existing.Length == 0 || existing.EndsWith('\n')
            ? existing + appended
            : existing + "\r\n" + appended;

        Session.RewriteDatabase(dbcPath => WriteStoredProcedureSource(dbcPath, combined));

        // Make the appended procedures callable now (merge into the loaded proc table + capture #DEFINEs).
        try
        {
            var prog = PrgParser.Parse(appended);
            foreach (var p in prog.Procedures) { _procs[p.Name] = p; ScanDefines(p.Body); }
        }
        catch { /* an un-parseable append is still persisted to the container; just not merged. */ }
    }

    /// <summary>Rewrite the DBC's <c>StoredProceduresSource</c> record CODE memo (the container is not held
    /// open by the session during this call — see <see cref="VfpSession.RewriteDatabase"/>).</summary>
    private static void WriteStoredProcedureSource(string dbcPath, string source)
    {
        // The DBC's memo sidecar is the sibling .DCT (not a .fpt); point the writer at it explicitly.
        string dct = SidecarFile.ChangeExtension(dbcPath, ".dct");
        using var writer = DbfWriter.Open(dbcPath,
            new DbfOptions { LockMode = LockMode.Shared, MemoPath = File.Exists(dct) ? dct : null });
        var schema = writer.Schema;
        int nameIdx = ColumnIndex(schema, "OBJECTNAME");
        int codeIdx = ColumnIndex(schema, "CODE");
        if (nameIdx < 0 || codeIdx < 0) return;
        for (int i = 0; i < schema.RecordCount; i++)
        {
            if (schema.GetRecord(i) is not { } rec) continue;
            if (rec["OBJECTNAME"]?.ToString()?.Trim() is not { } on
                || !on.Equals("StoredProceduresSource", StringComparison.OrdinalIgnoreCase)) continue;
            var values = new object?[schema.Columns.Count];
            for (int k = 0; k < values.Length; k++) values[k] = DbfWriter.KeepValue;
            values[codeIdx] = source;
            writer.UpdateRecord(i, values);
            writer.Flush();
            return;
        }
    }

    // ─────────────────────────── §C.7 PACK DATABASE ───────────────────────────

    /// <summary>PACK DATABASE — physical delete-compaction of EVERY member table of the current DBC
    /// (each via <c>DbfWriter.Pack()</c>). Member tables open in a work area are closed + reopened around the
    /// pack. FLAG: no ObjectID re-assignment and no transaction redirect for member tables (out of the RI
    /// corpus scope).</summary>
    private void ExecPackDatabase()
    {
        var db = _currentDbCleared ? null : Session.Database;
        if (db is null) return;
        foreach (var name in db.TableNames.ToList())
        {
            string? path = db.GetTablePath(name);
            if (path is null || !File.Exists(path)) continue;
            string full = Path.GetFullPath(path);
            InvalidateCachedWriter(full);
            var reopen = Session.CloseAreasForPath(full);
            // Exclusive open (read handles released above; Core Pack requires it). A member another
            // process holds SHARED throws a sharing IOException → skipped (best-effort), others proceed.
            try { using var w = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Exclusive }); w.Pack(); }
            catch { /* a member that cannot be packed (locked/absent) is skipped, others proceed. */ }
            finally { Session.ReopenAreas(reopen); }
        }
        foreach (var w in Session.OpenAreas)
            if (_meta.TryGetValue(w.Area, out var mm)) { mm.Cached = null; mm.Ordered = null; }
        if (Session.AreaAt(Session.CurrentArea) is not null) GoTop(Session.CurrentArea);
    }

    // ─────────────────────────── §C.7 VALIDATE DATABASE ───────────────────────────

    /// <summary>VALIDATE DATABASE [NOCONSOLE] [RECOVER] — a read-only diagnostic: it resolves each member
    /// table's path (a headless "is maintenance needed?" check) and NEVER mutates the container. FLAG: RECOVER
    /// (automatic repair) is not modelled — hackfox s4g319 warns it can irreversibly drop members; a headless
    /// interpreter must not auto-repair.</summary>
    private void ExecValidateDatabase(ValidateDatabaseStmt s)
    {
        var db = _currentDbCleared ? null : Session.Database;
        if (db is null) return;
        foreach (var name in db.TableNames)
            _ = db.GetTablePath(name);   // pure existence resolution; the boolean result has no headless sink.
        // RECOVER intentionally ignored (read-only diagnostic) — see the summary FLAG.
    }

    // ─────────────────────────── §C.14 DISPLAY STRUCTURE / TABLES ───────────────────────────

    /// <summary>DISPLAY | LIST STRUCTURE [IN area] [TO FILE cFile] — a text dump of a table's field structure
    /// (number / name / type word / width / dec, matching VFP's per-field columns loosely). Headless: output
    /// only when a TO FILE target is given.</summary>
    private void ExecDisplayStructure(DisplayStructureStmt s)
    {
        if (s.ToFile is null) return;
        int area = s.In is not null ? ResolveAreaRef(s.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;

        var sb = new StringBuilder();
        sb.Append("Structure for table:    ").AppendLine((wa.Table.SourcePath ?? string.Empty).ToUpperInvariant());
        sb.Append("Number of data records: ").AppendLine(wa.Table.RecordCount.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Field  Field Name      Type                 Width    Dec");
        int n = 0;
        foreach (var col in wa.Table.Columns)
        {
            if (col.IsSystem) continue;
            n++;
            string dec = col.Decimal > 0 ? col.Decimal.ToString(CultureInfo.InvariantCulture) : string.Empty;
            sb.Append(n.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
              .Append(col.Name.ToUpperInvariant().PadRight(16))
              .Append(VfpTypeWord(col.Type).PadRight(20))
              .Append(col.Length.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
              .Append(dec.PadLeft(5)).Append("      No").AppendLine();
        }
        File.WriteAllBytes(ResolveDataPath(NameOf(s.ToFile)), wa.Table.Encoding.GetBytes(sb.ToString()));
    }

    /// <summary>DISPLAY | LIST TABLES [TO FILE cFile] — list the current DBC's member tables + resolved paths.
    /// Headless: output only when a TO FILE target is given.</summary>
    private void ExecDisplayTables(DisplayTablesStmt s)
    {
        if (s.ToFile is null) return;
        var db = _currentDbCleared ? null : Session.Database;
        if (db is null) return;
        var sb = new StringBuilder();
        sb.Append("Tables in Database ")
          .AppendLine(Path.GetFileNameWithoutExtension(Session.DatabasePath ?? string.Empty).ToUpperInvariant());
        sb.AppendLine("  Name  Source");
        foreach (var name in db.TableNames)
            sb.Append("  ").Append(name).Append("  ").AppendLine(db.GetTablePath(name) ?? string.Empty);
        File.WriteAllBytes(ResolveDataPath(NameOf(s.ToFile)), db.ContainerEncoding.GetBytes(sb.ToString()));
    }

    /// <summary>Map a DBF field-type code to VFP's DISPLAY STRUCTURE "Type" word (letters only, so the shared
    /// field-tuple regex the oracle uses matches both VFP's and our output).</summary>
    private static string VfpTypeWord(char t) => t switch
    {
        'C' => "Character", 'N' => "Numeric", 'F' => "Float", 'I' => "Integer", 'B' => "Double",
        'Y' => "Currency", 'D' => "Date", 'T' => "DateTime", 'L' => "Logical", 'M' => "Memo",
        'G' => "General", 'P' => "Picture", 'W' => "Blob", 'Q' => "Varbinary", 'V' => "Varchar",
        _ => "Character",
    };

    // ─────────────────────────── §C.1 ADIR() ───────────────────────────

    /// <summary>ADIR(ArrayName [, cFileSkeleton [, cAttributes]]) — fill ArrayName with a (nFiles × 5) matrix:
    /// [1] Name (C, uppercase), [2] Size (N), [3] Date (D, last-write), [4] Time (C "HH:MM:SS"), [5]
    /// Attributes (C, the VFP 5-position "RASHD" dotted string). Returns the match count (0 leaves the array
    /// untouched). Normal (incl. READ-ONLY) files are listed by default; HIDDEN / SYSTEM files are excluded
    /// unless cAttributes contains the matching "H" / "S" letter; directories are included when it contains
    /// "D" (all oracle-pinned vs the VFP9 runtime: default listing shows the read-only file but not the hidden/system
    /// ones — read-only is NOT a default-exclude). FLAG: the file listing is confined to the session data
    /// directory (headless sandbox); the VFP DOS-isms of a "*." directory-only skeleton and the synthesized
    /// "."/".." pseudo-entries are not reproduced.</summary>
    private int FnADir(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string arrName = a[0].AsString;
        string skel = a.Length > 1 && a[1].AsString.Trim().Length > 0 ? a[1].AsString.Trim() : "*.*";
        string attrs = a.Length > 2 ? a[2].AsString.ToUpperInvariant() : string.Empty;
        string dir = Session.DataDirectory ?? Directory.GetCurrentDirectory();
        string pattern = skel == "*.*" ? "*" : skel;
        bool inclHidden = attrs.Contains('H'), inclSystem = attrs.Contains('S');

        var entries = new List<(string Name, long Size, System.DateTime When, string Attr, bool IsDir)>();
        try
        {
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                var fi = new FileInfo(f);
                // VFP default-excludes hidden/system (but NOT read-only) unless the matching letter is passed.
                if ((fi.Attributes & FileAttributes.Hidden) != 0 && !inclHidden) continue;
                if ((fi.Attributes & FileAttributes.System) != 0 && !inclSystem) continue;
                entries.Add((fi.Name, fi.Length, fi.LastWriteTime, AttrString(fi.Attributes), false));
            }
            if (attrs.Contains('D'))
                foreach (var d in Directory.GetDirectories(dir, pattern))
                {
                    var di = new DirectoryInfo(d);
                    entries.Add((di.Name, 0, di.LastWriteTime, AttrString(di.Attributes), true));
                }
        }
        catch { return 0; }

        if (entries.Count == 0) return 0;
        entries.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));

        var arr = Memory.RedimOrCreateArray(arrName, entries.Count, 5);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            arr.Set(i + 1, 1, VfpValue.Character(e.Name.ToUpperInvariant()));
            arr.Set(i + 1, 2, VfpValue.Number((decimal)e.Size));
            arr.Set(i + 1, 3, VfpValue.Date(DateOnly.FromDateTime(e.When)));
            arr.Set(i + 1, 4, VfpValue.Character(e.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
            arr.Set(i + 1, 5, VfpValue.Character(e.Attr));
        }
        return entries.Count;
    }

    /// <summary>VFP's ADIR column-5 attribute string: a fixed 5-position "RASHD" dotted mask (position present
    /// ⇒ that letter, else "."), oracle-pinned vs the VFP9 runtime (".A..." normal, "RA..." read-only, ".AS.." system,
    /// ".A.H." hidden, "....D" directory).</summary>
    private static string AttrString(FileAttributes fa) => new(new[]
    {
        (fa & FileAttributes.ReadOnly)  != 0 ? 'R' : '.',
        (fa & FileAttributes.Archive)   != 0 ? 'A' : '.',
        (fa & FileAttributes.System)    != 0 ? 'S' : '.',
        (fa & FileAttributes.Hidden)    != 0 ? 'H' : '.',
        (fa & FileAttributes.Directory) != 0 ? 'D' : '.',
    });

    // ─────────────────────────── §C.1 AFONT (GUI-bound — headless stub / FLAG) ───────────────────────────

    /// <summary>AFONT(ArrayName [, cFontName [, nFontSize]]) — GUI/GDI-bound. A headless server interpreter has
    /// no font subsystem: with two arguments (font + size) it returns the logical <c>.F.</c> VFP uses for
    /// "combination not available", otherwise 0 (no fonts) and the array is left unpopulated. FLAG: no real
    /// font enumeration (no GDI in the target).</summary>
    private VfpValue FnAFont(VfpValue[] a)
        => a.Length >= 3 ? VfpValue.Logical(false) : VfpValue.Integer(0);

    // ─────────────────────────── shared helpers ───────────────────────────

    private string ResolveDataPath(string name)
        => Path.IsPathRooted(name) ? name
            : Path.Combine(Session.DataDirectory ?? Directory.GetCurrentDirectory(), name);

    private string ResolveMemPath(string name)
    {
        string p = ResolveDataPath(name);
        return Path.HasExtension(p) ? p : p + ".mem";
    }

    /// <summary>SAVE-list filter: a name passes when it matches the LIKE skeleton (when present) AND does NOT
    /// match the EXCEPT skeleton (when present). No filter ⇒ everything passes.</summary>
    private static bool MemFilter(string name, string? like, string? except)
    {
        if (like is not null && !MatchSkeleton(name, like)) return false;
        if (except is not null && MatchSkeleton(name, except)) return false;
        return true;
    }

    /// <summary>VFP wildcard skeleton match (<c>*</c> = any run, <c>?</c> = one char), case-insensitive.</summary>
    private static bool MatchSkeleton(string name, string skeleton)
    {
        string rx = "^" + System.Text.RegularExpressions.Regex.Escape(skeleton)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
