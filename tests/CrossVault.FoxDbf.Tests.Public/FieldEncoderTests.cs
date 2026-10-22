using System.Text;
using System.Buffers.Binary;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Byte-level encode tests for <see cref="FieldEncoder"/> (plan §A5/§A5b/§D0) — the exact
/// inverse of <see cref="FieldDecoder"/>. Every expected byte sequence is HARDCODED and
/// cross-checked against the matching <see cref="FieldDecoderTests"/> case (same value ⇒
/// same bytes), so the two together prove a byte-parity round-trip for the canonical types.
///
/// Adversarial / edge focus (NOT happy path): a NEGATIVE Integer, a negative Currency, the
/// NULL→type-appropriate-empty rule (spaces for C/N/F/D, space for L, zero bytes for binary),
/// a DateTime carrying whole-second time, an empty Date, truncation/padding for C, and the
/// Varchar effective-length contract (value + trailing length byte + "varlen used" return).
///
/// These tests are PURE/in-memory: they encode into a stack buffer and compare bytes. No
/// fixture file is ever opened for writing (SAFETY rule).
/// </summary>
public sealed class FieldEncoderTests
{
    private static readonly Encoding Enc = Encoding.Latin1;

    private static DbfColumn Col(char type, int length, int dec = 0, byte flags = 0)
        => new("F", type, length, dec, 0, flags);

    /// <summary>Encode into a fresh buffer of exactly <paramref name="len"/> bytes and return it.</summary>
    private static byte[] Encode(DbfColumn col, object? value, int len, Encoding? enc = null)
    {
        var buf = new byte[len];
        FieldEncoder.Encode(col, value, enc ?? Enc, buf);
        return buf;
    }

    // ---- C (character): left-justified, space-padded, truncated -----------------

    [Fact]
    public void C_SpacePadsToWidth()
    {
        // "Hello" in C(10) → "Hello" + 5 trailing spaces (the exact inverse of
        // FieldDecoderTests.C_TrimsTrailingSpaces).
        Assert.Equal(Encoding.Latin1.GetBytes("Hello     "), Encode(Col('C', 10), "Hello", 10));
    }

    [Fact]
    public void C_NullAndEmpty_AllSpaces()
    {
        var allSpaces = Encoding.Latin1.GetBytes("     ");
        Assert.Equal(allSpaces, Encode(Col('C', 5), null, 5)); // null → spaces (NOT zero bytes)
        Assert.Equal(allSpaces, Encode(Col('C', 5), "", 5));
    }

    [Fact]
    public void C_OverlongValue_IsTruncatedToWidth()
    {
        // Best-effort coercion (§A10): never throw, just clip to the field width.
        Assert.Equal(Encoding.Latin1.GetBytes("Hel"), Encode(Col('C', 3), "Hello", 3));
    }

    // ---- I (4-byte signed int LE) ----------------------------------------------

    [Fact]
    public void I_LittleEndian_PositiveAndBoundary()
    {
        Assert.Equal(new byte[] { 0x64, 0x00, 0x00, 0x00 }, Encode(Col('I', 4), 100, 4));
        Assert.Equal(new byte[] { 0xFA, 0x00, 0x00, 0x00 }, Encode(Col('I', 4), 250, 4));
    }

    [Fact]
    public void I_NegativeInteger_RoundTrips()
    {
        // -1 → FF FF FF FF (inverse of FieldDecoderTests.I_LittleEndian's -1 case).
        var bytes = Encode(Col('I', 4), -1, 4);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
        Assert.Equal(-1, FieldDecoder.Decode(Col('I', 4), bytes, Enc));

        // A larger negative: -16777216 = 0xFF000000 → LE 00 00 00 FF.
        var b2 = Encode(Col('I', 4), -16_777_216, 4);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xFF }, b2);
        Assert.Equal(-16_777_216, FieldDecoder.Decode(Col('I', 4), b2, Enc));
    }

    [Fact]
    public void I_AcceptsLongValue()
    {
        // A row built from decoded values may hand an int as a boxed long — accept both.
        Assert.Equal(new byte[] { 0x64, 0x00, 0x00, 0x00 }, Encode(Col('I', 4), 100L, 4));
    }

    [Fact]
    public void I_Null_ZeroBytes()
    {
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00 }, Encode(Col('I', 4), null, 4));
    }

    [Theory]
    [InlineData('I')]
    [InlineData('+')]
    public void I_And_AutoInc_TruncateFractionalNumericsTowardZero(char type)
    {
        var col = Col(type, 4, 0, type == '+' ? (byte)0x08 : (byte)0);
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(Encode(col, 12.9m, 4)));
        Assert.Equal(-12, BinaryPrimitives.ReadInt32LittleEndian(Encode(col, -12.9m, 4)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(Encode(col, 12.9d, 4)));
        Assert.Equal(-12, BinaryPrimitives.ReadInt32LittleEndian(Encode(col, -12.9f, 4)));
        Assert.Equal(2_147_483_647,
            BinaryPrimitives.ReadInt32LittleEndian(Encode(col, 2_147_483_647.9m, 4)));
        Assert.Equal(-2_147_483_647,
            BinaryPrimitives.ReadInt32LittleEndian(Encode(col, -2_147_483_647.9m, 4)));
    }

    [Theory]
    [InlineData('I')]
    [InlineData('+')]
    public void I_And_AutoInc_AcceptEveryIntegralBoxWithinRange(char type)
    {
        var col = Col(type, 4, 0, type == '+' ? (byte)0x08 : (byte)0);
        (object Value, int Expected)[] cases =
        [
            ((short)-123, -123), ((byte)200, 200), ((sbyte)-100, -100),
            ((uint)2_147_483_647, 2_147_483_647), ((ushort)65_000, 65_000),
            ((ulong)2_147_483_647, 2_147_483_647), (123, 123), (123L, 123),
        ];
        foreach (var item in cases)
            Assert.Equal(item.Expected,
                BinaryPrimitives.ReadInt32LittleEndian(Encode(col, item.Value, 4)));
    }

    [Theory]
    [InlineData('I')]
    [InlineData('+')]
    public void I_And_AutoInc_AcceptOnlyVfpIntegerRange(char type)
    {
        var col = Col(type, 4, 0, type == '+' ? (byte)0x08 : (byte)0);
        Assert.Equal(int.MaxValue,
            BinaryPrimitives.ReadInt32LittleEndian(Encode(col, int.MaxValue, 4)));
        Assert.Equal(-2_147_483_647,
            BinaryPrimitives.ReadInt32LittleEndian(Encode(col, -2_147_483_647L, 4)));

        foreach (object value in new object[]
        {
            int.MinValue, 2_147_483_648L, -2_147_483_648L, decimal.MaxValue,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        })
        {
            var error = Assert.Throws<DbfWriteException>(() => Encode(col, value, 4));
            Assert.Equal(39, error.VfpErrorNumber);
            Assert.Equal("Numeric overflow. Data was lost.", error.Message);
        }
    }

    [Theory]
    [InlineData('I')]
    [InlineData('+')]
    public void I_And_AutoInc_RejectStringAndBoolAsTypeMismatch(char type)
    {
        var col = Col(type, 4, 0, type == '+' ? (byte)0x08 : (byte)0);
        foreach (object value in new object[] { "12", true, false })
        {
            var error = Assert.Throws<DbfWriteException>(() => Encode(col, value, 4));
            Assert.Equal(9, error.VfpErrorNumber);
            Assert.Equal("Data type mismatch.", error.Message);
        }
    }

    // ---- Y (currency: round(v*10000) int64 LE) ---------------------------------

    [Fact]
    public void Y_Currency_Positive()
    {
        // 1.0m → 10000 → int64 LE (inverse of FieldDecoderTests.Y_Currency_Positive).
        var bytes = Encode(Col('Y', 8), 1.0m, 8);
        Assert.Equal(new byte[] { 0x10, 0x27, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, bytes);
        Assert.Equal(1.0m, FieldDecoder.Decode(Col('Y', 8), bytes, Enc));
    }

    [Fact]
    public void Y_Currency_Negative()
    {
        // -2.5m → -25000 → int64 LE (inverse of FieldDecoderTests.Y_Currency_Negative).
        var bytes = Encode(Col('Y', 8), -2.5m, 8);
        Assert.Equal(new byte[] { 0x58, 0x9E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
        Assert.Equal(-2.5m, FieldDecoder.Decode(Col('Y', 8), bytes, Enc));
    }

    [Fact]
    public void Y_Null_ZeroBytes()
    {
        Assert.Equal(new byte[8], Encode(Col('Y', 8), null, 8));
    }

    [Fact]
    public void Y_OutOfRange_Saturates_NeverThrows()
    {
        // decimal.MaxValue * 10000 overflows decimal AND the int64 narrowing — both must
        // saturate, not throw (never-throw §A10 contract for 'Y', MUST-FIX). A positive
        // out-of-range currency clamps to long.MaxValue, a negative to long.MinValue.
        byte[] pos = null!, neg = null!;
        var ex = Record.Exception(() =>
        {
            pos = Encode(Col('Y', 8), decimal.MaxValue, 8);
            neg = Encode(Col('Y', 8), decimal.MinValue, 8);
        });
        Assert.Null(ex);
        Assert.Equal(BitConverter.GetBytes(long.MaxValue), pos);
        Assert.Equal(BitConverter.GetBytes(long.MinValue), neg);
    }

    // ---- B (IEEE-754 double LE) -------------------------------------------------

    [Fact]
    public void B_DoubleLittleEndian()
    {
        // 1234.5 (inverse of FieldDecoderTests.B_DoubleLittleEndian).
        var bytes = Encode(Col('B', 8), 1234.5, 8);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x4A, 0x93, 0x40 }, bytes);
        Assert.Equal(1234.5, (double)FieldDecoder.Decode(Col('B', 8), bytes, Enc)!, 10);
    }

    // ---- D (YYYYMMDD date) ------------------------------------------------------

    [Fact]
    public void D_Date_Yyyymmdd()
    {
        // DateOnly(2012,7,30) → "20120730" (inverse of FieldDecoderTests.D_ValidDate).
        var bytes = Encode(Col('D', 8), new DateOnly(2012, 7, 30), 8);
        Assert.Equal(Encoding.Latin1.GetBytes("20120730"), bytes);
        Assert.Equal(new DateOnly(2012, 7, 30), FieldDecoder.Decode(Col('D', 8), bytes, Enc));
    }

    [Fact]
    public void D_AcceptsDateTime()
    {
        // A boxed DateTime (the 'T' decode type) is accepted for a 'D' field; the time drops.
        var bytes = Encode(Col('D', 8), new DateTime(2012, 7, 30, 13, 0, 0), 8);
        Assert.Equal(Encoding.Latin1.GetBytes("20120730"), bytes);
    }

    [Fact]
    public void D_Null_AllSpaces()
    {
        // null Date → 8 spaces (the all-spaces sentinel the decoder reads back as null).
        Assert.Equal(Encoding.Latin1.GetBytes("        "), Encode(Col('D', 8), null, 8));
    }

    // ---- T / @ (Julian day + ms since midnight, 2x int32 LE) -------------------

    [Fact]
    public void T_DateTime_JulianPlusMillis()
    {
        // 2012-06-14 14:25:15 → days 2456093, ms 51915000 (inverse of
        // FieldDecoderTests.T_JulianPlusMillis). Exact byte parity holds because the
        // time is whole-second.
        var bytes = Encode(Col('T', 8), new DateTime(2012, 6, 14, 14, 25, 15), 8);
        Assert.Equal(new byte[] { 0x1D, 0x7A, 0x25, 0x00, 0xF8, 0x28, 0x18, 0x03 }, bytes);
        Assert.Equal(new DateTime(2012, 6, 14, 14, 25, 15), FieldDecoder.Decode(Col('T', 8), bytes, Enc));
    }

    [Fact]
    public void T_LastFractionalMillisecond_StoresValidMsOfDay_AndDecodesAtSecondPrecision()
    {
        var input = new DateTime(2012, 6, 14, 23, 59, 59).AddTicks(9_999_000);

        var bytes = Encode(Col('T', 8), input, 8);

        Assert.Equal(86_399_999, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(new DateTime(2012, 6, 15, 0, 0, 0), FieldDecoder.Decode(Col('T', 8), bytes, Enc));
    }

    [Fact]
    public void T_Null_AllZeroBytes()
    {
        // null DateTime → 8 zero bytes (days==0 is the decoder's empty-datetime sentinel).
        Assert.Equal(new byte[8], Encode(Col('T', 8), null, 8));
        Assert.Null(FieldDecoder.Decode(Col('T', 8), new byte[8], Enc));
    }

    // ---- L (logical) ------------------------------------------------------------

    [Fact]
    public void L_True_T_False_F_Null_Space()
    {
        Assert.Equal(new byte[] { (byte)'T' }, Encode(Col('L', 1), true, 1));
        Assert.Equal(new byte[] { (byte)'F' }, Encode(Col('L', 1), false, 1));
        // null → space (the uninitialized/empty logical value, NOT 'F').
        Assert.Equal(new byte[] { (byte)' ' }, Encode(Col('L', 1), null, 1));
    }

    // ---- N (numeric): right-justified ASCII, Decimal places --------------------

    [Fact]
    public void N_Dec0_RightJustified()
    {
        // 80053722 in N(8,0) → exact width (inverse of the per.dbf PER_BLZ canonical case).
        Assert.Equal(Encoding.Latin1.GetBytes("80053722"), Encode(Col('N', 8, 0), 80_053_722L, 8));
        // 0 in N(8,0) → "       0" (7 leading spaces).
        Assert.Equal(Encoding.Latin1.GetBytes("       0"), Encode(Col('N', 8, 0), 0L, 8));
    }

    [Fact]
    public void N_Dec2_RightJustified_InvariantCulture()
    {
        // 123.45m in N(10,2) → "    123.45" (inverse of nulltest rec[0] AMOUNT).
        Assert.Equal(Encoding.Latin1.GetBytes("    123.45"), Encode(Col('N', 10, 2), 123.45m, 10));
        // 0.00m in N(12,2) → "        0.00" (inverse of per.dbf PER_KNUMFE canonical).
        Assert.Equal(Encoding.Latin1.GetBytes("        0.00"), Encode(Col('N', 12, 2), 0.00m, 12));
    }

    [Fact]
    public void N_Negative_Decimal()
    {
        // -2.50m in N(6,2) → " -2.50" (sign consumes a slot; InvariantCulture '.').
        Assert.Equal(Encoding.Latin1.GetBytes(" -2.50"), Encode(Col('N', 6, 2), -2.50m, 6));
    }

    [Fact]
    public void N_Null_AllSpaces()
    {
        Assert.Equal(Encoding.Latin1.GetBytes("     "), Encode(Col('N', 5, 0), null, 5));
        Assert.Equal(Encoding.Latin1.GetBytes("       "), Encode(Col('N', 7, 2), null, 7));
    }

    [Fact]
    public void N_Overflow_NeverThrows_BestEffort()
    {
        // A value too wide for the field must NOT throw (§A10). Document best-effort:
        // VFP fills an overflowed numeric with '*'. We only require: no throw, exact width.
        var ex = Record.Exception(() =>
        {
            var bytes = Encode(Col('N', 3, 0), 123_456_789L, 3);
            Assert.Equal(3, bytes.Length);
        });
        Assert.Null(ex);
    }

    [Theory]
    [InlineData('N')]
    [InlineData('F')]
    public void NumericOverflow_FillsEntireFieldWithAsterisks(char type)
        => Assert.Equal("***"u8.ToArray(), Encode(Col(type, 3), 123456, 3));

    // ---- F (float): right-justified ASCII --------------------------------------

    [Fact]
    public void F_Dec2_RightJustified()
    {
        // 3.14 in F(8,2) → "    3.14".
        Assert.Equal(Encoding.Latin1.GetBytes("    3.14"), Encode(Col('F', 8, 2), 3.14, 8));
    }

    [Fact]
    public void F_Null_AllSpaces()
    {
        Assert.Equal(Encoding.Latin1.GetBytes("        "), Encode(Col('F', 8, 2), null, 8));
    }

    // ---- + (auto-increment: int32 LE, identical to 'I') ------------------------

    [Fact]
    public void AutoIncrement_EncodesLikeInt()
    {
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, Encode(Col('+', 4, 0, 0x08), 1, 4));
    }

    // ---- M (memo): a 0 pointer here; memo append is deferred to D1 --------------

    [Fact]
    public void M_WritesZeroPointer()
    {
        // The VFP 0x30/0x31/0x32 memo pointer is a 4-byte LE block number; D0 writes 0.
        Assert.Equal(new byte[4], Encode(Col('M', 4), "ignored text", 4));
        Assert.Equal(new byte[4], Encode(Col('M', 4), null, 4));
    }

    // ---- W (VFP9 Blob): a 0 pointer, NOT raw blob bytes -----------------------

    [Fact]
    public void W_Blob_WritesZeroPointer_NotRawBytes()
    {
        // 'W' (Blob) is an FPT-backed 4-byte block pointer decoded identically to 'M'
        // (FieldDecoder groups M/V/Q/W/G; DbfRecord dereferences M/W as an FPT pointer).
        // D0 must emit a 0 pointer sentinel — writing the blob's first 4 bytes here would
        // forge a garbage block pointer. MUST-FIX: 'W' shares the 'M' clear-to-zero branch,
        // it must NOT fall to the default WriteRawBytes path.
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.Equal(new byte[4], Encode(Col('W', 4), payload, 4)); // non-null blob → 0 pointer
        Assert.Equal(new byte[4], Encode(Col('W', 4), null, 4));    // null → 0 pointer
    }

    // ---- binary / General: raw bytes; null → zero bytes ------------------------

    [Fact]
    public void Binary_General_RawBytes_NullZeroFilled()
    {
        // A 'G' (General) / binary field round-trips a byte[] payload verbatim, zero-padded.
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.Equal(payload, Encode(Col('G', 4), payload, 4));
        Assert.Equal(new byte[4], Encode(Col('G', 4), null, 4)); // null → zero bytes
    }

    // ---- V (Varchar): value + trailing length byte; "varlen used" return -------

    [Fact]
    public void Varchar_Short_WritesLengthByte_AndReturnsVarlenUsed()
    {
        // "Bad Meets Evil" (14 bytes) in V(250) → value, space padding, last byte = 14,
        // and Encode returns true (caller must set the varlen bit). Mirrors dbase_32.dbf.
        var dest = new byte[250];
        bool varlenUsed = FieldEncoder.Encode(Col('V', 250, 0, 0x04), "Bad Meets Evil", Encoding.Latin1, dest);

        Assert.True(varlenUsed);
        Assert.Equal(Encoding.Latin1.GetBytes("Bad Meets Evil"), dest[..14]);
        Assert.All(dest[14..249], b => Assert.Equal(0x20, b)); // space padding (matches fixture)
        Assert.Equal(14, dest[249]);                            // length byte in the LAST byte
    }

    [Fact]
    public void Varchar_FullWidth_NoLengthByte_ReturnsFalse()
    {
        // A value that exactly fills the field width carries NO length byte and NO varlen
        // bit: the decoder reads the full width. Returns false.
        var dest = new byte[4];
        bool varlenUsed = FieldEncoder.Encode(Col('V', 4, 0, 0x04), "ABCD", Encoding.Latin1, dest);

        Assert.False(varlenUsed);
        Assert.Equal(Encoding.Latin1.GetBytes("ABCD"), dest);
    }

    [Fact]
    public void Varbinary_Short_SpacePads_WritesLength_AndCoercesString()
    {
        var dest = new byte[5];

        bool varlenUsed = FieldEncoder.Encode(Col('Q', 5, 0, 0x04), "AB", Encoding.Latin1, dest);

        Assert.True(varlenUsed);
        // VFP9 pads a Varbinary with 0x20, as a Varchar (oracle-pinned by MicroVfpAppendBlankFieldsVfp9OracleTests).
        Assert.Equal(new byte[] { 0x41, 0x42, 0x20, 0x20, 0x02 }, dest);
    }

    [Fact]
    public void Varbinary_FullWidth_HasNoLengthByte()
    {
        var dest = new byte[4];

        bool varlenUsed = FieldEncoder.Encode(Col('Q', 4, 0, 0x04), new byte[] { 1, 2, 3, 4 }, Enc, dest);

        Assert.False(varlenUsed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, dest);
    }

    [Fact]
    public void CharacterAndVarchar_TruncateUtf8OnlyAtCharacterBoundary()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x20 }, Encode(Col('C', 3), "éé", 3, utf8));

        var varchar = new byte[3];
        bool varlenUsed = FieldEncoder.Encode(Col('V', 3, 0, 0x04), "éé", utf8, varchar);
        Assert.True(varlenUsed);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x02 }, varchar);
    }

    [Fact]
    public void CharacterAndVarchar_WidthOne_DegradeWithoutPartialUtf8OrDbcsBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encodings = new[]
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        };
        string[] values = ["é", "あ"];

        for (int i = 0; i < encodings.Length; i++)
        {
            Assert.Equal(new byte[] { 0x20 }, Encode(Col('C', 1), values[i], 1, encodings[i]));

            var varchar = new byte[1];
            Assert.True(FieldEncoder.Encode(Col('V', 1, 0, 0x04), values[i], encodings[i], varchar));
            Assert.Equal(new byte[] { 0x00 }, varchar);
        }
    }

    [Fact]
    public void Character_EncodingFallback_IsWrittenOnlyWhenItFits()
    {
        var utf8WithReplacement = new UTF8Encoding(false, false);
        const string invalidSurrogate = "\uD800";

        Assert.Equal(new byte[] { 0x20 }, Encode(Col('C', 1), invalidSurrogate, 1, utf8WithReplacement));
        Assert.Equal(new byte[] { (byte)'?' }, Encode(Col('C', 1), invalidSurrogate, 1, Encoding.ASCII));
    }
}
