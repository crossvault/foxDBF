using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Byte-level decode tests for <see cref="FieldDecoder"/> (plan §A5/§A13). Expected
/// values are cross-checked against the Ruby <c>dbf</c> gem (5.1.1) and pinned here as
/// literals so the tests are self-contained. Adversarial focus: blank sentinels,
/// invalid dates, binary edge bytes, and the never-throw contract (§A10).
/// </summary>
public sealed class FieldDecoderTests
{
    // ASCII-compatible single-byte encoding for character fields (CP1252 ≈ Latin1 on ASCII).
    private static readonly Encoding Enc = Encoding.Latin1;

    private static DbfColumn Col(char type, int length, int dec = 0)
        => new("F", type, length, dec, 0);

    private static object? Decode(char type, int dec, params byte[] raw)
        => FieldDecoder.Decode(Col(type, raw.Length, dec), raw, Enc);

    // ---- C (character) ---------------------------------------------------

    [Fact]
    public void C_TrimsTrailingSpaces()
    {
        var raw = Encoding.Latin1.GetBytes("Hello     "); // 5 trailing spaces
        Assert.Equal("Hello", FieldDecoder.Decode(Col('C', raw.Length), raw, Enc));
    }

    [Fact]
    public void C_AllSpaces_EmptyString()
    {
        var raw = Encoding.Latin1.GetBytes("          "); // 10 spaces
        Assert.Equal("", FieldDecoder.Decode(Col('C', raw.Length), raw, Enc));
    }

    [Fact]
    public void C_StripsLeadingSpacesAndTrailingNuls()
    {
        // Leading spaces + embedded value + trailing NUL padding. Per spec §A5
        // (Trim) and the ruby String#strip oracle, BOTH ends are stripped — note
        // ruby's strip set includes NUL, which .NET's plain Trim() does not.
        byte[] raw = [(byte)' ', (byte)' ', (byte)'H', (byte)'i', 0x00, 0x00];
        Assert.Equal("Hi", FieldDecoder.Decode(Col('C', raw.Length), raw, Enc));
    }

    [Fact]
    public void ZeroLength_AlwaysNull()
    {
        Assert.Null(FieldDecoder.Decode(Col('C', 0), ReadOnlySpan<byte>.Empty, Enc));
    }

    // ---- N (numeric) -----------------------------------------------------

    [Fact]
    public void N_Dec0_ParsesLong()
    {
        var raw = Encoding.Latin1.GetBytes("  36"); // leading-space padded, dec=0
        var v = FieldDecoder.Decode(Col('N', raw.Length, 0), raw, Enc);
        Assert.IsType<long>(v);
        Assert.Equal(36L, v);
    }

    [Fact]
    public void N_DecGreaterThanZero_ParsesDecimal()
    {
        var raw = Encoding.Latin1.GetBytes("19.00");
        var v = FieldDecoder.Decode(Col('N', raw.Length, 2), raw, Enc);
        Assert.IsType<decimal>(v);
        Assert.Equal(19.00m, (decimal)v!);
    }

    [Fact]
    public void N_Negative_Decimal()
    {
        var raw = Encoding.Latin1.GetBytes("-2.50");
        Assert.Equal(-2.50m, (decimal)FieldDecoder.Decode(Col('N', raw.Length, 2), raw, Enc)!);
    }

    [Fact]
    public void N_AllSpaces_Null()
    {
        var raw = Encoding.Latin1.GetBytes("     ");
        Assert.Null(FieldDecoder.Decode(Col('N', raw.Length, 0), raw, Enc));
        Assert.Null(FieldDecoder.Decode(Col('N', raw.Length, 2), raw, Enc));
    }

    [Fact]
    public void N_Junk_NeverThrows()
    {
        var raw = Encoding.Latin1.GetBytes("**.**"); // overflow/garbage placeholder
        var ex = Record.Exception(() => FieldDecoder.Decode(Col('N', raw.Length, 2), raw, Enc));
        Assert.Null(ex);
    }

    // ---- F (float) -------------------------------------------------------

    [Fact]
    public void F_ParsesDouble()
    {
        var raw = Encoding.Latin1.GetBytes("3.14");
        var v = FieldDecoder.Decode(Col('F', raw.Length), raw, Enc);
        Assert.IsType<double>(v);
        Assert.Equal(3.14, (double)v!, 10);
    }

    [Fact]
    public void F_Blank_Null()
    {
        var raw = Encoding.Latin1.GetBytes("       ");
        Assert.Null(FieldDecoder.Decode(Col('F', raw.Length), raw, Enc));
    }

    // ---- I (4-byte signed int LE) ---------------------------------------

    [Fact]
    public void I_LittleEndian()
    {
        // 100 = 0x64 ; 250 = 0xFA
        Assert.Equal(100, Decode('I', 0, 0x64, 0x00, 0x00, 0x00));
        Assert.Equal(250, Decode('I', 0, 0xFA, 0x00, 0x00, 0x00));
        Assert.Equal(-1, Decode('I', 0, 0xFF, 0xFF, 0xFF, 0xFF));
    }

    // ---- Y (currency: int64 LE / 10000) ---------------------------------

    [Fact]
    public void Y_Currency_Positive()
    {
        // int64 10000 LE -> 1.0m
        var v = Decode('Y', 4, 0x10, 0x27, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        Assert.IsType<decimal>(v);
        Assert.Equal(1.0m, (decimal)v!);
    }

    [Fact]
    public void Y_Currency_Negative()
    {
        // int64 -25000 LE -> -2.5m
        var v = Decode('Y', 4, 0x58, 0x9E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);
        Assert.Equal(-2.5m, (decimal)v!);
    }

    // ---- B (IEEE-754 double LE) -----------------------------------------

    [Fact]
    public void B_DoubleLittleEndian()
    {
        // 1234.5 little-endian IEEE754
        var v = Decode('B', 0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x4A, 0x93, 0x40);
        Assert.IsType<double>(v);
        Assert.Equal(1234.5, (double)v!, 10);
    }

    // ---- D (YYYYMMDD date) ----------------------------------------------

    [Fact]
    public void D_ValidDate()
    {
        var raw = Encoding.Latin1.GetBytes("20120730");
        Assert.Equal(new DateOnly(2012, 7, 30), FieldDecoder.Decode(Col('D', 8), raw, Enc));
    }

    [Fact]
    public void D_AllSpaces_Null()
    {
        var raw = Encoding.Latin1.GetBytes("        ");
        Assert.Null(FieldDecoder.Decode(Col('D', 8), raw, Enc));
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("99999999")]
    [InlineData("2012073")]  // not 8 digits
    public void D_Invalid_Null(string text)
    {
        var raw = Encoding.Latin1.GetBytes(text.PadRight(8));
        Assert.Null(FieldDecoder.Decode(Col('D', raw.Length), raw, Enc));
    }

    // ---- T / @ (Julian day + ms since midnight, 2x int32 LE) ------------

    [Fact]
    public void T_JulianPlusMillis()
    {
        // days = 2456093 (2012-06-14), ms = 51915000 (14:25:15)
        var v = Decode('T', 0, 0x1D, 0x7A, 0x25, 0x00, 0xF8, 0x28, 0x18, 0x03);
        Assert.IsType<DateTime>(v);
        Assert.Equal(new DateTime(2012, 6, 14, 14, 25, 15), (DateTime)v!);
    }

    [Fact]
    public void At_BehavesLikeT()
    {
        var v = Decode('@', 0, 0x1D, 0x7A, 0x25, 0x00, 0xF8, 0x28, 0x18, 0x03);
        Assert.Equal(new DateTime(2012, 6, 14, 14, 25, 15), (DateTime)v!);
    }

    [Fact]
    public void T_DayZero_Null()
    {
        Assert.Null(Decode('T', 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(86_399_499, 2012, 6, 14, 23, 59, 59)]
    [InlineData(86_399_500, 2012, 6, 14, 23, 59, 59)]
    [InlineData(86_399_501, 2012, 6, 15, 0, 0, 0)]
    [InlineData(86_399_999, 2012, 6, 15, 0, 0, 0)]
    [InlineData(86_400_000, 2012, 6, 15, 0, 0, 0)]
    public void T_EndOfDayBoundary_MatchesVfp9(int millis, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.Equal(new DateTime(year, month, day, hour, minute, second), DecodeDateTime(2_456_093, millis));
    }

    [Theory]
    [InlineData(35_999_499, 9, 59, 59)]
    [InlineData(35_999_500, 10, 0, 0)]
    [InlineData(35_999_998, 10, 0, 0)]
    [InlineData(35_999_999, 10, 0, 0)]
    public void T_NonBoundaryMillis_RoundToNearestSecond(int millis, int hour, int minute, int second)
    {
        Assert.Equal(new DateTime(2012, 6, 14, hour, minute, second), DecodeDateTime(2_456_093, millis));
    }

    [Fact]
    public void T_MaxDateRollover_NeverThrowsAndDegradesToSameDateMidnight()
    {
        int maxJulianDay = 1_721_426 + DateOnly.MaxValue.DayNumber;

        var exception = Record.Exception(() => DecodeDateTime(maxJulianDay, 86_399_501));

        Assert.Null(exception);
        Assert.Equal(new DateTime(9999, 12, 31, 0, 0, 0), DecodeDateTime(maxJulianDay, 86_399_501));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_400_001)]
    [InlineData(int.MaxValue)]
    public void T_CorruptMillis_DegradeToSameDateMidnight(int millis)
    {
        Assert.Equal(new DateTime(2012, 6, 14, 0, 0, 0), DecodeDateTime(2_456_093, millis));
    }

    [Theory]
    [InlineData(86_399_499, 30, 23, 59, 59)]
    [InlineData(86_399_500, 31, 0, 0, 0)]
    [InlineData(86_399_999, 31, 0, 0, 0)]
    public void T_LateDateHalfSecond_MatchesVfp9(int millis, int day, int hour, int minute, int second)
    {
        Assert.Equal(new DateTime(9998, 12, day, hour, minute, second), DecodeDateTime(5_373_118, millis));
    }

    private static DateTime DecodeDateTime(int julianDay, int millis)
    {
        Span<byte> raw = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(raw, julianDay);
        BinaryPrimitives.WriteInt32LittleEndian(raw[4..], millis);
        return Assert.IsType<DateTime>(FieldDecoder.Decode(Col('T', 8), raw, Enc));
    }

    // ---- L (logical) -----------------------------------------------------

    [Theory]
    [InlineData((byte)'Y', true)]
    [InlineData((byte)'y', true)]
    [InlineData((byte)'T', true)]
    [InlineData((byte)'t', true)]
    [InlineData((byte)'N', false)]
    [InlineData((byte)'n', false)]
    [InlineData((byte)'F', false)]
    [InlineData((byte)'f', false)]
    [InlineData((byte)'0', false)]
    public void L_TrueFalse(byte b, bool expected)
    {
        var v = FieldDecoder.Decode(Col('L', 1), new[] { b }, Enc);
        Assert.IsType<bool>(v);
        Assert.Equal(expected, (bool)v!);
    }

    [Fact]
    public void L_Space_False()
    {
        // §A5 line 206 ("sonst/space → false") and the ruby gem (Boolean#type_cast,
        // blank_value=false): a 0x20 space byte — the common uninitialized value —
        // decodes to false, NOT null. Returning null would make GetBoolean propagate
        // null and diverge from the golden-master CSV.
        var v = FieldDecoder.Decode(Col('L', 1), new[] { (byte)' ' }, Enc);
        Assert.IsType<bool>(v);
        Assert.False((bool)v!);
    }

    [Theory]
    [InlineData((byte)0x20)] // space (uninitialized)
    [InlineData((byte)0x00)] // NUL
    [InlineData((byte)'?')]  // junk
    [InlineData((byte)'x')]  // unrecognized letter
    public void L_UnrecognizedByte_DegradesToFalse(byte b)
    {
        // Every non-{Y,y,T,t} byte degrades to false (not null), matching §A5 and ruby.
        var v = FieldDecoder.Decode(Col('L', 1), new[] { b }, Enc);
        Assert.IsType<bool>(v);
        Assert.False((bool)v!);
    }

    // ---- + (auto-increment: 4-byte LITTLE-endian int32, identical to 'I') ----
    // §A5b correction (verified): VFP stores '+' per-record as int32 LE, physically
    // identical to 'I'. ruby-dbf's big-endian MSB-as-sign bitfield is a faithfully
    // reproduced bug; the §A5b correction overrides §A5 and wins. go-dbase agrees.

    [Fact]
    public void AutoIncrement_One_LittleEndian()
    {
        // A real value of 1 -> LE bytes 01 00 00 00. (The old BE bitfield decoded
        // this to -16777216; the LE reading is the verified-correct value.)
        Assert.Equal(1, Decode('+', 0, 0x01, 0x00, 0x00, 0x00));
    }

    [Fact]
    public void AutoIncrement_DecodesAsLittleEndianInt32()
    {
        // 0x80,0x00,0x00,0x05 read LE -> 0x05000080 = 83886208 (NOT the ruby BE 5).
        Assert.Equal(83_886_208, Decode('+', 0, 0x80, 0x00, 0x00, 0x05));
        // 0x00,0x00,0x00,0x05 read LE -> 0x05000000 = 83886080 (NOT the ruby BE -5).
        Assert.Equal(83_886_080, Decode('+', 0, 0x00, 0x00, 0x00, 0x05));
    }

    [Fact]
    public void AutoIncrement_MatchesIntDecode()
    {
        // '+' must decode identically to 'I' for the same bytes.
        byte[] bytes = [0xFA, 0x00, 0x00, 0x00];
        Assert.Equal(Decode('I', 0, bytes), Decode('+', 0, bytes));
    }

    [Fact]
    public void AutoIncrement_ShortBuffer_Null()
    {
        Assert.Null(Decode('+', 0, 0x01, 0x00, 0x00)); // < 4 bytes
    }

    // ---- 0 (_NullFlags) and unmapped types -> raw string ----------------

    [Fact]
    public void UnmappedType_FallsBackToString()
    {
        var raw = Encoding.Latin1.GetBytes("xyz");
        var v = FieldDecoder.Decode(Col('Z', raw.Length), raw, Enc); // 'Z' is unmapped
        Assert.IsType<string>(v);
    }
}
