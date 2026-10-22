using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D5 — ALTER the structure of a writable DBF (add / drop / modify a column). The new column set
/// is validated and a fresh empty table image is assembled with <see cref="BuildHeaderImage"/>
/// (recomputing version byte, descriptors, the hidden <c>_NullFlags</c> column, header- and
/// record-length); every existing record is migrated into the new geometry (matched by column
/// name, so retained columns keep their data, dropped columns are discarded and added columns read
/// back NULL/empty); the rebuilt table is written to a SIBLING temp file and committed with an
/// atomic <see cref="File.Move(string, string, bool)"/>.
/// </summary>
/// <remarks>
/// SAFETY (§D-Leitplanken): the rebuild happens entirely on a temp file. The original is only
/// touched by the final atomic move once the temp table is complete and closed, so a fault at any
/// earlier point leaves the original byte-identical. On any failure the temp files are deleted and
/// the writer is re-pointed back at the (intact) original. Deleted records keep their deleted flag;
/// AutoIncrement values are preserved verbatim (the migration does NOT re-seed them).
/// </remarks>
public sealed partial class DbfWriter
{
    /// <summary>
    /// Add <paramref name="column"/> to the table (at <paramref name="at"/>, or appended when
    /// null) and rebuild. Existing rows read back the §A5b type-appropriate empty/NULL for the new
    /// field. Throws <see cref="DbfSchemaException"/> if the resulting schema is invalid.
    /// </summary>
    public void AddColumn(DbfColumnDef column, int? at = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(column);

        var defs = CurrentColumnDefs();
        int pos = Math.Clamp(at ?? defs.Count, 0, defs.Count);
        defs.Insert(pos, column);
        Alter(defs);
    }

    /// <summary>
    /// Drop the column named <paramref name="name"/> (case-insensitive) and rebuild, discarding its
    /// data. Throws <see cref="DbfSchemaException"/> if no such column exists or it is the last one.
    /// </summary>
    public void DropColumn(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        var defs = CurrentColumnDefs();
        int removed = defs.RemoveAll(d => StringComparer.OrdinalIgnoreCase.Equals(d.Name, name));
        if (removed == 0)
            throw new DbfSchemaException($"Cannot drop column '{name}': no such column.");
        Alter(defs);
    }

    /// <summary>
    /// Replace the column named <paramref name="name"/> (case-insensitive) with
    /// <paramref name="replacement"/> and rebuild. When the replacement keeps the same name its data
    /// is migrated (re-encoded into the new type/length, best-effort); a rename drops the old data.
    /// Throws <see cref="DbfSchemaException"/> if no such column exists.
    /// </summary>
    public void ModifyColumn(string name, DbfColumnDef replacement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(replacement);

        var defs = CurrentColumnDefs();
        int idx = defs.FindIndex(d => StringComparer.OrdinalIgnoreCase.Equals(d.Name, name));
        if (idx < 0)
            throw new DbfSchemaException($"Cannot modify column '{name}': no such column.");
        defs[idx] = replacement;
        Alter(defs);
    }

    /// <summary>
    /// Rebuild the table under the full target user-column set <paramref name="newColumns"/>
    /// (physical order), migrating every existing record by column name. This is the low-level §D5
    /// primitive behind <see cref="AddColumn"/> / <see cref="DropColumn"/> / <see cref="ModifyColumn"/>.
    /// </summary>
    public void Alter(IReadOnlyList<DbfColumnDef> newColumns)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(newColumns);

        var defs = newColumns.ToList();

        // The reopen options must be captured BEFORE we tear our handles down (they read the live
        // schema). The new table inherits the current code page and memo block size.
        var reopenOptions = new DbfOptions
        {
            LockMode = _lockMode,
            Encoding = _schema.EncodingIsExplicit ? _schema.Encoding : null,
        };
        var createOptions = new DbfCreateOptions
        {
            CodePage = ReadByteAt(29),
            MemoBlockSize = _fptBlockSize > 0 ? _fptBlockSize : 64,
            Encoding = reopenOptions.Encoding,
            LockMode = LockMode.Exclusive,
        };

        // Validate the target schema and assemble its empty image FIRST — a bad schema throws here,
        // before any temp file is created and before the original is touched.
        byte[] image = BuildHeaderImage(defs, createOptions, out bool newHasMemo);

        // §D-issue4: preserve the original 263-byte backlink (the .dbc database-container linkage)
        // so an Alter does NOT silently demote a contained table to a free table. Read it verbatim
        // from the tail of the current header and stamp it into the SAME region of the rebuilt
        // image (the new header length may differ, so the destination offset is recomputed).
        if (_version.HasBacklink && _headerLength > CreateBacklinkSize + CreateHeaderSize)
        {
            byte[] backlink = ReadRaw(_handle, _headerLength - CreateBacklinkSize, CreateBacklinkSize);
            int newHeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(8, 2));
            if (backlink.Length == CreateBacklinkSize && newHeaderLength >= CreateBacklinkSize)
                backlink.CopyTo(image.AsSpan(newHeaderLength - CreateBacklinkSize, CreateBacklinkSize));
        }

        // §D-issue3: hold the whole-file VFP byte-range lock across the snapshot read + rebuild +
        // commit (parity with PackCore/ZapCore), so under LockMode.Shared a concurrent VFP appender
        // cannot slip a record in between the live-count read and the atomic replace and have it
        // silently discarded with the old file. No-op in Exclusive (the OS already grants it), and
        // skipped when a caller-held public lock already covers the range (Windows rejects an
        // overlapping lock on the same handle). The lock is released together with our handle by
        // CloseHandlesForReplace below; on a pre-close fault the finally unlocks it.
        long lockStart = 0, lockLen = 0;
        bool locked = false;
#pragma warning disable CA1416 // FileStream.Lock/Unlock — Windows VFP coexistence (§D3).
        if (_lockMode != LockMode.Exclusive)
        {
            (lockStart, lockLen) = VfpLock.FileLockRange();
            bool covered = false;
            foreach (var h in _heldLocks)
            {
                if (h.Position <= lockStart && lockStart + lockLen <= h.Position + h.Length)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
            {
                _stream.Lock(lockStart, lockLen);
                locked = true;
            }
        }
#pragma warning restore CA1416

        // ---- read phase: snapshot every physical record (incl. deleted) keyed by column name ----
        // Read the LIVE count straight off the handle (the cached schema count can be stale after
        // appends) and decode each row through the current schema.
        var oldUserCols = _schema.Columns;
        int liveCount = (int)ReadOnDiskRecordCount();
        var snapshot = new List<(Dictionary<string, object?> Values, bool Deleted)>(liveCount);
        var rowBuf = new byte[_recordLength];
        for (int i = 0; i < liveCount; i++)
        {
            long off = _headerLength + (long)i * _recordLength;
            int read = ReadRawInto(_handle, off, rowBuf);
            if (read < _recordLength)
                // Truncated/corrupt tail: silently dropping the missing rows would migrate only PART of
                // the table into the rebuilt structure (irreversible row loss). Refuse instead.
                throw new InvalidDataException(
                    $"Table is truncated: expected {liveCount} records but record {i + 1} is incomplete " +
                    $"({read} of {_recordLength} bytes). Refusing to ALTER a partial table.");

            var copy = (byte[])rowBuf.Clone();
            var rec = new DbfRecord(_schema, copy);
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int ci = 0; ci < oldUserCols.Count; ci++)
            {
                var oc = oldUserCols[ci];
                // The writer's schema carries no memo reader (it was opened stream-only), so
                // FPT-backed content (M memo / W blob / G General-OLE / P Picture) is pulled
                // directly from OUR open .fpt sidecar; everything else decodes through the record.
                // This keeps the memo/OLE data across the rebuild — without G/P here their stale
                // block pointers would dangle into the freshly rebuilt (empty) .fpt.
                map[oc.Name] = oc.Type is 'M' or 'W' or 'G' or 'P'
                    ? ReadMemoContentForMigration(oc, copy.AsSpan(oc.Offset + 1, oc.Length))
                    : rec[ci];
            }
            snapshot.Add((map, copy.Length > 0 && copy[0] == 0x2A));
        }

        string path = Path.GetFullPath(_stream.Name);
        string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
        string stem = Path.GetFileNameWithoutExtension(path) + "_alter_" + Guid.NewGuid().ToString("N");
        string tmpDbf = Path.Combine(dir, stem + ".tmp");
        string tmpFpt = Path.ChangeExtension(tmpDbf, ".fpt"); // matches FindFpt(tmpDbf)
        string fptPath = SidecarFile.ChangeExtension(path, ".fpt");

        string token = Guid.NewGuid().ToString("N");
        string bakDbf = path + ".bak_" + token;
        string bakFpt = fptPath + ".bak_" + token;

        bool hadStructuralCdx = _hasStructuralCdx;
        bool handlesClosed = false;
        bool dbfCommitted = false;
        bool fptMoved = false;
        bool dbfBacked = false, fptBacked = false;
        try
        {
            // ---- build the replacement table on the temp file and migrate every record ----
            File.WriteAllBytes(tmpDbf, image);
            if (newHasMemo)
                WriteEmptyFpt(tmpFpt, createOptions.MemoBlockSize);

            using (var tw = Open(tmpDbf, new DbfOptions { LockMode = LockMode.Exclusive, Encoding = reopenOptions.Encoding }))
            {
                foreach (var (values, deleted) in snapshot)
                {
                    var positional = new object?[defs.Count];
                    for (int ci = 0; ci < defs.Count; ci++)
                        positional[ci] = values.TryGetValue(defs[ci].Name, out var v) ? v : null;
                    tw.AppendMigrated(positional, deleted);
                }
                tw.Flush();
            }

            // ---- close OUR handles so the original file can be atomically replaced ----
            // The original on disk is still untouched at this point; if anything below faults we
            // restore it from the staged backup and reopen it intact.
            CloseHandlesForReplace();
            handlesClosed = true;
            locked = false; // the byte-range lock was released together with the closed handle.

            // §D-issue2: stage the original pair as backups, then commit the .fpt BEFORE the .dbf so
            // the .dbf (the AUTHORITATIVE file, whose migrated block pointers index the new .fpt) is
            // the LAST committing step. A fault at any commit step rolls the original pair back into
            // place, so the original is NEVER destroyed (class-remark safety guarantee).
            try
            {
                // Stage the original pair as backups INSIDE the protected region so that a fault
                // between the two moves (or before the commit) is caught below and rolled back — the
                // original .dbf is never left only as a backup that the finally would then delete.
                if (File.Exists(path))
                {
                    File.Move(path, bakDbf);
                    dbfBacked = true;
                }
                if (File.Exists(fptPath))
                {
                    File.Move(fptPath, bakFpt);
                    fptBacked = true;
                }

                if (newHasMemo)
                {
                    File.Move(tmpFpt, fptPath);
                    fptMoved = true;
                }
                // else: the new structure has no memo → no sidecar is committed; the old one (now in
                // bakFpt) is dropped on success in the finally, leaving no stale .fpt behind.

                FaultBeforeDbfCommit?.Invoke();
                File.Move(tmpDbf, path); // last committing step → the .dbf is authoritative.
                dbfCommitted = true;
            }
            catch
            {
                // Roll back to the pre-Alter pair: discard the half-committed new sidecar and restore
                // the original .dbf/.fpt from their staged backups (byte-identical to before).
                try
                {
                    if (fptMoved && File.Exists(fptPath)) File.Delete(fptPath);
                    if (fptBacked && File.Exists(bakFpt)) File.Move(bakFpt, fptPath, overwrite: true);
                    if (dbfBacked && File.Exists(bakDbf)) File.Move(bakDbf, path, overwrite: true);
                }
                catch { /* best-effort rollback */ }
                throw;
            }
        }
        finally
        {
#pragma warning disable CA1416 // FileStream.Lock/Unlock — Windows VFP coexistence (§D3).
            // Pre-close fault only: the original handle is still live and holds the lock — release it.
            if (locked)
            {
                try { _stream.Unlock(lockStart, lockLen); } catch { /* best-effort */ }
            }
#pragma warning restore CA1416

            TryDelete(tmpDbf);
            if (!fptMoved)
                TryDelete(tmpFpt);

            // Drop the staged backups: on success they are the now-replaced originals; on a rolled-
            // back fault they have already been moved back to their original names (delete = no-op).
            TryDelete(bakDbf);
            TryDelete(bakFpt);

            // Only re-point the writer when we actually tore our handles down. On success this
            // opens the new file; on a fault AFTER CloseHandlesForReplace it reopens the (intact,
            // restored) original. If we faulted BEFORE closing, the original handles are still live
            // and valid — leave the writer exactly as it was.
            if (handlesClosed)
            {
                ApplyState(OpenComponentsFor(path, reopenOptions));
                if (dbfCommitted && hadStructuralCdx)
                {
                    _hasStructuralCdx = true;
                    InvalidateStructuralCdx();
                }
            }
        }
    }

    /// <summary>
    /// Reconstruct the current table's user columns as <see cref="DbfColumnDef"/>s (the basis for
    /// the add/drop/modify convenience overloads). AutoIncrement <c>Next</c>/<c>Step</c> are read
    /// from the live descriptor so they are preserved across the rebuild.
    /// </summary>
    private List<DbfColumnDef> CurrentColumnDefs()
    {
        var cols = _schema.Columns;
        var list = new List<DbfColumnDef>(cols.Count);
        for (int i = 0; i < cols.Count; i++)
        {
            var c = cols[i];
            if (c.IsSystem)
                continue; // never reproduce the hidden _NullFlags column (it is re-derived).

            DbfAutoIncrement? ai = null;
            if (c.IsAutoIncrement)
            {
                long descOff = _version.HeaderSize + (long)i * _version.DescriptorWidth;
                uint next = ReadU32LittleEndian(descOff + 19);
                byte step = ReadByteAt(descOff + 23);
                ai = new DbfAutoIncrement(next, step == 0 ? (byte)1 : step);
            }

            list.Add(new DbfColumnDef(c.Name, c.Type, c.Length, c.Decimal,
                nullable: c.IsNullable, binary: c.IsBinary, autoIncrement: ai));
        }
        return list;
    }

    /// <summary>
    /// Read an FPT-backed field's CONTENT from the writer's own open <c>.fpt</c> sidecar during §D5
    /// migration: a text memo returns a <see cref="string"/> (table encoding); a blob (<c>W</c>),
    /// General/OLE (<c>G</c>), Picture (<c>P</c>) or NOCPTRANS memo returns the raw <c>byte[]</c>. A
    /// null/blank pointer or unreadable block returns <see langword="null"/>. Re-appending the
    /// returned value writes a fresh block into the rebuilt table, so the content survives the rebuild.
    /// </summary>
    private object? ReadMemoContentForMigration(DbfColumn col, ReadOnlySpan<byte> rawField)
    {
        if (_fpt is null || _fptHandle is null || rawField.Length < 4)
            return null;

        int ptr = BinaryPrimitives.ReadInt32LittleEndian(rawField[..4]);
        if (ptr <= 0)
            return null;

        var (raw, _) = ReadRawMemoBlocks(ptr);
        if (raw is null || raw.Length < 8)
            return null;

        uint len = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4, 4));
        int n = (int)Math.Min(len, (uint)(raw.Length - 8));
        var content = raw.AsSpan(8, n);

        // 'W' blob, 'G' General-OLE, 'P' Picture and a NOCPTRANS 'M' round-trip as raw bytes; a
        // text 'M' as a decoded string.
        return col.Type is 'W' or 'G' or 'P' || col.IsBinary
            ? content.ToArray()
            : _schema.Encoding.GetString(content);
    }

    /// <summary>
    /// Migration append: write <paramref name="positional"/> (one per user column) as a new row,
    /// preserving the <paramref name="deleted"/> flag. Unlike <see cref="AppendRecord(object?[])"/>
    /// this does NOT lock, does NOT re-read the disk count, and does NOT reassign AutoIncrement
    /// columns — the stored values (including AutoIncrement ids) are written VERBATIM so the rebuild
    /// is loss-free. Used only against a freshly created temp table during §D5 Alter.
    /// </summary>
    private void AppendMigrated(object?[] positional, bool deleted)
    {
        long dataOffset = _headerLength + (long)_recordCount * _recordLength;

        byte[] record = RecordEncoder.Encode(_schema, positional, deleted);
        PatchMemoPointers(record, positional);

        _stream.Seek(dataOffset, SeekOrigin.Begin);
        _stream.Write(record, 0, record.Length);
        if (WritesEof)
            _stream.WriteByte(0x1A);
        _stream.SetLength(dataOffset + record.Length + (WritesEof ? 1 : 0));

        WriteHeaderCountAndDate(_recordCount + 1);
        _recordCount += 1;
    }

    /// <summary>
    /// Tear down the current handles (releasing any held byte-range locks, flushing, disposing the
    /// schema / <c>.fpt</c> / stream) ahead of an atomic file replace. Does NOT mark the writer
    /// disposed — <see cref="ApplyState"/> re-points it at the replacement file afterwards.
    /// </summary>
    private void CloseHandlesForReplace()
    {
        // Release the incremental .cdx accelerator (it holds a handle on the OLD sidecar that is about to be
        // replaced/reopened); ApplyState re-points the writer afterwards and the next append re-opens it.
        CloseCdxMaint(flush: true);
#pragma warning disable CA1416 // FileStream.Lock/Unlock — Windows VFP coexistence (§D3).
        foreach (var (position, length) in _heldLocks)
        {
            try { _stream.Unlock(position, length); } catch { /* best-effort */ }
        }
        _heldLocks.Clear();
#pragma warning restore CA1416

        try { _stream.Flush(); } catch { /* best-effort */ }
        try { _fpt?.Flush(); } catch { /* best-effort */ }
        _schema.Dispose();
        _fpt?.Dispose();
        _stream.Dispose();
        _fpt = null;
        _fptHandle = null;
    }
}
