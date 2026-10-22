using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// Bind-time check of every qualified reference (<c>q.field</c>; <c>q-&gt;field</c> is folded to that
/// spelling by the SQL lexer) in a statement, BEFORE any row is read. VFP9 resolves qualifiers when it
/// compiles the query — even over an empty table or behind <c>.F. AND</c> it raises the error — so the
/// check runs once per statement and costs nothing per row. Without it the executor's row adapters
/// read an unknown qualifier as <see langword="null"/> (WHERE silently matched zero rows) or strip it
/// and read the own table's column (projection silently returned the wrong column).
/// <para>Rules (VFP9 oracle, SqlArrowQualifierVfp9OracleTests):</para>
/// <list type="bullet">
///   <item>a source alias with a column it does not have → error 1806 "SQL: Column 'F' is not found.";</item>
///   <item>a qualifier an enclosing scope resolves (a correlated parent source, or — with a microVFP
///         interpreter bound — a defined <c>m.</c> var, an open work area or a defined variable) is left
///         to that scope;
///         an open work area then reads its CURRENT record, constant for the query, as VFP9 does;</item>
///   <item><c>m.var</c> that is not a defined memvar of the bound interpreter (or with no interpreter at
///         all) → 1806, as VFP9 reports an undefined memvar in SQL;</item>
///   <item>an open work area of a pure-SQL <see cref="VfpSession"/> that is not a source of the query →
///         rejected: the session keeps no record pointer, so VFP9's current-record value is not
///         reproducible (see SqlQualifiedWhereVfp9OracleTests for the decision);</item>
///   <item>in ORDER BY / GROUP BY, or naming a FROM table that carries a different local alias → 1806;
///         anything else → error 13 "Alias 'Q' is not found.".</item>
/// </list>
/// </summary>
internal sealed class QualifierBinder
{
    /// <summary>One query source: its alias, its table name as written in FROM, and its columns.</summary>
    internal readonly record struct Source(string Alias, string Table, ICollection<string> Columns);

    private readonly VfpSession _session;
    private readonly IReadOnlyList<Source> _sources;
    private readonly Func<string, bool>? _outerKnows;
    private readonly bool _hostBound;
    private readonly bool _checkBare;

    // outerKnows resolves a qualifier in an enclosing scope (correlated parent query or the interpreter)
    // and is null for a top-level pure-SQL statement; hostBound is true when an interpreter row ends the
    // outer chain (so m. resolves); checkBare also requires every UNQUALIFIED name to be a source column
    // (used by UPDATE/DELETE, whose row adapters resolve nothing else and read an unknown name as null).
    internal QualifierBinder(VfpSession session, IReadOnlyList<Source> sources,
        Func<string, bool>? outerKnows, bool hostBound, bool checkBare = false)
    {
        _session = session; _sources = sources; _outerKnows = outerKnows; _hostBound = hostBound;
        _checkBare = checkBare;
    }

    internal void Check(VfpExpression? expr, bool orderOrGroup = false)
    {
        if (expr is null) return;
        foreach (var name in expr.ReferencedFields())
        {
            int dot = name.IndexOf('.');
            if (dot < 0)
            {
                // A bare NULL is SQL's null literal (UPDATE … SET f = NULL), not a column; the expression
                // engine only knows .NULL. and parses it as a name that reads null.
                if (_checkBare && !name.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                    && !_sources.Any(s => s.Columns.Contains(name)))
                    throw ColumnNotFound(name);
                continue;
            }
            if (dot == 0 || dot != name.LastIndexOf('.')) continue;
            CheckOne(name[..dot], name[(dot + 1)..], orderOrGroup);
        }
    }

    internal void Check(SqlPredicate? p)
    {
        switch (p)
        {
            case null: return;
            case AndPredicate a: Check(a.Left); Check(a.Right); return;
            case OrPredicate o: Check(o.Left); Check(o.Right); return;
            case NotPredicate n: Check(n.Operand); return;
            case ExprPredicate e: Check(e.Expression); return;
            case InSubqueryPredicate i: Check(i.Left); return;        // the subquery binds when it runs
            case ScalarSubqueryPredicate s: Check(s.Left); return;
        }
    }

    /// <summary>Checks every clause of a SELECT that is evaluated against these sources.</summary>
    internal void CheckSelect(SelectStatement sel)
    {
        foreach (var item in sel.Items) Check(item.Expression);
        Check(sel.Where);
        Check(sel.WherePredicate);
        foreach (var j in sel.Joins) Check(j.On);
        foreach (var g in sel.GroupBy) Check(g, orderOrGroup: true);
        Check(sel.Having);
        foreach (var o in sel.OrderBy) Check(o.Expression, orderOrGroup: true);
    }

    private void CheckOne(string q, string field, bool orderOrGroup)
    {
        foreach (var s in _sources)
        {
            if (!string.Equals(s.Alias, q, StringComparison.OrdinalIgnoreCase)) continue;
            if (!s.Columns.Contains(field))
                throw ColumnNotFound(field);
            return;
        }

        if (_outerKnows?.Invoke(q) == true) return;

        if (string.Equals(q, "m", StringComparison.OrdinalIgnoreCase))
        {
            // With the interpreter bound, a DEFINED memvar resolves; an undefined one is VFP9's 1806.
            if (_hostBound && (_session.MemoryBridge is not { } bridge || bridge.TryReadMemvar(field, out _)))
                return;
            throw ColumnNotFound(field);
        }

        if (!_hostBound && _session.FindAreaByAlias(q) is not null)
            throw new FoxDbfSqlException(
                $"'{q}.{field}' refers to work area '{q}', which is not a source of this query. VFP reads that " +
                "work area's current record; a pure SQL session keeps no record pointer, so add the table to " +
                "FROM/JOIN or qualify with the query's own alias.");

        if (orderOrGroup) throw ColumnNotFound(field);
        foreach (var s in _sources)
            if (string.Equals(s.Table, q, StringComparison.OrdinalIgnoreCase))
                throw ColumnNotFound(field);

        throw new FoxDbfSqlException($"Alias '{q.ToUpperInvariant()}' is not found.") { VfpErrorNumber = 13 };
    }

    private static FoxDbfSqlException ColumnNotFound(string field)
        => new($"SQL: Column '{field.ToUpperInvariant()}' is not found.") { VfpErrorNumber = 1806 };
}
