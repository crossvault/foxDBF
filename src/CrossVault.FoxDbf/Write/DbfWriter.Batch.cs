namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D1 batch append — the throughput counterpart to the per-row <see cref="AppendRecord(object?[])"/>.
/// Where <see cref="AppendRecord(object?[])"/> takes the append lock, re-reads the on-disk
/// <c>RecordCount</c>, writes the row + <c>0x1A</c> EOF, stamps the header AND flushes on EVERY call,
/// <see cref="AppendRecords(System.Collections.Generic.IEnumerable{object?[]})"/> appends the WHOLE
/// batch under ONE append-region lock: re-read the count ONCE, encode + write every record
/// sequentially WITHOUT a per-row flush, write the single <c>0x1A</c> EOF + <c>SetLength</c> once,
/// stamp the header (RecordCount + last-update date) once, flush once. Memo (<c>.fpt</c>) blocks for
/// the batch are appended then flushed once. AutoIncrement advances across the batch exactly as the
/// per-row path would.
/// </summary>
public sealed partial class DbfWriter
{
    /// <summary>
    /// Append every row in <paramref name="rows"/> (each one value per public column, physical order)
    /// as a single batch and return their assigned zero-based record indices in order. AutoIncrement
    /// and memo (<c>M</c>/<c>W</c>/<c>G</c>/<c>P</c>) fields are resolved by the writer regardless of
    /// the value passed. An empty batch is a no-op (the file is left byte-identical, RecordCount
    /// unchanged) and returns an empty list.
    /// </summary>
    /// <remarks>
    /// INVARIANT: the resulting file is BYTE-IDENTICAL to calling
    /// <see cref="AppendRecord(object?[])"/> once per row in order — same records, RecordCount,
    /// <c>0x1A</c> EOF, header last-update date, memo bytes and AutoIncrement values.
    /// <para>
    /// ATOMICITY: the batch is atomic AS A UNIT — it holds ONE append-region lock for the whole run,
    /// so a concurrent VFP runtime cannot interleave a record into the middle of the batch. For
    /// fine-grained per-row coexistence (releasing the lock between rows) use
    /// <see cref="AppendRecord(object?[])"/>. The 2 GB DBF cap is enforced against the projected end
    /// of the batch before any bytes are written, so a refused (too-large) batch leaves the file and
    /// the RecordCount untouched.
    /// </para>
    /// </remarks>
    public System.Collections.Generic.IReadOnlyList<int> AppendRecords(
        System.Collections.Generic.IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var positional = new List<object?[]>();
        foreach (var r in rows)
        {
            ArgumentNullException.ThrowIfNull(r);
            positional.Add(ToPositional(r));
        }
        return AppendCoreBatch(positional);
    }

    /// <summary>
    /// Batch counterpart keyed by column name (missing keys → NULL); returns the assigned
    /// zero-based record indices in order. Same byte-identity and atomic-as-a-unit semantics as
    /// <see cref="AppendRecords(System.Collections.Generic.IEnumerable{object?[]})"/>.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<int> AppendRecords(
        System.Collections.Generic.IEnumerable<System.Collections.Generic.IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var positional = new List<object?[]>();
        foreach (var d in rows)
        {
            ArgumentNullException.ThrowIfNull(d);
            positional.Add(DictionaryToPositional(d));
        }
        return AppendCoreBatch(positional);
    }

    /// <summary>
    /// One-lock / one-flush batch append. Mirrors <see cref="AppendCore(object?[], bool[])"/> exactly so the
    /// on-disk result is byte-identical to N per-row appends, but re-reads the count ONCE, writes
    /// every row WITHOUT a per-row flush, then writes the single <c>0x1A</c> EOF + <c>SetLength</c>,
    /// stamps the header and flushes ONCE for the whole batch.
    /// RecordEncoder encoding/conversion failures are raised during a prepare pass before any
    /// PatchMemoPointers call or DBF/FPT mutation.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<int> AppendCoreBatch(List<object?[]> rows)
    {
        // Empty batch: a true no-op — no lock, no header stamp, no flush, file left byte-identical.
        if (rows.Count == 0)
            return Array.Empty<int>();

        var indices = new int[rows.Count];
        // §5.1: the exact written bytes + 1-based recno of every row, captured for a single batched
        // index-maintenance pass before the append lock is released.
        var appended = new List<(int RecNo, byte[] Record)>(rows.Count);

        // §D3: hold the APPEND lock for the WHOLE batch across the FRESH count re-read + every row
        // write + the single header bump, so a concurrent VFP appender cannot interleave a record
        // into the middle of the batch (atomic AS A UNIT). Released in WithLock's finally. As in the
        // per-row path the on-disk RecordCount (bytes 4–7) is re-read from the handle INSIDE the lock
        // — but ONCE for the batch rather than once per row.
        long appendPos = VfpLock.AppendLockPosition(_usesStructuralScheme);
        WithLock(appendPos, 1, () =>
        {
            CloseCdxMaint(flush: true);
            ValidateIndexes(CdxMaintMode.Append);
            int diskCount = (int)ReadOnDiskRecordCount();
            _recordCount = diskCount;

            int n = rows.Count;
            int finalCount = diskCount + n;
            long finalDataEnd = _headerLength + (long)finalCount * _recordLength;
            long endAfter = finalDataEnd + (WritesEof ? 1 : 0);

            // 2 GB guard (§D-Leitplanken): a DBF addresses records with 32-bit offsets, so the
            // resulting file end must stay below int.MaxValue. Fire BEFORE any mutation so a refused
            // batch leaves the file (and the count) untouched.
            if (endAfter > int.MaxValue)
                throw new DbfWriteException(
                    $"Batch append would grow the file past the 2 GB DBF cap (end offset {endAfter} exceeds int.MaxValue).");

            // AutoIncrement: read each autoinc column's current descriptor Next + Step ONCE, then
            // advance in memory across the batch. The per-row path re-reads the freshly persisted
            // Next each row, which is exactly startNext + k*step for row k — so tracking a running
            // value here and persisting the final advanced value once is byte-identical.
            var columns = _schema.Columns;
            var aiCol = new List<int>();
            var aiOffset = new List<long>();
            var aiRunning = new List<uint>();
            var aiStep = new List<byte>();
            for (int i = 0; i < columns.Count; i++)
            {
                if (!columns[i].IsAutoIncrement)
                    continue;
                long descOffset = _version.HeaderSize + (long)i * _version.DescriptorWidth;
                aiCol.Add(i);
                aiOffset.Add(descOffset + 19);
                aiRunning.Add(ReadU32LittleEndian(descOffset + 19));
                aiStep.Add(ReadByteAt(descOffset + 23));
            }

            // Prepare every record sequentially without any PatchMemoPointers/DBF/FPT mutation.
            // The existing appended buffers are committed only after every RecordEncoder.Encode
            // succeeds, so an encoding/conversion failure leaves both files byte-identical.
            for (int r = 0; r < n; r++)
            {
                var positional = rows[r];
                for (int a = 0; a < aiCol.Count; a++)
                {
                    positional[aiCol[a]] = unchecked((int)aiRunning[a]);
                    aiRunning[a] += aiStep[a];
                }

                byte[] record = RecordEncoder.Encode(_schema, positional, deleted: false);
                indices[r] = diskCount + r;
                appended.Add((diskCount + r + 1, record));   // 1-based recno + exact written bytes
            }

            // Reject candidate collisions before starting a DBF/CDX edit. A constraint refusal
            // must not leave the writer marked as needing index repair after a successful undo.
            ValidateCandidateBatch(appended, rows);

            // Commit every prepared record. NO per-row 0x1A / SetLength / flush — the next row's
            // data offset overwrites where the per-row path would have stamped 0x1A, and the final
            // single EOF below lands at exactly the same byte.
            using var mutation = BeginIndexMutation(_headerLength + (long)diskCount * _recordLength,
                append: true);
            for (int r = 0; r < n; r++)
            {
                var (_, record) = appended[r];
                PatchMemoPointers(record, rows[r]);

                long dataOffset = _headerLength + (long)(diskCount + r) * _recordLength;
                _stream.Seek(dataOffset, SeekOrigin.Begin);
                _stream.Write(record, 0, record.Length);
            }

            // Single 0x1A EOF + exact file length for the whole batch.
            if (WritesEof)
            {
                _stream.Seek(finalDataEnd, SeekOrigin.Begin);
                _stream.WriteByte(0x1A);
            }
            _stream.SetLength(endAfter);

            // ONE header stamp (RecordCount + last-update date) for the batch.
            WriteHeaderCountAndDate(finalCount);
            AppendHeaderWriteCount++;

            // Persist each autoinc descriptor's final advanced Next once (= startNext + n*step).
            for (int a = 0; a < aiCol.Count; a++)
                WriteU32LittleEndian(aiOffset[a], aiRunning[a]);

            // ONE flush for the whole batch.
            _stream.Flush();
            _fpt?.Flush();
            AppendDataFlushCount++;

            _recordCount = finalCount;

            // §5.1 structural-CDX maintenance: the whole batch is durably written above; keep the append
            // lock through the ordered page edits. Rebuilding would lose VFP9 append history.
            MaintainIndexesAfterAppendBatch(appended);
            mutation?.Commit();
            foreach (var (recNo, record) in appended)
                MaintainCompactIndexes(recNo, null, new DbfRecord(_schema, record));
            CloseCdxMaint(flush: true);
        });

        return indices;
    }
}
