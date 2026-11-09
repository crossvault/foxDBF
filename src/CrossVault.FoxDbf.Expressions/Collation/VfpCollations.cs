using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Built-in VFP collations and the factory that maps a collation / CDX sort-sequence name (the
/// tag-header string at offset 494) plus the TABLE code page to an <see cref="IVfpCollation"/>.
/// </summary>
/// <remarks>
/// VFP9 weights a key by its byte in the table code page, so weighted collations are per-code-page tables.
/// Supported sequence/code-page cells are byte-exact. Unavailable combinations return
/// Unsupported from Resolve and the MACHINE fallback from ByName; no approximate weights are used.
/// Session availability is a separate gate at SET COLLATE; existing tag maintenance uses the table page.
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
    /// (e.g. <c>Encoding.CodePage</c> of the table). Unavailable combinations and empty names fall back to MACHINE.
    /// </summary>
    public static IVfpCollation ByName(string? name, int codePage)
    {
        Resolve(name, codePage, out var collation);
        return collation;
    }

    /// <summary>
    /// Selects the collation for a CDX tag from its stored sort-sequence name (tag header
    /// @494, as surfaced by the index reader), for a CP1252 table: empty / "MACHINE" →
    /// <see cref="Machine"/>; "GENERAL" → <see cref="General"/>; "SPANISH" → SPANISH; "GERMAN" → GERMAN; any other
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
    /// or <see cref="CollationSupport.Unsupported"/> (unavailable combination; <paramref name="collation"/>
    /// is the MACHINE fallback and its keys are NOT the stored ones).
    /// </summary>
    internal static CollationSupport Resolve(string? name, int codePage, out IVfpCollation collation)
    {
        string n = name?.Trim() ?? string.Empty;
        if (n.Equals("CZECH", StringComparison.OrdinalIgnoreCase))
        {
            collation = CzechWeights.ForCodePage(codePage);
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }
        if (n.Equals("SLOVAK", StringComparison.OrdinalIgnoreCase))
        {
            collation = SlovakWeights.ForCodePage(codePage);
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }
        if (n.Equals("POLISH", StringComparison.OrdinalIgnoreCase))
        {
            collation = PolishWeights.ForCodePage(codePage);
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }
        if (n.Equals("HUNGARY", StringComparison.OrdinalIgnoreCase))
        {
            collation = HungaryWeights.ForCodePage(codePage);
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }
        if (n.Length == 0 || n.Equals("MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            collation = Machine;
            return CollationSupport.Exact;
        }

        switch ((n.ToUpperInvariant(), codePage))
        {
            case ("SPANISH", 437): collation = GeneralCollation.Spanish437; return CollationSupport.Exact;
            case ("SPANISH", 10000): collation = GeneralCollation.Spanish10000; return CollationSupport.Exact;
            case ("UNIQWT", 1252): collation = GeneralCollation.Uniqwt1252; return CollationSupport.Exact;
            case ("UNIQWT", 437): collation = GeneralCollation.Uniqwt437; return CollationSupport.Exact;
            case ("UNIQWT", 850): collation = GeneralCollation.Uniqwt850; return CollationSupport.Exact;
            case ("UNIQWT", 10000): collation = GeneralCollation.Uniqwt10000; return CollationSupport.Exact;
            case ("DUTCH", 1252): collation = GeneralCollation.Dutch1252; return CollationSupport.Exact;
            case ("DUTCH", 437): collation = GeneralCollation.Dutch437; return CollationSupport.Exact;
            case ("DUTCH", 850): collation = GeneralCollation.Dutch850; return CollationSupport.Exact;
            case ("DUTCH", 10000): collation = GeneralCollation.Dutch10000; return CollationSupport.Exact;
        }

        if (n.Equals("GERMAN", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1252 => GeneralCollation.German1252,
                437 => GeneralCollation.German437,
                10000 => GeneralCollation.German10000,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("NORDAN", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1252 => NordanWeights.Cp1252,
                437 => NordanWeights.Cp437,
                850 => NordanWeights.Cp850,
                865 => NordanWeights.Cp865,
                10000 => NordanWeights.Cp10000,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("SWEFIN", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1252 => SwefinWeights.Cp1252,
                437 => SwefinWeights.Cp437,
                850 => SwefinWeights.Cp850,
                865 => SwefinWeights.Cp865,
                10000 => SwefinWeights.Cp10000,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("ICELAND", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1252 => IcelandWeights.Cp1252,
                437 => IcelandWeights.Cp437,
                850 => IcelandWeights.Cp850,
                861 => IcelandWeights.Cp861,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("TURKISH", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1254 => GeneralCollation.Turkish1254,
                857 => GeneralCollation.Turkish857,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("RUSSIAN", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1251 => GeneralCollation.Russian1251,
                866 => GeneralCollation.Russian866,
                10007 => GeneralCollation.Russian10007,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        if (n.Equals("GREEK", StringComparison.OrdinalIgnoreCase))
        {
            collation = codePage switch
            {
                1253 => GeneralCollation.Greek1253,
                737 => GeneralCollation.Greek737,
                10006 => GeneralCollation.Greek10006,
                _ => Machine,
            };
            return ReferenceEquals(collation, Machine) ? CollationSupport.Unsupported : CollationSupport.Exact;
        }

        bool general = n.Equals("GENERAL", StringComparison.OrdinalIgnoreCase);
        if (!general && !n.Equals("SPANISH", StringComparison.OrdinalIgnoreCase))
        {
            collation = Machine;
            return CollationSupport.Unsupported;
        }

        switch (codePage)
        {
            case 852 when general:
                collation = GeneralCollation.Cp852;
                return CollationSupport.Exact;
            case 857 when general:
                collation = GeneralCollation.Cp857;
                return CollationSupport.Exact;
            case 861 when general:
                collation = GeneralCollation.Cp861;
                return CollationSupport.Exact;
            case 863 when general:
                collation = GeneralCollation.Cp863;
                return CollationSupport.Exact;
            case 865 when general:
                collation = GeneralCollation.Cp865;
                return CollationSupport.Exact;
            case 1254 when general:
                collation = GeneralCollation.Cp1254;
                return CollationSupport.Exact;
            case 10000 when general:
                collation = GeneralCollation.Cp10000;
                return CollationSupport.Exact;
            case 10029 when general:
                collation = GeneralCollation.Cp10029;
                return CollationSupport.Exact;
            case 620 when general:
                collation = GeneralCollation.Cp620;
                return CollationSupport.Exact;
            case 895 when general:
                collation = GeneralCollation.Cp895;
                return CollationSupport.Exact;
            case 437 when general:
                collation = GeneralCollation.Cp437;
                return CollationSupport.Exact;
            case 1250 when general:
                collation = GeneralCollation.Cp1250;
                return CollationSupport.Exact;
            case 850:
                collation = general ? GeneralCollation.Cp850 : GeneralCollation.Spanish850;
                return CollationSupport.Exact;
            case 1252:
                collation = general ? GeneralCollation.Instance : GeneralCollation.Spanish1252;
                return CollationSupport.Exact;
            default:
                collation = Machine;
                return CollationSupport.Unsupported;
        }
    }
}

/// <summary>How faithfully a resolved collation reproduces VFP9's stored keys (see
/// <see cref="VfpCollations.Resolve"/>).</summary>
internal enum CollationSupport
{
    /// <summary>Byte-identical to VFP9.</summary>
    Exact,

    /// <summary>Unknown sequence: the MACHINE fallback does not reproduce the stored keys at all.</summary>
    Unsupported,
}
