using System;
using System.Collections.Generic;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// One column of a <see cref="SqlResult"/> schema: the result column NAME as VFP9 gives it (upper
/// case: the select-item alias, or the source field name, or a synthesized <c>EXP_n</c> /
/// <c>CNT</c> / <c>SUM_FIELD</c>; a repeated name suffixed <c>_A</c>, <c>_B</c>, …), the VFP field TYPE char
/// (<c>C N F I Y B D T L M</c>), the declared <see cref="Length"/> / <see cref="Decimals"/>,
/// and the CLR type each row value boxes to. This is what the Phase-3 ADO.NET DataReader maps
/// its schema table from.
/// </summary>
public sealed class SqlColumn
{
    /// <summary>Result column name (alias / field name / synthesized).</summary>
    public string Name { get; }

    /// <summary>VFP field type char (C N F I Y B D T L M …).</summary>
    public char VfpType { get; }

    /// <summary>Declared field length.</summary>
    public int Length { get; }

    /// <summary>Declared decimal places (0 for non-numeric).</summary>
    public int Decimals { get; }

    /// <summary>The CLR type the column's row values box to (e.g. <see cref="string"/>,
    /// <see cref="decimal"/>, <see cref="DateOnly"/>, <see cref="bool"/>, <see cref="int"/>).</summary>
    public Type ClrType { get; }
    /// <summary>Whether a projected source field bypasses code-page translation.</summary>
    public bool IsBinary { get; init; }

    public SqlColumn(string name, char vfpType, int length, int decimals, Type clrType)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        VfpType = vfpType;
        Length = length;
        Decimals = decimals;
        ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
    }

    public override string ToString() => $"{Name} {VfpType}({Length},{Decimals}) -> {ClrType.Name}";
}

/// <summary>
/// The result of executing a VFP-SQL statement through a <see cref="VfpSession"/>.
/// <para>
/// For a SELECT it carries the column <see cref="Columns"/> schema and the <see cref="Rows"/>
/// (each an <c>object?[]</c> aligned to <see cref="Columns"/>, with VFP <c>.NULL.</c> as
/// <see langword="null"/>) — streamed where possible. DML / command forms will later add an
/// affected-record count.
/// </para>
/// </summary>
public sealed class SqlResult
{
    internal Dictionary<(object?[] Row, int Column), CrossVault.FoxDbf.Expressions.VfpValue>? OverflowValues { get; init; }
    /// <summary>The column schema, in select-list order. Empty for a non-row command.</summary>
    public IReadOnlyList<SqlColumn> Columns { get; }

    /// <summary>The result rows (each aligned to <see cref="Columns"/>). May be a lazily
    /// streamed sequence; enumerate once.</summary>
    public IEnumerable<object?[]> Rows { get; }

    /// <summary>
    /// The number of records a DML statement (INSERT / UPDATE / DELETE) affected: <c>&gt;= 0</c> for
    /// a DML result, and <c>-1</c> for a SELECT (which carries <see cref="Columns"/> + <see cref="Rows"/>
    /// instead). INSERT is single-row, so an INSERT result reports <c>1</c>.
    /// </summary>
    public int AffectedRecords { get; }

    public SqlResult(IReadOnlyList<SqlColumn> columns, IEnumerable<object?[]> rows)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        AffectedRecords = -1; // SELECT: not a DML row-count result.
    }

    private SqlResult(int affectedRecords)
    {
        Columns = Array.Empty<SqlColumn>();
        Rows = Array.Empty<object?[]>();
        AffectedRecords = affectedRecords;
    }

    /// <summary>The result of a DML statement (INSERT / UPDATE / DELETE): empty columns/rows and the
    /// <paramref name="affectedRecords"/> count (<c>&gt;= 0</c>).</summary>
    public static SqlResult Dml(int affectedRecords)
    {
        if (affectedRecords < 0) throw new ArgumentOutOfRangeException(nameof(affectedRecords));
        return new SqlResult(affectedRecords);
    }
}
