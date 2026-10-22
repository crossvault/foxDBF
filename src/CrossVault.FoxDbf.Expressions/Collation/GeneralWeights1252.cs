using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// The VFP <c>GENERAL</c> (CP1252 / Western) collation weight table, mechanically
/// translated from the CodeBase reference <c>cp1252generalCollationArray</c> /
/// <c>cp1252generalCompressArray</c> in
/// <c>ref/CodeBase-for-DBF/WorkingSource/COLL4ARR.C</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each of the 256 CP1252 byte slots has a <see cref="Head"/> (primary / case- and
/// accent-folded sort weight) and a <see cref="Tail"/> (diacritic / secondary weight).
/// A <see cref="Head"/> value of <see cref="Expand"/> (the C <c>EXPAND4CHAR_TO_TWO_BYTES</c>
/// sentinel) marks a ligature whose <see cref="Tail"/> slot is instead an index into
/// <see cref="Expansions"/>. A <see cref="Tail"/> value of <see cref="NoTail"/> (the C
/// <c>NO4TAIL_BYTES</c> sentinel) means the character contributes no diacritic byte.
/// </para>
/// <para>
/// COLL4ARR.C carries the same head/tail format for code pages 437 and 850, ported as <see cref="GeneralWeights437"/>
/// and <see cref="GeneralWeights850"/>; its 1250 array is a one-byte Croatian sequence, so the CP1250 GENERAL table
/// (<see cref="GeneralWeights1250"/>) is derived from VFP9's keys. All are run by <see cref="CollationTable"/>.
/// </para>
/// <para>
/// The <see cref="Head"/>/<see cref="Tail"/> values below are the verbatim
/// <c>cp1252generalCollationArray</c> (the stock build, <c>S4ELICON</c> undefined — the
/// standard VFP GENERAL sequence, NOT the optional Swedish variant) and
/// <see cref="Expansions"/> is <c>cp1252generalCompressArray</c>. Both were mechanically
/// parsed from COLL4ARR.C and verified byte-exact against real VFP9 GENERAL CDX keys
/// (a real-world customer USR.CDX).
/// </para>
/// </remarks>
public static class GeneralWeights1252
{
    /// <summary>Head sentinel: this character expands to two letters (C <c>EXPAND4CHAR_TO_TWO_BYTES</c>).</summary>
    public const byte Expand = 0xFF;

    /// <summary>Tail sentinel: this character carries no diacritic byte (C <c>NO4TAIL_BYTES</c>).</summary>
    public const byte NoTail = 0xFF;

    /// <summary>Primary (head) sort weight indexed by CP1252 byte. <see cref="Expand"/> = ligature.</summary>
    public static readonly byte[] Head =
    {
         16,  16,  16,  16,  16,  16,  16,  16,  16,  17,  16,  16,  16,  16,  16,  16,
         16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,  16,
         17,  18,  19,  20,  21,  22,  23,  24,  25,  26,  27,  28,  29,  30,  31,  32,
         86,  87,  88,  89,  90,  91,  92,  93,  94,  95,  33,  34,  35,  36,  37,  38,
         39,  96,  97,  98, 100, 102, 103, 104, 105, 106, 107, 108, 109, 111, 112, 114,
        115, 116, 117, 118, 119, 120, 122, 123, 124, 125, 126,  40,  41,  42,  43,  44,
         45,  96,  97,  98, 100, 102, 103, 104, 105, 106, 107, 108, 109, 111, 112, 114,
        115, 116, 117, 118, 119, 120, 122, 123, 124, 125, 126,  46,  47,  48,  49,  16,
         16,  16,  24,  50,  19,  51,  52,  53,  54,  55, 118,  24, 255,  16,  16,  16,
         16,  24,  24,  19,  19,  56,  30,  30,  57,  58, 118,  24, 255,  16,  16, 125,
         32,  59,  60,  61,  62,  63,  64,  65,  66,  67,  68,  19,  69,  30,  70,  71,
         72,  73,  88,  89,  74,  75,  76,  77,  78,  87,  79,  19,  80,  81,  82,  83,
         96,  96,  96,  96,  96,  96, 255,  98, 102, 102, 102, 102, 106, 106, 106, 106,
        101, 112, 114, 114, 114, 114, 114,  84, 129, 120, 120, 120, 120, 125, 255, 255,
         96,  96,  96,  96,  96,  96, 255,  98, 102, 102, 102, 102, 106, 106, 106, 106,
        101, 112, 114, 114, 114, 114, 114,  85, 129, 120, 120, 120, 120, 125, 255, 125,
    };

    /// <summary>Diacritic (tail) weight indexed by CP1252 byte. <see cref="NoTail"/> = none; for an
    /// <see cref="Expand"/> slot this holds the index into <see cref="Expansions"/>.</summary>
    public static readonly byte[] Tail =
    {
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255,   0, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255,   0,   0,
        255, 255, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255, 255, 255,
        255,   0, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255,   0,   0,
        255, 255, 255,   0, 255,   0, 255, 255, 255,   0, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   8, 255,   0, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255,   8, 255,   0, 255, 255,   4,
          1, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
          2,   1,   3,   5,   4,   6,   1,   7,   2,   1,   3,   4,   2,   1,   3,   4,
        255,   5,   2,   1,   3,   5,   4, 255, 255,   2,   1,   3,   4,   1,   2,   3,
          2,   1,   3,   5,   4,   6,   1,   7,   2,   1,   3,   4,   2,   1,   3,   4,
        255,   5,   2,   1,   3,   5,   4, 255, 255,   2,   1,   3,   4,   1,   2,   4,
    };

    /// <summary>Ligature expansions (CP1252 byte pairs) indexed by the <see cref="Tail"/> value of
    /// an <see cref="Expand"/> slot: 0=OE, 1=AE, 2=TH, 3=SS (cp1252generalCompressArray).</summary>
    public static readonly byte[][] Expansions =
    {
        new byte[] { 79, 69 }, // OE
        new byte[] { 65, 69 }, // AE
        new byte[] { 84, 72 }, // TH
        new byte[] { 83, 83 }, // SS
    };
}
