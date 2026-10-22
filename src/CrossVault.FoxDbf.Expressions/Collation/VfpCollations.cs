using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Built-in VFP collations and the factory that maps a collation / CDX sort-sequence name (the
/// tag-header string at offset 494) plus the TABLE code page to an <see cref="IVfpCollation"/>.
/// </summary>
/// <remarks>
/// VFP9 weights a key by its byte in the table code page, so GENERAL and SPANISH are per-code-page tables.
/// Byte-exact with VFP9: MACHINE (any code page), GENERAL and SPANISH on code pages <b>1252</b> and <b>850</b>.
/// GENERAL / SPANISH on any other code page fall back to the CP1252 table (right for ASCII, approximate
/// beyond); any other sequence (GERMAN, DUTCH, ...) is unsupported and falls back to MACHINE.
/// </remarks>
public static class VfpCollations
{
    /// <summary>MACHINE collation: raw CP1252 unsigned byte order (the engine default).</summary>
    public static IVfpCollation Machine { get; } = MachineCollation.Instance;

    /// <summary>GENERAL collation: the CP1252 Western sort sequence (case- and
    /// accent-folded primary ordering, accents resolved by a tail pass).</summary>
    public static IVfpCollation General { get; } = GeneralCollation.Instance;

    /// <summary>Resolves a collation by its VFP name (case-insensitive) for CP1252. Unknown or
    /// empty names fall back to MACHINE.</summary>
    public static IVfpCollation ByName(string? name) => ByName(name, 1252);

    /// <summary>
    /// Resolves a collation by its VFP name (case-insensitive) for a table in <paramref name="codePage"/>
    /// (e.g. <c>Encoding.CodePage</c> of the table: 1252, 850): MACHINE, GENERAL or SPANISH. GENERAL / SPANISH
    /// on a code page without its own table use the CP1252 table; unknown or empty names fall back to MACHINE.
    /// </summary>
    public static IVfpCollation ByName(string? name, int codePage)
    {
        Resolve(name, codePage, out var collation);
        return collation;
    }

    /// <summary>
    /// Selects the collation for a CDX tag from its stored sort-sequence name (tag header
    /// @494, as surfaced by the index reader), for a CP1252 table: empty / "MACHINE" →
    /// <see cref="Machine"/>; "GENERAL" → <see cref="General"/>; "SPANISH" → SPANISH; any other
    /// (unsupported) sequence → <see cref="Machine"/> as a documented, never-throwing fallback.
    /// </summary>
    public static IVfpCollation FromSortSequence(string? sortSequence) => ByName(sortSequence, 1252);

    /// <summary>
    /// Selects the collation for a CDX tag from its stored sort-sequence name and the code page of the table
    /// the tag indexes (the weights are per code page: a CP850 GENERAL key expands <c>Ä</c> to <c>AE</c>, a
    /// CP1252 one does not). Same fallbacks as <see cref="ByName(string?, int)"/>; never throws.
    /// </summary>
    public static IVfpCollation FromSortSequence(string? sortSequence, int codePage) => ByName(sortSequence, codePage);

    /// <summary>
    /// Resolves <paramref name="name"/> for <paramref name="codePage"/> and reports how faithfully the result
    /// reproduces VFP9's keys: <see cref="CollationSupport.Exact"/> (byte-identical),
    /// <see cref="CollationSupport.Approximate"/> (GENERAL / SPANISH on a code page without its own table: the
    /// CP1252 table) or <see cref="CollationSupport.Unsupported"/> (unknown sequence; <paramref name="collation"/>
    /// is the MACHINE fallback and its keys are NOT the stored ones).
    /// </summary>
    internal static CollationSupport Resolve(string? name, int codePage, out IVfpCollation collation)
    {
        string n = name?.Trim() ?? string.Empty;
        if (n.Length == 0 || n.Equals("MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            collation = Machine;
            return CollationSupport.Exact;
        }

        bool general = n.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);
        if (!general && !n.Equals("SPANISH", StringComparison.OrdinalIgnoreCase))
        {
            collation = Machine;
            return CollationSupport.Unsupported;
        }

        switch (codePage)
        {
            case 850:
                collation = general ? GeneralCollation.Cp850 : GeneralCollation.Spanish850;
                return CollationSupport.Exact;
            case 1252:
                collation = general ? GeneralCollation.Instance : GeneralCollation.Spanish1252;
                return CollationSupport.Exact;
            default:
                collation = general ? GeneralCollation.Instance : GeneralCollation.Spanish1252;
                return CollationSupport.Approximate;
        }
    }
}

/// <summary>How faithfully a resolved collation reproduces VFP9's stored keys (see
/// <see cref="VfpCollations.Resolve"/>).</summary>
internal enum CollationSupport
{
    /// <summary>Byte-identical to VFP9.</summary>
    Exact,

    /// <summary>A related table (CP1252) stands in for a code page without its own: right for ASCII only.</summary>
    Approximate,

    /// <summary>Unknown sequence: the MACHINE fallback does not reproduce the stored keys at all.</summary>
    Unsupported,
}
