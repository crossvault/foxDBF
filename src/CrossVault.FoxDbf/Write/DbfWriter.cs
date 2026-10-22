using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Writable handle over a DBF table — the WRITE counterpart to the read-only
/// <see cref="DbfTable"/> (plan §D1). Opens a <c>.dbf</c> read/write and offers
/// record append / in-place update / logical delete + recall, keeping the header
/// (<c>RecordCount</c> bytes 4–7, last-update date bytes 1–3) and the data region
/// consistent at every step.
/// </summary>
/// <remarks>
/// <para>
/// Encoding goes through <see cref="RecordEncoder"/> / <see cref="FieldEncoder"/>
/// (the exact inverse of the reader). Append writes the full row at
/// <c>HeaderLength + RecordCount * RecordLength</c>, increments the count, stamps the
/// date and writes the <c>0x1A</c> EOF byte after the records for VFP
/// <c>0x30/0x31/0x32</c> (absent only for an empty table). Update rewrites the FULL
/// row at <c>HeaderLength + index * RecordLength</c> (no partial-column write — the
/// go-dbase model). <see cref="Delete(int)"/> / <see cref="Recall(int)"/> flip the
/// leading record byte between <c>0x2A</c> and <c>0x20</c>.
/// </para>
/// <para>
/// AutoIncrement columns draw their value from the field descriptor's <c>Next</c>
/// (bytes 19–22) on append, then advance it by <c>Step</c> (byte 23) and persist the
/// descriptor. FoxPro memo (<c>.fpt</c>) content is appended as a new block at the
/// header <c>NextFree</c>, the pointer (4-byte LE block number) written into the record.
/// </para>
/// </remarks>
public sealed partial class DbfWriter : IDisposable
{
    // NOTE: these are NOT readonly because §D5 Alter rebuilds the table under a new geometry and
    // re-points the writer at the replacement file in place (see ReopenInPlace) so the caller's
    // existing `using var w` handle stays valid after an ADD/DROP/MODIFY column.
    // All set by ApplyState (called from the ctor); null! defaults satisfy nullable flow analysis.
    private FileStream _stream = null!;
    // The raw OS handle behind _stream. Reads of the live header (RecordCount) and the data
    // region during append-freshness / PACK go through RandomAccess on this handle so they
    // BYPASS the FileStream read buffer — a buffered read would otherwise serve a stale
    // RecordCount cached at open-time, defeating the §D3 coexistence freshness re-read.
    private SafeFileHandle _handle = null!;
    private SafeFileHandle? _fptHandle;
    private DbfTable _schema = null!;
    private DbfVersion _version;
    private int _headerLength;
    private int _recordLength;
    private int _recordCount;
    private bool _disposed;

    // §D3 locking: the concurrency mode and the precomputed structural-scheme discriminator
    // (header byte 28 bit 0 set OR version 0x30). _heldLocks tracks every byte-range lock
    // currently held through the PUBLIC surface (Lock/LockFile/scopes) so Dispose can release
    // anything the caller leaked. Internal per-mutation locks are always released in finally.
    private readonly LockMode _lockMode;
    private bool _usesStructuralScheme;
    // §D2/§D7: the TRUE structural-.cdx signal (header byte 28 bit 0, NOT the lock discriminator).
    // ONLY this gates Pack/Zap cdx invalidation + the byte-28 flag clear — a plain v0x30 table with
    // no structural .cdx must NOT touch any .cdx or set ReindexNeeded. _usesStructuralScheme stays
    // dedicated to lock-offset selection (which additionally treats every v0x30 as structural).
    private bool _hasStructuralCdx;
    private readonly HashSet<(long Position, long Length)> _heldLocks = new();

    // Sidecar FoxPro memo (.fpt) opened read/write when the table has memo columns and a
    // sidecar exists. Big-endian header: NextFree (u32 @0), block size (u16 @6).
    private FileStream? _fpt;
    private int _fptBlockSize;
    private uint _fptNextFree;

    private DbfWriter(OpenComponents components, LockMode lockMode)
    {
        _lockMode = lockMode;
        ApplyState(components);
    }

    /// <summary>The mutable state produced by opening a file; consumed by the ctor and by §D5 reopen.</summary>
    private readonly record struct OpenComponents(
        FileStream Stream, DbfTable Schema, FileStream? Fpt, int FptBlockSize, uint FptNextFree,
        bool UsesStructuralScheme, bool HasStructuralCdx);

    /// <summary>Point this writer at a freshly opened set of handles/schema (initial open or §D5 reopen).</summary>
    private void ApplyState(OpenComponents c)
    {
        _stream = c.Stream;
        _handle = c.Stream.SafeFileHandle;
        _fptHandle = c.Fpt?.SafeFileHandle;
        _schema = c.Schema;
        _version = c.Schema.Version;
        _headerLength = c.Schema.HeaderLength;
        _recordLength = c.Schema.RecordLength;
        _recordCount = c.Schema.RecordCount;
        _fpt = c.Fpt;
        _fptBlockSize = c.FptBlockSize;
        _fptNextFree = c.FptNextFree;
        _usesStructuralScheme = c.UsesStructuralScheme;
        _hasStructuralCdx = c.HasStructuralCdx;
    }

    /// <summary>Open <paramref name="path"/> for record writing with default options.</summary>
    public static DbfWriter Open(string path) => Open(path, new DbfOptions());

    /// <summary>Open <paramref name="path"/> for record writing with explicit <paramref name="options"/>.</summary>
    public static DbfWriter Open(string path, DbfOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(path))
            throw new DbfFileNotFoundException($"DBF file not found: {path}");

        return new DbfWriter(OpenComponentsFor(path, options), options.LockMode);
    }

    /// <summary>
    /// Open <paramref name="path"/> read/write and resolve every piece of writer state — the
    /// schema (riding the same stream), the §D3 lock discriminators and the FoxPro <c>.fpt</c>
    /// sidecar. Shared by the public <see cref="Open(string, DbfOptions)"/> and the §D5
    /// rebuild-then-reopen path so both produce identical state.
    /// </summary>
    private static OpenComponents OpenComponentsFor(string path, DbfOptions options)
    {
        // Single read/write handle; the schema DbfTable rides the SAME stream (leaveOpen)
        // so we never trip a Windows sharing violation by opening a second handle.
        // §D3: the share mode follows the LockMode — Exclusive denies all other handles
        // (FileShare.None), Shared permits a concurrent VFP runtime (FileShare.ReadWrite).
        FileShare share = options.LockMode == LockMode.Exclusive ? FileShare.None : FileShare.ReadWrite;
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, share);
        DbfTable schema;
        bool usesStructuralScheme;
        bool hasStructuralCdx;
        try
        {
            schema = DbfTable.Open(stream, options, leaveOpen: true);

            // §D3 discriminator: structural .cdx (header byte 28 bit 0) OR version byte 0x30.
            // Read byte 28 straight off the raw header on our own stream. hasStructuralCdx is the
            // TRUE byte-28-bit-0 signal (kept for §D2 Pack/Zap cdx invalidation); usesStructuralScheme
            // folds in v0x30 for lock-offset selection.
            stream.Seek(28, SeekOrigin.Begin);
            int flagByte = stream.ReadByte();
            hasStructuralCdx = flagByte >= 0 && (flagByte & 0x01) != 0;
            usesStructuralScheme = VfpLock.UsesStructuralScheme(schema.Version.Code, hasStructuralCdx);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        FileStream? fpt = null;
        int blockSize = 0;
        uint nextFree = 0;
        try
        {
            // Align with §D4 Create: open the .fpt for every memo-bearing type it writes one for
            // (M/W and also G/P), so a table created with a General/Picture column does not reopen
            // with a null sidecar despite the .fpt existing on disk.
            bool hasMemo = schema.Columns.Any(c => c.Type is 'M' or 'W' or 'G' or 'P');
            if (hasMemo)
            {
                // Honour an explicit memo sidecar override when given (e.g. a .dbc container whose memo is
                // the sibling .DCT, not a .fpt); otherwise auto-discover the .fpt beside the .dbf.
                string? fptPath = options.MemoPath is { Length: > 0 } mp && File.Exists(mp) ? mp : FindFpt(path);
                if (fptPath is not null)
                {
                    // FileShare.ReadWrite (not just Read) so a memo-bearing table can be opened by two
                    // Shared writers / a VFP session at once — matching the .dbf's share mode and the
                    // read path (MemoFile.cs), so the byte-range-lock multi-writer design holds for memo
                    // tables too.
                    fpt = new FileStream(fptPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    var head = new byte[8];
                    fpt.Seek(0, SeekOrigin.Begin);
                    int read = fpt.ReadAtLeast(head, 8, throwOnEndOfStream: false);
                    if (read >= 8)
                    {
                        nextFree = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(0, 4));
                        blockSize = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6, 2));
                    }
                    if (blockSize <= 0)
                        blockSize = 512;
                }
            }
        }
        catch
        {
            fpt?.Dispose();
            schema.Dispose();
            stream.Dispose();
            throw;
        }

        return new OpenComponents(stream, schema, fpt, blockSize, nextFree, usesStructuralScheme, hasStructuralCdx);
    }

    /// <summary>
    /// §D2/§D7 — raised once a destructive compaction (<see cref="Pack"/> / <see cref="Zap"/>) has
    /// invalidated a structural <c>.cdx</c> that cannot yet be rebuilt (CDX writing is §D7). A
    /// <c>true</c> value means the caller (or the next VFP <c>USE</c>) must reindex the table.
    /// Pack/Zap delete the now-stale <c>.cdx</c> sidecar (so VFP rebuilds it on next <c>USE</c>) and
    /// set this flag whenever the table uses the structural scheme. Covered by <c>DbfPackZapCdxTests</c>.
    /// </summary>
    public bool ReindexNeeded { get; private set; }

    /// <summary>
    /// Sentinel for an <see cref="UpdateRecord(int, object?[])"/> field value: KEEP the field's
    /// existing on-disk bytes instead of re-encoding a supplied value. Its purpose is FPT-backed
    /// columns (<c>M</c>/<c>W</c>/<c>G</c>/<c>P</c>) an UPDATE does not touch — feeding their
    /// read-back value (decoded memo CONTENT, or a raw 4-byte block POINTER) back through the encoder
    /// would append a duplicate <c>.fpt</c> block (unbounded growth) or, for <c>G</c>/<c>P</c>, write
    /// the pointer bytes as new block content and destroy the OLE/Picture object. A slot set to this
    /// sentinel preserves the existing block pointer verbatim (its <c>_NullFlags</c> bit included) and
    /// is never offered to the memo-append path.
    /// </summary>
    public static readonly object KeepValue = new();

    // ---- batch-append test seam (additive, §D throughput contract) -------------
    // Observability ONLY: how many times the APPEND path stamped the header (RecordCount +
    // last-update date) and flushed the data stream. The per-row AppendRecord does both once
    // PER CALL (→ N for N rows); the batched AppendRecords does both ONCE for the whole batch.
    // Internal so the test project can assert the throughput contract; never touched by
    // update / delete / compaction / alter. Does NOT alter any write semantics.
    internal int AppendHeaderWriteCount { get; private set; }
    internal int AppendDataFlushCount { get; private set; }

    // ---- incremental CDX maintenance test seam (project-review finding 5.1) -----
    // Test-only fault injector: when true, the NEXT write-path index-maintenance pass MUST throw so
    // the crash-safety backstop can be proven — the data row is written FIRST, then on a maintenance
    // failure <see cref="ReindexNeeded"/> is raised (so the next ordered read / VFP USE rebuilds) and
    // the exception is rethrown (never leave a half-edited page silently). Honoured by the incremental
    // maintenance the implementer wires into AppendCore/UpdateCore; NEVER set outside tests.
    internal bool FailIndexMaintenanceForTests { get; set; }

    /// <summary>The current number of physical records (the live header value, bytes 4–7).</summary>
    public int RecordCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _recordCount;
        }
    }

    /// <summary>The table schema (column geometry / encoding) backing this writer.</summary>
    public DbfTable Schema
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _schema;
        }
    }

    /// <summary>
    /// Append a record built from <paramref name="values"/> (one per public column, in
    /// physical order) and return its zero-based record index. AutoIncrement columns and
    /// memo (<c>M</c>/<c>W</c>) fields are resolved by the writer regardless of the value
    /// passed. Throws <see cref="DbfWriteException"/> if the append would cross the 2 GB cap.
    /// </summary>
    public int AppendRecord(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AppendCore(ToPositional(values));
    }

    /// <summary>Append a record keyed by column name (missing keys → NULL); returns its index.</summary>
    public int AppendRecord(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return AppendCore(DictionaryToPositional(values));
    }

    /// <summary>
    /// Rewrite the FULL row at <paramref name="index"/> from <paramref name="values"/>
    /// (one per public column, physical order). Other records are left byte-identical.
    /// </summary>
    public void UpdateRecord(int index, object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_recordCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        UpdateCore(index, ToPositional(values));
    }

    /// <summary>Rewrite the FULL row at <paramref name="index"/> from a name-keyed value map.</summary>
    public void UpdateRecord(int index, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_recordCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        UpdateCore(index, DictionaryToPositional(values));
    }

    /// <summary>Mark the record at <paramref name="index"/> deleted (leading byte → <c>0x2A</c>).</summary>
    public void Delete(int index) => SetDeleteFlag(index, 0x2A);

    /// <summary>Clear the deleted flag of the record at <paramref name="index"/> (leading byte → <c>0x20</c>).</summary>
    public void Recall(int index) => SetDeleteFlag(index, 0x20);

    /// <summary>Flush buffered writes through to disk, leaving a valid file.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseCdxMaint(flush: true);   // flush + release the incremental .cdx accelerator (finalises the sidecar)
        _stream.Flush();
        _fpt?.Flush();
    }

    // ---- VFP byte-range locking (plan §D3) -------------------------------------
    // FileStream.Lock/Unlock are Windows byte-range locks (the VFP coexistence protocol).
    // The whole WRITE path targets the Windows VFP runtime; silence the platform advisory.
#pragma warning disable CA1416

    /// <summary>
    /// Acquire the VFP record lock for the 1-based <paramref name="recNo"/>
    /// (<see cref="VfpLock.RecordLockPosition"/>). No-op in <see cref="LockMode.Exclusive"/>.
    /// </summary>
    public void Lock(int recNo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(recNo, 1);
        if (_lockMode == LockMode.Exclusive)
            return;
        long pos = VfpLock.RecordLockPosition(recNo, _usesStructuralScheme, _headerLength, _recordLength);
        AcquireTracked(pos, 1);
    }

    /// <summary>Release the VFP record lock for the 1-based <paramref name="recNo"/>.</summary>
    public void Unlock(int recNo)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(recNo, 1);
        if (_lockMode == LockMode.Exclusive)
            return;
        long pos = VfpLock.RecordLockPosition(recNo, _usesStructuralScheme, _headerLength, _recordLength);
        ReleaseTracked(pos, 1);
    }

    /// <summary>
    /// Acquire the whole-file VFP lock (<see cref="VfpLock.FileLockRange"/>): an EXCLUSIVE
    /// byte-range lock over <c>[0x40000000, 0x7FFFFFFE)</c> — the record/FLOCK region, stopping one
    /// byte short of the structural append anchor <c>0x7FFFFFFE</c>. Excluding the anchor lets a
    /// concurrent VFP <c>USE</c> open the table (VFP's open momentarily exclusive-locks the anchor),
    /// while the exclusive hold still denies a VFP <c>FLOCK()</c>, any record lock, and another
    /// FoxDbf writer's whole-file lock. No-op in <see cref="LockMode.Exclusive"/>.
    /// </summary>
    /// <exception cref="IOException">Another writer already holds an overlapping lock.</exception>
    public void LockFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lockMode == LockMode.Exclusive)
            return;
        var (start, length) = VfpLock.FileLockRange();
        AcquireTracked(start, length);
    }

    /// <summary>Release the whole-file VFP lock.</summary>
    public void UnlockFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lockMode == LockMode.Exclusive)
            return;
        var (start, length) = VfpLock.FileLockRange();
        ReleaseTracked(start, length);
    }

    /// <summary>
    /// Acquire the VFP HEADER lock — <c>RLOCK</c> of "record 0", which VFP models as a lock on the table
    /// header / append anchor (<see cref="VfpLock.AppendLockPosition"/>) used to serialise appends. No-op
    /// in <see cref="LockMode.Exclusive"/>.
    /// </summary>
    /// <exception cref="IOException">Another writer already holds an overlapping lock.</exception>
    public void LockHeader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lockMode == LockMode.Exclusive)
            return;
        AcquireTracked(VfpLock.AppendLockPosition(_usesStructuralScheme), 1);
    }

    /// <summary>Release the VFP header lock (<c>RLOCK</c> record 0).</summary>
    public void UnlockHeader()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lockMode == LockMode.Exclusive)
            return;
        ReleaseTracked(VfpLock.AppendLockPosition(_usesStructuralScheme), 1);
    }

    /// <summary>
    /// Acquire the record lock for the 1-based <paramref name="recNo"/> and return a token
    /// whose <see cref="IDisposable.Dispose"/> releases it (scope-based locking).
    /// </summary>
    public IDisposable LockScope(int recNo)
    {
        Lock(recNo);
        return new LockToken(this, () => Unlock(recNo));
    }

    /// <summary>
    /// Acquire the whole-file lock and return a token whose <see cref="IDisposable.Dispose"/>
    /// releases it (scope-based locking).
    /// </summary>
    public IDisposable LockFileScope()
    {
        LockFile();
        return new LockToken(this, UnlockFile);
    }

    /// <summary>
    /// True when this writer holds one or more EXPLICIT (caller-acquired) byte-range locks taken through
    /// the public surface (<see cref="Lock"/> / <see cref="LockFile"/> / <see cref="LockHeader"/>). The
    /// short-lived internal per-mutation locks are always released in <c>WithLock</c>'s finally and are
    /// never tracked here, so this reflects only locks the caller is deliberately holding. The microVFP
    /// interpreter reads it to PIN a cached writer that is coordinating an <c>RLOCK</c>/<c>FLOCK</c> — its
    /// cached-writer prune/invalidate must never silently drop a held byte-range lock (finding 5.13).
    /// </summary>
    public bool HasHeldLocks => _heldLocks.Count > 0;

    // ---- lock plumbing ---------------------------------------------------------

    /// <summary>Lock a byte range and remember it so Dispose can release any leak.</summary>
    private void AcquireTracked(long position, long length)
    {
        _stream.Lock(position, length);
        _heldLocks.Add((position, length));
    }

    /// <summary>Release a tracked byte range and forget it.</summary>
    private void ReleaseTracked(long position, long length)
    {
        _stream.Unlock(position, length);
        _heldLocks.Remove((position, length));
    }

    /// <summary>
    /// Run <paramref name="mutation"/> while holding a short-lived internal byte-range lock at
    /// (<paramref name="position"/>, <paramref name="length"/>), released in a strict finally so
    /// no internal lock ever leaks. No byte-range lock is taken in <see cref="LockMode.Exclusive"/>
    /// (the OS already grants exclusive access). NOT tracked — these never outlive the call.
    /// </summary>
    private void WithLock(long position, long length, Action mutation)
    {
        if (_lockMode == LockMode.Exclusive)
        {
            mutation();
            return;
        }

        // Re-entry guard: Windows byte-range locks reject an OVERLAPPING lock on the same handle,
        // so the canonical VFP idiom (hold a public Lock/LockFile, then call an auto-locking
        // mutator) would otherwise throw. If any already-held public lock fully COVERS the target
        // range, the mutation is already protected — do not re-lock (and do not unlock it here,
        // the public surface owns its lifetime). Covers the exact-byte case (public Lock(rec) then
        // UpdateRecord/Delete on the SAME byte) and the wide-range case (LockFile holds
        // [0x40000000, 0x7FFFFFFE) while a record-update lock 0x7FFFFFFE-recNo sits INSIDE it). An
        // append while holding LockFile targets the anchor 0x7FFFFFFE, which sits just OUTSIDE the
        // file range — it is not "covered", so it takes its own momentary, non-overlapping lock.
        bool covered = false;
        foreach (var h in _heldLocks)
        {
            if (h.Position <= position && position + length <= h.Position + h.Length)
            {
                covered = true;
                break;
            }
        }

        if (!covered)
            _stream.Lock(position, length);
        try
        {
            mutation();
        }
        finally
        {
            if (!covered)
                _stream.Unlock(position, length);
        }
    }

    /// <summary>Disposable token that runs a release action exactly once on Dispose.</summary>
    private sealed class LockToken : IDisposable
    {
        private readonly DbfWriter _owner;
        private Action? _release;

        public LockToken(DbfWriter owner, Action release)
        {
            _owner = owner;
            _release = release;
        }

        public void Dispose()
        {
            var release = _release;
            _release = null;
            if (release is null || _owner._disposed)
                return;
            release();
        }
    }
#pragma warning restore CA1416

    // ---- append / update cores -------------------------------------------------

    private int AppendCore(object?[] positional)
    {
        // §D3: hold the APPEND lock across the FRESH count re-read + row write + header bump so a
        // concurrent VFP appender cannot interleave on the same slot. Released in WithLock's
        // finally. The append byte-range lock provides mutual exclusion but NOT freshness, so the
        // on-disk RecordCount (bytes 4–7) is re-read from the handle INSIDE the lock — a writer
        // that opened before a concurrent VFP append would otherwise hold a stale cached count and
        // overwrite the VFP-appended record(s). CodeBase re-reads reccount inside the lock
        // (D4APPEND.C); we mirror that, including the 2 GB guard against the fresh value.
        long appendPos = VfpLock.AppendLockPosition(_usesStructuralScheme);
        int resultIndex = 0;
        WithLock(appendPos, 1, () =>
        {
            int diskCount = (int)ReadOnDiskRecordCount();
            _recordCount = diskCount;

            long dataOffset = _headerLength + (long)diskCount * _recordLength;
            int newCount = diskCount + 1;
            long endAfter = dataOffset + _recordLength + (WritesEof ? 1 : 0);

            // 2 GB guard (§D-Leitplanken): a DBF addresses records with 32-bit offsets, so the
            // resulting file end must stay below int.MaxValue. Fire BEFORE any mutation so a
            // refused append leaves the file (and the count) untouched.
            if (endAfter > int.MaxValue)
                throw new DbfWriteException(
                    $"Append would grow the file past the 2 GB DBF cap (end offset {endAfter} exceeds int.MaxValue).");

            // AutoIncrement: assign the descriptor's Next to the field (regardless of the value
            // passed) and remember the advanced value to persist after the row write succeeds.
            var pendingAutoInc = new List<(long offset, uint next)>();
            var columns = _schema.Columns;
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (!col.IsAutoIncrement)
                    continue;
                long descOffset = _version.HeaderSize + (long)i * _version.DescriptorWidth;
                uint next = ReadU32LittleEndian(descOffset + 19);
                byte step = ReadByteAt(descOffset + 23);
                positional[i] = unchecked((int)next);
                pendingAutoInc.Add((descOffset + 19, next + step));
            }

            byte[] record = RecordEncoder.Encode(_schema, positional, deleted: false);
            PatchMemoPointers(record, positional);

            // Write the row (overwriting any pre-existing EOF byte), the fresh EOF, and pin the
            // exact file length so no stale trailing bytes survive.
            _stream.Seek(dataOffset, SeekOrigin.Begin);
            _stream.Write(record, 0, record.Length);
            if (WritesEof)
                _stream.WriteByte(0x1A);
            _stream.SetLength(endAfter);

            WriteHeaderCountAndDate(newCount);
            AppendHeaderWriteCount++;

            foreach (var (offset, next) in pendingAutoInc)
                WriteU32LittleEndian(offset, next);

            _stream.Flush();
            _fpt?.Flush();
            AppendDataFlushCount++;

            _recordCount = newCount;
            resultIndex = newCount - 1;

            // §5.1 incremental maintenance: keep the append byte-range lock through the structural-CDX
            // read/modify/write so a second Shared writer cannot interleave page edits.
            MaintainIndexesAfterAppend(newCount, new DbfRecord(_schema, record));
        });

        return resultIndex;
    }

    /// <summary>
    /// Read the live on-disk <c>RecordCount</c> (header bytes 4–7, LE) straight from the OS handle
    /// via <see cref="RandomAccess"/>, BYPASSING the <see cref="FileStream"/> read buffer so a
    /// value cached at open-time (or by an earlier header read) can never mask a concurrent
    /// appender's update. Falls back to the cached count on a short read.
    /// </summary>
    private uint ReadOnDiskRecordCount()
    {
        Span<byte> buf = stackalloc byte[4];
        int read = RandomAccess.Read(_handle, buf, 4);
        return read >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buf) : (uint)_recordCount;
    }

    private void UpdateCore(int index, object?[] positional)
    {
        long offset = _headerLength + (long)index * _recordLength;
        // §5.1: the pre-image (old row) is needed to compute each tag's OLD key and the post-image for the
        // NEW key. Read it when index maintenance is active, regardless of any KeepValue slot.
        bool maintain = ShouldMaintainIndexes();
        byte[]? oldRecordBytes = null;
        byte[]? writtenRecord = null;

        // §D3: hold the RECORD lock for this row (1-based recNo) across the rewrite and any structural-CDX
        // maintenance produced by the key change.
        long recPos = VfpLock.RecordLockPosition(index + 1, _usesStructuralScheme, _headerLength, _recordLength);
        WithLock(recPos, 1, () =>
        {
            var columns = _schema.Columns;

            // KeepValue sentinel: a field whose EXISTING on-disk bytes must survive the full-row
            // rewrite (an FPT block pointer the UPDATE never touched). Read the live record so those
            // fields — and the deletion flag — can be copied back after the row is re-encoded. The same
            // pre-image bytes feed index maintenance (the OLD key), so read them whenever maintaining too.
            bool hasKeep = false;
            for (int i = 0; i < columns.Count && i < positional.Length; i++)
                if (ReferenceEquals(positional[i], KeepValue)) { hasKeep = true; break; }

            byte[]? existing = null;
            if (hasKeep || maintain)
            {
                existing = new byte[_recordLength];
                if (RandomAccess.Read(_handle, existing, offset) < _recordLength)
                    throw new DbfWriteException(
                        $"Could not read record {index} to preserve its existing field bytes.");
            }
            oldRecordBytes = existing;

            // UPDATE changes field data only. Preserve the live deletion marker for EVERY rewrite,
            // including full positional/dictionary updates that need no other pre-image bytes. Read
            // it before encoding or allocating FPT blocks so a corrupt/short record fails without
            // orphaning newly appended memo content.
            byte deletionMarker;
            if (existing is not null)
            {
                deletionMarker = existing[0];
            }
            else
            {
                var marker = new byte[1];
                if (RandomAccess.Read(_handle, marker, offset) != marker.Length)
                    throw new DbfWriteException(
                        $"Could not read record {index} deletion marker before updating it.");
                deletionMarker = marker[0];
            }

            // Encode the row. KeepValue slots are nulled for encoding so the memo-append path
            // (PatchMemoPointers) NEVER sees a read-back pointer/content as new blob data; their real
            // bytes are restored from `existing` below.
            object?[] toEncode = positional;
            if (hasKeep)
            {
                toEncode = (object?[])positional.Clone();
                for (int i = 0; i < toEncode.Length; i++)
                    if (ReferenceEquals(toEncode[i], KeepValue))
                        toEncode[i] = null;
            }

            byte[] record = RecordEncoder.Encode(_schema, toEncode, deleted: false);
            record[0] = deletionMarker;
            PatchMemoPointers(record, toEncode);

            if (hasKeep && existing is not null)
            {
                // Preserve each KeepValue field's existing bytes + its _NullFlags bit.
                for (int i = 0; i < columns.Count && i < positional.Length; i++)
                {
                    if (!ReferenceEquals(positional[i], KeepValue))
                        continue;
                    var col = columns[i];
                    Array.Copy(existing, col.Offset + 1, record, col.Offset + 1, col.Length);
                    CopyNullFlagBit(existing, record, col);
                }
            }

            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(record, 0, record.Length);
            writtenRecord = record;   // the exact bytes just written — reused for index maintenance

            // Stamp the last-update date (bytes 1–3); the count is unchanged by an in-place update.
            WriteDate();

            _stream.Flush();
            _fpt?.Flush();

            // §5.1 incremental maintenance: keep the row lock through the structural-CDX page edits.
            if (maintain && oldRecordBytes is not null && writtenRecord is not null)
                MaintainIndexesAfterUpdate(index + 1,
                    new DbfRecord(_schema, oldRecordBytes), new DbfRecord(_schema, writtenRecord));
        });
    }

    /// <summary>Copy a single column's <c>_NullFlags</c> bit from <paramref name="from"/> to
    /// <paramref name="to"/> (set OR clear), so a preserved FPT field keeps its exact null state.</summary>
    private void CopyNullFlagBit(byte[] from, byte[] to, DbfColumn col)
    {
        var nf = _schema.NullFlagsColumn;
        if (nf is null)
            return;
        var (_, nullBit) = _schema.GetVarlenNullBits(col);
        if (nullBit < 0)
            return;
        int idx = nf.Offset + 1 + (nullBit >> 3);
        if ((uint)idx >= (uint)to.Length || (uint)idx >= (uint)from.Length)
            return;
        byte mask = (byte)(1 << (nullBit & 7));
        if ((from[idx] & mask) != 0) to[idx] |= mask;
        else to[idx] &= (byte)~mask;
    }

    private void SetDeleteFlag(int index, byte flag)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)_recordCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        // §D3: hold the RECORD lock for this row (1-based recNo) across the flag flip.
        long recPos = VfpLock.RecordLockPosition(index + 1, _usesStructuralScheme, _headerLength, _recordLength);
        WithLock(recPos, 1, () =>
        {
            long offset = _headerLength + (long)index * _recordLength;
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.WriteByte(flag);
            _stream.Flush();
        });
    }

    // ---- memo append -----------------------------------------------------------

    /// <summary>
    /// For each FPT-backed column (<c>M</c> memo / <c>W</c> blob / <c>G</c> General-OLE /
    /// <c>P</c> Picture) with non-null content, append a block to the <c>.fpt</c> at
    /// <see cref="_fptNextFree"/> and write the 4-byte LE block number into the record field. A
    /// null value keeps the 0 pointer the encoder already wrote (→ null on read). <c>G</c>/<c>P</c>
    /// store a 4-byte FPT block pointer exactly like <c>M</c>/<c>W</c>; without them here a §D5
    /// Alter migration would write the stale OLD block pointer verbatim into the freshly rebuilt
    /// (empty) <c>.fpt</c> and silently corrupt every General/Picture field.
    /// </summary>
    private void PatchMemoPointers(byte[] record, object?[] positional)
    {
        var columns = _schema.Columns;
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.Type is not ('M' or 'W' or 'G' or 'P'))
                continue;

            object? value = i < positional.Length ? positional[i] : null;
            if (value is null or DBNull)
                continue;

            // Non-null memo/blob content but no usable .fpt sidecar: the encoder already wrote a
            // 0 pointer, so silently returning would discard user-supplied data (reads back null).
            // Fail loudly instead — this is a data-safety footgun, not a best-effort coercion.
            if (_fpt is null)
                throw new DbfWriteException(
                    $"Memo content supplied for column '{col.Name}' but no usable .fpt sidecar is available.");

            int block = AppendMemoBlock(col, value);
            if (block > 0 && col.Length >= 4)
                BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(col.Offset + 1, 4), block);
        }
    }

    private int AppendMemoBlock(DbfColumn col, object value)
    {
        if (_fptNextFree < (uint)FirstMemoDataBlock())
            throw new DbfWriteException(
                "The .fpt sidecar header is truncated or corrupt (NextFree points into the header region); refusing to append memo data.");

        byte[] content = value switch
        {
            byte[] b => b,
            string s => EncodeMemoText(col, s),
            _ => EncodeMemoText(col, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
        };

        int total = 8 + content.Length;
        int blocks = (total + _fptBlockSize - 1) / _fptBlockSize;
        if (blocks <= 0)
            blocks = 1;

        var buffer = new byte[blocks * _fptBlockSize];
        // FPT block header is BIG-endian: type (1 = text/memo) @0, content length @4.
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), (uint)content.Length);
        content.CopyTo(buffer, 8);

        if (_lockMode == LockMode.Shared)
        {
            // S4FOX memo-file lock. Reserve the block range in the header while holding the
            // sidecar lock, then release it before dumping the payload, as CodeBase does.
            const long memoLockPosition = 0x40000000L;
            uint pointer;

#pragma warning disable CA1416 // FileStream.Lock/Unlock are supported on the Windows target used for VFP interop.
            _fpt!.Lock(memoLockPosition, 1);
            try
            {
                Span<byte> diskHeader = stackalloc byte[4];
                if (RandomAccess.Read(_fptHandle!, diskHeader, 0) != diskHeader.Length)
                    throw new DbfWriteException("Could not read the FPT NextFree header.");

                uint diskNextFree = BinaryPrimitives.ReadUInt32BigEndian(diskHeader);
                pointer = Math.Max(_fptNextFree, diskNextFree);
                ulong reservationEnd = (ulong)pointer + (uint)blocks;
                if (pointer > int.MaxValue || reservationEnd > uint.MaxValue)
                    throw new DbfWriteException(
                        $"FPT memo allocation at block {pointer} with length {blocks} cannot be represented.");

                _fptNextFree = (uint)reservationEnd;

                BinaryPrimitives.WriteUInt32BigEndian(diskHeader, _fptNextFree);
                RandomAccess.Write(_fptHandle!, diskHeader, 0);
                _fpt.Flush(flushToDisk: true);
            }
            finally
            {
                _fpt.Unlock(memoLockPosition, 1);
            }
#pragma warning restore CA1416

            long sharedPos = (long)pointer * _fptBlockSize;
            _fpt.Seek(sharedPos, SeekOrigin.Begin);
            _fpt.Write(buffer, 0, buffer.Length);
            return (int)pointer;
        }

        uint exclusivePointer = _fptNextFree;
        long pos = (long)exclusivePointer * _fptBlockSize;

        _fpt!.Seek(pos, SeekOrigin.Begin);
        _fpt.Write(buffer, 0, buffer.Length);

        _fptNextFree = exclusivePointer + (uint)blocks;
        var head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, _fptNextFree);
        _fpt.Seek(0, SeekOrigin.Begin);
        _fpt.Write(head, 0, 4);

        return (int)exclusivePointer;
    }

    private byte[] EncodeMemoText(DbfColumn col, string s)
    {
        // A binary (NOCPTRANS) memo round-trips 1:1 via Latin1; a text memo via the table code page.
        Encoding enc = col.IsBinary ? Encoding.Latin1 : _schema.Encoding;
        return enc.GetBytes(s);
    }

    // ---- header maintenance ----------------------------------------------------

    private void WriteHeaderCountAndDate(int count)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)count);
        _stream.Seek(4, SeekOrigin.Begin);
        _stream.Write(buf);
        WriteDate();
    }

    private void WriteDate()
    {
        var now = DateTime.Now;
        Span<byte> d = stackalloc byte[3];
        d[0] = (byte)(now.Year % 100);
        d[1] = (byte)now.Month;
        d[2] = (byte)now.Day;
        _stream.Seek(1, SeekOrigin.Begin);
        _stream.Write(d);
    }

    // ---- low-level stream helpers ----------------------------------------------

    private uint ReadU32LittleEndian(long offset)
    {
        Span<byte> buf = stackalloc byte[4];
        _stream.Seek(offset, SeekOrigin.Begin);
        int read = _stream.ReadAtLeast(buf, 4, throwOnEndOfStream: false);
        return read >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buf) : 0u;
    }

    private byte ReadByteAt(long offset)
    {
        _stream.Seek(offset, SeekOrigin.Begin);
        int b = _stream.ReadByte();
        return b < 0 ? (byte)0 : (byte)b;
    }

    private void WriteU32LittleEndian(long offset, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(buf);
    }

    // ---- value plumbing --------------------------------------------------------

    /// <summary>True for the VFP versions (0x30/0x31/0x32) that carry the trailing 0x1A EOF byte.</summary>
    private bool WritesEof => _version.Code is 0x30 or 0x31 or 0x32;

    /// <summary>Copy <paramref name="values"/> into a fresh positional array sized to the public columns.</summary>
    private object?[] ToPositional(object?[] values)
    {
        int n = _schema.Columns.Count;
        var positional = new object?[n];
        int copy = Math.Min(n, values.Length);
        for (int i = 0; i < copy; i++)
            positional[i] = values[i];
        return positional;
    }

    /// <summary>Project a name-keyed value map into positional order (missing keys → null).</summary>
    private object?[] DictionaryToPositional(IReadOnlyDictionary<string, object?> values)
    {
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
            lookup[kv.Key] = kv.Value;

        var columns = _schema.Columns;
        var positional = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
            positional[i] = lookup.TryGetValue(columns[i].Name, out var v) ? v : null;
        return positional;
    }

    /// <summary>Locate the <c>.fpt</c> sidecar beside <paramref name="dbfPath"/> (case-insensitive).</summary>
    private static string? FindFpt(string dbfPath)
    {
        string candidate = SidecarFile.ChangeExtension(dbfPath, ".fpt");
        if (File.Exists(candidate))
            return candidate;

        string dir = Path.GetDirectoryName(dbfPath) is { Length: > 0 } d ? d : ".";
        if (!Directory.Exists(dir))
            return null;

        string name = Path.GetFileName(candidate);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                return file;
        }
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;

        // §D3: release any byte-range lock the caller acquired through the public surface and
        // never released, so nothing leaks past the handle being closed. Best-effort per lock.
#pragma warning disable CA1416
        foreach (var (position, length) in _heldLocks)
        {
            try { _stream.Unlock(position, length); } catch { /* best-effort lock release */ }
        }
        _heldLocks.Clear();
#pragma warning restore CA1416

        _disposed = true;
        CloseCdxMaint(flush: true);   // flush + release the incremental .cdx accelerator before the stream closes
        try { _stream.Flush(); } catch { /* best-effort */ }
        try { _fpt?.Flush(); } catch { /* best-effort */ }
        // The schema rides our stream with leaveOpen:true, so disposing it does not close it.
        _schema.Dispose();
        _fpt?.Dispose();
        _stream.Dispose();
    }
}
