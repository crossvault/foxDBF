using System;
using System.Globalization;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Per-record data source the expression engine reads fields from. The engine is
/// SELF-CONTAINED and never references the main library; the host adapts its own
/// record type to this contract.
/// </summary>
public interface IRowContext
{
    /// <summary>Returns the raw CLR value of a field by (case-insensitive) name, or
    /// <c>null</c> for a VFP <c>.NULL.</c> / absent field. The engine maps the CLR
    /// type to a <see cref="VfpValue"/>.</summary>
    object? GetField(string name);

    /// <summary>1-based current record number (for <c>RECNO()</c>).</summary>
    int RecNo { get; }

    /// <summary>Deletion flag (for <c>DELETED()</c>).</summary>
    bool Deleted { get; }

    /// <summary>Live table record count (for <c>RECCOUNT()</c>). Hosts that cannot
    /// supply it may return 0.</summary>
    int RecCount { get; }
}

/// <summary>
/// An OPTIONAL host hook an <see cref="IRowContext"/> may also implement so the expression engine
/// can delegate VFP functions it does not implement itself — the runtime STATE functions
/// (SEEK / ALIAS / SELECT / USED / RECNO(alias) / PCOUNT / SYS / EVALUATE / TYPE …) and user-defined
/// procedure/function calls — to a live interpreter. The engine consults this FIRST in
/// <c>VfpRuntime.CallFunction</c>: when <see cref="TryInvoke"/> returns <see langword="true"/> its
/// result is used; <see langword="false"/> falls through to the engine's built-in scalar functions.
/// </summary>
public interface IVfpFunctionHost
{
    /// <summary>Try to handle the function <paramref name="upperName"/> (already upper-cased) with the
    /// evaluated <paramref name="args"/>. Returns <see langword="true"/> + <paramref name="result"/> when
    /// the host owns the function, else <see langword="false"/> (the engine then handles it).</summary>
    bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result);

    /// <summary>Called for a function that neither <see cref="TryInvoke"/> nor the engine implements, also when
    /// an argument is <c>.NULL.</c>. An abbreviation of a VFP function (<c>SUBS</c>) never arrives here: the
    /// engine calls the function it names, and reports a function nobody implements under its full name.
    /// Return the call's value or throw the host's error; the default returns
    /// <c>.NULL.</c> (the engine's behaviour without a host). microVFP raises VFP error 1 here.</summary>
    VfpValue UnknownFunction(string upperName, VfpValue[] args) => VfpValue.Null;
}

/// <summary>
/// Minimal schema contract used for static type inference: VFP column type char
/// (e.g. <c>C N F I Y B D T L M</c>) plus length and decimals, addressed by name.
/// </summary>
public interface ISchema
{
    /// <summary>Looks up a column's VFP type/length/decimals. Returns false if absent.</summary>
    bool TryGetColumn(string name, out char type, out int length, out int decimals);
}

/// <summary>
/// A VFP collation (DX-2). Provides ordered string comparison for the active
/// sort sequence. The default is MACHINE (ordinal byte order).
/// </summary>
public interface IVfpCollation
{
    /// <summary>VFP collation name (e.g. MACHINE, GENERAL).</summary>
    string Name { get; }

    /// <summary>Three-way compare under this collation.</summary>
    int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right);

    /// <summary>
    /// Builds the collation key bytes for <paramref name="s"/> — the head/tail weight
    /// bytes the host pads to the tag key length (MACHINE with spaces, a weighted
    /// collation with 0x00) to reproduce the stored CDX key. For MACHINE this is simply
    /// the code-page (CP1252) encoding;
    /// for GENERAL it is the two-pass head+tail key. Consistent with <see cref="Compare"/>.
    /// </summary>
    byte[] GetCollatedKey(ReadOnlySpan<char> s);
}

// The built-in collations and the VfpCollations factory now live in the
// Collation/ folder: Collation/MachineCollation.cs, Collation/GeneralCollation.cs (GENERAL + SPANISH),
// Collation/CollationTable.cs, Collation/GeneralWeights1252.cs, Collation/GeneralWeights850.cs and
// Collation/VfpCollations.cs.

/// <summary>
/// Ambient evaluation settings: <c>SET EXACT</c> (default OFF, the VFP default),
/// the active <see cref="IVfpCollation"/> (default MACHINE), and an optional culture.
/// </summary>
public sealed class EvaluationContext
{
    // SET DECIMALS controls aggregate precision and the default descriptor scale of Double fields.
    internal int NumericDecimals { get; set; } = 2;

    /// <summary>SET EXACT. VFP default is OFF (<c>false</c>).</summary>
    public bool Exact { get; set; }

    /// <summary>
    /// SET DELETED. When <c>true</c> (<c>SET DELETED ON</c>) deleted records are EXCLUDED —
    /// VFP appends an implicit <c>AND NOT DELETED()</c> to every optimizable query. When
    /// <c>false</c> (<c>SET DELETED OFF</c>) deleted records participate like any other row.
    /// The library DEFAULT is ON (<c>true</c>), consistent with <c>DbfTable.Records</c> which
    /// skips deleted records.
    /// </summary>
    public bool Deleted { get; set; } = true;

    /// <summary>
    /// SET OPTIMIZE. When <c>true</c> (the default, <c>SET OPTIMIZE ON</c>) the query optimizer uses
    /// indexes (Rushmore). When <c>false</c> (<c>SET OPTIMIZE OFF</c>) no index is consulted and the
    /// query runs as a plain full scan — the result is identical, only slower.
    /// </summary>
    public bool Optimize { get; set; } = true;

    /// <summary>
    /// SET ANSI. VFP default is OFF (<c>false</c>). Governs the SQL <c>=</c> operator's STRING
    /// comparison WHEN <see cref="SqlSemantics"/> is on: ANSI OFF compares only up to the SHORTER
    /// operand's length (order-INDEPENDENT — <c>"Smith" = "Sm"</c> and <c>"Sm" = "Smith"</c> are BOTH
    /// true); ANSI ON pads the shorter operand with blanks for a full-length compare. It does NOT
    /// affect the Xbase <c>=</c> (which follows <see cref="Exact"/>) and never affects <c>==</c>.
    /// </summary>
    public bool Ansi { get; set; }

    /// <summary>
    /// When <c>true</c>, the <c>=</c> operator uses SQL comparison semantics (governed by
    /// <see cref="Ansi"/>) instead of Xbase <c>SET EXACT</c> semantics. The SQL executor sets this on
    /// the context it evaluates WHERE / HAVING / JOIN-ON predicates with; the Xbase filter path
    /// (LOCATE / SET FILTER / index keys, via the QueryOptimizer) leaves it <c>false</c> so its
    /// <see cref="Exact"/>-governed behaviour is unchanged. <c>==</c> stays exact in both modes and
    /// the active <see cref="Collation"/> applies either way.
    /// </summary>
    public bool SqlSemantics { get; set; }

    /// <summary>
    /// SET NULL. VFP default is OFF (<c>false</c>). Governs the DEFAULT nullability of a CREATE/ALTER
    /// TABLE column that states neither NULL nor NOT NULL: ON ⇒ such a column is nullable (and an omitted
    /// INSERT value becomes <c>.NULL.</c>), OFF ⇒ it is NOT NULL. An explicit NULL / NOT NULL clause always
    /// wins over this default.
    /// </summary>
    public bool NullSetting { get; set; }

    /// <summary>
    /// When <c>true</c>, a row read resolves a BLANK field (null bit clear, or a non-NULL-able column) to
    /// its typed empty value — as the VFP9 runtime does — instead of the public record API's
    /// <see langword="null"/>; only a set <c>_NullFlags</c> bit reads as <c>.NULL.</c>. Set by the SQL
    /// executor on the contexts of a query the microVFP runtime runs; the plain <c>VfpSession.Execute</c>
    /// / ADO.NET path leaves it off (blank → null, the IsDBNull contract). It also decides
    /// <c>EMPTY(.NULL.)</c>: <c>.F.</c> as in VFP9 when on, <c>.T.</c> when off (there a blank field reads as
    /// null and must stay EMPTY()). The microVFP interpreter turns it on for its own evaluation context.
    /// </summary>
    internal bool VfpBlankFields { get; set; }

    /// <summary>Active collation; default MACHINE.</summary>
    public IVfpCollation Collation { get; set; } = VfpCollations.Machine;

    /// <summary>Optional culture for locale-sensitive conversions.</summary>
    public CultureInfo? Culture { get; set; }

    /// <summary>
    /// Optional code page for <c>CHR()</c>/<c>ASC()</c>, which are byte- (code-page-)
    /// based in VFP, not Unicode. When <see langword="null"/> the engine uses CP1252
    /// (the common Western DBF code page). Set this to the table's actual code page
    /// to get byte-exact <c>CHR()</c>/<c>ASC()</c> for the high range (128-255).
    /// </summary>
    public System.Text.Encoding? Encoding { get; set; }

    // Only index plans containing non-native calls use this bridge. Ordinary key delegates stay native.
    internal Func<VfpExpression, IRowContext, EvaluationContext, VfpValue>? IndexEvaluator { get; set; }
    internal Func<string, string>? NormalizeIndexExpression { get; set; }
    internal Action<string>? ValidateIndexFunction { get; set; }
    // VFP runtime SYS(2335,0) suppresses missing index functions while committing the row.
    internal Func<string?, bool>? SkipUnavailableIndexExpression { get; set; }
    internal VfpRandom Random { get; set; } = new();
    /// <summary>VFP session code page (CPCURRENT), independent of the table/CHR encoding.
    /// Index expressions apply this session's case table to the table's raw bytes.
    /// Null preserves the standalone expression engine's existing casing defaults.</summary>
    public int? SessionCodePage { get; set; }

    /// <summary>A fresh context with VFP defaults (EXACT OFF, MACHINE collation).</summary>
    public static EvaluationContext Default => new();
}

// VFP9's seeded generator, measured with multiple seeds and successive calls.
internal sealed class VfpRandom
{
    private uint _state = unchecked((uint)Environment.TickCount);
    public double Next(double? seed)
    {
        if (seed is { } value) _state = value > 0 ? unchecked((uint)(long)value) : unchecked((uint)Environment.TickCount);
        _state = unchecked(_state * 87137421u + 21132487u);
        return _state / 4294967296d;
    }
}
