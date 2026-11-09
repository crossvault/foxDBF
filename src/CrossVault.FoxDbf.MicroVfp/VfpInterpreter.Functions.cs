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

/// <summary>microVFP builtin-function dispatch (HostInvoke) plus the runtime STATE + array function bodies.</summary>
public sealed partial class VfpInterpreter
{
    // ─────────────────────────── host (state) functions ───────────────────────────

    /// <summary>The expression engine's hook (<see cref="IVfpFunctionHost"/>): owns user-procedure calls
    /// + the runtime STATE functions; returns <see langword="false"/> for the engine's scalar built-ins.</summary>
    internal bool HostInvoke(string name, VfpValue[] a, EvaluationContext ctx, out VfpValue r)
    {
        // An array-element reference `arr(i)` / `arr(i,j)` arrives here as a "call" whose name IS the
        // array variable. A visible array binding takes precedence (VFP resolves the subscript, not a UDF).
        if (Memory.FindArray(name) is { } arr)
        {
            int s1 = a.Length > 0 ? (int)a[0].AsNumber : 1;
            int? s2 = a.Length > 1 ? (int)a[1].AsNumber : (int?)null;
            r = arr.Get(s1, s2);
            return true;
        }

        if (!VfpFunctionNames.IsBuiltin(name) && _procs.TryGetValue(name, out var proc))
        {
            r = CallProc(proc, a, a.Length, null);          // user function ⇒ BY VALUE.
            return true;
        }

        switch (name)
        {
            case "DTOT":
                if (a.Length != 1 || (!a[0].IsNull && a[0].Type != VfpType.Date))
                    throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
                r = default;
                return false; // Scalar conversion belongs to the shared expression/index engine.
            case "SELECT": r = FnSelect(a); return true;
            case "USED": r = VfpValue.Logical(a.Length > 0 && Session.FindAreaByAlias(a[0].AsString) is not null); return true;
            case "ALIAS": r = FnAlias(a); return true;
            case "DBF": r = VfpValue.Character(AreaArg(a, 0)?.Table.SourcePath ?? string.Empty); return true;
            case "DBC": r = VfpValue.Character(CurrentDbPath()); return true;
            case "INDBC": r = VfpValue.Logical(FnInDbc(a)); return true;
            case "ISEXCLUSIVE": r = VfpValue.Logical(AreaArg(a, 0)?.Exclusive ?? false); return true;
            case "ISREADONLY": r = VfpValue.Logical(AreaArg(a, 0)?.NoUpdate ?? false); return true;
            case "RECNO": r = VfpValue.Integer(FnRecno(a)); return true;
            case "HEADER": r = VfpValue.Integer(AreaArg(a, 0)?.Table.HeaderLength ?? 0); return true;
            case "LUPDATE": r = FnLupdate(a); return true;
            case "RECCOUNT": { var rcw = AreaArg(a, 0); r = VfpValue.Integer(rcw is null ? 0 : EffCount(rcw.Area, rcw)); return true; }
            case "EOF": r = VfpValue.Logical(MetaArg(a, 0)?.Eof ?? true); return true;
            case "BOF": r = VfpValue.Logical(MetaArg(a, 0)?.Bof ?? true); return true;
            case "FOUND": r = VfpValue.Logical(MetaArg(a, 0)?.Found ?? false); return true;
            case "DELETED": r = VfpValue.Logical(FnDeleted(a)); return true;
            case "FILTER": r = VfpValue.Character(FnFilter(a)); return true;
            case "SEEK": r = VfpValue.Logical(FnSeek(a)); return true;
            case "INDEXSEEK": r = VfpValue.Logical(FnIndexSeek(a)); return true;
            case "PCOUNT": case "PARAMETERS": r = VfpValue.Integer(CurrentCall.PassedCount); return true;
            case "PROGRAM": r = FnProgram(a); return true;
            case "TYPE": r = VfpValue.Character(TypeOf(a.Length > 0 ? a[0].AsString : string.Empty)); return true;
            // EVALUATE raises like VFP9 (a malformed expression, an unknown alias …) — unlike TYPE()/macro
            // probing, which rely on EvalText's swallow-to-.NULL.
            case "EVALUATE": case "EVAL": r = a.Length > 0 ? EvalTextLoud(a[0].AsString) : VfpValue.Null; return true;
            case "SET": r = FnSet(a); return true;
            case "RELATION": r = FnRelation(a); return true;
            case "TARGET": r = FnTarget(a); return true;
            case "SYS": r = FnSys(a); return true;
            // ── index introspection (microVFP INDEX/ORDER MODEL) ──
            case "TAGCOUNT": r = VfpValue.Integer(FnTagCount(a)); return true;
            case "TAG": r = VfpValue.Character(FnTag(a)); return true;
            case "TAGNO": r = VfpValue.Integer(FnTagNo(a)); return true;
            case "CDX": case "MDX": r = VfpValue.Character(FnCdx(a)); return true;
            case "NDX": r = VfpValue.Character(FnNdx(a)); return true;
            case "ORDER": r = VfpValue.Character(FnOrder(a)); return true;
            // VFP9 KEY() preserves string literal contents; FOR() uppercases them as well as identifiers.
            case "KEY": r = VfpValue.Character(VfpExpressionText.Normalize(ResolveSlot(a)?.KeyExpr ?? string.Empty)); return true;
            case "FOR": r = VfpValue.Character(ResolveSlot(a)?.ForExpr.ToUpperInvariant() ?? string.Empty); return true;
            case "UNIQUE": r = VfpValue.Logical(ResolveSlot(a)?.Unique ?? false); return true;
            case "DESCENDING": r = VfpValue.Logical(FnDescending(a)); return true;
            case "CANDIDATE": r = VfpValue.Logical(false); return true;   // no DBC index catalog → never a candidate.
            case "PRIMARY": r = VfpValue.Logical(false); return true;     // primary keys are a DBC-bound concept only.
            case "IDXCOLLATE": r = VfpValue.Character(ResolveSlot(a)?.Collation ?? string.Empty); return true;
            case "ATAGINFO": r = VfpValue.Integer(FnATagInfo(a)); return true;
            case "LOOKUP": r = FnLookup(a); return true;
            case "SECONDS": r = VfpValue.Number(DateTime.Now.TimeOfDay.TotalSeconds); return true;
            // Finding 5.13: RLOCK/LOCK/FLOCK take a REAL VFP-byte-compatible byte-range lock on the area's
            // live .dbf (via the Core DbfWriter surface) so a stored proc that coordinates via RLOCK is
            // mutually exclusive with a concurrent VFP client — honouring SET REPROCESS + SET MULTILOCKS.
            // ISRLOCKED/ISFLOCKED report OUR OWN held locks without taking one. See VfpInterpreter.Locking.
            case "RLOCK": case "LOCK": r = FnRlock(a); return true;
            case "FLOCK": r = FnFlock(a); return true;
            case "ISRLOCKED": r = FnIsRlocked(a); return true;
            case "ISFLOCKED": r = FnIsFlocked(a); return true;
            case "ALLT": r = VfpValue.Character((a.Length > 0 ? a[0].AsString : string.Empty).Trim(' ')); return true;
            case "OCCURS": r = VfpValue.Integer(FnOccurs(a)); return true;
            case "ATC": r = VfpValue.Integer(FnAtc(a)); return true;
            case "STRTRAN": r = VfpValue.Character(FnStrtran(a)); return true;
            case "ALEN": r = VfpValue.Integer(FnAlen(a)); return true;
            case "AERROR": r = FnAerror(a); return true;
            case "ERROR": r = VfpValue.Integer(_errNo); return true;
            case "MESSAGE": r = VfpValue.Character(a.Length > 0 && IsNumeric(a[0]) && (int)a[0].AsNumber == 1 ? string.Empty : _errMsg); return true;
            case "LINENO": r = VfpValue.Integer(_errLine); return true;
            case "ON": r = VfpValue.Character(string.Equals(a.Length > 0 ? a[0].AsString : string.Empty, "ERROR", StringComparison.OrdinalIgnoreCase) ? (Runtime.OnError ?? string.Empty) : string.Empty); return true;
            case "ISDIGIT": r = VfpValue.Logical(a.Length > 0 && a[0].AsString.Length > 0 && char.IsDigit(a[0].AsString[0])); return true;
            case "ISALPHA": r = VfpValue.Logical(a.Length > 0 && a[0].AsString.Length > 0 && char.IsLetter(a[0].AsString[0])); return true;
            case "TXNLEVEL": r = VfpValue.Integer(_txn.Count); return true;
            case "CURSORGETPROP": r = FnCursorGetProp(a); return true;
            case "CURSORSETPROP": r = FnCursorSetProp(a); return true;
            case "TABLEUPDATE": r = FnTableUpdate(a); return true;
            case "TABLEREVERT": r = FnTableRevert(a); return true;
            case "GETFLDSTATE": r = FnGetFldState(a); return true;
            case "OLDVAL": r = FnOldVal(a); return true;
            case "CURVAL": r = FnCurVal(a); return true;
            case "MESSAGEBOX": r = VfpValue.Integer(6); return true;           // IDYES (never reached in targets).
            case "COCREATEGUID": r = FnCoCreateGuid(); return true;

            // ─── P2 scalar/string batch (MICROVFP_EXTENSIONS_BACKLOG C.18) — STUBS, RED until implemented ───
            case "VARTYPE": r = FnVartype(a); return true;
            case "TRANSFORM": r = FnTransform(a); return true;
            case "PROPER": r = FnProper(a); return true;
            case "ISLOWER": r = VfpValue.Logical(FnIsLower(a)); return true;
            case "ISUPPER": r = VfpValue.Logical(FnIsUpper(a)); return true;
            case "STREXTRACT": r = VfpValue.Character(FnStrExtract(a)); return true;
            case "GETWORDCOUNT": r = VfpValue.Integer(FnGetWordCount(a)); return true;
            case "GETWORDNUM": r = VfpValue.Character(FnGetWordNum(a)); return true;
            case "SOUNDEX": r = VfpValue.Character(FnSoundex(a)); return true;
            case "DIFFERENCE": r = VfpValue.Integer(FnDifference(a)); return true;
            case "STRCONV": r = VfpValue.Character(FnStrConv(a)); return true;
            case "CPCONVERT": r = VfpValue.Character(FnCpConvert(a)); return true;
            case "CPCURRENT": r = VfpValue.Integer(FnCpCurrent(a)); return true;
            case "CPDBF": r = VfpValue.Integer(FnCpDbf(a)); return true;
            case "TEXTMERGE": r = VfpValue.Character(FnTextMerge(a)); return true;
            case "ALINES": r = VfpValue.Integer(FnAlines(a)); return true;

            // ─── P2 array batch (MICROVFP_EXTENSIONS_BACKLOG C.1/C.7) — STUBS, RED until implemented ───
            case "ACOPY": r = VfpValue.Integer(FnACopy(a)); return true;
            case "ADEL": r = VfpValue.Integer(FnADel(a)); return true;
            case "AINS": r = VfpValue.Integer(FnAIns(a)); return true;
            case "AELEMENT": r = VfpValue.Integer(FnAElement(a)); return true;
            case "ASUBSCRIPT": r = VfpValue.Integer(FnASubscript(a)); return true;
            case "AFIELDS": r = VfpValue.Integer(FnAFields(a)); return true;
            case "ASORT": r = VfpValue.Integer(FnASort(a)); return true;
            case "ADATABASES": r = VfpValue.Integer(FnADatabases(a)); return true;
            case "AUSED": r = VfpValue.Integer(FnAUsed(a)); return true;
            case "ASESSIONS": r = VfpValue.Integer(FnASessions(a)); return true;

            // Low-level file I/O (§C.2) + DBUSED (§C.5) + the P3 field/record/index batch (§C.6/8/9/11/12)
            // live in their OWN dispatch methods, NOT inline here: keeping this hot switch's method body (and
            // hence its native stack frame) at its prior size is what preserves the MaxCallDepth recursion
            // headroom — HostInvoke sits on EVERY nested UDF call.
            default: return TryInvokeFieldIdx(name, a, out r);
        }
    }

    // ─── P3 low-level file I/O (MICROVFP_EXTENSIONS_BACKLOG §C.2) + DBUSED (§C.5) — split OUT of the giant
    // HostInvoke switch on purpose (see the `default` arm above): a separate, sequentially-called method so
    // the file-I/O cases do NOT inflate HostInvoke's per-frame stack cost, which the recursion cap depends on.
    private bool TryInvokeFileIo(string name, VfpValue[] a, out VfpValue r)
    {
        switch (name)
        {
            case "FOPEN": r = FnFOpen(a); return true;
            case "FCREATE": r = FnFCreate(a); return true;
            case "FCLOSE": r = FnFClose(a); return true;
            case "FREAD": r = FnFRead(a); return true;
            case "FGETS": r = FnFGets(a); return true;
            case "FWRITE": r = FnFWrite(a); return true;
            case "FPUTS": r = FnFPuts(a); return true;
            case "FSEEK": r = FnFSeek(a); return true;
            case "FEOF": r = FnFEof(a); return true;
            case "FERROR": r = FnFError(); return true;
            case "FFLUSH": r = FnFFlush(a); return true;
            case "FCHSIZE": r = FnFChSize(a); return true;
            case "DBUSED": r = VfpValue.Logical(FnDbUsed(a)); return true;
            case "FILE": r = VfpValue.Logical(FnFile(a)); return true;
            // STRTOFILE()/FILETOSTR() — high-level one-shot file write/read (no handle subsystem). Grouped
            // with the low-level file I/O; they live here so HostInvoke's hot switch stays small (stack depth).
            case "STRTOFILE": r = FnStrToFile(a); return true;
            case "FILETOSTR": r = FnFileToStr(a); return true;
            // Chain onwards to the memo-line (MEMLINES/MLINE/ATLINE…) dispatch — same rationale: keep each
            // switch's native frame small so the MaxCallDepth recursion headroom is preserved.
            default: return TryInvokeMemoLines(name, a, out r);
        }
    }

    // ─────────────────────────── STRTOFILE / FILETOSTR (MICROVFP_EXTENSIONS_BACKLOG §C.18) ───────────────────────────

    // STRTOFILE(cString, cFileName [, nFlag | lAdditive]) → bytes written. Flags (VFP9-verified):
    //   .F./0 = overwrite (no BOM); .T./1 = ADDITIVE (append, no BOM);
    //   2 = overwrite + UTF-16LE BOM (FF FE); 4 = overwrite + UTF-8 BOM (EF BB BF).
    // The string bytes are written AS-IS (the BOM flags only PREPEND a marker — no re-encoding); the return
    // value counts the BOM. SET SAFETY prompting is not modelled (headless) — it always overwrites, matching
    // FCREATE. A numeric flag outside {0,1,2,4} raises VFP err 11 (verified against VFP9 — real VFP rejects
    // it, it does NOT silently overwrite). On an I/O failure STRTOFILE returns 0 (VFP9-verified — NOT -1).
    private VfpValue FnStrToFile(VfpValue[] a)
    {
        if (a.Length < 2)
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11); // VFP err 11.
        string path = a[1].AsString;
        byte[] data = LlEncoding.GetBytes(a[0].AsString);
        bool additive = false;
        byte[] bom = Array.Empty<byte>();
        if (a.Length > 2)
        {
            var f = a[2];
            if (f.Type == VfpType.Logical) additive = f.AsLogical;
            else
            {
                int flag = (int)f.AsNumber;
                switch (flag)
                {
                    case 0: break;                                       // overwrite, no BOM.
                    case 1: additive = true; break;                     // ADDITIVE (append).
                    case 2: bom = new byte[] { 0xFF, 0xFE }; break;      // UTF-16LE BOM.
                    case 4: bom = new byte[] { 0xEF, 0xBB, 0xBF }; break;// UTF-8 BOM.
                    default:
                        // Not a bitmask — only exact {0,1,2,4} are valid; anything else is VFP err 11.
                        throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
                }
            }
        }
        try
        {
            if (additive)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(data, 0, data.Length);
                return VfpValue.Integer(data.Length);
            }
            using var fw = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            if (bom.Length > 0) fw.Write(bom, 0, bom.Length);
            fw.Write(data, 0, data.Length);
            return VfpValue.Integer(bom.Length + data.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return VfpValue.Integer(0);   // VFP9 returns 0 on a write failure (verified) — NOT a -1 sentinel.
        }
    }

    // FILE(cFileName [, nFlags]) → does the FILE exist (VFP9-verified): leading/trailing blanks are ignored; a
    // relative name resolves against the data directory (SET DEFAULT); case does not matter (also on a
    // case-sensitive file system); "", a directory, a name without its extension and a wildcard skeleton are .F.
    // No argument raises 1229, a non-character argument (.NULL. included) raises 11. Deviation: microVFP has no
    // SET PATH, so FILE() does not search one; nFlags is accepted and ignored.
    private bool FnFile(VfpValue[] a)
    {
        if (a.Length < 1)
            throw new MicroVfpRuntimeException("Too few arguments.", 1229);
        if (a[0].Type != VfpType.Character)
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11);
        string name = a[0].AsString.Trim();
        if (name.Length == 0 || name.IndexOfAny(['*', '?']) >= 0) return false;
        if (Path.DirectorySeparatorChar != '\\') name = name.Replace('\\', Path.DirectorySeparatorChar);
        try { return SidecarFile.FindCaseInsensitive(ResolveDataPath(name)) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // FILETOSTR(cFileName) → the file's whole content as a (byte) string. A missing/unreadable file raises a
    // trappable VFP err 1 "File does not exist." (verified against VFP9 — it does NOT return an empty string).
    private VfpValue FnFileToStr(VfpValue[] a)
    {
        if (a.Length < 1)
            throw new MicroVfpRuntimeException("Function argument value, type, or count is invalid.", 11); // VFP err 11.
        try { return VfpValue.Character(LlEncoding.GetString(File.ReadAllBytes(a[0].AsString))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new MicroVfpRuntimeException("File does not exist.", 1);   // VFP err 1 (verified against VFP9).
        }
    }

    // PROGRAM([n]): no arg / n==0 ⇒ current proc name; n<0 ⇒ stack depth; n>0 ⇒ the program
    // n levels up the call stack ("" when out of range). _callStack[0] is the "(main)" frame, so the
    // meaningful depth (and the highest valid n) is _callStack.Count-1. The rierror RI loops
    // (`do while !empty(program(lnXX))`) walk this until the name is empty ⇒ they now terminate.
    private VfpValue FnProgram(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0]))
            return VfpValue.Character(CurrentCall.Name);
        int n = (int)a[0].AsNumber;
        if (n == 0) return VfpValue.Character(CurrentCall.Name);
        if (n < 0) return VfpValue.Integer(_callStack.Count - 1);
        int idx = _callStack.Count - 1 - n;
        return VfpValue.Character(idx >= 0 && idx < _callStack.Count ? _callStack[idx].Name : string.Empty);
    }

    private VfpValue FnSelect(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(Session.CurrentArea);
        var v = a[0];
        if (v.Type == VfpType.Character)
            return VfpValue.Integer(Session.FindAreaByAlias(v.AsString)?.Area ?? 0);
        int n = (int)v.AsNumber;
        return n switch
        {
            0 => VfpValue.Integer(Session.CurrentArea),
            1 => VfpValue.Integer(Session.HighestUnusedAreaNumber()),
            _ => VfpValue.Integer(Session.CurrentArea),
        };
    }

    private VfpValue FnAlias(VfpValue[] a)
    {
        VfpSession.WorkArea? wa = a.Length == 0 ? Session.AreaAt(Session.CurrentArea) : AreaArg(a, 0);
        return VfpValue.Character(wa?.Alias ?? string.Empty);
    }

    private int FnRecno(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return 0;
        var m = Meta(area);
        // A DEFAULT sees VFP's provisional negative record number for a buffered append.
        if (m.Buffering > 1 && DefaultRowFor(area) is { } pending && m.RecNo == pending.RecNo)
            return wa.Table.RecordCount - pending.RecNo;
        return m.Eof ? EffCount(area, wa) + 1 : m.RecNo;
    }

    private bool FnDeleted(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return false;
        var m = Meta(area);
        int rcTable = wa.Table.RecordCount;
        if (m.Buf is { } buf)
        {
            if (m.RecNo > rcTable)                              // buffered appended row.
            {
                int ai = m.RecNo - rcTable - 1;
                if (ai >= 0 && ai < buf.Appends.Count) return buf.Appends[ai].Deleted;
                return false;
            }
            if (m.RecNo >= 1 && m.RecNo <= rcTable && buf.Rows.TryGetValue(m.RecNo, out var e) && e.DeletedOverride is bool d)
                return d;                                       // buffered delete/recall shadows the on-disk flag.
        }
        return m.RecNo >= 1 && m.RecNo <= rcTable && wa.Table.IsRecordDeleted(m.RecNo - 1);
    }

    // FILTER([nWorkArea | cAlias]) — the VFP-normalised text of the area's SET FILTER expression, or "" when
    // no filter is set (or the area is not open). Defaults to the current work area; mirrors FnDeleted's
    // area-argument resolution. SET("FILTER") returns the SAME text for the current area (see FnSet).
    private string FnFilter(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        return _meta.TryGetValue(area, out var m) ? (m.FilterText ?? string.Empty) : string.Empty;
    }

    private bool FnSeek(VfpValue[] a)
    {
        if (a.Length == 0) return false;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        string? tag = a.Length > 2 ? a[2].AsString : null;
        return DoSeek(a[0], area, tag);
    }

    private VfpValue FnSet(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        return a[0].AsString.ToUpperInvariant() switch
        {
            "REPROCESS" => VfpValue.Character(Runtime.Reprocess == -2 ? "AUTOMATIC" : Runtime.Reprocess.ToString(CultureInfo.InvariantCulture)),
            "EXACT" => VfpValue.Character(_ctx.Exact ? "ON" : "OFF"),
            "DELETED" => VfpValue.Character(_ctx.Deleted ? "ON" : "OFF"),
            // SET("FILTER") — the current area's SET FILTER text (VFP-normalised), "" when none. Same text FILTER() reports.
            "FILTER" => VfpValue.Character(_meta.TryGetValue(Session.CurrentArea, out var fm) ? (fm.FilterText ?? string.Empty) : string.Empty),
            "ANSI" => VfpValue.Character(_ctx.Ansi ? "ON" : "OFF"),
            "COLLATE" => VfpValue.Character(_ctx.Collation?.Name ?? "MACHINE"),
            "UNIQUE" => VfpValue.Character(Runtime.Unique ? "ON" : "OFF"),
            "MULTILOCKS" => VfpValue.Character(Runtime.Multilocks ? "ON" : "OFF"),
            "NEAR" => VfpValue.Character(_setNear ? "ON" : "OFF"),
            "NULL" => VfpValue.Character(_ctx.NullSetting ? "ON" : "OFF"),
            "AUTOSAVE" => VfpValue.Character(_setAutosave ? "ON" : "OFF"),
            "MEMOWIDTH" => VfpValue.Number((decimal)_memoWidth), // NUMERIC (verified vs the VFP9 runtime: VARTYPE = "N").
            // SET("BLOCKSIZE") returns the RAW value that was set (1..32 = ×512, 33+ = bytes) — NOT the byte
            // count SYS(2012) reports (hackfox s4g089). NUMERIC. TEXTMERGE reads back ON/OFF; SET("TEXTMERGE",1)
            // is the concatenated begin+end delimiters (default "<<>>").
            "BLOCKSIZE" => VfpValue.Number((decimal)_blockSize),
            "TEXTMERGE" => a.Length > 1 && IsNumeric(a[1]) && (int)a[1].AsNumber == 1
                ? VfpValue.Character(_tmDelimBegin + _tmDelimEnd)
                : VfpValue.Character(_textMerge ? "ON" : "OFF"),
            "RELATION" => VfpValue.Character(RelationSetString(Session.CurrentArea)),  // reproduces the SET RELATION args.
            "SKIP" => VfpValue.Character(SkipSetString(Session.CurrentArea)),          // comma-list of 1:n aliases.
            "DATASESSION" => VfpValue.Number((decimal)Session.CurrentDataSessionId),   // NUMERIC (verified vs the VFP9 runtime) — the current data-session id (5.14).
            // SET("DATABASE") — the current DBC's NAME only (no drive/path/extension), "" when none current.
            "DATABASE" => VfpValue.Character(CurrentDbPath() is { Length: > 0 } p ? Path.GetFileNameWithoutExtension(p) : string.Empty),
            // SET("FIELDS") → ON/OFF; SET("FIELDS",1) → the bare field list, ", "-separated, uppercase (no
            // alias) — matches FLDLIST()'s list but without the alias qualifier — EXCEPT after SET FIELDS TO
            // ALL, when it is the literal "ALL" while FLDLIST() still enumerates every field (oracle-pinned
            // vs the VFP9 runtime, incl. that "ALL" survives SET FIELDS ON/OFF; hackfox s4g091).
            "FIELDS" => a.Length > 1 && IsNumeric(a[1]) && (int)a[1].AsNumber == 1
                ? VfpValue.Character(_setFieldsAll ? "ALL" : string.Join(", ", _setFields.Select(f => f.ToUpperInvariant())))
                : VfpValue.Character(_setFieldsOn ? "ON" : "OFF"),
            "TALK" => VfpValue.Character("OFF"),
            "COMPATIBLE" => VfpValue.Character("OFF"),
            _ => VfpValue.Character(string.Empty),
        };
    }

    // INDBC(cObjectName, cObjectType) — is a named object present in the CURRENT database container?
    // TABLE: matched against the DBC's long table names. FIELD: cObjectName MUST be alias.field (an
    // unqualified name returns .F. even when the field exists — hackfox s4g436 quirk); matched against
    // that table's long field names. INDEX/VIEW/CONNECTION are not modelled by microVFP's DBC reader → .F.
    private bool FnInDbc(VfpValue[] a)
    {
        if (a.Length < 2) return false;
        var db = _currentDbCleared ? null : Session.Database;
        if (db is null) return false;

        string name = a[0].AsString.Trim();
        string type = a[1].AsString.Trim().ToUpperInvariant();

        switch (type)
        {
            case "TABLE":
                foreach (var t in db.TableNames)
                    if (string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;

            case "FIELD":
            {
                int dot = name.IndexOf('.');
                if (dot <= 0 || dot >= name.Length - 1) return false;   // must be alias.field.
                string table = name[..dot], field = name[(dot + 1)..];
                bool known = false;
                foreach (var t in db.TableNames)
                    if (string.Equals(t, table, StringComparison.OrdinalIgnoreCase)) { table = t; known = true; break; }
                if (!known) return false;
                try
                {
                    foreach (var f in db.GetTableRules(table).Fields)
                        if (string.Equals(f.FieldName, field, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return false; }
                return false;
            }

            // INDEX (no DBC tag model), VIEW / CONNECTION (not parsed) — flagged as unsupported → .F.
            default:
                return false;
        }
    }

    private VfpValue FnSys(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 0;
        switch (n)
        {
            // SYS(0) = "<machine> # <station/user>" (network-dependent — see MICROVFP_SEMANTICS.md Nachtrag:
            // "netzabhängig, unzuverlässig"). VERIFIED against the VFP9 runtime on this box: it returns
            // "<MachineName> # <UserName>" (the part after "#" is the logged-on network user, NOT a numeric
            // station id). Matching that exactly is what lets createId's SYS(2007) workstation/user checksums
            // equal VFP9's. Stable + non-crashing: both halves come from the OS identity.
            case 0: return VfpValue.Character($"{Environment.MachineName} # {Environment.UserName}");
            case 1: return VfpValue.Character(JulianDay(DateTime.Today).ToString(CultureInfo.InvariantCulture));
            // SYS(10, nJulianDay) — Julian-day number → date string (inverse of SYS(11)). STUB, RED until implemented.
            case 10: return FnSys10(a);
            // SYS(15, cTransTable, cExpr) — character translation. Each char c of cExpr is replaced by the byte
            // at 1-based position ASC(c) of cTransTable; a code outside [1, LEN(table)] keeps the char verbatim
            // (verified vs the VFP9 runtime: identity table maps 'A'→'@', i.e. CHR(ASC(c)-1); table is arg2, expr arg3).
            case 15:
            {
                if (a.Length < 3) return VfpValue.Character(string.Empty);
                string table = a[1].AsString, src = a[2].AsString;
                var sb = new StringBuilder(src.Length);
                foreach (char c in src)
                {
                    int code = c;                                   // ASC (byte value on the Latin1-backed string)
                    sb.Append(code >= 1 && code <= table.Length ? table[code - 1] : c);
                }
                return VfpValue.Character(sb.ToString());
            }
            // SYS(14, nIndexNumber [, area]) — the KEY expression of the nth open index, ALL-CAPS (s4g266).
            // Unlike KEY(), SYS(14) REQUIRES the index number; an out-of-range number yields "" (no error).
            case 14:
            {
                if (a.Length < 2) return VfpValue.Character(string.Empty);
                var slot = ResolveSlot(a.Skip(1).ToArray());
                return VfpValue.Character(slot?.KeyExpr.ToUpperInvariant() ?? string.Empty);
            }
            // SYS(21 [, area]) — the NUMBER of the controlling index (legacy TAGNO of the master), "0" when
            // natural order. SYS(22 [, area]) — its NAME (legacy ORDER()), "" when natural. Both return a
            // CHARACTER string (verified vs the VFP9 runtime). hackfox rates them obsolete vs TAGNO()/ORDER().
            case 21: return VfpValue.Character(FnTagNo(a.Skip(1).ToArray()).ToString(CultureInfo.InvariantCulture));
            case 22: return VfpValue.Character(a.Length > 1 ? FnOrder(a.Skip(1).ToArray()) : FnOrder(Array.Empty<VfpValue>()));
            // SYS(2021, nIndexNumber [, area]) — the FOR filter of the n-th open index, ALL-CAPS (like FOR();
            // the number is REQUIRED). FLAG: VFP preserves the case of QUOTED strings inside the FOR here
            // (s4g266) — microVFP uppercases the whole expression (no re-tokenizer), matching non-quoted FORs.
            case 2021: return VfpValue.Character(a.Length < 2 ? string.Empty : (ResolveSlot(a.Skip(1).ToArray())?.ForExpr.ToUpperInvariant() ?? string.Empty));
            case 2007: return VfpValue.Character(Crc16Ccitt(a.Length > 1 ? a[1].AsString : string.Empty).ToString(CultureInfo.InvariantCulture));
            case 2015: return VfpValue.Character("_" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant());
            // SYS(2029 [, area]) — the DBF header's first (version/type) byte as a decimal string; "0" when
            // no table is open in the addressed area. A VFP table is "48" (0x30) — oracle-pinned vs the VFP9 runtime.
            case 2029:
            {
                var wa = AreaArg(a.Skip(1).ToArray(), 0);
                return VfpValue.Character(((int)(wa?.Table.Version.Code ?? 0)).ToString(CultureInfo.InvariantCulture));
            }
            default: return VfpValue.Character(string.Empty);
        }
    }

    /// <summary>LOOKUP(rReturn, eSearch, rSearched [, cTag]) — seek <paramref name="a"/>[1] (via the tag
    /// when given, else a sequential scan on rSearched) and return the rReturn field value at the hit; a
    /// miss parks the pointer (per SET NEAR) / at EOF and returns a blank. The field-name arguments arrive
    /// as quoted names (MicroVfpExprRewrite).</summary>
    private VfpValue FnLookup(VfpValue[] a)
    {
        if (a.Length < 3) return VfpValue.Logical(false);
        string returnField = a[0].AsString;
        VfpValue searchVal = a[1];
        string searchField = a[2].AsString;
        string? tag = a.Length > 3 ? a[3].AsString : null;
        int area = Session.CurrentArea;

        bool found = !string.IsNullOrEmpty(tag)
            ? DoSeek(searchVal, area, tag)
            : LookupSequential(area, searchField, searchVal);

        if (found) return EvalText(returnField);
        return VfpValue.Character(string.Empty);   // miss ⇒ blank (FOUND() stays .F.).
    }

    /// <summary>Sequential LOCATE for <c>field = value</c> from the top of the work area; leaves the
    /// pointer on the first match (or EOF).</summary>
    private bool LookupSequential(int area, string field, VfpValue value)
    {
        GoTop(area);
        while (!Meta(area).Eof)
        {
            var cur = EvalText(field);
            if (ValuesLooseEqual(cur, value)) return true;
            Skip(area, 1);
        }
        return false;
    }

    private static bool ValuesLooseEqual(VfpValue x, VfpValue y)
    {
        if (x.Type == VfpType.Character || y.Type == VfpType.Character)
            return string.Equals(x.AsString?.TrimEnd(), y.AsString?.TrimEnd(), StringComparison.Ordinal);
        return x.AsNumber == y.AsNumber;
    }

    // ALEN(arr[,n]) — total elements (no n / n<=0), rows (n=1) or columns (n=2; 0 for 1-D). The array name
    // arrives as a string (MicroVfpExprRewrite quotes the first arg); an unknown array returns 0.
    private int FnAlen(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int dim = a.Length > 1 && IsNumeric(a[1]) ? (int)a[1].AsNumber : 0;
        return arr.ALen(dim);
    }

    // AERROR(arr) — the full 7-column contract (MICROVFP_SEMANTICS.md Nachtrag). (Re)dimensions the named
    // array to (rows,7), fills it from the RETAINED last error (so it works both inside the ON ERROR
    // handler AND after it returns, when the live ERROR()/MESSAGE() are cleared), and returns the row
    // count. No error yet ⇒ returns 0 and leaves the array untouched. The array name arrives as a string
    // (MicroVfpExprRewrite quotes the first arg). ODBC/OLE multi-row layouts are out of the RI corpus.
    private VfpValue FnAerror(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(0);
        string name = a[0].AsString;
        if (string.IsNullOrEmpty(name) || Runtime.LastErrorNumber == 0) return VfpValue.Integer(0);

        var arr = Memory.RedimOrCreateArray(name, 1, 7);
        arr.Set(1, 1, VfpValue.Integer(Runtime.LastErrorNumber));                                   // [1] ERROR()
        arr.Set(1, 2, VfpValue.Character(Runtime.LastErrorMessage ?? string.Empty));                 // [2] MESSAGE()
        arr.Set(1, 3, Runtime.LastErrorDetail is { } d ? VfpValue.Character(d) : VfpValue.Null);     // [3] detail/SYS(2018)
        arr.Set(1, 4, Runtime.LastErrorArea > 0 ? VfpValue.Integer(Runtime.LastErrorArea) : VfpValue.Null); // [4] work area
        // [5] = .NULL., EXCEPT a trigger failure (1539 → 1 insert/2 update/3 delete) or a field-rule error
        // (→ the violating field number).
        arr.Set(1, 5, Runtime.LastErrorTrigger != 0 ? VfpValue.Integer(Runtime.LastErrorTrigger)
                    : Runtime.LastErrorField != 0 ? VfpValue.Integer(Runtime.LastErrorField)
                    : VfpValue.Null);
        arr.Set(1, 6, VfpValue.Null);                                                                // [6] .NULL.
        arr.Set(1, 7, VfpValue.Null);                                                                // [7] .NULL.
        return VfpValue.Integer(1);
    }

    // ─────────────────────────── P2 array batch (MICROVFP_EXTENSIONS_BACKLOG C.1/C.7) ───────────────────────────
    // Authoritative semantics: backlog C.1/C.7 + hackfox s4g210/211/213/292/666, verified byte-for-byte
    // against the VFP9 runtime. VFP arrays are 1-BASED, ROW-MAJOR; a "2-D" array is rows×cols with a
    // parallel LINEAR (row-major) element view. Array NAMES arrive as strings (MicroVfpExprRewrite quotes
    // the reference argument(s)); an unknown array is a silent 0 (except ASUBSCRIPT, which errors — s4g213).

    /// <summary>ACOPY(aSource, aDest [, nStart [, nCount [, nDestStart]]]) — LINEAR (row-major) copy. When
    /// aDest does not yet exist it is created MATCHING aSource's dimensions (hackfox s4g210 quirk), even for
    /// a partial-range copy. Returns the number of elements copied.</summary>
    private int FnACopy(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var src = Memory.FindArray(a[0].AsString);
        if (src is null) return 0;
        int srcLen = src.Length;

        int start = a.Length > 2 && IsNumeric(a[2]) ? (int)a[2].AsNumber : 1;
        if (start < 1) start = 1;
        int count = a.Length > 3 && IsNumeric(a[3]) ? (int)a[3].AsNumber : srcLen - start + 1;
        if (count < 0) count = srcLen - start + 1;
        int destStart = a.Length > 4 && IsNumeric(a[4]) ? (int)a[4].AsNumber : 1;
        if (destStart < 1) destStart = 1;

        string destName = a[1].AsString;
        var dest = Memory.FindArray(destName);
        bool destPreexisted = dest is not null;
        // The auto-create-matching-source-dims quirk (s4g210) applies ONLY when aDest does not yet exist.
        dest ??= Memory.RedimOrCreateArray(destName, src.Rows, src.Cols);

        int copied = 0;
        for (int k = 0; k < count; k++)
        {
            int sIdx = start + k, dIdx = destStart + k;
            if (sIdx < 1 || sIdx > srcLen) break;
            if (dIdx < 1 || dIdx > dest.Length)
            {
                // A PRE-EXISTING dest is never grown: VFP9 raises (verified live) rather than clipping.
                if (destPreexisted) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
                break;
            }
            dest.SetLinear(dIdx, src.GetLinear(sIdx));
            copied++;
        }
        return copied;
    }

    /// <summary>ADEL(ArrayName, nElement [, nRowOrColumn]) — delete an element/row (default) or a column
    /// (3rd arg &gt; 1); size UNCHANGED, the freed tail slot(s) <c>.F.</c>-filled (hackfox s4g211). Returns 1.
    /// An out-of-range or non-positive index is a runtime ERROR in VFP9 (verified live), not a silent no-op.</summary>
    private int FnADel(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int idx = (int)a[1].AsNumber;
        if (IsColumnMode(arr, a))
        {
            if (idx < 1 || idx > arr.Cols) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.DeleteColumn(idx);
        }
        else
        {
            if (idx < 1 || idx > arr.Rows) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.DeleteRow(idx);
        }
        return 1;                                            // s4g211: the error-return VALUE is unreliable; on success ⇒ 1.
    }

    /// <summary>AINS(ArrayName, nElement [, nRowOrColumn]) — insert a blank (<c>.F.</c>) element/row (default)
    /// or column (3rd arg &gt; 1); size UNCHANGED, the original last element/row/column is LOST (hackfox
    /// s4g211 — kept 1:1, not "protected against"). Returns 1.</summary>
    private int FnAIns(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int idx = (int)a[1].AsNumber;
        if (IsColumnMode(arr, a))
        {
            if (idx < 1 || idx > arr.Cols) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.InsertColumn(idx);
        }
        else
        {
            if (idx < 1 || idx > arr.Rows) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.InsertRow(idx);
        }
        return 1;
    }

    // ADEL/AINS 3rd parameter: column mode only for a 2-D array and a value > 1 (s4g211: "a number less
    // than or equal to 1 is identical to omitting the parameter"; 2 is the only documented column trigger).
    private static bool IsColumnMode(VfpArray arr, VfpValue[] a)
        => arr.Is2D && a.Length > 2 && IsNumeric(a[2]) && (int)a[2].AsNumber > 1;

    /// <summary>AELEMENT(ArrayName, nRow [, nCol]) — the LINEAR (row-major) element number for the given
    /// subscripts. With BOTH subscripts on a 2-D array it is (nRow-1)*Cols+nCol. With a SINGLE subscript on
    /// a 2-D array VFP treats it as an index validated against Rows and returns the subscript itself (an
    /// out-of-range value is a runtime ERROR, not 0). For a 1-D array an out-of-range subscript ⇒ 0
    /// (hackfox s4g213: no error).</summary>
    private int FnAElement(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int r = (int)a[1].AsNumber;
        if (arr.Is2D)
        {
            if (a.Length > 2 && IsNumeric(a[2]))
            {
                int c = (int)a[2].AsNumber;
                if (r < 1 || r > arr.Rows || c < 1 || c > arr.Cols) return 0;
                return (r - 1) * arr.Cols + c;
            }
            // Single subscript on a 2-D array: validated against Rows, returns the subscript itself.
            if (r < 1 || r > arr.Rows)
                throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            return r;
        }
        return r >= 1 && r <= arr.Length ? r : 0;
    }

    /// <summary>ASUBSCRIPT(ArrayName, nElement, nSubscript) — the row (nSubscript=1) or column (2) subscript
    /// for a LINEAR element number. Unlike AELEMENT, an out-of-range element (or a column subscript on a 1-D
    /// array) is a REAL runtime ERROR, NOT a 0 fallback (hackfox s4g213 — deliberately reproduced).</summary>
    private int FnASubscript(VfpValue[] a)
    {
        if (a.Length < 3) throw new MicroVfpRuntimeException("ASUBSCRIPT() requires three arguments.");
        var arr = Memory.FindArray(a[0].AsString)
                  ?? throw new MicroVfpRuntimeException("ASUBSCRIPT(): the variable is not an array.");
        int n = (int)a[1].AsNumber;
        int sub = (int)a[2].AsNumber;
        if (n < 1 || n > arr.Length)
            throw new MicroVfpRuntimeException("ASUBSCRIPT(): element number is out of range.");
        if (arr.Is2D)
            return sub switch
            {
                1 => (n - 1) / arr.Cols + 1,
                2 => (n - 1) % arr.Cols + 1,
                _ => throw new MicroVfpRuntimeException("ASUBSCRIPT(): invalid subscript selector."),
            };
        if (sub == 1) return n;
        throw new MicroVfpRuntimeException("ASUBSCRIPT(): a 1-D array has no column subscript.");
    }

    /// <summary>AFIELDS(ArrayName [, cAlias | nWorkArea]) — (re)dimension ArrayName to (nFields × 18) and
    /// fill one row per field: [1] name, [2] type, [3] length, [4] decimals, [5] nullable, [6] NOCPTRANS
    /// (binary), [7]-[18] the DBC-property columns (blank for a free table). The hidden <c>_NullFlags</c>
    /// system field is NOT reported (hackfox s4g292). Returns the field count.</summary>
    private int FnAFields(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        VfpSession.WorkArea? wa;
        if (a.Length > 1)
        {
            var v = a[1];
            wa = v.Type == VfpType.Character
                ? Session.FindAreaByAlias(v.AsString)
                : Session.AreaAt((int)v.AsNumber);
        }
        else wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return 0;

        var fields = new List<DbfColumn>();
        foreach (var c in wa.Table.Columns) if (!c.IsSystem) fields.Add(c);
        int n = fields.Count;
        if (n == 0) return 0;

        var arr = Memory.RedimOrCreateArray(a[0].AsString, n, 18);
        for (int i = 0; i < n; i++)
        {
            var c = fields[i];
            arr.Set(i + 1, 1, VfpValue.Character(c.Name));
            arr.Set(i + 1, 2, VfpValue.Character(c.Type.ToString()));
            arr.Set(i + 1, 3, VfpValue.Integer(c.Length));
            arr.Set(i + 1, 4, VfpValue.Integer(c.Decimal));
            arr.Set(i + 1, 5, VfpValue.Logical(c.IsNullable));
            arr.Set(i + 1, 6, VfpValue.Logical(c.IsBinary));
            for (int col = 7; col <= 18; col++) arr.Set(i + 1, col, VfpValue.Character(string.Empty));
        }
        return n;
    }

    /// <summary>ASORT(ArrayName [, nStart [, nCount [, nSortOrder [, nFlags]]]]) — in-place sort. For a 2-D
    /// array nStart identifies both the starting row AND the KEY COLUMN (its column), whole rows move with
    /// the key. nSortOrder any NONZERO value ⇒ descending (0/omitted ⇒ ascending). nFlags bit 1 ⇒
    /// case-insensitive C compare. All
    /// sorted elements must share a data type. Returns 1.</summary>
    private int FnASort(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;

        int startEl = a.Length > 1 && IsNumeric(a[1]) ? (int)a[1].AsNumber : 1;
        if (startEl < 1) startEl = 1;
        int numSorted = a.Length > 2 && IsNumeric(a[2]) ? (int)a[2].AsNumber : -1;
        bool desc = a.Length > 3 && IsNumeric(a[3]) && (int)a[3].AsNumber != 0; // VFP: ANY nonzero ⇒ descending.
        bool ci = a.Length > 4 && IsNumeric(a[4]) && ((int)a[4].AsNumber & 1) != 0;

        if (arr.Is2D)
        {
            int cols = arr.Cols;
            int keyCol = (startEl - 1) % cols + 1;
            int startRow = (startEl - 1) / cols + 1;
            int rowCount = numSorted < 0 ? arr.Rows - startRow + 1 : numSorted;
            SortRowRange(arr, startRow, rowCount, keyCol, desc, ci);
        }
        else
        {
            int count = numSorted < 0 ? arr.Length - startEl + 1 : numSorted;
            SortElementRange(arr, startEl, count, desc, ci);
        }
        return 1;
    }

    private void SortElementRange(VfpArray arr, int start, int count, bool desc, bool ci)
    {
        if (count <= 1) return;
        var items = new VfpValue[count];
        for (int i = 0; i < count; i++) items[i] = arr.GetLinear(start + i);
        Array.Sort(items, (x, y) => (desc ? -1 : 1) * SortCompare(x, y, ci));
        for (int i = 0; i < count; i++) arr.SetLinear(start + i, items[i]);
    }

    private void SortRowRange(VfpArray arr, int startRow, int rowCount, int keyCol, bool desc, bool ci)
    {
        if (rowCount <= 1) return;
        int cols = arr.Cols;
        var rows = new VfpValue[rowCount][];
        for (int r = 0; r < rowCount; r++)
        {
            rows[r] = new VfpValue[cols];
            for (int c = 1; c <= cols; c++) rows[r][c - 1] = arr.Get(startRow + r, c);
        }
        Array.Sort(rows, (x, y) => (desc ? -1 : 1) * SortCompare(x[keyCol - 1], y[keyCol - 1], ci));
        for (int r = 0; r < rowCount; r++)
            for (int c = 1; c <= cols; c++) arr.Set(startRow + r, c, rows[r][c - 1]);
    }

    // Type-homogeneous compare using the session collation (reused from the expression engine's decision:
    // MACHINE/GENERAL). Mixed data types are a VFP error 9/11 (backlog C.1) — reproduced as a runtime error.
    private int SortCompare(VfpValue x, VfpValue y, bool ci)
    {
        int cx = SortClass(x), cy = SortClass(y);
        if (cx != cy)
            throw new MicroVfpRuntimeException("ASORT(): array elements are not the same data type.", 9); // VFP err 9 "Data type mismatch." (oracle-pinned).
        return cx switch
        {
            0 => ci
                ? _ctx.Collation.Compare(x.AsString.ToUpperInvariant().AsSpan(), y.AsString.ToUpperInvariant().AsSpan())
                : _ctx.Collation.Compare(x.AsString.AsSpan(), y.AsString.AsSpan()),
            1 => x.AsNumber.CompareTo(y.AsNumber),
            2 => x.AsDateTime.CompareTo(y.AsDateTime),
            3 => x.AsLogical.CompareTo(y.AsLogical),
            _ => 0,
        };
    }

    private static int SortClass(VfpValue v) => v.Type switch
    {
        VfpType.Character => 0,
        VfpType.Numeric or VfpType.Integer or VfpType.Currency => 1,
        VfpType.Date or VfpType.DateTime => 2,
        VfpType.Logical => 3,
        _ => 4,
    };

    /// <summary>ADATABASES(ArrayName) — fill (1 × 2) [name, full DBC path] for the open database container,
    /// or return 0 when none is open. microVFP has a single open DBC (backlog C.7). hackfox s4g666: column 2
    /// is always the full path regardless of SET FULLPATH.</summary>
    private int FnADatabases(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string? dbc = Session.DatabasePath;
        if (Session.Database is null || string.IsNullOrEmpty(dbc)) return 0;
        var arr = Memory.RedimOrCreateArray(a[0].AsString, 1, 2);
        arr.Set(1, 1, VfpValue.Character(Path.GetFileNameWithoutExtension(dbc)));
        arr.Set(1, 2, VfpValue.Character(dbc));
        return 1;
    }

    /// <summary>AUSED(ArrayName [, nDataSessionId]) — fill (nAreas × 2) [alias, work-area number] for every
    /// open work area (ordered by area number) of the given data session (5.14; default = the current
    /// session), or return 0 when that session has none open / does not exist.</summary>
    private int FnAUsed(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        int sid = a.Length >= 2 && IsNumeric(a[1]) ? (int)a[1].AsNumber : Session.CurrentDataSessionId;
        var areas = new List<VfpSession.WorkArea>(Session.OpenAreasOf(sid));
        if (areas.Count == 0) return 0;
        areas.Sort((x, y) => x.Area.CompareTo(y.Area));
        var arr = Memory.RedimOrCreateArray(a[0].AsString, areas.Count, 2);
        for (int i = 0; i < areas.Count; i++)
        {
            arr.Set(i + 1, 1, VfpValue.Character(areas[i].Alias));
            arr.Set(i + 1, 2, VfpValue.Integer(areas[i].Area));
        }
        return areas.Count;
    }

    /// <summary>ASESSIONS(ArrayName) — fill a 1-D array with the existing data-session ids, ascending (5.14).
    /// Always at least [1] (the default public session); grows as CreateDataSession adds private sessions.</summary>
    private int FnASessions(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var ids = Session.DataSessionIds;   // ascending; always includes 1.
        var arr = Memory.RedimOrCreateArray(a[0].AsString, ids.Count, 0);
        for (int i = 0; i < ids.Count; i++) arr.SetLinear(i + 1, VfpValue.Integer(ids[i]));
        return ids.Count;
    }

    private VfpValue FnCoCreateGuid()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        Memory.Set("lcBuffer", VfpValue.Character(Encoding.Latin1.GetString(bytes)));
        return VfpValue.Integer(0);
    }

}
