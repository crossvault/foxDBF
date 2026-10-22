using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Shared scaffolding for the Phase-1b VFP-SQL session / SELECT-executor tests:
/// a throwaway temp DBF+CDX fixture (NEVER a committed fixture), an independent brute-force
/// SELECT ORACLE (a naive full-scan interpreter over <see cref="DbfTable"/>, deliberately a
/// DIFFERENT code path from the real executor: <see cref="VfpExpression.Evaluate"/> + LINQ vs.
/// the executor's <c>Compile</c> + Rushmore), and value-normalizing row comparers.
/// </summary>
internal static partial class SqlTestSupport
{
    /// <summary>One source row of the canonical test table (the ground truth the tests reason from).</summary>
    internal readonly record struct Person(
        int Id, string Name, string City, decimal Amount, DateOnly Hired, bool Active, bool Deleted);

    /// <summary>
    /// The canonical 10-row dataset. Crafted to exercise: char/numeric/date predicates, AND/OR/NOT,
    /// DISTINCT + GROUP BY (duplicate "Brown" / repeated cities / repeated amounts), aggregates,
    /// ORDER BY (collation), a DELETED row (#7), and SQL '=' order-independence ("Smith" vs "Sm").
    /// </summary>
    internal static readonly Person[] People =
    {
        new(1,  "Smith",    "Berlin",  100.00m, new(2001, 1, 15), true,  false),
        new(2,  "Sm",       "Berlin",  200.00m, new(2002, 3, 20), false, false),
        new(3,  "Smithson", "Munich",  300.00m, new(2003, 5, 10), true,  false),
        new(4,  "Jones",    "Berlin",  150.00m, new(2001, 7,  1), true,  false),
        new(5,  "Brown",    "Munich",  250.00m, new(2004, 9, 30), false, false),
        new(6,  "Brown",    "Hamburg", 250.00m, new(2005,11, 11), true,  false),
        new(7,  "Adams",    "Hamburg", 999.99m, new(2006, 2,  2), true,  true),   // DELETED
        new(8,  "Clark",    "Munich",  300.00m, new(2007, 4,  4), false, false),
        new(9,  "Davis",    "Berlin",   50.00m, new(2008, 6,  6), true,  false),
        new(10, "Evans",    "Hamburg", 400.00m, new(2009, 8,  8), false, false),
    };

    // ---- temp fixture ---------------------------------------------------------------------

    /// <summary>A throwaway temp directory holding one or more temp tables; deleted on Dispose.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foxdbf_sql_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Writes the canonical dataset to <paramref name="dbfPath"/> with a structural CDX
    /// (tags on ID / NAME / CITY / AMOUNT / HIRED) and marks row 7 deleted.
    /// </summary>
    internal static void CreatePersonTable(string dbfPath, bool withIndex = true)
    {
        using (var w = DbfWriter.Create(dbfPath, new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("NAME", 'C', 20),
            new DbfColumnDef("CITY", 'C', 10),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
            new DbfColumnDef("HIRED", 'D', 8),
            new DbfColumnDef("ACTIVE", 'L', 1),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var p in People)
                w.AppendRecord(p.Id, p.Name, p.City, p.Amount, p.Hired, p.Active);

            if (withIndex)
                foreach (var (tag, key) in new[] { ("TID", "ID"), ("TNAME", "NAME"), ("TCITY", "CITY"), ("TAMT", "AMOUNT"), ("THIRED", "HIRED") })
                    w.CreateTag(new CdxTagDefinition(tag, key));

            // delete row 7 (1-based) → physical index 6
            w.Delete(6);
            w.Flush();
        }
    }

    // ---- IRowContext over a DbfRecord (for the oracle) ------------------------------------

    private sealed class RecRow : IRowContext
    {
        private readonly DbfRecord _r;
        private readonly bool _empty;
        /// <summary>Representative of an EMPTY aggregate group (all fields null).</summary>
        public static readonly RecRow Empty = new();
        private RecRow() { _empty = true; }
        public RecRow(DbfRecord r, int recNo, int recCount) { _r = r; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => !_empty && _r.IsDeleted;
        public object? GetField(string name) => _empty ? null : _r.GetExpressionValue(name);
    }

    // ---- the independent brute-force oracle -----------------------------------------------

    private static readonly Regex AggRx =
        new(@"^\s*(SUM|AVG|COUNT|MIN|MAX)\s*\((?<arg>.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static bool IsAggregate(SelectItem it, out string func, out string? arg)
    {
        func = ""; arg = null;
        if (it.AggregateStarFunction is not null) { func = it.AggregateStarFunction.ToUpperInvariant(); arg = "*"; return true; }
        if (it.Expression is null) return false;
        var m = AggRx.Match(it.Expression.Text);
        if (!m.Success) return false;
        string a = m.Groups["arg"].Value;
        // exclude scalar MIN(a,b)/MAX(a,b): a top-level comma means it is NOT a SQL aggregate.
        if (HasTopLevelComma(a)) return false;
        func = m.Groups[1].Value.ToUpperInvariant(); arg = a.Trim();
        return true;
    }

    private static bool HasTopLevelComma(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Executes <paramref name="select"/> over <paramref name="table"/> the naive way and returns the
    /// schema-less rows (each <c>object?[]</c>, numerics normalized). Supports the Phase-1b subset:
    /// single FROM table, WHERE, GROUP BY + COUNT/SUM/AVG/MIN/MAX + HAVING, ORDER BY (ordinal/expr,
    /// ASC/DESC), DISTINCT, TOP. JOINs / multi-table are out of scope (the executor rejects them).
    /// </summary>
    internal static List<object?[]> RunOracle(DbfTable table, EvaluationContext ctx, SelectStatement select)
    {
        // 1) surviving records via a full scan + interpreted WHERE.
        var rows = new List<RecRow>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var rc = new RecRow(rec, recno, table.RecordCount);
            if (select.Where is null) { rows.Add(rc); continue; }
            var v = select.Where.Evaluate(rc, ctx);
            if (v.Type == VfpType.Logical && v.AsLogical) rows.Add(rc);
        }

        bool aggregate = select.GroupBy.Count > 0 || select.Items.Any(it => IsAggregate(it, out _, out _));

        List<object?[]> outRows;
        List<RecRow> rowOrderRep; // a representative row per output row (for ORDER BY by expression)
        if (!aggregate)
        {
            outRows = rows.Select(r => Project(r, select.Items, ctx, table)).ToList();
            rowOrderRep = rows;
        }
        else
        {
            // group by the tuple of GROUP BY expression values
            var groups = new List<(List<object?> Key, List<RecRow> Members)>();
            if (select.GroupBy.Count == 0)
            {
                // whole-table aggregate: a single group of all surviving rows.
                groups.Add((new List<object?>(), rows));
            }
            else
            {
                foreach (var r in rows)
                {
                    var key = select.GroupBy.Select(g => Norm(ToClr(g.Evaluate(r, ctx)))).ToList();
                    int idx = groups.FindIndex(x => KeyEq(x.Key, key, ctx));
                    if (idx < 0) { groups.Add((key, new List<RecRow> { r })); }
                    else groups[idx].Members.Add(r);
                }
            }

            outRows = new List<object?[]>();
            rowOrderRep = new List<RecRow>();
            foreach (var (key, members) in groups)
            {
                // empty whole-table group still emits one row; never index members[0] unconditionally.
                var rep = members.Count > 0 ? members[0] : RecRow.Empty;
                if (select.Having is not null)
                {
                    var h = EvalAggregateExpr(select.Having, members, rep, ctx, table);
                    if (!(h.Type == VfpType.Logical && h.AsLogical)) continue;
                }
                outRows.Add(select.Items.Select(it => ProjectAggregate(it, members, rep, ctx, table)).ToArray());
                rowOrderRep.Add(rep);
            }
        }

        // carry (row, rep) pairs together so ORDER BY / TOP can re-evaluate row-level keys.
        var pairs = outRows.Select((row, i) => (row, rep: rowOrderRep[i])).ToList();

        // ORDER BY (aggregate ORDER BY by a select item reads the computed output column).
        if (select.OrderBy.Count > 0)
        {
            IOrderedEnumerable<(object?[] row, RecRow rep)>? ordered = null;
            for (int k = 0; k < select.OrderBy.Count; k++)
            {
                var ob = select.OrderBy[k];
                var keySel = OrderKeySel(ob, select.Items, table, aggregate, ctx);
                ordered = k == 0
                    ? (ob.Descending ? pairs.OrderByDescending(x => x, OneShot(keySel, ctx)) : pairs.OrderBy(x => x, OneShot(keySel, ctx)))
                    : (ob.Descending ? ordered!.ThenByDescending(x => x, OneShot(keySel, ctx)) : ordered!.ThenBy(x => x, OneShot(keySel, ctx)));
            }
            pairs = ordered!.ToList();
        }

        // DISTINCT (collation-aware dedup)
        if (select.Distinct)
        {
            var seen = new List<(object?[] row, RecRow rep)>();
            foreach (var p in pairs)
                if (!seen.Any(s => RowEq(s.row, p.row, ctx))) seen.Add(p);
            pairs = seen;
        }

        // TOP n — keep all rows whose full ORDER BY key ties the nth row's key (VFP semantics).
        if (select.Top is int top)
        {
            top = Math.Max(0, top);
            if (top == 0) pairs = new List<(object?[], RecRow)>();
            else if (top < pairs.Count)
            {
                if (select.OrderBy.Count == 0) pairs = pairs.Take(top).ToList();
                else
                {
                    var keySels = select.OrderBy.Select(ob => OrderKeySel(ob, select.Items, table, aggregate, ctx)).ToList();
                    int end = top;
                    while (end < pairs.Count && keySels.All(ks => CompareVfp(ks(pairs[end]), ks(pairs[top - 1]), ctx) == 0)) end++;
                    pairs = pairs.Take(end).ToList();
                }
            }
        }

        return pairs.Select(p => p.row).ToList();
    }

    private static Func<(object?[] row, RecRow rep), VfpValue> OrderKeySel(
        OrderItem ob, IReadOnlyList<SelectItem> items, DbfTable table, bool aggregate, EvaluationContext ctx)
    {
        int colIdx = -1;
        if (ob.Ordinal is int ord) colIdx = ord - 1;
        else if (aggregate) colIdx = ResolveOrderColumn(ob, items, table);
        if (colIdx >= 0)
        {
            int c = colIdx;
            return t => VfpValue.FromClr(c < t.row.Length ? t.row[c] : null);
        }
        return t => ob.Expression!.Evaluate(t.rep, ctx);
    }

    private static int ResolveOrderColumn(OrderItem ob, IReadOnlyList<SelectItem> items, DbfTable table)
    {
        if (ob.Expression is null) return -1;
        string t = StripWs(ob.Expression.Text);
        int col = 0;
        foreach (var it in items)
        {
            if (it.IsStar) { col += table.Columns.Count; continue; }
            if (it.Alias is not null && Eq(StripWs(it.Alias), t)) return col;
            if (it.AggregateStarFunction is not null)
            {
                string fn = it.AggregateStarFunction; // COUNT(*) vs the ORDER BY's COUNT(1) rewrite
                if (Eq(StripWs(fn + "(*)"), t) || Eq(StripWs(fn + "(1)"), t)) return col;
            }
            else if (it.Expression is not null && Eq(StripWs(it.Expression.Text), t)) return col;
            col++;
        }
        return -1;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripWs(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    // build an IComparer<T> from a VfpValue key selector (stable enough for tests)
    private static IComparer<T> OneShot<T>(Func<T, VfpValue> sel, EvaluationContext ctx)
        => Comparer<T>.Create((a, b) => CompareVfp(sel(a), sel(b), ctx));

    private static object?[] Project(RecRow r, IReadOnlyList<SelectItem> items, EvaluationContext ctx, DbfTable table)
    {
        var outv = new List<object?>();
        foreach (var it in items)
        {
            if (it.IsStar) { foreach (var c in table.Columns) outv.Add(Norm(r.GetField(c.Name))); }
            else outv.Add(Norm(ToClr(it.Expression!.Evaluate(r, ctx))));
        }
        return outv.ToArray();
    }

    private static object? ProjectAggregate(SelectItem it, List<RecRow> members, RecRow rep, EvaluationContext ctx, DbfTable table)
    {
        if (IsAggregate(it, out var func, out var arg))
        {
            if (func == "COUNT" && arg == "*") return (decimal)members.Count;
            if (func == "COUNT")
            {
                var e = VfpExpression.Parse(arg!);
                return (decimal)members.Count(m => { var v = e.Evaluate(m, ctx); return !v.IsNull; });
            }
            var ex = VfpExpression.Parse(arg!);
            var vals = members.Select(m => ex.Evaluate(m, ctx)).Where(v => !v.IsNull).ToList();
            switch (func)
            {
                case "SUM": return vals.Aggregate(0m, (acc, v) => acc + v.AsNumber);
                case "AVG": return vals.Count == 0 ? (decimal?)null : vals.Aggregate(0m, (acc, v) => acc + v.AsNumber) / vals.Count;
                case "MIN": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) <= 0 ? a : b)));
                case "MAX": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfp(a, b, ctx) >= 0 ? a : b)));
            }
        }
        // non-aggregate item in an aggregate query (a GROUP BY column) → value of the first member;
        // an empty group has no member to read → null.
        if (it.IsStar) throw new InvalidOperationException("'*' is not valid in the oracle's aggregate projection");
        return members.Count == 0 ? null : Norm(ToClr(it.Expression!.Evaluate(rep, ctx)));
    }

    private static VfpValue EvalAggregateExpr(VfpExpression having, List<RecRow> members, RecRow rep, EvaluationContext ctx, DbfTable table)
    {
        // For the tested HAVING shapes (e.g. COUNT(*) > 1, SUM(AMOUNT) >= 500) substitute the
        // aggregate sub-expression's computed scalar and re-evaluate. We support a single top-level
        // comparison "agg op const". '=' / '<>' use SQL/ANSI semantics (matching the executor).
        var m = Regex.Match(having.Text, @"^\s*(?<l>.+?)\s*(?<op><=|>=|<>|!=|==|=|<|>)\s*(?<r>.+?)\s*$", RegexOptions.Singleline);
        if (!m.Success) return having.Evaluate(rep, ctx);
        VfpValue Side(string s)
        {
            var fake = new SelectItem(false, null, SafeParse(s), null);
            if (IsAggregate(fake, out _, out _))
                return VfpValue.FromClr(ProjectAggregate(fake, members, rep, ctx, table));
            return VfpExpression.Parse(s).Evaluate(rep, ctx);
        }
        var lv = Side(m.Groups["l"].Value);
        var rv = Side(m.Groups["r"].Value);
        string op = m.Groups["op"].Value;
        if (op is "=" or "<>" or "!=")
        {
            bool eq = lv.Type == VfpType.Character && rv.Type == VfpType.Character
                ? VfpRuntime.StrEqSql(lv.AsString, rv.AsString, ctx)
                : CompareVfp(lv, rv, ctx) == 0;
            return VfpValue.Logical(op == "=" ? eq : !eq);
        }
        int c = CompareVfp(lv, rv, ctx);
        return op switch
        {
            "==" => VfpValue.Logical(c == 0),
            "<" => VfpValue.Logical(c < 0),
            "<=" => VfpValue.Logical(c <= 0),
            ">" => VfpValue.Logical(c > 0),
            ">=" => VfpValue.Logical(c >= 0),
            _ => VfpValue.Logical(false),
        };
    }

    private static VfpExpression? SafeParse(string s)
    {
        try { return VfpExpression.Parse(s); } catch { return null; }
    }

    // ---- value helpers --------------------------------------------------------------------

    internal static object? ToClr(VfpValue v) => v.Type switch
    {
        VfpType.Null => null,
        VfpType.Character => v.AsString,
        VfpType.Logical => v.AsLogical,
        VfpType.Date => v.AsDate,
        VfpType.DateTime => v.AsDateTime,
        _ => v.AsNumber,
    };

    /// <summary>Normalize a CLR cell so the two engines' numerics/strings compare equal.</summary>
    internal static object? Norm(object? o) => o switch
    {
        null => null,
        string s => s,
        bool b => b,
        decimal m => m,
        double d => (decimal)d,
        float f => (decimal)f,
        int i => (decimal)i,
        long l => (decimal)l,
        short sh => (decimal)sh,
        byte by => (decimal)by,
        DateOnly d => d,
        DateTime dt => dt,
        _ => o,
    };

    private static int CompareVfp(VfpValue a, VfpValue b, EvaluationContext ctx)
    {
        if (a.IsNull && b.IsNull) return 0;
        if (a.IsNull) return -1;
        if (b.IsNull) return 1;
        if (a.Type == VfpType.Character && b.Type == VfpType.Character)
            return ctx.Collation.Compare(a.AsString.AsSpan(), b.AsString.AsSpan());
        if (a.Type == VfpType.Logical && b.Type == VfpType.Logical)
            return a.AsLogical.CompareTo(b.AsLogical);
        if (a.Type is VfpType.Date or VfpType.DateTime && b.Type is VfpType.Date or VfpType.DateTime)
            return a.AsDateTime.CompareTo(b.AsDateTime);
        return a.AsNumber.CompareTo(b.AsNumber);
    }

    // ---- row comparison -------------------------------------------------------------------

    internal static bool CellEq(object? a, object? b)
    {
        a = Norm(a); b = Norm(b);
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && b is decimal db) return da == db;
        if (a is DateOnly oa && b is DateOnly ob) return oa == ob;
        if (a is DateTime ta && b is DateTime tb) return ta == tb;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb) return ba == bb;
        return Equals(a, b);
    }

    internal static bool RowEq(object?[] a, object?[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!CellEq(a[i], b[i])) return false;
        return true;
    }

    // ---- collation-aware comparison (GROUP BY key / DISTINCT dedup) -----------------------

    private static bool CellEq(object? a, object? b, EvaluationContext ctx)
    {
        a = Norm(a); b = Norm(b);
        if (a is null || b is null) return a is null && b is null;
        if (a is decimal da && b is decimal db) return da == db;
        if (a is DateOnly oa && b is DateOnly ob) return oa == ob;
        if (a is DateTime ta && b is DateTime tb) return ta == tb;
        if (a is string sa && b is string sb) return ctx.Collation.Compare(sa.AsSpan(), sb.AsSpan()) == 0;
        if (a is bool ba && b is bool bb) return ba == bb;
        return Equals(a, b);
    }

    private static bool KeyEq(List<object?> a, List<object?> b, EvaluationContext ctx)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!CellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    private static bool RowEq(object?[] a, object?[] b, EvaluationContext ctx)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!CellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    /// <summary>Assert two rowsets are equal as ORDERED sequences of rows.</summary>
    internal static void AssertRowsEqualOrdered(List<object?[]> expected, List<object?[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.True(RowEq(expected[i], actual[i]),
                $"row {i} differs: expected [{Fmt(expected[i])}] actual [{Fmt(actual[i])}]");
    }

    /// <summary>Assert two rowsets are equal as MULTISETS (order-independent).</summary>
    internal static void AssertRowsEqualUnordered(List<object?[]> expected, List<object?[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var remaining = new List<object?[]>(actual);
        foreach (var e in expected)
        {
            int idx = remaining.FindIndex(r => RowEq(e, r));
            Assert.True(idx >= 0, $"missing expected row [{Fmt(e)}]");
            remaining.RemoveAt(idx);
        }
    }

    internal static List<object?[]> Materialize(SqlResult r) => r.Rows.Select(x => x.ToArray()).ToList();

    private static string Fmt(object?[] row) => string.Join(", ", row.Select(c => c?.ToString() ?? "NULL"));
}
