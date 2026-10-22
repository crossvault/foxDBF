using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §D8 / Design Peak 7 — TRUE LATE MATERIALIZATION for the Rushmore residual scan.
///
/// THE INEFFICIENCY this suite pins down: <see cref="QueryOptimizer.FindRecords"/> builds an
/// index candidate bitmap, then runs the residual as
/// <c>foreach (rec in table.EnumerateAll(includeDeleted:true)) { if (!candidate) continue; eval }</c>.
/// So even a HIGHLY SELECTIVE optimized query READS EVERY physical record buffer and only skips the
/// compiled-filter EVAL for non-candidates. <c>RecordsScanned</c> reports the eval count (tiny), but
/// the actual record READS are O(table). The index narrows the eval, not the I/O.
///
/// THE FIX (Peak 7): on the OPTIMIZED path (a candidate set exists — today the SET DELETED ON case),
/// iterate ONLY the candidate physical indices and read each via random access
/// (<see cref="DbfTable.GetRecord(int)"/>) instead of <see cref="DbfTable.EnumerateAll(bool)"/> over
/// the whole table. The NON-optimized path and the deliberately full-scanning SET DELETED OFF path
/// keep <see cref="DbfTable.EnumerateAll(bool)"/>.
///
/// TWO test families:
///   (1) RESULT-INVARIANCE (the MUST — passes today AND after the fix): for many filter shapes,
///       across SET EXACT on/off × SET DELETED on/off, the optimizer returns EXACTLY the brute-force
///       full-scan set — including deleted-but-matching rows (OFF includes, ON excludes), a candidate
///       that is deleted (skipped under ON, present under OFF), and memo / nullable fields that the
///       residual must still read correctly through the random-access path.
///   (2) READ-REDUCTION (the VALUE — currently FAILS, deterministic, NOT timing): a counting stream
///       wrapper measures how many physical record buffers are actually READ. A SELECTIVE optimized
///       query must read approximately the candidate count, NOT the whole table. The non-optimized
///       and SET DELETED OFF paths must still read every record.
///
/// SAFETY: every fixture is a TEMP file, deleted on dispose. No committed fixture is touched.
/// </summary>
public sealed class QueryOptimizerLateMaterializationTests
    : IClassFixture<QueryOptimizerLateMaterializationTests.LargeTable>
{
    private readonly LargeTable _fx;
    public QueryOptimizerLateMaterializationTests(LargeTable fx) => _fx = fx;

    // ============================================================ read-counting stream

    /// <summary>
    /// A pass-through <see cref="Stream"/> that counts how many PHYSICAL record buffers the
    /// reader actually reads. Both <see cref="DbfTable.EnumerateAll(bool)"/> and
    /// <see cref="DbfTable.GetRecord(int)"/> read a record by FIRST seeking to its data-region
    /// offset (<c>HeaderLength + index*RecordLength</c>, <see cref="SeekOrigin.Begin"/>) and then
    /// reading <c>RecordLength</c> bytes — exactly ONE such seek per record read. So counting
    /// begin-seeks into the data region (offset ≥ <see cref="DataStart"/>) yields the record-read
    /// count, robust to any read splitting. Header / descriptor parsing seeks only BELOW
    /// <see cref="DataStart"/>, and we <see cref="Reset"/> after open anyway.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        /// <summary>The byte offset of the first data record; begin-seeks at/after this count as reads.</summary>
        public long DataStart { get; set; } = long.MaxValue;

        /// <summary>The number of physical record buffers read since the last <see cref="Reset"/>.</summary>
        public int RecordReads { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public void Reset() => RecordReads = 0;

        public override long Seek(long offset, SeekOrigin origin)
        {
            long pos = _inner.Seek(offset, origin);
            // A record read always seeks to its absolute data-region offset before reading.
            if (origin == SeekOrigin.Begin && offset >= DataStart) RecordReads++;
            return pos;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Open the dbf through a <see cref="CountingStream"/> and the cdx by path; counter zeroed at the data start.</summary>
    private static (DbfTable table, CdxFile cdx, CountingStream counter) OpenCounted(string dbf, string cdx)
    {
        var fs = new FileStream(dbf, FileMode.Open, FileAccess.Read, FileShare.Read);
        var counter = new CountingStream(fs);
        var table = DbfTable.Open(counter, leaveOpen: false); // table owns (and disposes) the counter+fs
        var cdxFile = CdxFile.Open(cdx, table);
        counter.DataStart = table.HeaderLength;
        counter.Reset();
        return (table, cdxFile, counter);
    }

    // ============================================================ brute-force ground truth

    private sealed class DbfRow : IRowContext
    {
        private readonly DbfRecord _rec;
        public DbfRow(DbfRecord rec, int recNo, int recCount) { _rec = rec; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec.GetExpressionValue(name);
    }

    /// <summary>
    /// Ground truth honouring SET DELETED: compile the filter under <paramref name="ctx"/> and
    /// evaluate it over EVERY physical record (deleted included) in recno order; when SET DELETED
    /// is ON the implicit <c>AND NOT DELETED()</c> skips deleted rows before the filter.
    /// </summary>
    private static List<int> BruteForce(DbfTable table, string filter, EvaluationContext ctx)
    {
        var compiled = VfpExpression.Parse(filter).Compile(ctx);
        var hits = new List<int>();
        int recno = 0;
        foreach (var rec in table.EnumerateAll(includeDeleted: true))
        {
            recno++;
            if (ctx.Deleted && rec.IsDeleted) continue;
            var v = compiled(new DbfRow(rec, recno, table.RecordCount));
            if (v.Type == VfpType.Logical && v.AsLogical)
                hits.Add(recno);
        }
        return hits;
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();

    // ============================================================ (1) RESULT-INVARIANCE (must)

    /// <summary>
    /// The headline INVARIANT: across every SET EXACT × SET DELETED combination, the optimizer's
    /// record set EQUALS the brute-force full scan for a broad spread of filter shapes (integer /
    /// numeric / char / date equality+range, OR over two tags, NOT, &lt;&gt;, INLIST, a mixed
    /// indexed+residual conjunct, an unindexed field, and memo / nullable residual leaves). This
    /// holds today AND must keep holding after the late-materialization fix — only HOW MANY records
    /// are read may change, never WHICH records match.
    /// </summary>
    [Theory]
    [InlineData("ID = 30")]
    [InlineData("AMOUNT >= 100 AND AMOUNT <= 300")]
    [InlineData("ID = 5 OR AMOUNT = 200")]
    [InlineData("NOT (ID = 30)")]
    [InlineData("AMOUNT <> 200")]
    [InlineData("INLIST(ID, 3, 7, 30)")]
    [InlineData("BETWEEN(AMOUNT, 50, 150)")]
    [InlineData("NAME = 'al'")]
    [InlineData("DT >= {^2020-01-10}")]
    [InlineData("AMOUNT > 250 AND CODE = 'X'")]     // mixed: indexed AMOUNT + residual CODE
    [InlineData("ID <= 15 AND MEMO = 'MATCH'")]      // selective ID + memo residual (random-access read)
    [InlineData("ID <= 40 AND NN > 50")]             // selective ID + nullable residual (random-access read)
    [InlineData("CODE = 'X'")]                        // unindexed → full-scan fallback
    public void Optimized_Equals_BruteForce_AcrossExactAndDeleted(string filter)
    {
        var (dir, dbf, cdx) = BuildInvarianceTable();
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            foreach (bool exact in new[] { false, true })
            foreach (bool deleted in new[] { false, true })
            {
                var ctx = new EvaluationContext { Exact = exact, Deleted = deleted };
                var expected = BruteForce(table, filter, ctx);
                var result = QueryOptimizer.FindRecords(table, cdxFile, filter, ctx);
                Assert.Equal(
                    Sorted(expected),
                    Sorted(result.RecordNumbers));
            }
        }
        finally { TryDelete(dir); }
    }

    /// <summary>
    /// A candidate that is DELETED must never leak under SET DELETED ON, yet must reappear under OFF.
    /// Recno 30 (ID=30) is deleted but its key is still in the CDX, so the AMOUNT/ID index candidate
    /// set legitimately contains it — the residual (random-access read) must drop it under ON.
    /// </summary>
    [Fact]
    public void DeletedCandidate_SkippedUnderOn_PresentUnderOff()
    {
        var (dir, dbf, cdx) = BuildInvarianceTable();
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            var on = QueryOptimizer.FindRecords(table, cdxFile, "ID = 30", new EvaluationContext { Deleted = true });
            Assert.DoesNotContain(30, on.RecordNumbers);
            Assert.Empty(on.RecordNumbers);

            var off = QueryOptimizer.FindRecords(table, cdxFile, "ID = 30", new EvaluationContext { Deleted = false });
            Assert.Equal(new[] { 30 }, Sorted(off.RecordNumbers));
        }
        finally { TryDelete(dir); }
    }

    /// <summary>
    /// A deleted row whose key falls inside an indexed RANGE: included under OFF, excluded under ON,
    /// and both agree with brute force. Pins the random-access residual's deleted-row handling for a
    /// multi-candidate optimized leaf.
    /// </summary>
    [Fact]
    public void DeletedInRange_IncludedUnderOff_ExcludedUnderOn()
    {
        var (dir, dbf, cdx) = BuildInvarianceTable();
        try
        {
            using var table = DbfTable.Open(dbf);
            using var cdxFile = CdxFile.Open(cdx, table);

            const string filter = "ID >= 1 AND ID <= 60"; // every recno is an index candidate
            var on = QueryOptimizer.FindRecords(table, cdxFile, filter, new EvaluationContext { Deleted = true });
            var off = QueryOptimizer.FindRecords(table, cdxFile, filter, new EvaluationContext { Deleted = false });

            // The three deleted recnos (7, 15, 30) participate under OFF, vanish under ON.
            foreach (int del in new[] { 7, 15, 30 })
            {
                Assert.DoesNotContain(del, on.RecordNumbers);
                Assert.Contains(del, off.RecordNumbers);
            }
            Assert.Equal(Sorted(BruteForce(table, filter, new EvaluationContext { Deleted = true })), Sorted(on.RecordNumbers));
            Assert.Equal(Sorted(BruteForce(table, filter, new EvaluationContext { Deleted = false })), Sorted(off.RecordNumbers));
        }
        finally { TryDelete(dir); }
    }

    // ============================================================ (2) READ-REDUCTION (value; FAILS today)

    /// <summary>
    /// THE VALUE. A SELECTIVE optimized equality (1 candidate out of 5000) must READ approximately
    /// the candidate count — NOT the whole table. TODAY this FAILS: the residual loops
    /// <see cref="DbfTable.EnumerateAll(bool)"/> over all 5000 physical records, reading every buffer
    /// even though only one is a candidate. After the late-materialization fix only the survivor is read.
    /// </summary>
    [Fact]
    public void SelectiveOptimizedQuery_ReadsApproximatelyCandidateCount_NotWholeTable()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            var result = QueryOptimizer.FindRecords(table, cdx, "ID = 1234"); // default ctx = SET DELETED ON

            Assert.Equal(new[] { 1234 }, Sorted(result.RecordNumbers)); // correctness anchor
            Assert.True(result.Optimized);
            // The whole point: only ~1 record buffer should be read, not all 5000.
            Assert.True(counter.RecordReads <= 50,
                $"a single-candidate optimized query should read ~1 record, but read {counter.RecordReads} of {table.RecordCount}");
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    /// <summary>
    /// A selective optimized RANGE (recnos 1000..1010) must read ~11 records, not the whole table.
    /// FAILS today (reads all 5000 via the full <see cref="DbfTable.EnumerateAll(bool)"/> loop).
    /// </summary>
    [Fact]
    public void SelectiveOptimizedRange_ReadsOnlyTheCandidateSlice()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            var result = QueryOptimizer.FindRecords(table, cdx, "ID >= 1000 AND ID <= 1010");

            Assert.Equal(Enumerable.Range(1000, 11).ToArray(), Sorted(result.RecordNumbers));
            Assert.True(result.Optimized);
            Assert.True(counter.RecordReads <= 50,
                $"an 11-candidate range should read ~11 records, but read {counter.RecordReads} of {table.RecordCount}");
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    /// <summary>
    /// A deleted single candidate: the residual must still RANDOM-ACCESS just that one record
    /// (read ~1), find it deleted, and exclude it under SET DELETED ON — not scan the whole table.
    /// FAILS today (full <see cref="DbfTable.EnumerateAll(bool)"/> loop reads all 5000).
    /// </summary>
    [Fact]
    public void SelectiveOptimizedQuery_DeletedCandidate_StillReadsOnlyTheCandidate()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            // Recno 4999 (index 4998) is deleted in the fixture; its ID key is still indexed.
            var result = QueryOptimizer.FindRecords(table, cdx, "ID = 4999"); // SET DELETED ON

            Assert.Empty(result.RecordNumbers); // deleted → excluded under ON
            Assert.True(counter.RecordReads <= 50,
                $"a single (deleted) candidate should read ~1 record, but read {counter.RecordReads} of {table.RecordCount}");
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    // ---- guards: the paths that MUST still read every record (pass today and after) ----

    /// <summary>
    /// An UNINDEXED filter has no candidate set, so it deliberately full-scans: it must read every
    /// physical record. This guards that the fix never starves the legitimate full-scan path.
    /// </summary>
    [Fact]
    public void UnindexedFilter_StillReadsEveryRecord()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            var result = QueryOptimizer.FindRecords(table, cdx, "CODE = 'X'");

            Assert.False(result.Optimized);
            Assert.NotEmpty(result.RecordNumbers);
            // Full scan over every physical record (EnumerateAll includeDeleted:true).
            Assert.Equal(table.RecordCount, counter.RecordReads);
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    /// <summary>
    /// SET DELETED OFF deliberately full-scans even for an optimizable leaf (the CDX omits deleted
    /// rows, so the bitmap would be incomplete). It must keep reading every physical record. Pass
    /// today and after the fix (which only changes the SET DELETED ON optimized path).
    /// </summary>
    [Fact]
    public void OptimizableLeaf_SetDeletedOff_StillFullScans()
    {
        var (table, cdx, counter) = OpenCounted(_fx.Dbf, _fx.Cdx);
        try
        {
            var ctx = new EvaluationContext { Deleted = false };
            var result = QueryOptimizer.FindRecords(table, cdx, "ID = 1234", ctx);

            Assert.Equal(new[] { 1234 }, Sorted(result.RecordNumbers));
            // OFF path reads every physical record despite the indexable leaf.
            Assert.Equal(table.RecordCount, counter.RecordReads);
        }
        finally { cdx.Dispose(); table.Dispose(); }
    }

    // ============================================================ fixtures

    /// <summary>
    /// Build a 60-row invariance table: ID (Integer, tag), AMOUNT (Numeric, tag, duplicates),
    /// NAME (Character MACHINE, tag), DT (Date, tag), CODE (Character, UNINDEXED), MEMO (memo),
    /// NN (nullable Numeric). Recnos 7, 15 and 30 are deleted (recno 30 = ID 30, an indexable
    /// candidate that is deleted). Returns the temp dir + dbf + cdx paths.
    /// </summary>
    private static (string dir, string dbf, string cdx) BuildInvarianceTable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_latemat_inv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "inv.dbf");
        var cols = new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
            new DbfColumnDef("NAME", 'C', 20),
            new DbfColumnDef("DT", 'D', 8),
            new DbfColumnDef("CODE", 'C', 4),
            new DbfColumnDef("MEMO", 'M', 4),
            new DbfColumnDef("NN", 'N', 10, 0, nullable: true),
        };
        var epoch = new DateTime(2020, 1, 1);
        using (var w = DbfWriter.Create(dbf, cols))
        {
            for (int i = 1; i <= 60; i++)
            {
                int amount = (i % 30) * 10;                       // 0..290, duplicates → range scans
                string name = (i % 2 == 0) ? "alice" : "bob";     // 'al' prefix matches the even rows
                DateTime dt = epoch.AddDays(i);                   // recno i → 2020-01-01 + i days
                string code = (i % 5 == 0) ? "X" : "Y";           // unindexed
                string memo = (i % 3 == 0) ? "MATCH" : "OTHER";   // memo residual
                object? nn = (i % 7 == 0) ? null : i;             // some NULL, some > 50
                w.AppendRecord(new object?[] { i, amount, name, dt, code, memo, nn });
            }
            w.Delete(6);   // recno 7
            w.Delete(14);  // recno 15
            w.Delete(29);  // recno 30 (ID = 30) — a deleted indexable candidate

            w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
            w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            w.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
            w.CreateTag(new CdxTagDefinition("DTTAG", "DT"));
        }
        return (dir, dbf, Path.ChangeExtension(dbf, ".cdx"));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>
    /// A large (5000-row) temp table built ONCE for the read-reduction family: ID (Integer, tag),
    /// AMOUNT (Numeric, tag), CODE (Character, UNINDEXED). Recno 4999 is deleted (a deleted
    /// indexable candidate). Deleted on dispose.
    /// </summary>
    public sealed class LargeTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 5000;

        public LargeTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_latemat_big_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "big.dbf");
            var cols = new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("CODE", 'C', 4),
            };
            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                    w.AppendRecord(new object?[] { i, i % 100, (i % 10 == 0) ? "X" : "Y" });
                w.Delete(4998); // recno 4999 — a deleted indexable candidate
                w.CreateTag(new CdxTagDefinition("IDTAG", "ID"));
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }
            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
