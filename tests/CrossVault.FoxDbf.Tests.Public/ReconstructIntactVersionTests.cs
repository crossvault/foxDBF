using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// <see cref="Recovery.Reconstruct"/> keeps an intact, recognized version byte when the on-disk
/// HeaderLength matches its layout (offset(0x0D) + 1, plus 263 for the VFP backlink). Only the record
/// count is rebuilt, from the file length. Before, a VFP free table of only C (and M) columns had no
/// discriminator, was inferred as dBase 0x03 without the backlink, and read the 263 backlink bytes as
/// records: 5 records came back as 29 (Paket R5; VFP9 at SET TABLEVALIDATE TO 0 reads 5 when the header
/// count is patched to 8).
/// </summary>
public sealed class ReconstructIntactVersionTests
{
    private const int Rows = 5;

    public static TheoryData<byte, bool, bool, int> Layouts()
    {
        var data = new TheoryData<byte, bool, bool, int>();
        foreach (byte version in new byte[] { 0x30, 0x31 })
            foreach (bool memo in new[] { false, true })
                foreach (bool dbc in new[] { false, true })
                    foreach (int count in new[] { 8, 3, Rows })
                        data.Add(version, memo, dbc, count);
        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Reconstruct_trusts_an_intact_vfp_version_and_header_length(byte version, bool memo, bool dbc, int headerCount)
    {
        byte[] bytes = BuildVfp(version, memo, dbc, headerCount);
        int expectedHeader = 32 + (memo ? 2 : 1) * 32 + 1 + 263;

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(version, table.Version.Code);
        Assert.Equal(expectedHeader, table.HeaderLength);
        Assert.Equal(1 + 10 + (memo ? 4 : 0), table.RecordLength);
        Assert.Equal(Rows, table.RecordCount);
        Assert.Equal(
            Enumerable.Range(1, Rows).Select(i => "r" + i),
            table.EnumerateAll(includeDeleted: true).Select(r => r.GetString("C")!.TrimEnd()));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(3)]
    public void Reconstruct_still_reads_a_dbase_iii_table_with_a_patched_count(int headerCount)
    {
        const int header = 32 + 32 + 1;
        var bytes = new byte[header + Rows * 11 + 1];
        bytes[0] = 0x03;
        WriteGeometry(bytes, headerCount, header, 11);
        WriteDescriptor(bytes.AsSpan(32, 32), "C", 'C', 10);
        bytes[header - 1] = 0x0D;
        WriteRows(bytes, header, 11);

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(0x03, table.Version.Code);
        Assert.Equal(header, table.HeaderLength);
        Assert.Equal(Rows, table.RecordCount);
        Assert.Equal("r5", table.GetRecord(Rows - 1)!.Value.GetString("C")!.TrimEnd());
    }

    [Fact]
    public void Reconstruct_infers_the_version_when_the_header_length_does_not_match_the_version_byte()
    {
        // 0x31 on disk, but HeaderLength claims the dBase layout (no backlink) while the file has the
        // 263-byte backlink: the pair is not trusted and the descriptors decide (I column -> VFP 0x30).
        byte[] bytes = BuildVfp(0x31, memo: false, dbc: false, headerCount: Rows, withInteger: true);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), 32 + 2 * 32 + 1);

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(0x30, table.Version.Code);
        Assert.Equal(32 + 2 * 32 + 1 + 263, table.HeaderLength);
        Assert.Equal(Rows, table.RecordCount);
        Assert.Equal("r5", table.GetRecord(Rows - 1)!.Value.GetString("C")!.TrimEnd());
    }

    [Fact]
    public void Reconstruct_keeps_the_vfp_version_of_a_file_cut_inside_the_backlink()
    {
        // HeaderLength 328 matches 0x30, but the file ends at byte 200: no record is complete. Inferring
        // 0x03 without the backlink would read the backlink's NUL bytes as 12 records.
        byte[] bytes = BuildVfp(0x30, memo: false, dbc: false, headerCount: Rows)[..200];

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(0x30, table.Version.Code);
        Assert.Equal(0, table.RecordCount);
    }

    [Theory]
    [InlineData((byte)0x83)]
    [InlineData((byte)0x8B)]
    [InlineData((byte)0xF5)]
    public void Reconstruct_keeps_an_intact_memo_version_of_the_dbase_layout(byte version)
    {
        byte[] bytes = BuildDbase(version, memo: true);

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(version, table.Version.Code);
        Assert.Equal(32 + 2 * 32 + 1, table.HeaderLength);
        Assert.Equal(Rows, table.RecordCount);
        Assert.Equal("r5", table.GetRecord(Rows - 1)!.Value.GetString("C")!.TrimEnd());
    }

    [Fact]
    public void Reconstruct_does_not_keep_a_version_without_memo_file_for_a_table_with_memo_columns()
    {
        // 0x03 has no memo file; keeping it would read every M value as null. The inference picks 0x83.
        byte[] bytes = BuildDbase(0x03, memo: true);

        using var table = DbfTable.Open(new MemoryStream(bytes), new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(0x83, table.Version.Code);
        Assert.NotEqual(MemoKind.None, table.Version.MemoKind);
        Assert.Equal(Rows, table.RecordCount);
    }

    /// <summary>A dBase-layout table (no backlink): C(10) and an optional M(10) memo pointer, five
    /// records, header count 8.</summary>
    private static byte[] BuildDbase(byte version, bool memo)
    {
        int descriptors = memo ? 2 : 1;
        int header = 32 + descriptors * 32 + 1;
        int recordLength = 1 + 10 + (memo ? 10 : 0);
        var bytes = new byte[header + Rows * recordLength + 1];
        bytes[0] = version;
        WriteGeometry(bytes, 8, header, recordLength);
        WriteDescriptor(bytes.AsSpan(32, 32), "C", 'C', 10);
        if (memo) WriteDescriptor(bytes.AsSpan(64, 32), "M", 'M', 10);
        bytes[header - 1] = 0x0D;
        WriteRows(bytes, header, recordLength);
        if (memo)
            for (int i = 0; i < Rows; i++)
                bytes.AsSpan(header + i * recordLength + 11, 10).Fill(0x20);
        return bytes;
    }

    /// <summary>A VFP table: C(10), then an optional M(4) memo pointer (or an I(4) when
    /// <paramref name="withInteger"/>), the 263-byte backlink (a ".dbc" path when <paramref name="dbc"/>),
    /// five records and the 0x1A EOF byte, with the header record count set to <paramref name="headerCount"/>.</summary>
    private static byte[] BuildVfp(byte version, bool memo, bool dbc, int headerCount, bool withInteger = false)
    {
        int descriptors = memo || withInteger ? 2 : 1;
        int header = 32 + descriptors * 32 + 1 + 263;
        int recordLength = 1 + 10 + (descriptors == 2 ? 4 : 0);
        var bytes = new byte[header + Rows * recordLength + 1];
        bytes[0] = version;
        if (memo) bytes[28] = 0x02;
        WriteGeometry(bytes, headerCount, header, recordLength);
        WriteDescriptor(bytes.AsSpan(32, 32), "C", 'C', 10);
        if (memo) WriteDescriptor(bytes.AsSpan(64, 32), "M", 'M', 4);
        else if (withInteger) WriteDescriptor(bytes.AsSpan(64, 32), "ID", 'I', 4);
        int terminator = 32 + descriptors * 32;
        bytes[terminator] = 0x0D;
        if (dbc) Encoding.ASCII.GetBytes("..\\data\\probe.dbc").CopyTo(bytes.AsSpan(terminator + 1));
        WriteRows(bytes, header, recordLength);
        return bytes;
    }

    private static void WriteRows(byte[] bytes, int header, int recordLength)
    {
        for (int i = 0; i < Rows; i++)
        {
            int at = header + i * recordLength;
            bytes[at] = 0x20;
            bytes.AsSpan(at + 1, 10).Fill(0x20);
            Encoding.ASCII.GetBytes("r" + (i + 1)).CopyTo(bytes.AsSpan(at + 1));
        }
        bytes[^1] = 0x1A;
    }

    private static void WriteGeometry(byte[] bytes, int count, int header, int recordLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)count);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), checked((ushort)header));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), checked((ushort)recordLength));
    }

    private static void WriteDescriptor(Span<byte> descriptor, string name, char type, byte length)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(descriptor);
        descriptor[11] = (byte)type;
        descriptor[16] = length;
    }
}
