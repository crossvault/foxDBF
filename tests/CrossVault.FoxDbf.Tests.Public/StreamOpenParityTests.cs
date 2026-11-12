using System.Text;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// <see cref="DbfTable.Open(Stream, Stream?, DbfOptions?, bool)"/>: a table opened through streams
/// returns exactly the values it returns through its path (backlog R4) — every fixture, every
/// record, every value incl. memos — also when the streams start at (different) non-zero positions.
/// </summary>
public class StreamOpenParityTests
{
    private const int Prefix = 100;
    private const int MemoPrefix = 37;

    // (dbf start, memo start): the same base, different bases (a dbf/memo base mix-up shows), and a
    // dbf at an offset with its memo at 0.
    private static readonly (int Dbf, int Memo)[] Starts = [(0, 0), (Prefix, MemoPrefix), (Prefix, 0)];

    [Fact]
    public void EveryPublicFixture_StreamOpen_MatchesPathOpen_AtOffsetZeroAndAtOffset()
    {
        var failures = new List<string>();
        int opened = 0, notOpened = 0, withMemo = 0, memoValues = 0;

        foreach (var path in AllFixtureTables())
        {
            DbfTable byPath;
            try { byPath = DbfTable.Open(path); }
            catch { notOpened++; continue; } // only fixtures that open by path are in scope
            using var _ = byPath;
            opened++;

            string? memoPath = FindMemoSidecar(path);
            if (memoPath is not null) withMemo++;

            foreach (var (dbfStart, memoStart) in Starts)
            {
                using var dbf = StreamAt(File.ReadAllBytes(path), dbfStart);
                using var memo = memoPath is null ? null : StreamAt(File.ReadAllBytes(memoPath), memoStart);
                using var byStream = DbfTable.Open(dbf, memo);
                int memos = CompareTables(byPath, byStream,
                    $"{Path.GetRelativePath(Fixtures.RepoRoot, path)} @dbf+{dbfStart}/memo+{memoStart}", failures);
                if (dbfStart == 0) memoValues += memos;
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
        // Not vacuous: the sweep covers the fixture tree and real memo content (counts at writing:
        // all 116 tables open, 22 carry a memo sidecar, 437 non-null memo values).
        Assert.True(opened >= 110, $"only {opened} fixtures opened ({notOpened} did not)");
        Assert.True(withMemo >= 20, $"only {withMemo} fixtures with a memo sidecar");
        Assert.True(memoValues >= 400, $"only {memoValues} non-null memo values compared");
    }

    [Theory]
    [InlineData((byte)0x83)] // dBase III + .dbt
    [InlineData((byte)0x8B)] // dBase IV + .dbt
    public void DbtMemoTable_StreamOpen_MatchesPathOpen(byte version)
    {
        var (dbfBytes, dbtBytes) = BuildDbtTable(version, ["first memo text", "second, a bit longer memo text"]);
        using var dir = new TempDir();
        string dbfPath = Path.Combine(dir.Path, "notes.dbf");
        File.WriteAllBytes(dbfPath, dbfBytes);
        File.WriteAllBytes(Path.Combine(dir.Path, "notes.dbt"), dbtBytes);

        using var byPath = DbfTable.Open(dbfPath);
        Assert.NotNull(byPath.Memo);
        foreach (var (dbfStart, memoStart) in Starts)
        {
            using var byStream = DbfTable.Open(StreamAt(dbfBytes, dbfStart), StreamAt(dbtBytes, memoStart));
            var failures = new List<string>();
            int memos = CompareTables(byPath, byStream, $"0x{version:x2} @dbf+{dbfStart}/memo+{memoStart}", failures);
            Assert.Empty(failures);
            Assert.Equal(2, memos);
        }
    }

    [Fact]
    public void StartOffset_RandomAccessAndMemos_AreRelativeToTheStartPosition()
    {
        string path = Fixtures.Tastrade("employee.dbf");
        using var byPath = DbfTable.Open(path);
        using var byStream = DbfTable.Open(
            StreamAt(File.ReadAllBytes(path), Prefix), StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")), MemoPrefix));

        Assert.NotNull(byStream.Memo);
        Assert.Equal(byPath.RecordCount, byStream.RecordCount);
        for (int i = byPath.RecordCount - 1; i >= 0; i--) // backwards: every read is a fresh seek
        {
            var expected = byPath.GetRecord(i, includeDeleted: true)!.Value;
            var actual = byStream.GetRecord(i, includeDeleted: true)!.Value;
            Assert.True(expected.Raw.SequenceEqual(actual.Raw));
            for (int c = 0; c < byPath.Columns.Count; c++)
                Assert.True(SameValue(Value(expected, c), Value(actual, c)), $"record {i} column {byPath.Columns[c].Name}");
        }
    }

    [Fact]
    public void TableRunsFromTheStartPositionToTheEndOfTheStream()
    {
        // Documented contract: the file spans from the stream's current position to its end. With
        // nothing after the table, an embedded table at offset N reads exactly like its path open
        // (Reconstruct derives the record count from that length, so it pins the end bound too).
        string path = Fixtures.Tastrade("customer.dbf");
        byte[] bytes = File.ReadAllBytes(path);
        var reconstruct = new DbfOptions { Recovery = Recovery.Reconstruct };
        var failures = new List<string>();
        using var byPath = DbfTable.Open(path, reconstruct);
        foreach (var options in new[] { new DbfOptions(), reconstruct })
        {
            using var reference = DbfTable.Open(path, options);
            using var byStream = DbfTable.Open(StreamAt(bytes, 300), memo: null, options);
            CompareTables(reference, byStream, $"customer @+300, {options.Recovery}", failures);
        }

        // A table followed by other data is opened through a stream bounded to the table's length.
        var embedded = new byte[300 + bytes.Length + 700];
        bytes.CopyTo(embedded, 300);
        Array.Fill(embedded, (byte)0x5A, 300 + bytes.Length, 700);
        using var bounded = DbfTable.Open(new MemoryStream(embedded, 300, bytes.Length), memo: null, reconstruct);
        CompareTables(byPath, bounded, "customer bounded inside a larger buffer", failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void OffsetView_LengthAndPosition_AreRelativeToItsBase()
    {
        var inner = StreamAt(new byte[500], Prefix);
        using var view = new OffsetReadStream(inner, Prefix);
        Assert.Equal(500, view.Length);
        Assert.Equal(0, view.Position);
        Assert.Equal(20, view.Seek(-480, SeekOrigin.End));
        Assert.Equal(Prefix + 20, inner.Position);
        Assert.Throws<IOException>(() => view.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void ExistingStreamOverload_StillReadsFromPositionZero()
    {
        // Compatibility: Open(Stream) never honoured the position (a just-written MemoryStream left
        // at its end still opens); only the new overload treats the position as the table start.
        string path = Fixtures.Tastrade("customer.dbf");
        var ms = new MemoryStream(File.ReadAllBytes(path));
        ms.Seek(0, SeekOrigin.End);
        using var byOldOverload = DbfTable.Open(ms);
        using var byPath = DbfTable.Open(path);
        Assert.Equal(byPath.RecordCount, byOldOverload.RecordCount);
        Assert.Equal(byPath.Records.Count(), byOldOverload.Records.Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LeaveOpen_AppliesToBothStreams(bool leaveOpen)
    {
        var dbf = StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.dbf")), Prefix);
        var memo = StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")), Prefix);

        using (var table = DbfTable.Open(dbf, memo, leaveOpen: leaveOpen))
            Assert.NotEmpty(table.Records.ToList());

        Assert.Equal(leaveOpen, dbf.CanRead);
        Assert.Equal(leaveOpen, memo.CanRead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedOpen_DisposesBothStreams_UnlessLeaveOpen(bool leaveOpen)
    {
        var dbf = StreamAt(new byte[64], Prefix); // zeroed header: structurally impossible
        var memo = StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")), 0);

        Assert.Throws<DbfCorruptHeaderException>(() => DbfTable.Open(dbf, memo, leaveOpen: leaveOpen));
        Assert.Equal(leaveOpen, dbf.CanRead);
        Assert.Equal(leaveOpen, memo.CanRead);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonSeekableDbfStream_ThrowsAtOpen(bool leaveOpen)
    {
        var dbf = new NonSeekableStream(File.ReadAllBytes(Fixtures.Tastrade("employee.dbf")));
        var memo = StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")), 0);

        var ex = Assert.Throws<NotSupportedException>(() => DbfTable.Open(dbf, memo, leaveOpen: leaveOpen));
        Assert.Contains("seekable", ex.Message);
        Assert.Equal(!leaveOpen, dbf.IsDisposed);
        Assert.Equal(leaveOpen, memo.CanRead);
    }

    [Fact]
    public void NonSeekableMemoStream_ThrowsAtOpen_InsteadOfReadingNullMemos()
    {
        var dbf = StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.dbf")), 0);
        var memo = new NonSeekableStream(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")));

        var ex = Assert.Throws<NotSupportedException>(() => DbfTable.Open(dbf, memo));
        Assert.Contains("'memo'", ex.Message);
        Assert.False(dbf.CanRead);
        Assert.True(memo.IsDisposed);
    }

    [Fact]
    public void NullMemoStream_ReadsLikeAPathOpenWithoutTheMemoFile()
    {
        using var dir = new TempDir();
        string bare = Path.Combine(dir.Path, "employee.dbf");
        File.Copy(Fixtures.Tastrade("employee.dbf"), bare); // no employee.fpt beside it

        using var byPath = DbfTable.Open(bare);
        using var byStream = DbfTable.Open(StreamAt(File.ReadAllBytes(bare), Prefix), memo: null);

        Assert.Null(byPath.Memo);
        Assert.Null(byStream.Memo);
        Assert.Contains(byStream.Columns, c => c.Type == 'M');
        var failures = new List<string>();
        Assert.Equal(0, CompareTables(byPath, byStream, "employee without memo", failures));
        Assert.Empty(failures);
    }

    [Fact]
    public void NullMemoStream_HonoursMemoPath_GivenMemoStreamWinsOverIt()
    {
        string dbfPath = Fixtures.Tastrade("employee.dbf");
        using var byPath = DbfTable.Open(dbfPath);
        var failures = new List<string>();

        var withMemoPath = new DbfOptions { MemoPath = Fixtures.Tastrade("employee.fpt") };
        using (var viaMemoPath = DbfTable.Open(StreamAt(File.ReadAllBytes(dbfPath), 0), memo: null, withMemoPath))
            Assert.True(CompareTables(byPath, viaMemoPath, "MemoPath", failures) > 0);

        var wrongMemoPath = new DbfOptions { MemoPath = Path.Combine(Path.GetTempPath(), "no-such-memo.fpt") };
        using (var viaStream = DbfTable.Open(StreamAt(File.ReadAllBytes(dbfPath), 0),
                   StreamAt(File.ReadAllBytes(Fixtures.Tastrade("employee.fpt")), 0), wrongMemoPath))
            Assert.True(CompareTables(byPath, viaStream, "memo stream over MemoPath", failures) > 0);

        Assert.Empty(failures);
    }

    [Fact]
    public void MemoStream_ForAVersionWithoutMemo_IsIgnoredAndDisposed()
    {
        var (dbfBytes, dbtBytes) = BuildDbtTable(0x03, ["unused"]);
        var memo = StreamAt(dbtBytes, 0);

        using var table = DbfTable.Open(StreamAt(dbfBytes, 0), memo);

        Assert.Null(table.Memo);
        Assert.False(memo.CanRead);
        Assert.Single(table.Records);
    }

    // ---- helpers ----

    private static IEnumerable<string> AllFixtureTables()
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Fixtures.RepoRoot, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains($"{sep}bin{sep}") && !p.Contains($"{sep}obj{sep}"))
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    // The sidecar a path open discovers: .dct for a .dbc, else .fpt then .dbt (case-insensitive).
    private static string? FindMemoSidecar(string dbfPath)
    {
        string[] exts = dbfPath.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase) ? [".dct"] : [".fpt", ".dbt"];
        return exts.Select(e => SidecarFile.FindCaseInsensitive(SidecarFile.ChangeExtension(dbfPath, e)))
            .FirstOrDefault(p => p is not null);
    }

    // A stream holding `prefix` junk bytes followed by the file, positioned at the file start.
    private static MemoryStream StreamAt(byte[] bytes, int prefix)
    {
        var ms = new MemoryStream(prefix + bytes.Length);
        ms.Write(Enumerable.Repeat((byte)0xA5, prefix).ToArray());
        ms.Write(bytes);
        ms.Position = prefix;
        return ms;
    }

    // Compares geometry, every physical record (incl. deleted) and every decoded value. Returns the
    // number of non-null memo (M/W) values seen, so callers can prove memos were really compared.
    private static int CompareTables(DbfTable expected, DbfTable actual, string label, List<string> failures)
    {
        int memoValues = 0;
        if (expected.Version.Code != actual.Version.Code || expected.RecordCount != actual.RecordCount
            || expected.HeaderLength != actual.HeaderLength || expected.RecordLength != actual.RecordLength
            || expected.Encoding.CodePage != actual.Encoding.CodePage || (expected.Memo is null) != (actual.Memo is null))
        {
            failures.Add($"{label}: table geometry/encoding/memo presence differs");
            return 0;
        }
        var cols = expected.Columns.Select(c => (c.Name, c.Type, c.Length, c.Decimal, c.Offset)).ToList();
        if (!cols.SequenceEqual(actual.Columns.Select(c => (c.Name, c.Type, c.Length, c.Decimal, c.Offset))))
        {
            failures.Add($"{label}: columns differ");
            return 0;
        }

        var exp = expected.EnumerateAll(includeDeleted: true).ToList();
        var act = actual.EnumerateAll(includeDeleted: true).ToList();
        if (exp.Count != act.Count)
        {
            failures.Add($"{label}: {exp.Count} records by path, {act.Count} by stream");
            return 0;
        }
        for (int r = 0; r < exp.Count; r++)
        {
            if (!exp[r].Raw.SequenceEqual(act[r].Raw))
                failures.Add($"{label}: record {r} raw bytes differ");
            for (int c = 0; c < expected.Columns.Count; c++)
            {
                object? e = Value(exp[r], c), a = Value(act[r], c);
                if (!SameValue(e, a))
                    failures.Add($"{label}: record {r} column {expected.Columns[c].Name}: '{e}' vs '{a}'");
                else if (e is not null && expected.Columns[c].Type is 'M' or 'W')
                    memoValues++;
            }
        }
        return memoValues;
    }

    // A decode exception is part of the observable value: both opens must throw the same type.
    private static object? Value(DbfRecord record, int column)
    {
        try { return record[column]; }
        catch (Exception ex) { return "throws " + ex.GetType().Name; }
    }

    private static bool SameValue(object? a, object? b)
        => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    // A minimal two-column (C name, M notes) table in the given version with a .dbt sidecar in the
    // matching layout. dBase III memos are 0x1A-terminated; dBase IV memos carry a FF FF 08 00 block
    // header and a little-endian length. A trailing empty block keeps no memo in the last block.
    private static (byte[] Dbf, byte[] Dbt) BuildDbtTable(byte version, string[] memos)
    {
        const int headerLength = 32 + 2 * 32 + 1, recordLength = 1 + 10 + 10, block = 512;
        var dbf = new MemoryStream();
        var header = new byte[32];
        header[0] = version;
        header[1] = 124; header[2] = 1; header[3] = 1;
        BitConverter.TryWriteBytes(header.AsSpan(4), memos.Length);
        BitConverter.TryWriteBytes(header.AsSpan(8), (short)headerLength);
        BitConverter.TryWriteBytes(header.AsSpan(10), (short)recordLength);
        dbf.Write(header);
        dbf.Write(Descriptor("NAME", 'C'));
        dbf.Write(Descriptor("NOTES", 'M'));
        dbf.WriteByte(0x0D);

        var dbt = new MemoryStream();
        var head = new byte[block];
        BitConverter.TryWriteBytes(head.AsSpan(0), memos.Length + 2);
        dbt.Write(head);
        for (int i = 0; i < memos.Length; i++)
        {
            dbf.WriteByte((byte)' ');
            dbf.Write(Encoding.ASCII.GetBytes($"NAME{i}".PadRight(10)));
            dbf.Write(Encoding.ASCII.GetBytes((i + 1).ToString().PadLeft(10)));

            var text = Encoding.ASCII.GetBytes(memos[i]);
            var memoBlock = new byte[block];
            if (version == 0x8B)
            {
                memoBlock[0] = 0xFF; memoBlock[1] = 0xFF; memoBlock[2] = 0x08;
                BitConverter.TryWriteBytes(memoBlock.AsSpan(4), text.Length);
                text.CopyTo(memoBlock, 8);
            }
            else
            {
                text.CopyTo(memoBlock, 0);
                memoBlock[text.Length] = 0x1A;
                memoBlock[text.Length + 1] = 0x1A;
            }
            dbt.Write(memoBlock);
        }
        dbt.Write(new byte[block]);
        dbf.WriteByte(0x1A);
        return (dbf.ToArray(), dbt.ToArray());

        static byte[] Descriptor(string name, char type)
        {
            var d = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(d, 0);
            d[11] = (byte)type;
            d[16] = 10;
            return d;
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("foxdbf-stream-").FullName;
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
