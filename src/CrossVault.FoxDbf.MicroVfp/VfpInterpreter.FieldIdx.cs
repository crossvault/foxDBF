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

/// <summary>microVFP P3 batch 3 — field/record/index manipulation + introspection: ISBLANK/BLANK,
/// FLDLIST, KEYMATCH, GETNEXTMODIFIED, SETFLDSTATE, APPEND MEMO, REPLACE FROM ARRAY, COPY INDEXES /
/// COPY TAG, CLOSE INDEXES, SET BLOCKSIZE / SET TEXTMERGE [DELIMITERS]. All behaviours pinned to the VFP9 runtime.</summary>
public sealed partial class VfpInterpreter
{
    // ── session state (P3 batch 3) ──
    private int _blockSize = 64;                 // SET BLOCKSIZE — RAW value (1..32 = ×512, 33+ = bytes). Default 64.
    private bool _textMerge;                     // SET TEXTMERGE ON|OFF.
    private string _tmDelimBegin = "<<";         // SET TEXTMERGE DELIMITERS TO — default << >>.
    private string _tmDelimEnd = ">>";

    /// <summary>The .fpt block size in BYTES a subsequently-created memo file gets, derived from the raw
    /// SET BLOCKSIZE value: 0 ⇒ 1-byte blocks, 1..32 ⇒ multiples of 512, 33+ ⇒ explicit bytes (hackfox s4g089).</summary>
    private int MemoBlockBytes() => _blockSize <= 0 ? 1 : (_blockSize <= 32 ? _blockSize * 512 : _blockSize);

    /// <summary>The P3 field/record/index FUNCTION dispatch — chained from HostInvoke's <c>default</c> arm so
    /// these cases live OUT of the giant hot switch, keeping its native stack frame small (the MaxCallDepth
    /// recursion headroom depends on it — see HostInvoke's note). Falls through to the file-I/O dispatch.</summary>
    private bool TryInvokeFieldIdx(string name, VfpValue[] a, out VfpValue r)
    {
        switch (name)
        {
            case "ISBLANK": r = VfpValue.Logical(FnIsBlank(a)); return true;
            case "FLDLIST": r = VfpValue.Character(FnFldList(a)); return true;
            case "ADIR": r = VfpValue.Integer(FnADir(a)); return true;
            case "AFONT": r = FnAFont(a); return true;
            case "KEYMATCH": r = VfpValue.Logical(FnKeyMatch(a)); return true;
            case "GETNEXTMODIFIED": r = VfpValue.Integer(FnGetNextModified(a)); return true;
            case "SETFLDSTATE": r = VfpValue.Logical(FnSetFldState(a)); return true; // s4g395: validated but inert for real tables.
            default: return TryInvokeFileIo(name, a, out r);
        }
    }

    // ─────────────────────────── SET BLOCKSIZE / SET TEXTMERGE ───────────────────────────

    private void SetBlockSize(string arg)
    {
        var parts = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && parts[0].Equals("TO", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (idx >= parts.Length) return;
        var v = EvalText(parts[idx]);
        int n = IsNumeric(v) ? (int)v.AsNumber : int.TryParse(v.AsString, out var p) ? p : _blockSize;
        if (n >= 0) _blockSize = n;
    }

    /// <summary>SET TEXTMERGE ON|OFF | DELIMITERS TO [cBegin [, cEnd]] | TO … . The output-redirection
    /// (<c>TO file|memvar</c>) form is a recognised no-op (no TEXT…ENDTEXT model). DELIMITERS with no
    /// argument resets to <c>&lt;&lt;</c>/<c>&gt;&gt;</c>; the delimiters feed TEXTMERGE()'s defaults.</summary>
    private void SetTextMerge(string arg)
    {
        string w = PrgScan.FirstWord(arg);
        if (w.Equals("ON", StringComparison.OrdinalIgnoreCase)) { _textMerge = true; return; }
        if (w.Equals("OFF", StringComparison.OrdinalIgnoreCase)) { _textMerge = false; return; }
        if (w.Equals("DELIMITERS", StringComparison.OrdinalIgnoreCase))
        {
            string rest = PrgScan.AfterFirstWord(arg).Trim();
            if (rest.StartsWith("TO", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(2).Trim();
            if (rest.Length == 0) { _tmDelimBegin = "<<"; _tmDelimEnd = ">>"; return; }
            var pieces = PrgScan.SplitTopCommas(rest).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (pieces.Count >= 1) _tmDelimBegin = EvalText(pieces[0]).AsString;
            _tmDelimEnd = pieces.Count >= 2 ? EvalText(pieces[1]).AsString : _tmDelimBegin;
            return;
        }
        // SET TEXTMERGE TO [file|memvar] [ADDITIVE] [NOSHOW] — output redirection, not modelled. FLAG.
    }

    // ─────────────────────────── ISBLANK() ───────────────────────────

    /// <summary>ISBLANK(eExpression) — .T. when the value is BLANK. For a bare FIELD reference (rewritten to
    /// <c>ISBLANK('name', .T.)</c> by <see cref="MicroVfpExprRewrite"/>) the blank state is a RAW-byte
    /// property (a blanked numeric reads 0 but is byte-distinct from a written 0); a non-field expression is
    /// judged by VALUE. Verified vs the VFP9 runtime: C/D blank iff empty; N/L blank iff never assigned; integer
    /// (and other binary types) NEVER blank; numeric 0 / logical .F. NOT blank.</summary>
    private bool FnIsBlank(VfpValue[] a)
    {
        if (a.Length == 0) return false;

        // Field-reference marker (a[1] == .T.): resolve name → (area, column) and inspect the record bytes;
        // fall back to value-based blankness when the name is not actually a field (a memvar).
        if (a.Length >= 2 && a[1].Type == VfpType.Logical && a[1].AsLogical)
        {
            string name = a[0].AsString;
            int area = Session.CurrentArea;
            string field = name;
            int dot = name.IndexOf('.');
            if (dot > 0 && Session.FindAreaByAlias(name[..dot]) is { } aliasWa) { area = aliasWa.Area; field = name[(dot + 1)..]; }
            var wa = Session.AreaAt(area);
            if (wa is not null && ColumnIndex(wa.Table, field) is int ci && ci >= 0)
                return IsFieldBlank(wa, area, ci);
            return IsBlankValue(EvalText(name));   // not a column ⇒ a memvar/expression ⇒ value-based.
        }
        return IsBlankValue(a[0]);
    }

    /// <summary>The BLANK state of a FIELD's current-record bytes: C/V/M/G blank iff the decoded content is
    /// empty/all-whitespace; N/F/D/L blank iff every raw byte is a space (the never-assigned / BLANKed state);
    /// T (datetime) blank iff every raw byte is ZERO — the {} / never-assigned sentinel a real datetime can
    /// never share (its Julian day is always large + non-zero), verified vs the VFP9 runtime; the remaining binary
    /// types (I/Y/B/…) are NEVER blank (a written 0 and a blank share all-zero bytes — oracle-pinned for I).</summary>
    private bool IsFieldBlank(VfpSession.WorkArea wa, int area, int colIdx)
    {
        var col = wa.Table.Columns[colIdx];
        switch (col.Type)
        {
            case 'C': case 'V': case 'M': case 'G':
                return string.IsNullOrWhiteSpace(ReadField(wa, col.Name)?.ToString());
            case 'N': case 'F': case 'D': case 'L': case 'T': case '@':
            {
                byte pad = col.Type is 'T' or '@' ? (byte)0x00 : (byte)0x20;   // datetime blanks all-zero; C/N/D/L all-space.
                var m = Meta(area);
                if (m.RecNo < 1 || m.RecNo > wa.Table.RecordCount) return false;
                if (wa.Table.GetRecord(m.RecNo - 1) is not DbfRecord rec) return false;
                foreach (var by in rec.GetRawField(col))
                    if (by != pad) return false;
                return true;
            }
            default:
                return false;   // integer / currency / double / binary — never blank (oracle-pinned for I).
        }
    }

    /// <summary>Value-based blankness (a non-field ISBLANK argument): character blank iff empty/all-spaces;
    /// date/datetime blank iff the empty date; numeric 0 / logical .F. / .NULL. are NOT blank.</summary>
    private static bool IsBlankValue(VfpValue v) => v.Type switch
    {
        VfpType.Character => (v.AsString ?? string.Empty).Trim(' ').Length == 0,
        VfpType.Date => v.AsDate == default,
        VfpType.DateTime => v.AsDateTime == default,
        _ => false,
    };

    // ─────────────────────────── BLANK command ───────────────────────────

    /// <summary>BLANK [FIELDS …] [scope][FOR][WHILE][IN] — reset the target fields to their BLANK bytes. A
    /// field REPLACEd with <c>.NULL.</c> is written by the encoder as the type's empty (all-spaces for text,
    /// all-zero for binary) on a NON-nullable column — exactly VFP's BLANK bytes — so BLANK reuses the full
    /// REPLACE pipeline (buffering / RI / txn / cdx maintenance). FLAG: on a NULLABLE column this would set a
    /// real .NULL.; VFP BLANK never does (not exercised — the corpus/tests use non-nullable columns).</summary>
    private void ExecBlank(BlankStmt b)
    {
        int area = b.In is not null ? ResolveAreaRef(b.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;

        IEnumerable<string> targets = b.Fields.Count > 0
            ? b.Fields
            : wa.Table.Columns.Where(c => !c.IsSystem).Select(c => c.Name);

        var clauses = targets
            .Where(f => ColumnIndex(wa.Table, StripQualifier(f)) >= 0)
            .Select(f => new ReplaceClause(NameRef.OfName(f), PrgExpr.Parse(".NULL."), false))
            .ToList();
        if (clauses.Count == 0) return;
        ExecReplace(new ReplaceStmt(clauses, b.Scope, b.For, b.While, b.In));
    }

    // ─────────────────────────── FLDLIST() ───────────────────────────

    /// <summary>FLDLIST([n]) — the field list set by SET FIELDS (verified vs the VFP9 runtime: it is the SET FIELDS
    /// list, NOT a FIELD()-style by-number lookup), alias-qualified + uppercase, comma-no-space
    /// (e.g. <c>SALES.ID,SALES.AMT</c>). Empty when no list is set. FLAG: the n-th-item form FLDLIST(n) and
    /// the actual field-visibility RESTRICTION (fields behaving as absent) are not modelled — SET FIELDS here
    /// only tracks the list for FLDLIST()/SET("FIELDS") introspection (hackfox s4g091: views supersede it).</summary>
    private string FnFldList(VfpValue[] a)
        => string.Join(",", _setFields.Select(f => (_setFieldsAlias.Length > 0 ? _setFieldsAlias + "." : string.Empty)
                                                    + f.ToUpperInvariant()));

    // ─────────────────────────── KEYMATCH() ───────────────────────────

    /// <summary>KEYMATCH(uKey [, nWhichKey [, area]]) — .T. iff uKey exists in the addressed index, WITHOUT
    /// moving the record pointer (verified vs the VFP9 runtime: RECNO() is unchanged after a hit AND a miss). Unlike
    /// SEEK, no AreaMeta state is touched. nWhichKey is the 1-based index number across the open set; absent
    /// ⇒ the controlling order.</summary>
    private bool FnKeyMatch(VfpValue[] a)
    {
        if (a.Length == 0) return false;
        VfpValue key = a[0];
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        string? identity;
        if (a.Length > 1 && IsNumeric(a[1]))
        {
            int n = (int)a[1].AsNumber;
            var inv = IndexInventory(area);
            if (n < 1 || n > inv.Count) return false;
            identity = inv[n - 1].Name;
        }
        else identity = DefaultOrderIdentity(area);
        if (identity is null) return false;
        return KeyMatchProbe(area, identity, key);
    }

    /// <summary>Seek <paramref name="key"/> in the order <paramref name="identity"/> and report whether it
    /// exists — SIDE-EFFECT FREE (no AreaMeta mutation, so the pointer never moves).</summary>
    private bool KeyMatchProbe(int area, string identity, VfpValue key)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return false;
        using var src = OpenOrderSource(area, identity);
        if (src is null) return false;

        uint? recno = null;
        if (src.CdxTag is { } t)
        {
            byte[]? characterNeedle = t.IsCharacterKey && key.Type == VfpType.Character
                ? NaturalCharacterSeekNeedle(t, key)
                : null;
            recno = characterNeedle is not null
                ? t.Seek(characterNeedle.AsSpan(), false)
                : t.Seek(key.ToClr() ?? string.Empty);
        }
        else if (src.Idx is not null)
            recno = SeekIdx(src, key);
        return recno is uint r && r >= 1 && r <= (uint)wa.Table.RecordCount;
    }

    // ─────────────────────────── GETNEXTMODIFIED() ───────────────────────────

    /// <summary>GETNEXTMODIFIED(nRec [, area [, nMask]]) — over the REAL TableBuffer, the VFP iteration
    /// protocol (verified vs the VFP9 runtime): for nRec ≥ 0, the recno of the next modified EXISTING row after nRec;
    /// once those are exhausted the FIRST buffered APPEND (−1). Buffered appends are walked as DESCENDING
    /// negative recnos: feeding −n back returns the next append −(n+1), until 0 signals none left. An
    /// UNBUFFERED area raises catchable VFP error 1596 (not a silent 0). FLAG: nMask (GETFLDSTATE filter) is
    /// accepted but ignored — not needed by the corpus.</summary>
    private int FnGetNextModified(VfpValue[] a)
    {
        int nRec = a.Length > 0 && IsNumeric(a[0]) ? (int)a[0].AsNumber : 0;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var m = Meta(area);
        if (m.Buffering <= 1)
            throw new MicroVfpRuntimeException("Table buffering is not enabled.", 1596);
        if (m.Buf is not { } buf) return 0;                    // buffering on, nothing pending.

        if (nRec < 0)
        {
            // Iterating buffered APPENDs: −1 → Appends[0], −2 → Appends[1], … . The next append after −n is
            // Appends[n] (recno −(n+1)); 0 once the appends are exhausted (verified: GETNEXTMODIFIED(−1)=−2
            // with two buffered appends). Feeding −1 back must NOT re-match positive rows.
            int n = -nRec;
            return n < buf.Appends.Count ? -(n + 1) : 0;
        }

        // nRec ≥ 0: the next modified EXISTING row with recno > nRec, else the FIRST append (−1), else 0.
        int? next = null;
        foreach (var k in buf.Rows.Keys)
            if (k > nRec && (next is null || k < next)) next = k;
        if (next is int nn) return nn;
        return buf.Appends.Count > 0 ? -1 : 0;   // first buffered append reports as −1 (new record).
    }

    // ─────────────────────────── SETFLDSTATE() ───────────────────────────

    /// <summary>SETFLDSTATE(cField|nField, nState [, area]) — validates the field + state and returns .T./.F.
    /// accordingly, but is INERT on a real table: GETFLDSTATE keeps deriving the state from the actual buffer
    /// (hackfox s4g395 — the view-buffering effect is not modelled). An UNBUFFERED area raises catchable VFP
    /// error 1586 (verified live), matching GETFLDSTATE; an unknown character name raises catchable error 11,
    /// while numeric field 0 remains the record delete-state. FLAG: view TABLEUPDATE steering is out of
    /// scope (no view model).</summary>
    private bool FnSetFldState(VfpValue[] a)
    {
        if (a.Length < 2) return false;
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return false;
        if (Meta(area).Buffering <= 1)
            throw new MicroVfpRuntimeException("Function requires row or table buffering mode.", 1586);
        bool isField0 = IsNumeric(a[0]) && a[0].AsNumber == 0m;
        int colIdx = IsNumeric(a[0]) ? (int)a[0].AsNumber - 1 : ColumnIndex(wa.Table, StripQualifier(a[0].AsString));
        // -1 is the internal column index for the legitimate numeric field 0. Do not let it also
        // accept an unresolved character name: VFP9 raises error 11 for an unknown field name.
        if (!isField0 && colIdx < 0)
            throw new MicroVfpRuntimeException("Field name was not found.", 11);
        bool fieldOk = isField0 || (colIdx >= 0 && colIdx < wa.Table.Columns.Count);
        int state = (int)a[1].AsNumber;
        bool stateOk = state is >= 0 and <= 4;
        return fieldOk && stateOk;
    }

    // ─────────────────────────── APPEND MEMO … FROM ───────────────────────────

    /// <summary>APPEND MEMO mField FROM cFile [OVERWRITE] [AS nCodePage] — copy the file's content into the
    /// current record's memo field (additive by default; replacing on OVERWRITE — verified vs the VFP9 runtime). The
    /// source is read as RAW BYTES and copied 1:1 (binary-safe — hackfox s4g066: works for .FXP / arbitrary
    /// binary, not just text): the bytes are decoded with the SAME single-byte encoding the memo write path
    /// re-encodes with (Latin1 for a NOCPTRANS/binary memo, else the table code page), so the round-trip is
    /// byte-exact — never the BOM-stripping / replacement-char corruption a UTF-8 text read caused. Routes
    /// the write through the REPLACE pipeline via a scratch memvar. FLAG: the AS nCodePage source-codepage
    /// conversion is unsupported and rejected (without AS, VFP copies 1:1 — hackfox s4g066 point 3).</summary>
    private void ExecAppendMemo(AppendMemoStmt am)
    {
        var wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return;
        string field = StripQualifier(NameOf(am.Field));
        int ci = ColumnIndex(wa.Table, field);
        if (ci < 0)
            throw new MicroVfpRuntimeException($"APPEND MEMO: '{field}' is not a field of the current table.");
        if (am.AsCodePage is not null)
            throw new MicroVfpRuntimeException("APPEND MEMO: the AS nCodePage source-codepage conversion is not supported.");

        string src = NameOf(am.Source);
        string path = Path.IsPathRooted(src) ? src
            : Path.Combine(Session.DataDirectory ?? Directory.GetCurrentDirectory(), src);
        if (!File.Exists(path))
            throw new MicroVfpRuntimeException($"APPEND MEMO: file '{src}' not found.");

        // Decode the raw file bytes with the field's own memo encoding so the ExecReplace re-encode is a
        // byte-for-byte round-trip (Latin1 is fully bijective; the table code page is symmetric for the
        // field, matching DbfTable.ReadColumn ⇄ DbfWriter's memo write).
        byte[] bytes = File.ReadAllBytes(path);
        var enc = wa.Table.Columns[ci].IsBinary ? Encoding.Latin1 : wa.Table.Encoding;
        string content = enc.GetString(bytes);

        string existing = am.Overwrite ? string.Empty : (ReadField(wa, field)?.ToString() ?? string.Empty);
        string result = existing + content;

        const string tmp = "__am_memo_tmp__";
        Memory.Set(tmp, VfpValue.Character(result));
        try
        {
            ExecReplace(new ReplaceStmt(
                new[] { new ReplaceClause(NameRef.OfName(field), PrgExpr.Parse("m." + tmp), false) },
                null, null, null, null));
        }
        finally { Memory.Release(tmp); }
    }

    // ─────────────────────────── REPLACE FROM ARRAY ───────────────────────────

    /// <summary>REPLACE FROM ARRAY aArray [FIELDS …] — update the CURRENT record's fields from array ROW 1
    /// (a 2-D array's row-1 slice, or a 1-D array's elements), element j → field j in physical field order
    /// (or the FIELDS list). A 2-D array contributes at most its COLUMN count per record — never spilling
    /// row 2's elements into the current record (linear subscripts are capped at Cols). Memo/general/blob
    /// /picture fields are SKIPPED (they hold a .F. placeholder in a COPY TO ARRAY result — hackfox s4g386).
    /// Reuses the REPLACE pipeline. FLAG: the multi-record scope form (one array ROW per record, backlog
    /// "Jede Array-Zeile entspricht einem Record") is rejected — ExecReplace only ever touches the current
    /// record, so honouring row→record would need a scope iterator microVFP lacks; silently writing row 1
    /// into (only) the current record would mis-represent the command, so a scoped/FOR/WHILE form throws.</summary>
    private void ExecReplaceFromArray(ReplaceFromArrayStmt rfa)
    {
        int area = rfa.In is not null ? ResolveAreaRef(rfa.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var arr = Memory.FindArray(rfa.ArrayName);
        if (arr is null) return;

        if (rfa.Scope is not null || rfa.For is not null || rfa.While is not null)
            throw new MicroVfpRuntimeException(
                "REPLACE FROM ARRAY: the multi-record scope form (one array row per record) is not supported.");

        var fields = rfa.Fields.Count > 0
            ? rfa.Fields.Where(f => ColumnIndex(wa.Table, StripQualifier(f)) >= 0).ToList()
            : wa.Table.Columns.Where(c => !c.IsSystem).Select(c => c.Name).ToList();

        // Elements from array ROW 1 only: a 2-D array's first-row slice is its first Cols linear elements
        // (row-major), so cap at Cols to avoid spilling row 2. A 1-D array maps all its elements.
        int perRow = arr.Is2D ? arr.Cols : arr.Length;
        int n = Math.Min(fields.Count, perRow);
        var clauses = new List<ReplaceClause>(n);
        for (int i = 0; i < n; i++)
        {
            int ci = ColumnIndex(wa.Table, StripQualifier(fields[i]));
            if (ci < 0) continue;
            if (wa.Table.Columns[ci].Type is 'M' or 'G' or 'W' or 'P') continue;   // memo/general/blob/picture — skipped.
            clauses.Add(new ReplaceClause(NameRef.OfName(fields[i]), PrgExpr.Parse($"{rfa.ArrayName}({i + 1})"), false));
        }
        if (clauses.Count == 0) return;
        ExecReplace(new ReplaceStmt(clauses, null, null, null, rfa.In));
    }

    // ─────────────────────────── COPY TAG / COPY INDEXES ───────────────────────────

    /// <summary>COPY TAG cTag [OF cCdx] TO cIdx — extract one compound-index tag as a standalone legacy
    /// <c>.idx</c>, rebuilt over the live table from the tag's KEY/FOR expression (byte-sane + seekable —
    /// verified vs the VFP9 runtime). The new <c>.idx</c> is NOT auto-opened (VFP behaviour).</summary>
    private void ExecCopyTag(CopyTagStmt ct)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("COPY TAG: no table is open in the current work area.");

        string tagName = NameOf(ct.Tag).Trim();
        string? file = ct.OfCdx is null ? null : NameOf(ct.OfCdx).Trim();
        var slot = FilterInventory(IndexInventory(area), file)
            .FirstOrDefault(s => string.Equals(s.Name, tagName, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
            throw new MicroVfpRuntimeException($"COPY TAG: tag '{tagName}' not found in the current work area.");

        string idxPath = ResolveSidecarPath(path, NameOf(ct.ToIdx).Trim(), ".idx");
        string keyExpr = slot.KeyExpr;
        string? forExpr = slot.ForExpr.Length > 0 ? slot.ForExpr : null;
        bool unique = slot.Unique;
        BuildTagOnDisk(path, w => w.CreateStandaloneIdx(idxPath, keyExpr, forExpr, unique, _ctx, !_ctx.Deleted));
    }

    /// <summary>COPY INDEXES cIdxList | ALL [TO cCdx] — compile the named open standalone <c>.idx</c> files
    /// as new tags (named after each source file's stem, uppercased) in the structural (or named) compound
    /// index, rebuilt over the live table from each <c>.idx</c>'s KEY/FOR expression.</summary>
    private void ExecCopyIndexes(CopyIndexesStmt ci)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("COPY INDEXES: no table is open in the current work area.");

        bool structural = ci.ToCdx is null;
        string cdxPath = structural
            ? Path.ChangeExtension(path, ".cdx")
            : ResolveSidecarPath(path, NameOf(ci.ToCdx!).Trim(), ".cdx");

        var idxPaths = ci.All
            ? IdxFileInventory(area)
            : ci.Sources.Select(src => ResolveExistingIndexPath(path, NameOf(src).Trim()))
                        .Where(IsIdxPath).ToList();
        if (idxPaths.Count == 0)
            throw new MicroVfpRuntimeException("COPY INDEXES: no standalone .idx source is open.");

        var defs = new List<CdxTagDefinition>(idxPaths.Count);
        foreach (var idxPath in idxPaths)
        {
            using var idx = IdxFile.Open(idxPath, wa.Table.Encoding);
            var h = idx.Header;
            string forExpr = h.ForExpression.Trim();
            defs.Add(new CdxTagDefinition(
                Path.GetFileNameWithoutExtension(idxPath).ToUpperInvariant(),
                h.KeyExpression.Trim(), forExpr.Length > 0 ? forExpr : null,
                descending: false, "MACHINE", h.IsUnique));
        }

        BuildTagOnDisk(path, w =>
        {
            foreach (var def in defs)
                if (structural) w.CreateTag(def, _ctx, includeDeleted: !_ctx.Deleted);
                else w.CreateTagIn(cdxPath, structural: false, def, _ctx, includeDeleted: !_ctx.Deleted);
        });
        if (!structural) AddExtraIndex(area, cdxPath);
    }

    // ─────────────────────────── CLOSE INDEXES ───────────────────────────

    /// <summary>Close every NON-structural index of <paramref name="area"/> (standalone <c>.idx</c> +
    /// non-structural <c>.cdx</c>), leaving the structural <c>.cdx</c> open; revert the controlling order to
    /// natural when it was one of the closed indexes (verified vs the VFP9 runtime).</summary>
    private void CloseNonStructuralIndexes(int area)
    {
        var m = Meta(area);
        m.ExtraIndexes = null;
        if (!string.IsNullOrEmpty(m.Order))
        {
            if (OpenOrderSource(area, m.Order!) is { } src) src.Dispose();   // still resolvable (structural) ⇒ keep.
            else { m.Order = null; ResetOrderState(m); }
        }
    }
}
