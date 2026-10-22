using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D2 PACK / ZAP — physical compaction of a writable DBF, modelled on CodeBase
/// <c>d4pack.c</c> (<c>dfile4packData</c>) and <c>d4zap.c</c> (<c>dfile4zapData</c>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Pack"/> drops every logically-deleted row (leading byte <c>0x2A</c>), slides the
/// survivors forward to <c>HeaderLength</c>, rewrites the FoxPro <c>.fpt</c> sidecar with full
/// block-pointer fix-up (orphaned/garbage memo blocks are discarded), truncates the <c>.dbf</c>
/// (and the <c>.fpt</c>) to the new length, resets <c>RecordCount</c> (bytes 4–7), re-stamps the
/// last-update date. NO trailing <c>0x1A</c> is written — oracle-verified: VFP9's PACK drops the EOF byte (it reappears on the next append).
/// </para>
/// <para>
/// <see cref="Zap"/> discards ALL rows: it truncates the <c>.dbf</c> to header-only, sets the
/// count to 0, re-stamps the date and resets the memo file to its header block (NextFree pointing
/// just past the reserved header region).
/// </para>
/// <para>
/// SAFETY: the whole survivor set and the rebuilt <c>.fpt</c> are computed IN MEMORY first; the
/// destructive in-place rewrite only begins once that has fully succeeded, so a failure while
/// reading/encoding leaves the original file byte-identical. Both run under the §D3 whole-file
/// byte-range lock (skipped in <see cref="LockMode.Exclusive"/>). Record and memo bytes are read
/// through <see cref="RandomAccess"/> on the raw handle so a buffered <see cref="FileStream"/>
/// snapshot can never feed stale data into the rewrite.
/// </para>
/// </remarks>
public sealed partial class DbfWriter
{
#pragma warning disable CA1416 // FileStream.Lock/Unlock — Windows VFP coexistence (§D3).

    /// <summary>
    /// Remove every deleted record, compact the survivors (and their memos) to the front, and
    /// truncate the file. Held under the whole-file VFP lock; no-op-safe on an all-live table
    /// (it simply rewrites identical content). See the type remarks for the full contract.
    /// </summary>
    public void Pack()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireExclusiveForCompaction();
        // Exclusive is now guaranteed → the whole-file lock is a no-op (WithLock passes straight
        // through in LockMode.Exclusive); kept for symmetry with the historical §D3 bracket.
        var (fileStart, fileLength) = VfpLock.FileLockRange();
        WithLock(fileStart, fileLength, PackCore);
    }

    /// <summary>
    /// Discard ALL records: truncate the <c>.dbf</c> to header-only, set the count to 0, re-stamp
    /// the date and reset the memo sidecar to its header block. Held under the whole-file VFP lock.
    /// </summary>
    public void Zap()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireExclusiveForCompaction();
        var (fileStart, fileLength) = VfpLock.FileLockRange();
        WithLock(fileStart, fileLength, ZapCore);
    }

#pragma warning restore CA1416

    /// <summary>
    /// §D2 exclusivity gate — VFP-faithful (oracle-verified against vfp9.exe, 2026-07-05): PACK and ZAP
    /// require the table to be opened EXCLUSIVELY. A <see cref="LockMode.Shared"/> writer refuses with the
    /// typed VFP error <b>110</b> ("File must be opened exclusively.") — the EXACT number the VFP9 runtime
    /// raises for <c>USE … SHARED</c> followed by <c>PACK</c>/<c>ZAP</c>. The refusal is thrown BEFORE any
    /// byte is read or written, so the file stays byte-identical to before the attempt (no partial
    /// compaction, no corruption). Carried through <see cref="IVfpErrorCode"/> so the microVFP interpreter's
    /// error trap surfaces 110 without message-text sniffing.
    /// </summary>
    private void RequireExclusiveForCompaction()
    {
        if (_lockMode != LockMode.Exclusive)
            throw new DbfWriteException("File must be opened exclusively.", 110);
    }

    private void PackCore()
    {
        int diskCount = (int)ReadOnDiskRecordCount();
        _recordCount = diskCount;

        // Memo columns whose pointer (4-byte LE at Offset+1) we must relocate. Only meaningful
        // when a usable .fpt sidecar is open; without it we copy record bytes verbatim (pointers
        // are left untouched — there is no memo store to compact). 'G' (General/OLE) and 'P'
        // (Picture) store a 4-byte .fpt block pointer exactly like M/W and MUST relocate too —
        // otherwise Pack rebuilds the .fpt without those blocks and leaves them dangling.
        var memoColumns = _schema.Columns
            .Where(c => c.Type is ('M' or 'W' or 'G' or 'P') && c.Length >= 4)
            .ToList();
        bool compactMemo = _fpt is not null && _fptHandle is not null && memoColumns.Count > 0;

        // Rebuild the .fpt in memory: start with a verbatim copy of the reserved header region
        // (preserves block size @6 and any other header bytes); data blocks are re-emitted in
        // survivor order from NextFree = firstDataBlock.
        int firstDataBlock = FirstMemoDataBlock();
        MemoryStream? newFpt = null;
        uint newNextFree = 0;
        if (compactMemo)
        {
            long headerRegion = (long)firstDataBlock * _fptBlockSize;
            var headerBytes = ReadRaw(_fptHandle!, 0, (int)headerRegion);
            newFpt = new MemoryStream();
            newFpt.Write(headerBytes, 0, headerBytes.Length);
            newNextFree = (uint)firstDataBlock;
        }

        try
        {
            // ---- read phase (no mutation): gather survivors, relocate their memo blocks --------
            var survivors = new List<byte[]>(diskCount);
            var record = new byte[_recordLength];
            for (int i = 0; i < diskCount; i++)
            {
                long off = _headerLength + (long)i * _recordLength;
                int read = ReadRawInto(_handle, off, record);
                if (read < _recordLength)
                    break; // truncated/corrupt tail — stop rather than emit a partial row.
                if (record[0] == 0x2A)
                    continue; // logically deleted → dropped.

                var copy = (byte[])record.Clone();
                if (compactMemo)
                {
                    foreach (var col in memoColumns)
                    {
                        int ptr = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(col.Offset + 1, 4));
                        if (ptr <= 0)
                            continue; // null/blank memo — keep the 0 pointer.

                        var (raw, blocks) = ReadRawMemoBlocks(ptr);
                        if (raw is null)
                        {
                            // Unreadable/garbage block → NULL the pointer so the field degrades to
                            // empty/null after Pack. Leaving the original block number would, in the
                            // compacted .fpt, alias a DIFFERENT survivor's relocated memo (ptr <
                            // newNextFree) or dangle off the end — silent memo-data corruption.
                            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(col.Offset + 1, 4), 0);
                            continue;
                        }

                        uint assigned = newNextFree;
                        newFpt!.Seek((long)assigned * _fptBlockSize, SeekOrigin.Begin);
                        newFpt.Write(raw, 0, raw.Length);
                        newNextFree += (uint)blocks;
                        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(col.Offset + 1, 4), (int)assigned);
                    }
                }
                survivors.Add(copy);
            }

            int newCount = survivors.Count;
            // ORACLE-VERIFIED (2026-07-05, vfp9.exe probe): VFP9's PACK does NOT write a trailing 0x1A —
            // a packed 3x15-byte table is exactly header+45 bytes (the EOF byte VFP itself wrote at
            // CREATE/APPEND time is DROPPED by PACK). Match that: no EOF byte on the pack rewrite.
            // (Appends after PACK re-introduce it via AppendRecord, same as VFP.)
            bool writeEof = false;

            // ---- commit phase: rewrite .dbf data region, then the .fpt ----------------------
            long writeOffset = _headerLength;
            _stream.Seek(writeOffset, SeekOrigin.Begin);
            foreach (var r in survivors)
            {
                _stream.Write(r, 0, r.Length);
                writeOffset += r.Length;
            }
            if (writeEof)
            {
                _stream.WriteByte(0x1A);
                writeOffset += 1;
            }
            _stream.SetLength(writeOffset);
            WriteHeaderCountAndDate(newCount);
            _recordCount = newCount;

            if (compactMemo)
            {
                Span<byte> nf = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(nf, newNextFree);
                newFpt!.Seek(0, SeekOrigin.Begin);
                newFpt.Write(nf);

                byte[] fptBytes = newFpt.ToArray();
                _fpt!.Seek(0, SeekOrigin.Begin);
                _fpt.Write(fptBytes, 0, fptBytes.Length);
                _fpt.SetLength(fptBytes.Length);
                _fptNextFree = newNextFree;
            }

            _stream.Flush();
            _fpt?.Flush();

            InvalidateStructuralCdx();
        }
        finally
        {
            newFpt?.Dispose();
        }
    }

    private void ZapCore()
    {
        // .dbf → header-only (no records, no EOF byte: an empty table carries no 0x1A).
        _stream.SetLength(_headerLength);
        WriteHeaderCountAndDate(0);
        _recordCount = 0;

        // .fpt → reset to the reserved header region with NextFree pointing just past it; the
        // block-size field (@6) and the rest of the header are preserved.
        if (_fpt is not null)
        {
            int firstDataBlock = FirstMemoDataBlock();
            long memoLength = (long)firstDataBlock * _fptBlockSize;
            _fpt.SetLength(memoLength);
            Span<byte> nf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(nf, (uint)firstDataBlock);
            _fpt.Seek(0, SeekOrigin.Begin);
            _fpt.Write(nf);
            _fptNextFree = (uint)firstDataBlock;
        }

        _stream.Flush();
        _fpt?.Flush();

        InvalidateStructuralCdx();
    }

    /// <summary>
    /// §D2/§D7 — structural <c>.cdx</c> invalidation after a destructive compaction. Gated on the
    /// TRUE byte-28-bit-0 signal (<see cref="_hasStructuralCdx"/>), NOT the lock discriminator: a
    /// plain v0x30 table with no structural index must be left entirely alone. When the table DOES
    /// carry a structural index we (1) delete the now-stale sidecar so VFP rebuilds it on next
    /// <c>USE</c>, (2) clear the header's structural-CDX flag (byte 28 bit 0) so the header no longer
    /// advertises an index that is gone — otherwise the real VFP9 runtime raises "structural .cdx not
    /// found" and refuses to open the table — and (3) raise <see cref="ReindexNeeded"/>.
    /// </summary>
    private void InvalidateStructuralCdx()
    {
        // Release the incremental .cdx accelerator FIRST — an open handle would block DeleteStructuralCdx,
        // and the sidecar is about to be discarded anyway (no need to flush pending edits).
        CloseCdxMaint(flush: false);
        if (!_hasStructuralCdx)
            return;

        // Flip the IN-MEMORY truth FIRST: from here on this writer must treat the structural .cdx as gone
        // (ShouldMaintainIndexes → false via _hasStructuralCdx) and demand a REINDEX. Doing this BEFORE the
        // best-effort disk steps means that even if the sidecar DELETE fails (a concurrent reader holds the
        // .cdx) or ClearStructuralCdxFlag throws, maintenance never keeps editing an already-invalidated
        // sidecar — the ShouldMaintainIndexes/ReindexNeeded semantics stay coherent regardless.
        _hasStructuralCdx = false;
        _usesStructuralScheme = VfpLock.UsesStructuralScheme(_version.Code, false);
        ReindexNeeded = true;

        DeleteStructuralCdx(_stream.Name);
        ClearStructuralCdxFlag();
    }

    /// <summary>
    /// Clear the structural-CDX flag (header byte 28, bit <c>0x01</c>) in place, preserving every
    /// other flag in that byte (e.g. the memo bit <c>0x02</c>). No-op if the bit is already clear.
    /// </summary>
    private void ClearStructuralCdxFlag()
    {
        _stream.Seek(28, SeekOrigin.Begin);
        int current = _stream.ReadByte();
        if (current < 0)
            return; // header truncated — nothing safe to rewrite.

        byte cleared = (byte)(current & ~0x01);
        if (cleared == (byte)current)
            return; // already clear.

        _stream.Seek(28, SeekOrigin.Begin);
        _stream.WriteByte(cleared);
        _stream.Flush();
    }

    /// <summary>
    /// The first usable memo DATA block: the <c>.fpt</c> reserves a 512-byte header region, so
    /// data begins at <c>ceil(512 / blockSize)</c> (≥ 1) — block 1 for 512-byte blocks, block 8
    /// for 64-byte blocks. NextFree of a freshly-emptied memo equals this value.
    /// </summary>
    private int FirstMemoDataBlock()
    {
        int bs = _fptBlockSize > 0 ? _fptBlockSize : 512;
        int blocks = (512 + bs - 1) / bs;
        return blocks < 1 ? 1 : blocks;
    }

    /// <summary>
    /// Read the full raw block run for the memo at <paramref name="blockNumber"/>: the BIG-endian
    /// length (@4) gives the content size, from which the block count
    /// (<c>ceil((8 + length) / blockSize)</c>) and the byte run are derived. Returns the
    /// block-aligned bytes (zero-padded on a short tail) plus the block count, or
    /// <c>(null, 0)</c> when the header cannot be read.
    /// </summary>
    private (byte[]? Raw, int Blocks) ReadRawMemoBlocks(int blockNumber)
    {
        long pos = (long)blockNumber * _fptBlockSize;
        Span<byte> header = stackalloc byte[8];
        if (RandomAccess.Read(_fptHandle!, header, pos) < 8)
            return (null, 0);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        long fptLength = RandomAccess.GetLength(_fptHandle!);
        if (pos < 0 || 8L + length > fptLength - pos)
            return (null, 0);

        long total = 8L + length;
        int blocks = (int)((total + _fptBlockSize - 1) / _fptBlockSize);
        if (blocks <= 0)
            blocks = 1;

        var buffer = new byte[(long)blocks * _fptBlockSize];
        ReadRawInto(_fptHandle!, pos, buffer); // short tail leaves the rest zero-padded (block aligned).
        return (buffer, blocks);
    }

    /// <summary>Read exactly <paramref name="count"/> bytes at <paramref name="offset"/> from the handle (buffer-bypassing).</summary>
    private static byte[] ReadRaw(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, int count)
    {
        if (count <= 0)
            return [];
        var buffer = new byte[count];
        ReadRawInto(handle, offset, buffer);
        return buffer;
    }

    /// <summary>Fill <paramref name="buffer"/> from <paramref name="offset"/> via <see cref="RandomAccess"/>; returns bytes read.</summary>
    private static int ReadRawInto(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(total), offset + total);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Locate and delete the structural .cdx sidecar beside <paramref name="dbfPath"/>.
    /// Case-insensitive search matching both .CDX and .cdx extensions. Best-effort: logs no
    /// error if the file does not exist or cannot be deleted (the reindex flag tells the
    /// caller a rebuild is needed anyway).
    /// </summary>
    private static void DeleteStructuralCdx(string dbfPath)
    {
        try
        {
            string candidate = SidecarFile.ChangeExtension(dbfPath, ".cdx");
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
                return;
            }

            // Case-insensitive search for .CDX (Windows filesystems are case-insensitive but
            // the exact casing may differ).
            string dir = Path.GetDirectoryName(dbfPath) is { Length: > 0 } d ? d : ".";
            if (!Directory.Exists(dir))
                return;

            string name = Path.GetFileName(candidate);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    return;
                }
            }
        }
        catch
        {
            // Best-effort: the reindex flag informs the caller that a rebuild is needed.
            // A failure to delete the file (permissions, locked by another process, etc.)
            // does not warrant an exception — VFP will handle the stale index gracefully.
        }
    }
}
