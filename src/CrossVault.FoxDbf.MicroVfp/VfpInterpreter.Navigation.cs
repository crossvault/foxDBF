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

/// <summary>microVFP record navigation — GO/SKIP/SEEK/LOCATE/CONTINUE/SCAN traversal, SET KEY visibility, and the record-pointer model (AreaMeta).</summary>
public sealed partial class VfpInterpreter
{
    // ─────────────────────────── LOCATE / CONTINUE (project-review 5.2) ───────────────────────────
    //
    // LOCATE positions on the FIRST record — in the area's CURRENT order (the master index if one is set,
    // else physical) — that satisfies FOR within the scope/WHILE window, honouring SET DELETED /
    // SET KEY / SET FILTER visibility through the SAME Visible() gate GO/SKIP use. Found ⇒ pointer on the record,
    // FOUND()=.T.; the search runs off the end ⇒ EOF(), FOUND()=.F. LOCATE with no FOR positions on the
    // first visible record. CONTINUE resumes the LAST LOCATE of the CURRENT work area from the record AFTER
    // the current one, reusing its remembered FOR/WHILE + scope window (per AreaMeta); CONTINUE with no
    // prior LOCATE raises VFP error 42. Both are ONE pointer move and fire the GO/SKIP side effects:
    // MaybeAutoCommitRow before leaving the row, RepositionChildren after landing.

    private enum LocScope { All, Rest, Next, Record }

    private void ExecLocate(LocateStmt loc)
    {
        int area = Session.CurrentArea;
        if (Session.AreaAt(area) is null) return;     // no table open ⇒ nothing to search.
        var m = Meta(area);

        var (kind, count) = ResolveLocScope(loc.Scope);
        // A WHILE with NO explicit scope keyword defaults to REST — the search starts at the CURRENT record,
        // not the top of the table (the canonical `SEEK key` / `LOCATE … WHILE key=…` key-group idiom relies
        // on this; a top restart would discard the SEEK). Mirrors ExecScan's `fromCurrent` rule. Guard on the
        // RAW parsed scope (loc.Scope) so an explicit ALL is never overridden. VFP9-verified.
        if (loc.Scope is null && loc.While is not null) kind = LocScope.Rest;

        MaybeAutoCommitRow(area);                     // leaving the current row commits its pending edit (mode 2/3).
        var wa = Session.AreaAt(area);                // MUST re-fetch: a commit may reopen the file areas.
        if (wa is null) return;

        // Remember this LOCATE so a following CONTINUE can resume it (per work area).
        m.LocateActive = true;
        m.LocateFor = loc.For;
        m.LocateWhile = loc.While;

        if (kind == LocScope.Record)
        {
            m.LocateWindow = new HashSet<int> { count };
            m.LocateWindowFull = true;                // single-record window is "full" (exhaust ⇒ park on it).
            LocateOnRecord(area, wa, m, count, loc.For, loc.While);
            RepositionChildren(area);
            return;
        }

        var order = TraversalOrder(area, wa, includeCurrent: kind != LocScope.All); // recnos in the CURRENT order (index order or physical).
        int startIdx = kind == LocScope.All ? 0 : CurrentIndex(order, m);   // REST/NEXT start at the current record.
        if (startIdx < 0) startIdx = order.Count;     // current at EOF/BOF ⇒ REST/NEXT window is empty.

        List<int> candidates;
        bool unbounded;
        if (kind == LocScope.Next)
        {
            var (window, full) = BuildNextWindow(order, wa, startIdx, count);
            m.LocateWindow = new HashSet<int>(window);
            m.LocateWindowFull = full;
            candidates = window;
            unbounded = false;
        }
        else                                          // ALL / REST — unbounded to the end of the order.
        {
            m.LocateWindow = null;
            m.LocateWindowFull = false;
            candidates = SubListFrom(order, startIdx);
            unbounded = true;
        }

        if (kind != LocScope.All && candidates.Count > 0) candidates[0] = Math.Abs(candidates[0]);
        RunLocateScan(area, wa, m, candidates, loc.For, loc.While, unbounded, m.LocateWindowFull,
            includeCurrent: kind != LocScope.All);
        RepositionChildren(area);
    }

    private void ExecContinue()
    {
        int area = Session.CurrentArea;
        var m = Meta(area);
        if (!m.LocateActive)
            throw new MicroVfpRuntimeException("The LOCATE command must be issued before the CONTINUE command.", 42);

        MaybeAutoCommitRow(area);
        var wa = Session.AreaAt(area);                // MUST re-fetch: a commit may reopen the file areas.
        if (wa is null) return;

        var order = TraversalOrder(area, wa, includeCurrent: true);
        int curIdx = CurrentIndex(order, m);
        int from = (m.Eof || curIdx < 0) ? order.Count : curIdx + 1;   // resume AFTER the current record.

        List<int> candidates = SubListFrom(order, from);
        bool unbounded = m.LocateWindow is null;
        if (!unbounded) candidates = candidates.Where(m.LocateWindow!.Contains).ToList();   // stay inside the NEXT/RECORD window.

        RunLocateScan(area, wa, m, candidates, m.LocateFor, m.LocateWhile, unbounded, m.LocateWindowFull);
        RepositionChildren(area);
    }

    /// <summary>Parse the LOCATE scope head (null/"ALL"/"REST"/"NEXT n"/"RECORD n"); the count expression is
    /// evaluated in the current context (VFP allows an expression, e.g. <c>NEXT lnCount</c>).</summary>
    private (LocScope Kind, int Count) ResolveLocScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return (LocScope.All, 0);
        string s = scope.Trim();
        int sp = s.IndexOf(' ');
        string head = (sp < 0 ? s : s.Substring(0, sp)).ToUpperInvariant();
        string rest = sp < 0 ? string.Empty : s.Substring(sp + 1).Trim();
        return head switch
        {
            "ALL" => (LocScope.All, 0),
            "REST" => (LocScope.Rest, 0),
            "NEXT" => (LocScope.Next, ScopeCount(rest)),
            "RECORD" => (LocScope.Record, ScopeCount(rest)),
            _ => (LocScope.All, 0),                    // unrecognised head ⇒ ALL (VFP's default scope).
        };

        int ScopeCount(string expr)
        {
            try { return (int)Eval(PrgExpr.Parse(expr)).AsNumber; } catch { return 0; }
        }
    }

    /// <summary>The recnos of <paramref name="area"/> in its CURRENT traversal order — the active index
    /// sequence (<see cref="ActiveOrder"/>) or, with no controlling order, physical 1..count.</summary>
    private List<int> TraversalOrder(int area, VfpSession.WorkArea wa, bool includeCurrent = false)
    {
        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            var m = Meta(area);
            if (includeCurrent && !m.Eof && m.RecNo >= 1 && m.RecNo <= EffCount(area, wa)
                && CurrentIndex(ord, m) < 0)
            {
                // A reopen/order reset can discard the tombstone. REST still starts on that
                // physical row; synthesize its slot without adding it to the live index cache.
                int position = MissingOrderInsertionPoint(area, m);
                var scoped = new List<int>(m.Ordered!);
                scoped.Insert(position, -m.RecNo);
                return scoped;
            }
            return ord;
        }
        int rc = EffCount(area, wa);
        var list = new List<int>(rc);
        for (int i = 1; i <= rc; i++) list.Add(i);
        return list;
    }

    /// <summary>The index of the CURRENT record within <paramref name="order"/>, or -1 when the pointer is
    /// at EOF/BOF (or on a record absent from the active index).</summary>
    private static int CurrentIndex(List<int> order, AreaMeta m)
    {
        if (ReferenceEquals(order, m.Ordered)) return OrderSlot(m, m.RecNo);
        int slot = order.IndexOf(m.RecNo);
        return slot >= 0 ? slot : order.IndexOf(-m.RecNo);
    }

    private static int OrderSlot(AreaMeta m, int rec)
    {
        var order = m.Ordered!;
        int pos = m.OrderPos;
        if (pos >= 0 && pos < order.Count && Math.Abs(order[pos]) == rec) return pos;
        // Scope walkers normally advance one entry; keep COUNT/SUM linear without a dictionary.
        if (pos + 1 >= 0 && pos + 1 < order.Count && Math.Abs(order[pos + 1]) == rec) return pos + 1;
        EnsureOrderSlots(m);
        return m.OrderSlots!.GetValueOrDefault(rec, -1);
    }

    private static void EnsureOrderSlots(AreaMeta m)
    {
        if (m.OrderSlots is not null) return;
        var slots = new Dictionary<int, int>(m.Ordered!.Count);
        for (int i = 0; i < m.Ordered.Count; i++)
            slots.TryAdd(Math.Abs(m.Ordered[i]), i); // Damaged or mid-write indexes may repeat a recno.
        m.OrderSlots = slots;
    }

    private static List<int> SubListFrom(List<int> order, int from)
        => from >= order.Count ? new List<int>() : order.GetRange(from, order.Count - from);

    /// <summary>The first <paramref name="count"/> VISIBLE recnos of the NEXT window, walking <paramref
    /// name="order"/> from <paramref name="startIdx"/>, plus whether the window reached its full count (a
    /// full window that finds nothing parks on its last record; a window truncated by EOF goes to EOF).</summary>
    private (List<int> Window, bool Full) BuildNextWindow(List<int> order, VfpSession.WorkArea wa, int startIdx, int count)
    {
        var w = new List<int>();
        for (int idx = Math.Max(0, startIdx); idx < order.Count && w.Count < count; idx++)
            if (idx == startIdx || Visible(wa, order[idx])) w.Add(Math.Abs(order[idx]));
        return (w, count > 0 && w.Count == count);
    }

    /// <summary>Walk <paramref name="candidates"/> (already in order, already the right scope subset) for the
    /// first record satisfying FOR, honouring WHILE (stops on the first .F., parking there) and visibility.
    /// No match: an <paramref name="unbounded"/> (ALL/REST) walk parks at EOF; a full bounded (NEXT/RECORD)
    /// window leaves the pointer where it is — on the FIRST LOCATE that is its last examined record, on a
    /// CONTINUE whose remaining slice is already empty it stays put (only FOUND() clears) (VFP9-verified).</summary>
    private void RunLocateScan(int area, VfpSession.WorkArea wa, AreaMeta m, List<int> candidates,
        PrgExpr? forExpr, PrgExpr? whileExpr, bool unbounded, bool windowFull, bool includeCurrent = false)
    {
        var ord = ActiveOrder(area);
        foreach (int rec in candidates)
        {
            bool current = includeCurrent && rec == m.RecNo;
            includeCurrent = false;
            if (!current && !Visible(wa, rec)) continue;
            PositionPointer(m, ord, rec);
            if (whileExpr is not null && !Truth(Eval(whileExpr))) { m.Found = false; return; }  // WHILE .F. ⇒ stop here.
            if (forExpr is null || Truth(Eval(forExpr))) { m.Found = true; return; }             // match ⇒ done.
        }
        // No match after the walk.
        if (!unbounded && windowFull)
        {
            // A fully-counted bounded window (NEXT n / RECORD n) that finds nothing does NOT move to EOF: the
            // pointer stays where it is (VFP9-verified). On the FIRST LOCATE a full window always examined a
            // record, so the pointer is parked on that last record; on a CONTINUE whose remaining window slice
            // is already empty (lastExamined == -1) the pointer is simply left untouched — RecNo/Bof/Eof below
            // are skipped and only FOUND() clears.
            m.Found = false;
            return;
        }
        m.RecNo = EffCount(area, wa) + 1; m.Eof = true; m.Bof = false; m.Found = false;
        m.Cached = null; m.OldVals = null;
        if (ord is not null) m.OrderPos = ord.Count;
    }

    /// <summary>LOCATE RECORD n — GO n (explicit; no visibility skip), then evaluate WHILE/FOR on it; the
    /// pointer stays on record n whether or not it matches (VFP9-verified). Out-of-range n ⇒ EOF/not found.</summary>
    private void LocateOnRecord(int area, VfpSession.WorkArea wa, AreaMeta m, int rec, PrgExpr? forExpr, PrgExpr? whileExpr)
    {
        var ord = ActiveOrder(area);
        if (rec < 1 || rec > wa.Table.RecordCount)
        {
            m.RecNo = EffCount(area, wa) + 1; m.Eof = true; m.Bof = false; m.Found = false;
            m.Cached = null; m.OldVals = null;
            if (ord is not null) m.OrderPos = ord.Count;
            return;
        }
        PositionPointer(m, ord, rec);
        if (whileExpr is not null && !Truth(Eval(whileExpr))) { m.Found = false; return; }
        m.Found = forExpr is null || Truth(Eval(forExpr));
    }

    /// <summary>
    /// <c>COUNT [Scope] [FOR lExpr] [WHILE lExpr] TO VarName</c> — counts the records of the current area that
    /// satisfy FOR inside the scope/WHILE window, in the CURRENT order, honouring SET DELETED / SET FILTER /
    /// SET KEY through the same <see cref="Visible"/> gate LOCATE uses. Pointer afterwards (VFP9-verified):
    /// ALL/REST ⇒ EOF; NEXT n ⇒ on the n-th visible record (or EOF when the table ran out first; NEXT 0
    /// leaves it untouched); a failing WHILE ⇒ on that record; RECORD n ⇒ on record n, which is counted
    /// even when SET DELETED ON would hide it. A WHILE without a scope means REST, as for LOCATE/SCAN.
    /// </summary>
    private void ExecCount(CountStmt c)
    {
        int area = Session.CurrentArea;
        if (Session.AreaAt(area) is null)
            throw new MicroVfpRuntimeException("No table is open in the current work area.", 52);
        var m = Meta(area);
        var (kind, count) = ResolveLocScope(c.Scope);
        if (c.Scope is null && c.While is not null) kind = LocScope.Rest;

        MaybeAutoCommitRow(area);                     // leaving the current row commits its pending edit (mode 2/3).
        var wa = Session.AreaAt(area);                // MUST re-fetch: a commit may reopen the file areas.
        if (wa is null) return;
        var ord = ActiveOrder(area);

        int n = 0;
        if (kind == LocScope.Record)
        {
            if (count < 1 || count > wa.Table.RecordCount)
                throw new MicroVfpRuntimeException("Record is out of range.", 5);
            PositionPointer(m, ord, count);
            if ((c.While is null || Truth(Eval(c.While))) && (c.For is null || Truth(Eval(c.For)))) n = 1;
        }
        else
        {
            var order = TraversalOrder(area, wa, includeCurrent: kind != LocScope.All);
            int startIdx = kind == LocScope.All ? 0 : CurrentIndex(order, m);
            if (startIdx < 0) startIdx = order.Count;   // current at EOF/BOF ⇒ REST/NEXT window is empty.
            int remaining = kind == LocScope.Next ? count : int.MaxValue;
            bool stopped = remaining <= 0;              // NEXT 0 counts nothing and does not move.
            for (int i = startIdx; !stopped && i < order.Count; i++)
            {
                int rec = kind != LocScope.All && i == startIdx ? Math.Abs(order[i]) : order[i];
                // REST/NEXT process the CURRENT record as-is (VFP9: GO <deleted rec>, COUNT NEXT 1 under SET
                // DELETED ON counts it); only the records reached by skipping honour visibility.
                if ((kind == LocScope.All || i != startIdx) && !Visible(wa, rec)) continue;
                PositionPointer(m, ord, rec);
                if (c.While is not null && !Truth(Eval(c.While))) { stopped = true; break; }
                if (c.For is null || Truth(Eval(c.For))) n++;
                if (--remaining == 0) stopped = true;
            }
            if (!stopped)
            {
                m.RecNo = EffCount(area, wa) + 1; m.Eof = true; m.Bof = false; m.Found = false;
                m.Cached = null; m.OldVals = null;
                if (ord is not null) m.OrderPos = ord.Count;
            }
        }
        RepositionChildren(area);
        if (c.Target is not null) AssignTo(c.Target, VfpValue.Integer(n));
    }

    /// <summary>Position the pointer on <paramref name="rec"/> for FOR/WHILE evaluation (re-basing the record
    /// cache + OLDVAL buffer + index position), the SAME state a GO would set — but WITHOUT the GO/SKIP side
    /// effects, which LOCATE fires ONCE for its whole scan.</summary>
    private static void PositionPointer(AreaMeta m, List<int>? ord, int rec)
    {
        m.RecNo = rec; m.Eof = false; m.Bof = false; m.Cached = null; m.OldVals = null;
        m.OrderPos = ord is null ? -1 : CurrentIndex(ord, m);
    }

    private void ExecSeek(SeekStmt sk)
    {
        int area = sk.In is not null ? ResolveAreaRef(sk.In) : Session.CurrentArea;
        string? tag = sk.Order is null ? null : NameOf(sk.Order);
        DoSeek(Eval(sk.Key), area, tag);
    }

    private void ExecGo(GoStmt go)
    {
        int area = go.In is not null ? ResolveAreaRef(go.In) : Session.CurrentArea;
        if (area <= 0) return;
        if (go.Keyword == "TOP") GoTop(area);
        else if (go.Keyword == "BOTTOM") GoBottom(area);
        else if (go.Record is not null) GoRecord(area, (int)Eval(go.Record).AsNumber);
    }

    private void ExecSkip(SkipStmt sp)
    {
        int area = sp.In is not null ? ResolveAreaRef(sp.In) : Session.CurrentArea;
        int n = sp.Count is null ? 1 : (int)Eval(sp.Count).AsNumber;
        Skip(area, n);
    }

    /// <summary>SET KEY TO [eLow [, eHigh]] | RANGE eLow, eHigh — limit the current work area's visible
    /// records to those whose MASTER-index key equals eLow (single) or lies in [eLow, eHigh] (range).
    /// Requires a controlling index. <c>SET KEY TO</c> (no arg) clears the range.</summary>
    private void SetKey(string arg)
    {
        string rest = arg;
        if (PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
            rest = PrgScan.AfterFirstWord(rest);
        rest = rest.Trim();

        // Optional trailing IN <area> clause selects a different work area than the current one.
        int area = Session.CurrentArea;
        int inPos = PrgScan.IndexOfKeyword(rest, "IN");
        if (inPos >= 0)
        {
            string areaTok = PrgScan.AfterFirstWord(rest.Substring(inPos)).Trim();
            rest = rest.Substring(0, inPos).Trim();
            int resolved = int.TryParse(areaTok, out var n) ? n : (Session.FindAreaByAlias(areaTok)?.Area ?? 0);
            if (resolved > 0) area = resolved;
        }
        var m = Meta(area);

        if (rest.Length == 0)   // SET KEY TO → clear.
        {
            m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
            return;
        }

        // The controlling order may be sourced from ANY open index (structural/extra .cdx or standalone
        // .idx), so resolve the master through the full-inventory path — not just the structural .cdx.
        using (var master = OpenMasterOrder(area))
        {
            if (master is null)
                throw new MicroVfpRuntimeException(
                    "SET KEY TO: no controlling index is active in the current work area (SET ORDER first).");

            // A NON-character key whose logical type cannot be resolved (a composite expression that isn't a
            // bare numeric/date field) cannot be decoded into a comparable value, so a range over it would
            // silently mismatch. Refuse LOUDLY rather than produce a wrong (empty / full) visible set.
            // Character keys are always handled byte-wise (below) — including UPPER(name)-style expressions
            // and GENERAL-collated weights — so they never hit this guard.
            if (!master.IsCharacterKey && master.KeyType == IndexKeyType.Unknown)
                throw new MicroVfpRuntimeException(
                    "SET KEY TO: the controlling index key type cannot be decoded for a range restriction.");
        }

        bool rangeKw = PrgScan.FirstWord(rest).Equals("RANGE", StringComparison.OrdinalIgnoreCase);
        if (rangeKw) rest = PrgScan.AfterFirstWord(rest).Trim();
        var parts = PrgScan.SplitTopCommas(rest).ToList();

        if (rangeKw || parts.Count > 1)
        {
            string lo = parts.Count > 0 ? parts[0].Trim() : string.Empty;
            string hi = parts.Count > 1 ? parts[1].Trim() : string.Empty;
            m.KeyRange = true;
            m.KeyLow = lo.Length > 0 ? EvalText(lo) : null;
            m.KeyHigh = hi.Length > 0 ? EvalText(hi) : null;
        }
        else
        {
            var single = EvalText(rest);
            if (single.Type == VfpType.Character && single.AsString.Length == 0)
            {
                // SET KEY TO '' clears the scope like a bare SET KEY TO (VFP9-verified: every row, SET('KEY') = '',
                // under any collation and SET EXACT).
                m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
                return;
            }
            m.KeyRange = false;
            m.KeyLow = single;
            m.KeyHigh = null;
        }
        m.KeySet = true;
        m.KeyVisible = null;   // rebuilt lazily by Visible().
    }

    /// <summary>SET FILTER TO [lExpr] — set the CURRENT work area's record-visibility predicate (fed into
    /// the SAME <see cref="Visible"/> gate as SET DELETED / SET KEY). Bare <c>SET FILTER TO</c> clears it.
    /// VFP validates/evaluates the expression at SET time: an unknown field/variable raises err 12
    /// ("Variable 'X' is not found.") right here — oracle-pinned — leaving the prior filter state untouched.
    /// The predicate lives in per-area <see cref="AreaMeta"/> so it follows USE/close and the data session in
    /// lockstep (5.14). <see cref="FnFilter"/> / <c>SET("FILTER")</c> report the VFP-normalised text.</summary>
    private void SetFilter(string arg)
    {
        int area = Session.CurrentArea;
        var m = Meta(area);

        string rest = arg;
        if (PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
            rest = PrgScan.AfterFirstWord(rest);
        rest = rest.Trim();

        if (rest.Length == 0)   // SET FILTER TO → clear (FILTER()/SET("FILTER") return "").
        {
            m.FilterExpr = null; m.FilterText = null;
            return;
        }

        // VFP evaluates the filter at SET time; an unknown name is err 12 AT THIS STATEMENT (not deferred to
        // navigation). microVFP's engine coerces unknown names to .NULL. instead of throwing, so we validate
        // the referenced names explicitly BEFORE mutating state (a throw leaves the prior filter in place).
        ValidateFilterNames(rest, area);

        // ...and evaluates it once against the current record (VFP9-verified, also at EOF and on an empty table):
        // an unknown function raises error 1 here, a UDF is called, an error inside it surfaces.
        var filter = PrgExpr.Parse(rest);
        ProbeEvaluate(filter);

        m.FilterExpr = filter;
        m.FilterText = VfpExpressionText.Normalize(rest);
    }

    /// <summary>VFP raises err 12 ("Variable 'X' is not found.") at SET FILTER TO time for a name that is
    /// neither a field of the current table nor a memvar/array/#define. microVFP's expression engine silently
    /// coerces an unknown identifier to <c>.NULL.</c> (no throw), so we scan the filter text's BARE
    /// identifiers — outside string literals, not a function call (not followed by '('), not a dotted operator
    /// / logical constant — and raise the same typed error for the first unknown one. Only ever runs on the
    /// SET FILTER path, so a conservative miss cannot affect any other command.</summary>
    private void ValidateFilterNames(string exprText, int area)
    {
        var wa = Session.AreaAt(area);
        foreach (var name in BareFilterIdentifiers(exprText))
        {
            if (wa is not null && ColumnIndex(wa.Table, name) >= 0) continue;   // a field of the current table.
            if (Memory.IsDefined(name) || Memory.FindArray(name) is not null) continue;   // a memvar / array.
            if (_defines.ContainsKey(name)) continue;                           // a #DEFINE constant.
            if (name.Equals("_triggerlevel", StringComparison.OrdinalIgnoreCase)) continue;
            throw new MicroVfpRuntimeException($"Variable '{name.ToUpperInvariant()}' is not found.", 12);
        }
    }

    // The VFP logical/operator words that are NOT variable references (so an identifier scan must skip them);
    // the dotted forms (.AND./.OR./.NOT./.T./.F./.NULL./.Y./.N.) are also skipped via the preceding-dot test.
    private static readonly HashSet<string> FilterReservedWords = new(StringComparer.OrdinalIgnoreCase)
    { "AND", "OR", "NOT", "T", "F", "Y", "N", "NULL" };

    /// <summary>Enumerate the BARE identifiers of a filter expression eligible to be a field/variable
    /// reference: a maximal identifier run (<see cref="CrossVault.FoxDbf.Expressions.VfpIdentifier"/>) that is OUTSIDE a string literal
    /// (<c>"…"</c>/<c>'…'</c>/<c>[…]</c>), NOT immediately followed by <c>(</c> (a function call), NOT
    /// immediately preceded by <c>.</c> or <c>-&gt;</c> (a dotted operator / an <c>alias-&gt;field</c> tail),
    /// NOT the alias part of an <c>alias.field</c> qualifier, and not a reserved logical word.</summary>
    private static IEnumerable<string> BareFilterIdentifiers(string expr)
    {
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '"' || c == '\'' || c == '[')     // skip a string literal wholesale.
            {
                char close = c == '[' ? ']' : c;
                i++;
                while (i < expr.Length && expr[i] != close) i++;
                continue;
            }
            if (VfpIdentifier.IsStart(c))
            {
                int start = i;
                while (i < expr.Length && (VfpIdentifier.IsPart(expr[i]))) i++;
                string ident = expr.Substring(start, i - start);
                // Look one char ahead (skipping spaces) for a call '(' or a member '.'/alias qualifier — those
                // make the ident a function name or an alias, not a bare field/variable reference.
                int j = i; while (j < expr.Length && expr[j] == ' ') j++;
                char next = j < expr.Length ? expr[j] : '\0';
                char prev = start > 0 ? expr[start - 1] : '\0';
                i--;                                    // the for-loop's i++ re-consumes the terminator.
                if (next == '(') continue;              // FUNC(… ) — a function call, not a variable.
                if (next == '.') continue;              // alias.field / .AND.-style — the alias/keyword head.
                if (next == '-' && j + 1 < expr.Length && expr[j + 1] == '>') continue;   // alias->field head.
                if (prev == '.') continue;              // .field tail / .AND. keyword — not a bare name.
                if (prev == '>' && start > 1 && expr[start - 2] == '-') continue;   // alias->field tail.
                if (FilterReservedWords.Contains(ident)) continue;
                yield return ident;
            }
        }
    }

    /// <summary>Open the controlling (master) ORDER of <paramref name="area"/> across the FULL open index
    /// set (structural <c>.cdx</c>, extra <c>.cdx</c>, standalone <c>.idx</c>), or null when none is active.
    /// The caller MUST dispose the result (it may own a temp file handle).</summary>
    private OrderSource? OpenMasterOrder(int area)
    {
        var m = Meta(area);
        return string.IsNullOrEmpty(m.Order) ? null : OpenOrderSource(area, m.Order!);
    }

    /// <summary>The STORED traversal direction of the controlling order, resolved over the full open set
    /// (a standalone <c>.idx</c> is always ascending). Feeds the SET ORDER … ASCENDING|DESCENDING override.</summary>
    private bool MasterDescending(int area)
    {
        using var src = OpenMasterOrder(area);
        return src?.Descending ?? false;
    }

    /// <summary>Build the set of recnos whose master-index key falls in the area's active SET KEY range.
    /// A missing tag ⇒ no restriction (every record). CHARACTER keys are compared on the SAME collated key
    /// bytes the tag STORES (so MACHINE, GENERAL weights and UPPER()-style expression keys all compare
    /// correctly instead of against decoded weight-garbage); other keys decode to a value and compare by
    /// value order.</summary>
    private HashSet<int> BuildKeyVisible(VfpSession.WorkArea wa, AreaMeta m)
    {
        var set = new HashSet<int>();
        // Resolve the controlling order across the FULL open index set (structural/extra .cdx or
        // standalone .idx), not just the structural .cdx, so SET KEY works for any open index.
        using var src = OpenMasterOrder(wa.Area);
        if (src is null)
        {
            for (int r = 1; r <= wa.Table.RecordCount; r++) set.Add(r);
            return set;
        }

        return KeyVisibleFromEntries(m, src, OrderedEntries(src));
    }

    private HashSet<int> KeyVisibleFromEntries(AreaMeta m, OrderSource src, IEnumerable<(byte[] Key, int Recno)> entries)
    {
        var set = new HashSet<int>();
        if (src.IsCharacterKey)
        {
            if (!m.KeyRange)
            {
                // Single value: the stored key equals the padded bound or starts with the bound prefix, per SET
                // EXACT and the order's collation (see ScopeCharacterKey).
                if (m.KeyLow is not { } single) return set;   // no bound value ⇒ nothing matches (never fail-open).
                byte[] bound = ScopeCharacterKey(src, single, _ctx.Exact, out bool equality);
                foreach (var (key, recno) in entries)
                {
                    if (equality ? CompareBytesUnsigned(key, bound) == 0 : IsBytePrefix(bound, key))
                        set.Add(recno);
                }
                return set;
            }

            // Range: encode each bound the SAME way the order stores keys (CDX collation weights or standalone
            // IDX code-page bytes), padded to the key length. Raw comparison mirrors on-disk order.
            byte[]? loBytes = m.KeyLow is { } lo ? PaddedCharacterKey(src, lo) : null;
            byte[]? hiBytes = m.KeyHigh is { } hi ? PaddedCharacterKey(src, hi) : null;
            foreach (var (key, recno) in entries)
            {
                if (loBytes is not null && CompareBytesUnsigned(key, loBytes) < 0) continue;
                if (hiBytes is not null && CompareBytesUnsigned(key, hiBytes) > 0) continue;
                set.Add(recno);
            }
            return set;
        }

        foreach (var (keyBytes, recno) in entries)
        {
            var key = src.DecodeKey(keyBytes);
            if (KeyInRange(m, key)) set.Add(recno);
        }
        return set;
    }

    /// <summary>
    /// VFP9 positions GO TOP / GO BOTTOM (and so SCAN) in a single-value SET KEY scope by SEEKing the bound and
    /// stepping at most once; in two cases one END of the scope is not found and the move parks at EOF, although
    /// SKIP and SEEK still reach the rows inside the scope (VFP9-verified):
    /// <list type="bullet">
    ///   <item>the high-key end with a MACHINE bound longer than the key (GO BOTTOM ascending, GO TOP descending);</item>
    ///   <item>the low-key end of a GENERAL / SPANISH order under SET EXACT OFF with trailing blanks in the bound: the
    ///   SEEK needle also matches keys whose heads end there ('Ab' for 'Ab '), which sort before the scope; one of them
    ///   is stepped over, with two first the end is missed (GO TOP ascending, GO BOTTOM descending).</item>
    /// </list>
    /// </summary>
    private bool ScopeEndMisses(int area, bool top)
    {
        if (!_meta.TryGetValue(area, out var m) || !m.KeySet || m.KeyRange
            || m.KeyLow is not { Type: VfpType.Character } single)
            return false;
        using var src = OpenMasterOrder(area);
        if (src is null || !src.IsCharacterKey) return false;
        bool descending = src.Descending != m.OrderReversed;
        bool highEnd = top == descending;
        if (highEnd)
            return IndexKey.CharacterKeyPad(src.CollationName) == 0x20   // MACHINE (a weighted bound is cut)
                && NaturalCharacterKey(src, single).Length > src.KeyLength;
        if (_ctx.Exact || src.CdxTag is null)
            return false;
        var needle = SeekCharacterNeedle(src, single, exact: false);
        if (needle.Exact || needle.TrailingBlanks == 0)
            return false;
        byte[] scopePrefix = ScopeCharacterKey(src, single, exact: false, out _);
        var ascending = OrderedEntries(src);
        if (src.Descending) ascending = ascending.Reverse();
        int outside = 0;
        bool started = false;
        foreach (var (key, _) in ascending)
        {
            if (!started && !needle.Matches(key)) continue;
            started = true;
            if (IsBytePrefix(scopePrefix, key)) return false;
            if (++outside == 2) return true;
        }
        return false;
    }

    /// <summary>Park <paramref name="area"/> at EOF after a GO TOP / GO BOTTOM whose SET KEY scope end VFP9 does not find
    /// (see <see cref="ScopeEndMisses"/>).</summary>
    private void ParkIfScopeEndMissed(int area, bool top)
    {
        if (!ScopeEndMisses(area, top)) return;
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var ord = ActiveOrder(area);
        if (ord is not null) m.OrderPos = ord.Count;
        m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = top; m.Cached = null;
    }

    /// <summary>The stored-key bytes for a CHARACTER SET KEY range bound: the natural CDX weights or IDX
    /// code-page bytes, right-padded to or truncated at the order's key length with the order's pad (blanks for
    /// MACHINE and standalone IDX, 0x00 for a GENERAL / SPANISH tag — VFP9's stored form).</summary>
    private static byte[] PaddedCharacterKey(OrderSource src, VfpValue bound)
    {
        var natural = NaturalCharacterKey(src, bound);
        var key = new byte[src.KeyLength];
        Array.Fill(key, src.KeyPad);
        int copy = Math.Min(natural.Length, src.KeyLength);
        natural.AsSpan(0, copy).CopyTo(key);
        return key;
    }

    /// <summary>The bound of a single-value SET KEY under SET EXACT <paramref name="exact"/> (VFP9-verified): with
    /// <paramref name="equality"/> the stored key must equal it, else it is a byte prefix of the stored key. A CDX tag
    /// builds it per its collation (<see cref="CdxTag.EncodeCharacterScopeKey"/>: under GENERAL / SPANISH and EXACT OFF
    /// the head weights only, like SEEK); a standalone IDX holds raw MACHINE bytes, cut to the key length and, for
    /// EXACT ON, padded with blanks.</summary>
    private static byte[] ScopeCharacterKey(OrderSource src, VfpValue bound, bool exact, out bool equality)
    {
        if (src.CdxTag is { } tag)
        {
            try
            {
                return tag.EncodeCharacterScopeKey(bound.AsString ?? string.Empty, exact, out equality);
            }
            catch (NotSupportedException ex)
            {
                throw new MicroVfpRuntimeException(ex.Message);
            }
        }
        equality = exact;
        if (exact) return PaddedCharacterKey(src, bound);
        var natural = NaturalCharacterKey(src, bound);
        return natural.Length <= src.KeyLength ? natural : natural.AsSpan(0, src.KeyLength).ToArray();
    }

    /// <summary>The natural stored byte needle for a character order. Standalone IDX uses its
    /// resolved table/direct-field encoding; a CDX tag its own stored-key form (MACHINE: table
    /// code-page bytes, GENERAL: collation weights).</summary>
    private static byte[] NaturalCharacterKey(OrderSource src, VfpValue value)
    {
        if (value.IsBinary) return System.Text.Encoding.Latin1.GetBytes(value.AsString);
        string text = value.AsString ?? string.Empty;
        if (src.Idx is not null)
            return src.IdxCharacterEncoding.GetBytes(text);
        return EncodeTagCharacterKey(src.CdxTag!, text);
    }

    /// <summary>The character needle a SEEK, SEEK(), KEYMATCH() or relation reposition on <paramref name="src"/>
    /// matches under SET EXACT <paramref name="exact"/> (VFP9-verified, the same rule for all of them): a CDX tag's
    /// <see cref="CdxTag.CreateCharacterSeekNeedle"/>; a standalone IDX's raw MACHINE bytes (a prefix, or under EXACT ON
    /// the key equal to them plus blanks).</summary>
    private static CharacterSeekNeedle SeekCharacterNeedle(OrderSource src, VfpValue key, bool exact)
    {
        if (key.IsBinary) return new CharacterSeekNeedle(System.Text.Encoding.Latin1.GetBytes(key.AsString), exact, src.KeyPad);
        if (src.Idx is not null)
            return new CharacterSeekNeedle(NaturalCharacterKey(src, key), exact, src.KeyPad);
        try
        {
            return src.CdxTag!.CreateCharacterSeekNeedle(key.AsString ?? string.Empty, exact);
        }
        catch (NotSupportedException ex)
        {
            throw new MicroVfpRuntimeException(ex.Message);
        }
    }

    /// <summary><see cref="CdxTag.EncodeCharacterKey"/>, surfacing an unsupported collating sequence (a tag built by
    /// VFP under e.g. GERMAN) as a catchable runtime error instead of a silent miss.</summary>
    private static byte[] EncodeTagCharacterKey(CdxTag tag, string text)
    {
        try
        {
            return tag.EncodeCharacterKey(text);
        }
        catch (NotSupportedException ex)
        {
            throw new MicroVfpRuntimeException(ex.Message);
        }
    }

    /// <summary>Unsigned, shorter-sorts-first byte comparison (the CDX key sort order).</summary>
    private static int CompareBytesUnsigned(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>True when <paramref name="needle"/> is an unsigned byte prefix of <paramref name="key"/>.</summary>
    private static bool IsBytePrefix(byte[] needle, byte[] key)
    {
        if (needle.Length > key.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (needle[i] != key[i]) return false;
        return true;
    }

    private bool KeyInRange(AreaMeta m, IndexKey key)
    {
        // An undecodable key (Value null / KeyType Unknown) must fail CLOSED — never admit every record.
        if (key.Value is null) return false;
        if (!m.KeyRange)
            return m.KeyLow is { } single && KeyMatchesSingle(key, single);
        if (m.KeyLow is { } lo && CompareKeyToBound(key, lo) < 0) return false;
        if (m.KeyHigh is { } hi && CompareKeyToBound(key, hi) > 0) return false;
        return true;
    }

    private bool KeyMatchesSingle(IndexKey key, VfpValue bound)
        // Non-character keys only (character keys are compared byte-wise in CharKeyInRange); a numeric /
        // date single-value SET KEY is exact value equality.
        => key.Value is not null && CompareKeyToBound(key, bound) == 0;

    /// <summary>Three-way compare a decoded (non-character) index key against a SET KEY bound value in
    /// numeric/date value order. Undecodable/mismatched types compare equal.</summary>
    private int CompareKeyToBound(IndexKey key, VfpValue bound)
    {
        object? kv = key.Value;
        switch (kv)
        {
            case string ks:
                return _ctx.Collation.Compare(ks, bound.AsString ?? string.Empty);
            case int or long or double or decimal:
                return Convert.ToDouble(kv, CultureInfo.InvariantCulture).CompareTo((double)bound.AsNumber);
            case DateOnly kdo:
            {
                var b = bound.ToClr();
                DateOnly? bo = b as DateOnly? ?? (b is DateTime bt ? DateOnly.FromDateTime(bt) : null);
                return bo is { } bb ? kdo.CompareTo(bb) : 0;
            }
            case DateTime kdt:
            {
                var b = bound.ToClr();
                DateTime? bo = b as DateTime? ?? (b is DateOnly bd ? bd.ToDateTime(TimeOnly.MinValue) : null);
                return bo is { } bb ? kdt.CompareTo(bb) : 0;
            }
            default:
                return 0;
        }
    }

    // ─────────────────────────── record-pointer model ───────────────────────────

    private sealed class AreaMeta
    {
        public int RecNo = 1;
        public bool Eof;
        public bool Bof;
        public bool Found;
        public string? Order;
        public DbfRecord? Cached;   // current-record cache (invalidated on move/write).
        public int CachedRec = -1;
        public List<int>? Ordered;  // recnos in the active index order (null ⇒ physical order).
        public List<byte[]>? OrderKeys;
        public Dictionary<int, int>? OrderSlots;
        public bool OrderDescending;
        public bool OrderKeyIsField;
        public bool OrderIsStructural;
        public string? OrderExpression;
        public Func<VfpValue, byte[]>? OrderEncodeKey;
        public string? OrderedFor;  // the tag name the Ordered cache was built for.
        public string? OrderedKeyExpr; // the controlling order's KEY expression (5.5: tag-scoped cache refresh).
        public int OrderPos = -1;   // current position within Ordered (when index-ordered).
        public bool OrderReversed;  // SET ORDER … DESCENDING|ASCENDING override: traverse the tag reversed.
        public Dictionary<string, object?>? OldVals; // OLDVAL() per field (pre-change buffer values).

        // ── row/table BUFFERING (microVFP P1 gap #4) ──
        // Buffering mode 1..5 (1=none/write-through [default], 2=pess-row, 3=opt-row, 4=pess-table,
        // 5=opt-table). Single-process ⇒ pessimistic==optimistic (no conflict detection is fabricated).
        public int Buffering = 1;
        public TableBuffer? Buf;         // pending buffered edits/appends (null ⇒ nothing buffered).
        // CURSORSETPROP free-table property subset (get/set-backed per area; no view model).
        public string? SourceName;       // CURSORGETPROP/SETPROP("SourceName") — defaults to the alias.
        public VfpValue? SourceType;     // CURSORGETPROP/SETPROP("SourceType").
        public string? DatabaseProp;     // CURSORGETPROP/SETPROP("Database").

        // ── SET FILTER (per-area record-visibility predicate; feeds Visible()) ──
        // The filter lives HERE, in per-area state, so it follows USE (fresh AreaMeta) / close (Meta removed)
        // in lockstep and swaps with the data session (the whole _meta map swaps — 5.14). Bare SET FILTER TO
        // clears both fields. FilterExpr is the parsed predicate evaluated against each record as navigation
        // touches it; FilterText is the VFP-normalised text FILTER()/SET("FILTER") report (upper-cased,
        // whitespace-stripped — e.g. `cat = "A"` → `CAT="A"`, oracle-pinned).
        public PrgExpr? FilterExpr;     // the parsed filter predicate (null ⇒ no filter on this area).
        public string? FilterText;      // FILTER()/SET("FILTER") text (VFP-normalised); null ⇒ no filter.

        // ── SET KEY (master-index key-range scope; microVFP P1 gap #1) ──
        public bool KeySet;             // a SET KEY range is active on this area's master index.
        public bool KeyRange;           // true ⇒ [KeyLow, KeyHigh] range; false ⇒ single-value match.
        public VfpValue? KeyLow;        // the single value / range low bound (null ⇒ open low).
        public VfpValue? KeyHigh;       // the range high bound (null ⇒ open high / single-value mode).
        public HashSet<int>? KeyVisible; // cached recnos within the key range (null ⇒ rebuild lazily).

        // ── SET RELATION (this area as PARENT; microVFP P1 gap #2) ──
        public List<Relation>? Relations; // child relations set on THIS area (null ⇒ none).

        // ── LOCATE / CONTINUE (project-review 5.2) — the last LOCATE issued in THIS area, remembered so a
        // following CONTINUE resumes the SAME search (same FOR/WHILE + scope window) from the current
        // position instead of restarting. Per work area. ──
        public bool LocateActive;          // a LOCATE has run in this area ⇒ CONTINUE is legal (else VFP err 42).
        public PrgExpr? LocateFor;         // the remembered FOR predicate (null ⇒ match the first visible record).
        public PrgExpr? LocateWhile;       // the remembered WHILE predicate (stops the walk at the first .F.).
        public HashSet<int>? LocateWindow; // bounded scope (NEXT/RECORD) candidate recnos; null ⇒ ALL/REST (to end).
        public bool LocateWindowFull;      // a NEXT window that reached its full count (exhaust ⇒ park, not EOF).

        // ── multi-index model (microVFP INDEX/ORDER MODEL) ──
        // Non-structural index files opened via SET INDEX TO / USE … INDEX, in OPEN order (full paths).
        // The structural .cdx (auto-opened on USE) is held by WorkArea.Cdx and is NOT listed here.
        public List<string>? ExtraIndexes;
    }

    /// <summary>Effective record count including any buffered appended rows (== the on-disk count when the
    /// area has no buffer, so all non-buffered navigation is unchanged).</summary>
    private int EffCount(int area, VfpSession.WorkArea wa)
    {
        int rc = wa.Table.RecordCount;
        if (_meta.TryGetValue(area, out var m) && m.Buffering > 1 && m.Buf is { } buf) rc += buf.Appends.Count;
        if (DefaultRowFor(area) is { AppendBlank: false } pending) rc = Math.Max(rc, pending.RecNo);
        return rc;
    }

    private AreaMeta Meta(int area)
    {
        if (_meta.TryGetValue(area, out var m)) return m;
        m = new AreaMeta();
        _meta[area] = m;
        GoTopCore(area);   // lazy meta creation is NOT a user move → never fires the relation hook.
        return m;
    }

    private bool Visible(VfpSession.WorkArea wa, int rec, HashSet<int>? keyVisible = null, bool includeDeleted = false)
    {
        // Removed index entries retain a negative-recno slot, avoiding an O(n) shift per DELETE.
        if (rec < 1) return false;
        bool deleted;
        if (rec > wa.Table.RecordCount)
        {
            if (!_meta.TryGetValue(wa.Area, out var am) || am.Buf is not { } ab) return false;
            int ai = rec - wa.Table.RecordCount - 1;
            if (ai < 0 || ai >= ab.Appends.Count) return false;
            deleted = ab.Appends[ai].Deleted;
        }
        else
        {
            // With SET DELETED OFF (or RECALL's inclusive walk), the mark cannot hide
            // this row. Avoid a DBF flag read on every SKIP and again in the SCAN body.
            deleted = !includeDeleted && _ctx.Deleted &&
                (_meta.TryGetValue(wa.Area, out var bufferedMeta)
                && bufferedMeta.Buf?.Rows.TryGetValue(rec, out var edit) == true
                && edit.DeletedOverride is bool bufferedDeleted
                    ? bufferedDeleted : wa.Table.IsRecordDeleted(rec - 1));
        }
        if (!includeDeleted && _ctx.Deleted && deleted) return false;
        // SET KEY / SET FILTER: per-area visibility gates layered on top of SET DELETED, composing by AND
        // (a record shows only when NOT deleted-hidden AND in the SET KEY range AND the filter passes). Use
        // TryGetValue — never Meta() — to avoid re-entering GoTop while a navigation loop is already inside
        // Visible(). Buffered appends must pass the same visibility gates.
        if (_meta.TryGetValue(wa.Area, out var m))
        {
            // SET KEY: only records whose MASTER-index key falls in the active key range are visible. The
            // visible set is derived from the controlling tag's entries once, then cached (invalidated on a
            // data change / order change / SET KEY change).
            if (m.KeySet)
            {
                keyVisible ??= m.KeyVisible ??= BuildKeyVisible(wa, m);
                if (!keyVisible.Contains(rec))
                    return false;
            }
            // SET FILTER: the area's filter predicate is the FINAL visibility gate — evaluated against this
            // record through the expression engine (PassesFilter). Kept last because it is the costliest
            // check (a full expression evaluation) and the cheap deleted/key gates have already run. A direct
            // GOTO <n> / RECNO() / RECCOUNT() bypass Visible() entirely, so they still land on / count a
            // filtered-out row (VFP9-verified).
            if (m.FilterExpr is not null && !PassesFilter(wa, m, rec))
                return false;
        }
        return true;
    }

    // Re-entrancy guard for SET FILTER evaluation: while a filter predicate is being evaluated, any nested
    // Visible() call (a filter UDF that itself navigates) treats records as visible so it can never recurse
    // into the same filter. Single flag (not per-area) — a filter that navigates a DIFFERENT area during its
    // own evaluation is a pathological edge; short-circuiting its filter there is acceptable and terminates.
    private bool _inFilterEval;

    /// <summary>Evaluate the area's <c>SET FILTER</c> predicate against record <paramref name="rec"/> WITHOUT
    /// disturbing the user-visible pointer: temporarily re-base the pointer (and, only when the filter's area
    /// is not current, select it) so the engine resolves the filter's bare field references against
    /// <paramref name="rec"/> of <paramref name="wa"/>, evaluate, then restore everything. A non-logical /
    /// <c>.NULL.</c> result HIDES the record — VFP filter semantics. A VFP error raised while evaluating (error 1
    /// from an unknown function, an error inside a filter UDF) propagates out of the navigating command, as in
    /// VFP9; any other evaluation failure hides the record. The pointer/cache save-restore mirrors
    /// <see cref="ExecCopyToArray"/>'s scan probe.</summary>
    private bool PassesFilter(VfpSession.WorkArea wa, AreaMeta m, int rec)
    {
        if (_inFilterEval) return true;                 // nested filter eval ⇒ treat as visible (never recurse).
        int savArea = Session.CurrentArea;
        int savRec = m.RecNo; bool savEof = m.Eof, savBof = m.Bof;
        var savCached = m.Cached; int savCachedRec = m.CachedRec;
        _inFilterEval = true;
        try
        {
            if (wa.Area != savArea) Session.SelectArea(wa.Area);   // bare fields must resolve against wa's area.
            m.RecNo = rec; m.Eof = false; m.Bof = false; m.Cached = null;
            VfpValue v;
            try { v = Eval(m.FilterExpr!); }
            catch (Exception ex) when (ex is not MicroVfpRuntimeException and not IVfpErrorCode { VfpErrorNumber: not null })
            { return false; }                           // a non-VFP failure hides the row; a VFP error propagates.
            return Truth(v);
        }
        finally
        {
            _inFilterEval = false;
            m.RecNo = savRec; m.Eof = savEof; m.Bof = savBof;
            m.Cached = savCached; m.CachedRec = savCachedRec;
            if (wa.Area != savArea) Session.SelectArea(savArea);
        }
    }

    /// <summary>Resolve the active index tag for <paramref name="area"/> (from <see cref="AreaMeta.Order"/>),
    /// building/refreshing the cached recno sequence in index order; null ⇒ navigate physically.</summary>
    private List<int>? ActiveOrder(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null || string.IsNullOrEmpty(m.Order)) return null;
        if (m.Ordered is not null && m.OrderedFor == m.Order) return m.Ordered;

        using var src = OpenOrderSource(area, m.Order!);   // structural cdx, extra cdx, or standalone idx.
        if (src is null) return null;
        // Ordinary traversal needs only recnos. Retaining every key and a dictionary here
        // dwarfs the order itself; materialize them only for removed-entry navigation.
        var ordered = OrderedEntries(src).Select(e => e.Recno).ToList();
        if (m.OrderReversed) ordered.Reverse();
        m.OrderKeys = null;
        m.OrderSlots = null;
        m.OrderDescending = src.Descending != m.OrderReversed;
        m.Ordered = ordered;
        m.OrderedFor = m.Order;
        m.OrderIsStructural = wa.Cdx?.Tag(m.Order!) is not null;
        // Remember the controlling key expression so a later REPLACE can decide (tag-scoped, 5.5) whether it
        // touched the order key — a CDX tag exposes it; a standalone .idx does not (null ⇒ drop on any REPLACE).
        m.OrderedKeyExpr = src.CdxTag?.KeyExpression?.Trim();
        m.OrderKeyIsField = ColumnIndex(wa.Table, m.OrderedKeyExpr ?? "") >= 0;
        m.OrderExpression = src.CdxTag?.KeyExpression ?? src.Idx!.Header.KeyExpression;
        // Encoding uses immutable header metadata only, never the source's owned stream.
        bool character = src.IsCharacterKey;
        var keyType = src.KeyType;
        int keyLength = src.KeyLength;
        m.OrderEncodeKey = value => character ? PaddedCharacterKey(src, value)
            : IndexKey.Encode(value.ToClr(), keyType) is { Length: > 0 } bytes ? bytes : new byte[keyLength];
        m.OrderPos = -1;
        return m.Ordered;
    }

    private void GoTopCore(int area)
    {
        var m = _meta.TryGetValue(area, out var mm) ? mm : (_meta[area] = new AreaMeta());
        var wa = Session.AreaAt(area);
        if (wa is null) { m.RecNo = 0; m.Eof = true; m.Bof = true; m.Cached = null; m.OldVals = null; return; }
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            for (int p = 0; p < ord.Count; p++)
                if (InRange(wa, ord[p]) && Visible(wa, ord[p]))
                { m.OrderPos = p; m.RecNo = ord[p]; m.Eof = false; m.Bof = false; return; }
            m.OrderPos = ord.Count; m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = true; return;
        }
        int rc = EffCount(area, wa);   // GO TOP may land on a buffered append when no on-disk row is visible.
        for (int i = 1; i <= rc; i++)
            if (Visible(wa, i)) { m.RecNo = i; m.Eof = false; m.Bof = false; return; }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = true;
    }

    private void GoBottomCore(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            for (int p = ord.Count - 1; p >= 0; p--)
                if (InRange(wa, ord[p]) && Visible(wa, ord[p]))
                { m.OrderPos = p; m.RecNo = ord[p]; m.Eof = false; m.Bof = false; return; }
            m.OrderPos = ord.Count; m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = true; return;
        }
        int rc = EffCount(area, wa);   // GO BOTTOM lands on the last buffered appended row when present.
        for (int i = rc; i >= 1; i--)
            if (Visible(wa, i)) { m.RecNo = i; m.Eof = false; m.Bof = false; return; }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = true;
    }

    private void GoRecordCore(int area, int rec)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        int rc = wa is null ? 0 : EffCount(area, wa);   // buffered appends are addressable (GO n past the disk count).
        m.RecNo = rec;
        m.Eof = rec > rc;
        m.Bof = rec < 1;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        // Re-anchor the index position to this physical record (so a following SKIP walks index order).
        var ord = ActiveOrder(area);
        if (ord is not null) m.OrderPos = OrderSlot(m, rec);
    }

    private static bool InRange(VfpSession.WorkArea wa, int rec) => rec >= 1 && rec <= wa.Table.RecordCount;

    private void SkipCore(int area, int count)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        if (count == 0) return;

        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            int dirO = count > 0 ? 1 : -1, stepsO = Math.Abs(count);
            int pos = m.OrderPos;
            if (pos < 0 || (pos < ord.Count && ord[pos] < 0 && !m.OrderKeyIsField))
            {
                if (pos < 0 && !m.Eof) EnsureOrderSlots(m);
                pos = OrderSlot(m, m.RecNo);
                if (pos >= 0 && ord[pos] < 0 && !m.OrderKeyIsField) pos = -1;
                if (pos < 0 && m.RecNo >= 1 && m.RecNo <= EffCount(area, wa))
                {
                    // A deletion-dependent FOR can remove the current entry. VFP resumes at
                    // its key's insertion point, in either direction, rather than going to TOP.
                    pos = MissingOrderInsertionPoint(area, m) - (dirO > 0 ? 1 : 0);
                    ord = m.Ordered!;
                }
                else if (pos < 0 && m.Eof) pos = ord.Count;
                else if (pos < 0) { GoTopCore(area); pos = m.OrderPos; }
            }
            while (stepsO > 0)
            {
                pos += dirO;
                if (pos >= ord.Count) { m.OrderPos = ord.Count; m.RecNo = EffCount(area, wa) + 1; m.Eof = true; m.Bof = false; return; }
                if (pos < 0) { m.OrderPos = -1; m.Bof = true; m.Eof = false; GoTopCore(area); m.Bof = true; return; }
                if (InRange(wa, ord[pos]) && Visible(wa, ord[pos])) stepsO--;
            }
            m.OrderPos = pos; m.RecNo = ord[pos]; m.Eof = false; m.Bof = false;
            return;
        }

        int rc = EffCount(area, wa);   // SKIP can walk onto/through buffered appended rows.
        int dir = count > 0 ? 1 : -1, steps = Math.Abs(count), cur = m.RecNo;
        while (steps > 0)
        {
            cur += dir;
            if (cur > rc) { m.RecNo = rc + 1; m.Eof = true; m.Bof = false; return; }
            if (cur < 1) { m.RecNo = 1; m.Bof = true; m.Eof = false; GoTopCore(area); m.Bof = true; return; }
            if (Visible(wa, cur)) steps--;
        }
        m.RecNo = cur; m.Eof = false; m.Bof = false;
    }

    private int MissingOrderInsertionPoint(int area, AreaMeta m)
    {
        if (m.OrderKeys is null)
        {
            // Read the refreshed live tree: removed expression keys may have changed value,
            // so their old tombstones cannot take part in this binary search.
            using var src = OpenOrderSource(area, m.Order!, resolveHosted: false);
            var entries = OrderedEntries(src!).ToList();
            if (m.OrderReversed) entries.Reverse();
            m.Ordered = entries.Select(e => e.Recno).ToList();
            m.OrderKeys = entries.Select(e => e.Key).ToList();
            m.OrderSlots = null;
            m.OrderPos = -1;
            EnsureOrderSlots(m);
        }
        int selected = Session.CurrentArea;
        VfpValue value;
        try
        {
            Session.SelectArea(area);
            value = EvalTextLoud(m.OrderExpression!);
        }
        finally { Session.SelectArea(selected); }
        byte[] key = m.OrderEncodeKey!(value);
        bool descending = m.OrderDescending;
        // VFP uses the current expression value for a missing expression key (including
        // IIF(DELETED(), ...)); ordinary field keys keep their original cached slot.
        int low = 0, high = m.Ordered!.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            int compare = CompareBytesUnsigned(m.OrderKeys![mid], key);
            if (compare == 0) compare = Math.Abs(m.Ordered[mid]).CompareTo(m.RecNo);
            if (descending ? compare > 0 : compare < 0) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private void RefreshDeletionOrder(string path, DbfWriter writer)
    {
        foreach (var wa in Session.OpenAreas)
        {
            if (!SamePath(wa.Table.SourcePath, path) || !_meta.TryGetValue(wa.Area, out var m)) continue;
            m.KeyVisible = null;
            if (m.Ordered is null) continue;
            // Additional indexes do not supply structural-tag deltas; refresh them normally.
            if (!m.OrderIsStructural)
            {
                m.Ordered = null;
                m.OrderPos = -1;
                continue;
            }
            foreach (var change in writer.DeletionIndexChanges ?? [])
            {
                if (!change.Tag.Equals(m.Order, StringComparison.OrdinalIgnoreCase)) continue;
                int slot = OrderSlot(m, change.Recno);
                if (change.Key is null)
                {
                    if (slot >= 0) m.Ordered[slot] = -change.Recno;
                }
                else if (slot >= 0 && (m.OrderKeyIsField ||
                    m.OrderKeys is not null && m.OrderKeys[slot].AsSpan().SequenceEqual(change.Key)))
                    m.Ordered[slot] = change.Recno;
                else
                {
                    m.Ordered = null;
                    m.OrderKeys = null;
                    m.OrderSlots = null;
                    m.OrderPos = -1;
                    break;
                }
            }
        }
    }

    // ── navigation wrappers (microVFP P1 gap #2) — every USER parent move goes through one of these and,
    // after positioning THIS area, repositions any related child areas (recursively, for chained
    // relations). The *Core methods hold the unchanged navigation logic and are used for internal,
    // NON-move calls (lazy Meta creation, a mid-SKIP BOF snap, the reposition itself) so those never
    // re-fire the hook. RepositionChildren early-returns when the area has no relations, so the ~2100
    // relation-free tests pay only a dictionary lookup. ─────────────────────────────────────────────
    // Batch 4: every USER nav move first flushes any DEFERRED fast-append refresh for the area, so GO
    // BOTTOM/TOP/n, SKIP and SEEK see the just-appended rows (and a loop-internal SKIP after a scan-body
    // INSERT into the scanned table stays correct). No-op when nothing is pending.
    private void GoTop(int area) { if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area); MaybeAutoCommitRow(area); GoTopCore(area); ParkIfScopeEndMissed(area, top: true); RepositionChildren(area); }
    private void GoBottom(int area) { if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area); MaybeAutoCommitRow(area); GoBottomCore(area); ParkIfScopeEndMissed(area, top: false); RepositionChildren(area); }
    private void GoRecord(int area, int rec) { if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area); MaybeAutoCommitRow(area); GoRecordCore(area, rec); RepositionChildren(area); }

    private void Skip(int area, int count)
    {
        if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area);
        MaybeAutoCommitRow(area);
        SkipCore(area, count);
        ApplyOneToManyBound(area);   // SET SKIP: clamp a one-to-many child to its parent-key group.
        RepositionChildren(area);
    }

    private bool DoSeek(VfpValue key, int area, string? tag)
    {
        if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends(area);
        MaybeAutoCommitRow(area);
        bool ok = DoSeekCore(key, area, tag);
        RepositionChildren(area);
        return ok;
    }

    private bool DoSeekCore(VfpValue key, int area, string? tag, bool relationSeek = false)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) { m.Found = false; return false; }
        string? identity = tag ?? DefaultOrderIdentity(area);
        if (identity is null) { m.Found = false; return false; }

        if (key.Type == VfpType.Character && !key.IsBinary)
            key = VfpValue.Character(VfpSessionText.ForTable(key.AsString, ConnectionEncoding(), wa.Table.Encoding,
                byteString: _ctx.Encoding?.CodePage == System.Text.Encoding.Latin1.CodePage));

        using var src = OpenOrderSource(area, identity);
        if (src is null) { m.Found = false; return false; }

        // A character value seeks with the needle VFP9 builds for SEEK, SEEK(), KEYMATCH(), INDEXSEEK() and the
        // relation reposition alike: SET EXACT ON matches the whole key (value plus pad), EXACT OFF a prefix (under
        // GENERAL / SPANISH the head weights only) — so under EXACT ON SEEK 'Stra' misses 'Straße' and a short value
        // misses a COMPOSITE key (relate on `cust_id`, tag `cust_id+ord_id`: hackfox quirk 1), while EXACT OFF keeps
        // the prefix match. The raw-byte needle also serves a composite character tag, which the value-typed
        // Seek(object) cannot (its expression resolves to KeyType.Unknown).
        CharacterSeekNeedle? characterNeedle = src.IsCharacterKey && key.Type == VfpType.Character
            ? SeekCharacterNeedle(src, key, _ctx.Exact)
            : null;

        uint? recno = null;
        if (src.CdxTag is { } t)
        {
            recno = characterNeedle is not null
                ? t.Seek(characterNeedle)
                : t.Seek(key.ToClr() ?? string.Empty);
        }
        else if (src.Idx is not null)
        {
            recno = SeekIdx(src, key, characterNeedle);
        }

        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        if (recno is uint r && r >= 1 && r <= (uint)wa.Table.RecordCount)
        {
            int hit = (int)r;
            bool cdxSeekNeedsOrderedLanding = src.CdxTag is not null && (src.CdxTag.Descending != m.OrderReversed);
            if (cdxSeekNeedsOrderedLanding)
            {
                // CdxTag.Seek walks stored ascending bytes. For a descending controlling order it is only an
                // existence probe; duplicate-key positioning must land on the first matching (for SEEK: visible)
                // entry in that order — the relation reposition too (VFP9-verified on a descending child tag).
                int vis = FirstVisibleSeekMatch(src, key, wa, characterNeedle, requireVisible: !relationSeek);
                if (vis >= 1)
                    hit = vis;
                else
                {
                    m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = false; m.Found = false; m.Cached = null;
                    return false;
                }
            }
            // Deleted/filtered hits advance to the first visible matching entry. A single-value SET KEY only
            // steps past ONE out-of-scope hit (FirstVisibleSeekMatch); a second makes SEEK miss even when a later
            // match is in scope. VFP9 checks this before DELETED/FILTER. Relation repositioning bypasses visibility.
            if (!relationSeek && !Visible(wa, hit))
            {
                int vis = FirstVisibleSeekMatch(src, key, wa, characterNeedle);
                if (vis >= 1)
                {
                    m.RecNo = vis; m.Eof = false; m.Bof = false; m.Found = true; m.Cached = null;
                    var ordv = ActiveOrder(area);
                    if (ordv is not null) m.OrderPos = ordv.IndexOf(vis);
                    return true;
                }
                m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = false; m.Found = false; m.Cached = null;
                return false;
            }
            m.RecNo = hit; m.Eof = false; m.Bof = false; m.Found = true; m.Cached = null;
            var ord = ActiveOrder(area);
            if (ord is not null) m.OrderPos = ord.IndexOf(hit);
            return true;
        }

        // Miss: SET NEAR ON leaves the pointer on the record just past where the key would sort
        // (s4g268 — SEEK only, not relation repositioning); NEAR OFF (default) parks at EOF. A needle longer than the
        // key (a MACHINE value wider than the field) parks at EOF under NEAR ON too (VFP9-verified).
        if (_setNear && !relationSeek && !(characterNeedle is { } cn && cn.Bytes.Length > src.KeyLength))
        {
            int nearRec = NearRecord(src, key, area, characterNeedle);
            if (nearRec >= 1)
            {
                m.RecNo = nearRec; m.Eof = false; m.Bof = false; m.Found = false; m.Cached = null;
                var ord = ActiveOrder(area);
                if (ord is not null) m.OrderPos = ord.IndexOf(nearRec);
                return false;
            }
        }
        m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Found = false; m.Cached = null;
        return false;
    }

    /// <summary>After a SEEK index hit lands on a record HIDDEN by the central <see cref="Visible"/> gate
    /// (SET DELETED / SET KEY / SET FILTER), find the first VISIBLE record among the index entries that still
    /// MATCH the seek key — walking the controlling order. VFP9 permits only one step past an entry outside a
    /// single-value SET KEY scope, even if those entries are also deleted/filtered out. Returns the recno, or −1
    /// when the scope cannot be reached or every matching entry is hidden (⇒ the caller parks the
    /// pointer at EOF with FOUND()=.F.). Works over a CDX tag OR a standalone <c>.idx</c> source. Without
    /// <paramref name="requireVisible"/> it is the first matching entry in the controlling order.</summary>
    private int FirstVisibleSeekMatch(OrderSource src, VfpValue key, VfpSession.WorkArea wa, CharacterSeekNeedle? characterNeedle,
        bool requireVisible = true)
    {
        var entries = OrderedEntries(src);
        if (_meta.TryGetValue(wa.Area, out var m) && m.OrderReversed)
            entries = entries.Reverse();

        HashSet<int>? scope = null;
        if (requireVisible && m is { KeySet: true, KeyRange: false })
            scope = m.KeyVisible ??= BuildKeyVisible(wa, m);
        int outsideScope = 0;
        foreach (var (keyBytes, recno) in entries)
        {
            if (recno < 1 || recno > wa.Table.RecordCount) continue;
            if (!SeekEntryMatches(keyBytes, key, src, characterNeedle)) continue;
            if (scope is not null && !scope.Contains(recno) && ++outsideScope == 2)
                return -1;
            if (!requireVisible || Visible(wa, recno)) return recno;
        }
        return -1;
    }

    /// <summary>Does an index entry's stored key MATCH the SEEK needle the same way <see cref="DoSeekCore"/>'s
    /// hit did? Character keys match by the needle's rule (<see cref="CharacterSeekNeedle.Matches"/>: prefix or,
    /// under SET EXACT ON, the whole key); every other type matches by VALUE equality. Used to walk the equal-key
    /// run when the first hit is filtered/deleted out.</summary>
    private bool SeekEntryMatches(byte[] keyBytes, VfpValue key, OrderSource src, CharacterSeekNeedle? characterNeedle)
    {
        if (src.IsCharacterKey && key.Type == VfpType.Character)
            return (characterNeedle ?? SeekCharacterNeedle(src, key, _ctx.Exact)).Matches(keyBytes);
        if (src.CdxTag is { } t)
            return CompareDecodedToValue(IndexKey.Decode(keyBytes, t.KeyType), t.KeyType, key) == 0;
        if (src.Idx is not null)
            return SeekValueMatches(IndexKey.Decode(keyBytes, src.IdxKeyType), src.IdxKeyType, key);
        return false;
    }

    /// <summary>Seek <paramref name="key"/> in a standalone <c>.idx</c> order. Character keys match the raw stored
    /// bytes against <paramref name="characterNeedle"/> (see <see cref="SeekCharacterNeedle"/>); other types decode.</summary>
    private static uint? SeekIdx(OrderSource src, VfpValue key, CharacterSeekNeedle? characterNeedle)
    {
        if (src.Idx is null) return null;
        if (VfpRuntime.UnsupportedIndexFunction(src.Idx.Header.KeyExpression) is { } function
            && VfpFunctionNames.IsBuiltin(function))
            throw new NotSupportedException($"Index '{src.Name}': function {function}() is not supported by the index engine for SEEK.");
        foreach (var e in src.Idx.EnumerateEntries())
        {
            if (characterNeedle is not null)
            {
                if (characterNeedle.Matches(e.Key))
                    return e.RecordNumber;
                continue;
            }
            var decoded = IndexKey.Decode(e.Key, src.IdxKeyType);
            if (SeekValueMatches(decoded, src.IdxKeyType, key))
                return e.RecordNumber;
        }
        return null;
    }

    private static bool SeekValueMatches(IndexKey decoded, IndexKeyType type, VfpValue key)
    {
        switch (type)
        {
            case IndexKeyType.Character:
                string dk = decoded.AsString ?? string.Empty;
                string want = key.AsString ?? string.Empty;
                return dk.StartsWith(want.TrimEnd(), StringComparison.Ordinal);   // prefix seek.
            case IndexKeyType.Integer:
                return decoded.AsInt32 is int di && di == (int)key.AsNumber;
            case IndexKeyType.Numeric:
                return decoded.AsDouble is double dd && dd == (double)key.AsNumber;
            case IndexKeyType.Date:
            {
                var b = key.ToClr();
                DateOnly? bo = b as DateOnly? ?? (b is DateTime bt ? DateOnly.FromDateTime(bt) : null);
                return decoded.AsDate is { } dv && bo is { } bb && dv == bb;
            }
            default:
                return false;
        }
    }

    /// <summary>SET NEAR: the record the pointer parks on after a failed SEEK — the first entry that sorts
    /// AFTER the seek key in the CONTROLLING order that is in-range and visible; −1 when past the end.</summary>
    private int NearRecord(OrderSource src, VfpValue key, int area, CharacterSeekNeedle? characterNeedle)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return -1;
        bool reversed = _meta.TryGetValue(area, out var m) && m.OrderReversed;
        bool descending = src.Descending;
        if (reversed) descending = !descending;

        // Walk the ACTUAL (possibly-overridden) traversal order — the SAME sequence ActiveOrder builds:
        // the order's stored order, reversed when a SET ORDER … ASCENDING|DESCENDING override flipped it.
        // Scanning the un-reversed base with the flipped comparison would return the FARTHEST match, not
        // the nearest (s4g268: NEAR parks on the record JUST past where the key would sort).
        var entries = OrderedEntries(src);
        if (reversed) entries = entries.Reverse();

        foreach (var (keyBytes, recno) in entries)
        {
            int cmp = CompareStoredKeyToSeek(keyBytes, key, src, characterNeedle);
            bool after = descending ? cmp < 0 : cmp > 0;   // sorts strictly after the seek key.
            if (after && recno >= 1 && recno <= wa.Table.RecordCount && Visible(wa, recno))
                return recno;
        }
        return -1;
    }

    /// <summary>Three-way compare a STORED key (index bytes) against the SEEK value: &gt;0 when the stored
    /// key sorts after the seek value, &lt;0 before, 0 equal — in the key's own encoding.</summary>
    private int CompareStoredKeyToSeek(byte[] keyBytes, VfpValue key, OrderSource src, CharacterSeekNeedle? characterNeedle)
    {
        // Character keys: the raw stored bytes against where the needle sorts (unsigned byte order; under SET EXACT
        // ON the needle plus pad, so a key the needle is only a prefix of sorts after it).
        if (src.IsCharacterKey && key.Type == VfpType.Character)
        {
            byte[] needle = (characterNeedle ?? SeekCharacterNeedle(src, key, _ctx.Exact)).SortKey(src.KeyLength);
            int n = Math.Min(keyBytes.Length, needle.Length);
            for (int i = 0; i < n; i++) { int d = keyBytes[i] - needle[i]; if (d != 0) return d; }
            return keyBytes.Length - needle.Length;
        }
        if (src.Idx is not null)
            return CompareDecodedToValue(IndexKey.Decode(keyBytes, src.IdxKeyType), src.IdxKeyType, key);
        var t = src.CdxTag!;
        return CompareDecodedToValue(IndexKey.Decode(keyBytes, t.KeyType), t.KeyType, key);
    }

    private static int CompareDecodedToValue(IndexKey decoded, IndexKeyType type, VfpValue key) => type switch
    {
        IndexKeyType.Integer => (decoded.AsInt32 ?? 0).CompareTo((int)key.AsNumber),
        IndexKeyType.Numeric => (decoded.AsDouble ?? 0d).CompareTo((double)key.AsNumber),
        IndexKeyType.Character => string.CompareOrdinal(decoded.AsString ?? string.Empty, key.AsString ?? string.Empty),
        IndexKeyType.Date => (decoded.AsDate ?? default).CompareTo(
            key.ToClr() is DateOnly d ? d : (key.ToClr() is DateTime dt ? DateOnly.FromDateTime(dt) : default)),
        IndexKeyType.DateTime => (decoded.AsDateTime ?? default).CompareTo(key.AsDateTime),
        _ => 0,
    };

}
