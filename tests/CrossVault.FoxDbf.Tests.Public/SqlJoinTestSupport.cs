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
/// Phase-2 (multi-table / JOIN) scaffolding: a small set of RELATED temp tables (company → dept → emp)
/// and an INDEPENDENT brute-force nested-loop JOIN ORACLE. The oracle is a deliberately different code
/// path from the real executor — it materializes every source, forms composite rows by naive
/// O(n·m) nested loops (cross/inner/left), then evaluates ON / WHERE / projection / GROUP BY / HAVING /
/// ORDER BY / DISTINCT / TOP with <see cref="VfpExpression.Evaluate"/> over a COMPOSITE row context.
/// <para>SAFETY: throwaway temp tables only — never a committed fixture.</para>
/// </summary>
internal static partial class SqlTestSupport
{
    // ---- related fixtures (company → dept → emp) ------------------------------------------

    internal readonly record struct Company(int CompId, string CName);
    internal readonly record struct Dept(int DeptId, string DName, int CompId);
    internal readonly record struct Emp(int EmpId, string EName, int DeptId, decimal Salary);

    internal static readonly Company[] Companies =
    {
        new(100, "Acme"),
        new(200, "Globex"),
    };

    internal static readonly Dept[] Depts =
    {
        new(1, "Sales",       100),
        new(2, "Engineering", 100),
        new(3, "Marketing",   200),
        new(4, "Empty",       200),  // NO employees → LEFT JOIN dept→emp yields NULL emp fields
    };

    internal static readonly Emp[] Emps =
    {
        new(1, "Alice", 1,  5000.00m),
        new(2, "Bob",   1,  4000.00m),
        new(3, "Carol", 2,  6000.00m),
        new(4, "Dave",  2,  5500.00m),
        new(5, "Eve",   3,  4500.00m),
        new(6, "Frank", 99, 3000.00m), // ORPHAN deptid → INNER excludes; emp LEFT JOIN dept → NULL dept
    };

    /// <summary>
    /// Writes the company/dept/emp tables to <paramref name="dir"/>. When <paramref name="withIndex"/>
    /// is true, structural CDX tags are created on every join key (company.COMPID, dept.DEPTID,
    /// dept.COMPID, emp.DEPTID) so the executor's inner-side lookups CAN be index-accelerated; when
    /// false NO tags exist, forcing the scan path. The result set must be identical either way.
    /// </summary>
    internal static void CreateJoinTables(string dir, bool withIndex)
    {
        string company = System.IO.Path.Combine(dir, "company.dbf");
        string dept = System.IO.Path.Combine(dir, "dept.dbf");
        string emp = System.IO.Path.Combine(dir, "emp.dbf");

        using (var w = DbfWriter.Create(company, new[]
        {
            new DbfColumnDef("COMPID", 'I', 4),
            new DbfColumnDef("CNAME", 'C', 15),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var c in Companies) w.AppendRecord(c.CompId, c.CName);
            if (withIndex) w.CreateTag(new CdxTagDefinition("TCID", "COMPID"));
            w.Flush();
        }

        using (var w = DbfWriter.Create(dept, new[]
        {
            new DbfColumnDef("DEPTID", 'I', 4),
            new DbfColumnDef("DNAME", 'C', 15),
            new DbfColumnDef("COMPID", 'I', 4),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var d in Depts) w.AppendRecord(d.DeptId, d.DName, d.CompId);
            if (withIndex)
            {
                w.CreateTag(new CdxTagDefinition("TDID", "DEPTID"));
                w.CreateTag(new CdxTagDefinition("TDCID", "COMPID"));
            }
            w.Flush();
        }

        using (var w = DbfWriter.Create(emp, new[]
        {
            new DbfColumnDef("EMPID", 'I', 4),
            new DbfColumnDef("ENAME", 'C', 15),
            new DbfColumnDef("DEPTID", 'I', 4),
            new DbfColumnDef("SALARY", 'N', 10, 2),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var e in Emps) w.AppendRecord(e.EmpId, e.EName, e.DeptId, e.Salary);
            if (withIndex) w.CreateTag(new CdxTagDefinition("TEDID", "DEPTID"));
            w.Flush();
        }
    }

    // ---- materialized source --------------------------------------------------------------

    private sealed class OracleSource
    {
        public string Alias = "";
        public List<string> Columns = new();
        public List<Dictionary<string, object?>> Rows = new();
    }

    /// <summary>One physical source row inside a composite: its alias + field map, or an ALL-NULL
    /// placeholder (the unmatched inner side of a LEFT JOIN).</summary>
    private readonly record struct Bound(string Alias, Dictionary<string, object?>? Fields);

    /// <summary>A COMPOSITE row context spanning all current source rows. Resolves <c>alias.field</c>
    /// (qualified) and a bare <c>field</c> (first source that has it — the oracle does not police
    /// ambiguity; the executor must). Returns null for an all-NULL (LEFT-unmatched) source.</summary>
    private sealed class CompositeRow : IRowContext
    {
        private readonly IReadOnlyList<Bound> _bounds;
        public CompositeRow(IReadOnlyList<Bound> bounds) => _bounds = bounds;
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name)
        {
            int dot = name.IndexOf('.');
            if (dot >= 0)
            {
                string alias = name[..dot];
                string field = name[(dot + 1)..];
                foreach (var b in _bounds)
                    if (string.Equals(b.Alias, alias, StringComparison.OrdinalIgnoreCase))
                        return b.Fields is null ? null : Get(b.Fields, field);
                return null;
            }
            foreach (var b in _bounds)
                if (b.Fields is not null && b.Fields.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                    return Get(b.Fields, name);
            return null;
        }

        private static object? Get(Dictionary<string, object?> fields, string field)
        {
            foreach (var kv in fields)
                if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }

    private static OracleSource Materialize(FromSource fs, string dir, EvaluationContext ctx)
    {
        string path = System.IO.Path.Combine(dir, fs.Table.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? fs.Table : fs.Table + ".dbf");
        using var t = DbfTable.Open(path);
        var src = new OracleSource { Alias = (fs.Alias ?? System.IO.Path.GetFileNameWithoutExtension(fs.Table)).ToUpperInvariant() };
        foreach (var c in t.Columns) src.Columns.Add(c.Name);
        foreach (var rec in t.EnumerateAll(includeDeleted: true))
        {
            if (ctx.Deleted && rec.IsDeleted) continue;
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in t.Columns) map[c.Name] = rec.GetExpressionValue(c.Name);   // VFP cursor width
            src.Rows.Add(map);
        }
        return src;
    }

    // ---- the brute-force nested-loop JOIN oracle ------------------------------------------

    /// <summary>
    /// Executes <paramref name="select"/> (with multi-table FROM and/or JOINs) the naive way and
    /// returns the schema-less rows (each <c>object?[]</c>, numerics normalized). Supports the
    /// Phase-2 subset: INNER / LEFT / RIGHT / FULL JOIN, comma cross-join, WHERE spanning sources, projection
    /// (<c>*</c> / <c>alias.*</c> / <c>alias.field</c> / expr [AS]), GROUP BY + aggregates + HAVING,
    /// ORDER BY (ordinal/expr/alias, ASC/DESC), DISTINCT and TOP.
    /// </summary>
    internal static List<object?[]> RunJoinOracle(string dir, EvaluationContext ctx, SelectStatement select)
    {
        // 1) materialize every source in FROM/JOIN order.
        var bases = select.From.Select(fs => Materialize(fs, dir, ctx)).ToList();
        var joinSources = select.Joins.Select(j => Materialize(j.Source, dir, ctx)).ToList();

        // 2) build composite rows. The first FROM entry seeds; every other FROM entry is a CROSS
        //    (inner join on .T.); every explicit JOIN applies its own type + ON predicate. The list of
        //    aliases bound so far (in FROM/JOIN order) is tracked so a RIGHT/FULL step can emit an
        //    unmatched inner row with EVERY prior (left) source NULL.
        var leftAliases = new List<string> { bases[0].Alias };
        var composites = bases[0].Rows.Select(r => new List<Bound> { new(bases[0].Alias, r) }).ToList();

        for (int i = 1; i < bases.Count; i++)
        {
            composites = Step(composites, leftAliases, bases[i], JoinType.Inner, onTrue: true, null, ctx);
            leftAliases.Add(bases[i].Alias);
        }

        for (int j = 0; j < select.Joins.Count; j++)
        {
            composites = Step(composites, leftAliases, joinSources[j], select.Joins[j].JoinType, onTrue: false, select.Joins[j].On, ctx);
            leftAliases.Add(joinSources[j].Alias);
        }

        // 3) WHERE over composite rows.
        if (select.Where is not null)
            composites = composites.Where(c =>
            {
                var v = select.Where.Evaluate(new CompositeRow(c), ctx);
                return v.Type == VfpType.Logical && v.AsLogical;
            }).ToList();

        // ordered list of (alias, columns) for '*' / 'alias.*' expansion.
        var layout = new List<(string Alias, List<string> Cols)>();
        foreach (var b in bases) layout.Add((b.Alias, b.Columns));
        foreach (var s in joinSources) layout.Add((s.Alias, s.Columns));

        bool aggregate = select.GroupBy.Count > 0 || select.Items.Any(it => IsJoinAggregate(it, out _, out _));

        List<object?[]> outRows;
        List<CompositeRow> reps;
        if (!aggregate)
        {
            outRows = composites.Select(c => ProjectJoin(c, select.Items, layout, ctx)).ToList();
            reps = composites.Select(c => new CompositeRow(c)).ToList();
        }
        else
        {
            var groups = new List<(List<object?> Key, List<List<Bound>> Members)>();
            if (select.GroupBy.Count == 0)
            {
                groups.Add((new List<object?>(), composites));
            }
            else
            {
                foreach (var c in composites)
                {
                    var rc = new CompositeRow(c);
                    var key = select.GroupBy.Select(g => Norm(ToClr(g.Evaluate(rc, ctx)))).ToList();
                    int idx = groups.FindIndex(x => JoinKeyEq(x.Key, key, ctx));
                    if (idx < 0) groups.Add((key, new List<List<Bound>> { c }));
                    else groups[idx].Members.Add(c);
                }
            }

            outRows = new List<object?[]>();
            reps = new List<CompositeRow>();
            foreach (var (_, members) in groups)
            {
                var rep = members.Count > 0 ? new CompositeRow(members[0]) : new CompositeRow(Array.Empty<Bound>());
                if (select.Having is not null && !EvalJoinHaving(select.Having, members, rep, ctx)) continue;
                outRows.Add(select.Items.Select(it => ProjectJoinAggregate(it, members, rep, ctx)).ToArray());
                reps.Add(rep);
            }
        }

        var pairs = outRows.Select((row, i) => (row, rep: reps[i])).ToList();

        // ORDER BY.
        if (select.OrderBy.Count > 0)
        {
            IOrderedEnumerable<(object?[] row, CompositeRow rep)>? ordered = null;
            for (int k = 0; k < select.OrderBy.Count; k++)
            {
                var ob = select.OrderBy[k];
                var keySel = JoinOrderKeySel(ob, select.Items, layout, aggregate, ctx);
                ordered = k == 0
                    ? (ob.Descending ? pairs.OrderByDescending(x => x, JoinCmp(keySel, ctx)) : pairs.OrderBy(x => x, JoinCmp(keySel, ctx)))
                    : (ob.Descending ? ordered!.ThenByDescending(x => x, JoinCmp(keySel, ctx)) : ordered!.ThenBy(x => x, JoinCmp(keySel, ctx)));
            }
            pairs = ordered!.ToList();
        }

        // DISTINCT.
        if (select.Distinct)
        {
            var seen = new List<(object?[] row, CompositeRow rep)>();
            foreach (var p in pairs)
                if (!seen.Any(s => JoinRowEq(s.row, p.row, ctx))) seen.Add(p);
            pairs = seen;
        }

        // TOP n (ties on the full ORDER BY key are kept).
        if (select.Top is int top)
        {
            top = Math.Max(0, top);
            if (top == 0) pairs = new List<(object?[], CompositeRow)>();
            else if (top < pairs.Count)
            {
                if (select.OrderBy.Count == 0) pairs = pairs.Take(top).ToList();
                else
                {
                    var keySels = select.OrderBy.Select(ob => JoinOrderKeySel(ob, select.Items, layout, aggregate, ctx)).ToList();
                    int end = top;
                    while (end < pairs.Count && keySels.All(ks => CompareVfpJoin(ks(pairs[end]), ks(pairs[top - 1]), ctx) == 0)) end++;
                    pairs = pairs.Take(end).ToList();
                }
            }
        }

        return pairs.Select(p => p.row).ToList();
    }

    private static List<List<Bound>> Step(
        List<List<Bound>> current, List<string> leftAliases, OracleSource inner,
        JoinType type, bool onTrue, VfpExpression? on, EvaluationContext ctx)
    {
        var next = new List<List<Bound>>();
        var innerMatched = new bool[inner.Rows.Count]; // for RIGHT/FULL: which inner rows found a match
        foreach (var combo in current)
        {
            bool matched = false;
            for (int ri = 0; ri < inner.Rows.Count; ri++)
            {
                var candidate = new List<Bound>(combo) { new(inner.Alias, inner.Rows[ri]) };
                bool ok = onTrue;
                if (!onTrue)
                {
                    var v = on!.Evaluate(new CompositeRow(candidate), ctx);
                    ok = v.Type == VfpType.Logical && v.AsLogical;
                }
                if (ok) { next.Add(candidate); matched = true; innerMatched[ri] = true; }
            }
            // LEFT / FULL: an outer (left) row with no inner match emits one row, inner side all-NULL.
            if (!matched && (type == JoinType.Left || type == JoinType.Full))
                next.Add(new List<Bound>(combo) { new(inner.Alias, null) });
        }

        // RIGHT / FULL: every inner (right) row that matched NO left row appears once, with EVERY prior
        // (left) source NULL. The output column order stays FROM/JOIN declaration order (left… then inner),
        // so a RIGHT JOIN is the LEFT machinery with the driving side swapped but the layout unchanged.
        if (type == JoinType.Right || type == JoinType.Full)
        {
            for (int ri = 0; ri < inner.Rows.Count; ri++)
                if (!innerMatched[ri])
                {
                    var combo = leftAliases.Select(a => new Bound(a, null)).ToList();
                    combo.Add(new Bound(inner.Alias, inner.Rows[ri]));
                    next.Add(combo);
                }
        }
        return next;
    }

    // ---- projection -----------------------------------------------------------------------

    private static object?[] ProjectJoin(
        List<Bound> combo, IReadOnlyList<SelectItem> items, List<(string Alias, List<string> Cols)> layout, EvaluationContext ctx)
    {
        var rc = new CompositeRow(combo);
        var outv = new List<object?>();
        foreach (var it in items)
        {
            if (it.IsStar)
            {
                foreach (var (alias, cols) in layout)
                {
                    if (it.StarAlias is not null && !string.Equals(it.StarAlias, alias, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var c in cols)
                        outv.Add(Norm(rc.GetField(alias + "." + c)));
                }
            }
            else outv.Add(Norm(ToClr(it.Expression!.Evaluate(rc, ctx))));
        }
        return outv.ToArray();
    }

    private static object? ProjectJoinAggregate(SelectItem it, List<List<Bound>> members, CompositeRow rep, EvaluationContext ctx)
    {
        if (IsJoinAggregate(it, out var func, out var arg))
        {
            if (func == "COUNT" && arg == "*") return (decimal)members.Count;
            if (func == "COUNT")
            {
                var e = VfpExpression.Parse(arg!);
                return (decimal)members.Count(m => !e.Evaluate(new CompositeRow(m), ctx).IsNull);
            }
            var ex = VfpExpression.Parse(arg!);
            var vals = members.Select(m => ex.Evaluate(new CompositeRow(m), ctx)).Where(v => !v.IsNull).ToList();
            switch (func)
            {
                case "SUM": return vals.Aggregate(0m, (acc, v) => acc + v.AsNumber);
                case "AVG": return vals.Count == 0 ? (decimal?)null : vals.Aggregate(0m, (acc, v) => acc + v.AsNumber) / vals.Count;
                case "MIN": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfpJoin(a, b, ctx) <= 0 ? a : b)));
                case "MAX": return vals.Count == 0 ? null : Norm(ToClr(vals.Aggregate((a, b) => CompareVfpJoin(a, b, ctx) >= 0 ? a : b)));
            }
        }
        if (it.IsStar) throw new InvalidOperationException("'*' is not valid in the oracle's aggregate projection");
        return members.Count == 0 ? null : Norm(ToClr(it.Expression!.Evaluate(rep, ctx)));
    }

    private static bool EvalJoinHaving(VfpExpression having, List<List<Bound>> members, CompositeRow rep, EvaluationContext ctx)
    {
        var m = Regex.Match(having.Text, @"^\s*(?<l>.+?)\s*(?<op><=|>=|<>|!=|==|=|<|>)\s*(?<r>.+?)\s*$", RegexOptions.Singleline);
        if (!m.Success)
        {
            var v = members.Count == 0 ? VfpValue.Logical(false) : having.Evaluate(rep, ctx);
            return v.Type == VfpType.Logical && v.AsLogical;
        }
        VfpValue Side(string s)
        {
            var fake = new SelectItem(false, null, VfpExpression.Parse(s), null);
            if (IsJoinAggregate(fake, out _, out _))
                return VfpValue.FromClr(ProjectJoinAggregate(fake, members, rep, ctx));
            return VfpExpression.Parse(s).Evaluate(rep, ctx);
        }
        var lv = Side(m.Groups["l"].Value);
        var rv = Side(m.Groups["r"].Value);
        string op = m.Groups["op"].Value;
        if (op is "=" or "<>" or "!=")
        {
            bool eq = lv.Type == VfpType.Character && rv.Type == VfpType.Character
                ? VfpRuntime.StrEqSql(lv.AsString, rv.AsString, ctx)
                : CompareVfpJoin(lv, rv, ctx) == 0;
            return op == "=" ? eq : !eq;
        }
        int c = CompareVfpJoin(lv, rv, ctx);
        return op switch { "==" => c == 0, "<" => c < 0, "<=" => c <= 0, ">" => c > 0, ">=" => c >= 0, _ => false };
    }

    // ---- ORDER BY helpers (join) ----------------------------------------------------------

    private static Func<(object?[] row, CompositeRow rep), VfpValue> JoinOrderKeySel(
        OrderItem ob, IReadOnlyList<SelectItem> items, List<(string Alias, List<string> Cols)> layout, bool aggregate, EvaluationContext ctx)
    {
        int colIdx = -1;
        if (ob.Ordinal is int ord) colIdx = ord - 1;
        else if (aggregate) colIdx = ResolveJoinOrderColumn(ob, items, layout);
        if (colIdx >= 0)
        {
            int c = colIdx;
            return t => VfpValue.FromClr(c < t.row.Length ? t.row[c] : null);
        }
        return t => ob.Expression!.Evaluate(t.rep, ctx);
    }

    private static int ResolveJoinOrderColumn(OrderItem ob, IReadOnlyList<SelectItem> items, List<(string Alias, List<string> Cols)> layout)
    {
        if (ob.Expression is null) return -1;
        string t = StripWsJoin(ob.Expression.Text);
        int col = 0;
        foreach (var it in items)
        {
            if (it.IsStar)
            {
                foreach (var (alias, cols) in layout)
                    if (it.StarAlias is null || string.Equals(it.StarAlias, alias, StringComparison.OrdinalIgnoreCase))
                        col += cols.Count;
                continue;
            }
            if (it.Alias is not null && Eq(StripWsJoin(it.Alias), t)) return col;
            if (it.AggregateStarFunction is not null)
            {
                string fn = it.AggregateStarFunction;
                if (Eq(StripWsJoin(fn + "(*)"), t) || Eq(StripWsJoin(fn + "(1)"), t)) return col;
            }
            else if (it.Expression is not null && Eq(StripWsJoin(it.Expression.Text), t)) return col;
            col++;
        }
        return -1;
        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripWsJoin(string s) => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static IComparer<T> JoinCmp<T>(Func<T, VfpValue> sel, EvaluationContext ctx)
        => Comparer<T>.Create((a, b) => CompareVfpJoin(sel(a), sel(b), ctx));

    // ---- aggregate detection (shared shape with the single-table oracle) ------------------

    private static readonly Regex JoinAggRx =
        new(@"^\s*(SUM|AVG|COUNT|MIN|MAX)\s*\((?<arg>.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static bool IsJoinAggregate(SelectItem it, out string func, out string? arg)
    {
        func = ""; arg = null;
        if (it.AggregateStarFunction is not null) { func = it.AggregateStarFunction.ToUpperInvariant(); arg = "*"; return true; }
        if (it.Expression is null) return false;
        var m = JoinAggRx.Match(it.Expression.Text);
        if (!m.Success) return false;
        string a = m.Groups["arg"].Value;
        int depth = 0;
        foreach (char ch in a) { if (ch == '(') depth++; else if (ch == ')') depth--; else if (ch == ',' && depth == 0) return false; }
        func = m.Groups[1].Value.ToUpperInvariant(); arg = a.Trim();
        return true;
    }

    // ---- value helpers (join) -------------------------------------------------------------

    private static int CompareVfpJoin(VfpValue a, VfpValue b, EvaluationContext ctx)
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

    private static bool JoinCellEq(object? a, object? b, EvaluationContext ctx)
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

    private static bool JoinKeyEq(List<object?> a, List<object?> b, EvaluationContext ctx)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!JoinCellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    private static bool JoinRowEq(object?[] a, object?[] b, EvaluationContext ctx)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!JoinCellEq(a[i], b[i], ctx)) return false;
        return true;
    }

    // =======================================================================================
    //  UNION [ALL] oracle
    // =======================================================================================

    /// <summary>
    /// Executes a UNION [ALL] chain the naive way and returns the schema-less, value-normalized rows.
    /// <para>
    /// VFP unions by POSITION: each branch's core (its own DISTINCT / WHERE / GROUP BY / HAVING /
    /// projection / joins, but NOT the union-level ORDER BY / TOP / INTO) is run independently, then the
    /// results are folded left-to-right. A link is <c>UNION ALL</c> (concatenate) or <c>UNION</c> (the
    /// accumulated result is de-duplicated, collation-aware, NULLs equal). The trailing ORDER BY rides
    /// the LAST branch in the chain and orders the WHOLE result (oracle: 1-based ORDINAL only); the TOP
    /// rides the FIRST branch and tops the final result (tie-aware on the ORDER BY keys).
    /// </para>
    /// </summary>
    internal static List<object?[]> RunUnionOracle(string dir, EvaluationContext ctx, SelectStatement root)
    {
        // flatten the chain into branch selects + the per-link ALL flag (allFlags[i-1] = how branch i
        // combines with the accumulated result).
        var selects = new List<SelectStatement>();
        var allFlags = new List<bool>();
        var cur = root;
        selects.Add(cur);
        while (cur.Union is not null) { allFlags.Add(cur.Union.All); cur = cur.Union.Query; selects.Add(cur); }

        var acc = RunBranchCore(dir, ctx, selects[0]);
        for (int i = 1; i < selects.Count; i++)
        {
            acc.AddRange(RunBranchCore(dir, ctx, selects[i]));
            if (!allFlags[i - 1]) acc = DedupRows(acc, ctx); // a plain UNION link dedups the running result
        }

        // overall ORDER BY (rides the last branch) — oracle resolves by 1-based ordinal only.
        var orderBy = selects[^1].OrderBy;
        if (orderBy.Count > 0)
        {
            IOrderedEnumerable<object?[]>? ordered = null;
            for (int k = 0; k < orderBy.Count; k++)
            {
                var ob = orderBy[k];
                var sel = OrdinalKey(ob);
                ordered = k == 0
                    ? (ob.Descending ? acc.OrderByDescending(x => x, JoinCmp(sel, ctx)) : acc.OrderBy(x => x, JoinCmp(sel, ctx)))
                    : (ob.Descending ? ordered!.ThenByDescending(x => x, JoinCmp(sel, ctx)) : ordered!.ThenBy(x => x, JoinCmp(sel, ctx)));
            }
            acc = ordered!.ToList();
        }

        // overall TOP (rides the first branch) — tie-aware on the ORDER BY keys (VFP semantics).
        if (selects[0].Top is int top)
        {
            top = Math.Max(0, top);
            if (top == 0) acc = new List<object?[]>();
            else if (top < acc.Count)
            {
                if (orderBy.Count == 0) acc = acc.Take(top).ToList();
                else
                {
                    var keySels = orderBy.Select(OrdinalKey).ToList();
                    int end = top;
                    while (end < acc.Count && keySels.All(ks => CompareVfpJoin(ks(acc[end]), ks(acc[top - 1]), ctx) == 0)) end++;
                    acc = acc.Take(end).ToList();
                }
            }
        }

        return acc;
    }

    private static Func<object?[], VfpValue> OrdinalKey(OrderItem ob)
    {
        int ci = (ob.Ordinal ?? throw new InvalidOperationException(
                     "the union oracle resolves ORDER BY by 1-based ordinal only")) - 1;
        return r => VfpValue.FromClr(ci >= 0 && ci < r.Length ? r[ci] : null);
    }

    /// <summary>Runs ONE union branch's core: the branch with its union-level clauses
    /// (ORDER BY / TOP / INTO / UNION) stripped, dispatched to the single-table or the join oracle.</summary>
    private static List<object?[]> RunBranchCore(string dir, EvaluationContext ctx, SelectStatement branch)
    {
        var core = branch with
        {
            OrderBy = Array.Empty<OrderItem>(),
            Top = null,
            TopPercent = false,
            Into = null,
            Union = null,
        };

        if (core.Joins.Count > 0 || core.From.Count > 1)
            return RunJoinOracle(dir, ctx, core);

        var fs = core.From[0];
        string path = System.IO.Path.Combine(
            dir, fs.Table.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? fs.Table : fs.Table + ".dbf");
        using var t = DbfTable.Open(path);
        return RunOracle(t, ctx, core);
    }

    private static List<object?[]> DedupRows(List<object?[]> rows, EvaluationContext ctx)
    {
        var seen = new List<object?[]>();
        foreach (var r in rows)
            if (!seen.Any(s => JoinRowEq(s, r, ctx))) seen.Add(r);
        return seen;
    }
}
