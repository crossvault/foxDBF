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
