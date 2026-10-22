using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D4 — CREATE a DBF table from scratch (≥ VFP6). Writes a valid EMPTY <c>.dbf</c> (header,
/// 32-byte field descriptors, the auto-added hidden <c>_NullFlags</c> system column, the
/// <c>0x0D</c> terminator and the 263-byte backlink), picks the version byte by feature
/// (<c>0x30</c> base / <c>0x31</c> AutoIncrement / <c>0x32</c> Varchar-Varbinary), creates the
/// companion <c>.fpt</c> when a memo column exists, and returns an open <see cref="DbfWriter"/>
/// ready for <see cref="DbfWriter.AppendRecord(object?[])"/>.
/// </summary>
public sealed partial class DbfWriter
{
    // The standard 32-byte header, 32-byte descriptors, and the 263-byte VFP backlink block.
    private const int CreateHeaderSize = 32;
    private const int CreateDescriptorWidth = 32;
    private const int CreateBacklinkSize = 263;

    /// <summary>
    /// Create a new DBF at <paramref name="path"/> from <paramref name="columns"/> and return an
    /// open writer (plan §D4). Throws <see cref="DbfSchemaException"/> for an invalid schema
    /// (empty/duplicate name, bad length, &gt; 255 columns).
    /// </summary>
    public static DbfWriter Create(string path, IEnumerable<DbfColumnDef> columns, DbfCreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(columns);
        options ??= new DbfCreateOptions();

        var defs = columns.ToList();

        // Validate + assemble the full header image in memory (throws BEFORE any byte is written).
        byte[] buffer = BuildHeaderImage(defs, options, out bool hasMemo);

        // ---- write the files atomically (temp file + atomic move; never clobber unless asked) ---
        WriteTableImageAtomically(path, buffer, hasMemo, options.MemoBlockSize, options.Overwrite);

        // ---- reopen as a ready writer ---------------------------------------------
        var openOptions = new DbfOptions
        {
            LockMode = options.LockMode,
            Encoding = options.Encoding,
        };
        return Open(path, openOptions);
    }

    /// <summary>
    /// Validate <paramref name="defs"/> (plan §D4) and assemble the complete in-memory <c>.dbf</c>
    /// header image — header, 32-byte descriptors, the auto-added hidden <c>_NullFlags</c> system
    /// column, the <c>0x0D</c> terminator, the 263-byte backlink and the trailing <c>0x1A</c> EOF.
    /// Throws <see cref="DbfSchemaException"/> for any invalid schema. Sets <paramref name="hasMemo"/>
    /// when a companion <c>.fpt</c> is required. Shared by §D4 Create and §D5 Alter so both produce
    /// byte-identical geometry from the same column set.
    /// </summary>
    internal static byte[] BuildHeaderImage(IReadOnlyList<DbfColumnDef> defs, DbfCreateOptions options, out bool hasMemo)
    {
        // ---- §D4 schema validation (reject BEFORE any byte is written) -------------
        if (defs.Count == 0)
            throw new DbfSchemaException("A DBF must define at least one column.");
        if (defs.Count > 255)
            throw new DbfSchemaException($"A DBF supports at most 255 columns ({defs.Count} requested).");

        // The hidden VFP null-bitmap column is auto-added by name; a USER column may not claim it,
        // else the file would carry two physical descriptors named "_NullFlags" — a layout the real
        // VFP9 runtime rejects and which corrupts the reader's system-column bitmap detection.
        // Field names are stored in the TABLE's code page, exactly like VFP9 does (a CP1252 'Ó' is 0xD3,
        // the same byte vfp_test/uninames/unifree.dbf carries) and exactly how the reader decodes them.
        var nameEncoding = options.Encoding ?? Encodings.ResolveEncoding(options.CodePage);
        var nameBytes = new byte[defs.Count][];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_NullFlags" };
        for (int i = 0; i < defs.Count; i++)
        {
            var c = defs[i];
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new DbfSchemaException("A column name must be a non-empty string.");
            if (c.Name.Length > 10)
                throw new DbfSchemaException(
                    $"Column name '{c.Name}' exceeds the 10-character DBF field-name limit.");
            // VFP stores field names upper-cased in the table code page (razónSoc → RAZÓNSOC, œuvre → ŒUVRE).
            nameBytes[i] = CodePageText.Encode(CodePageText.ToUpperInCodePage(c.Name, nameEncoding), nameEncoding,
                "Column name", m => new DbfSchemaException(m));
            if (nameBytes[i].Length > 10)
                throw new DbfSchemaException(
                    $"Column name '{c.Name}' needs {nameBytes[i].Length} bytes in code page {nameEncoding.CodePage}; " +
                    "the DBF field-name slot holds 10.");
            if (StringComparer.OrdinalIgnoreCase.Equals(c.Name, "_NullFlags"))
                throw new DbfSchemaException(
                    "Column name '_NullFlags' is reserved for the VFP null-bitmap system column.");
            if (!seen.Add(c.Name))
                throw new DbfSchemaException($"Duplicate column name '{c.Name}' (names are case-insensitive).");
            if (c.Length < 1 || c.Length > 255)
                throw new DbfSchemaException(
                    $"Column '{c.Name}' has an out-of-range length ({c.Length}); the descriptor length byte holds 1–255.");
            ValidateTypeLength(c);
        }

        // ---- feature detection → version byte / memo flag / _NullFlags ------------
        bool hasAutoInc = defs.Any(c => c.AutoIncrement is not null);
        bool hasVarlen = defs.Any(c => c.Type is 'V' or 'Q');
        hasMemo = defs.Any(c => c.Type is 'M' or 'W' or 'G' or 'P');

        // 0x32 (Varchar/Varbinary) outranks 0x31 (AutoIncrement) outranks 0x30 (base).
        byte versionByte = hasVarlen ? (byte)0x32 : hasAutoInc ? (byte)0x31 : (byte)0x30;

        // _NullFlags bit budget: 1 bit per nullable field, 1 bit per varlen (V/Q) field
        // (a field that is BOTH consumes 2 bits). Assigned LSB-first in physical order.
        int bitCount = 0;
        foreach (var c in defs)
        {
            if (c.Type is 'V' or 'Q') bitCount++;
            if (c.Nullable) bitCount++;
        }
        bool needsNullFlags = bitCount > 0;
        int nullFlagsLen = needsNullFlags ? (bitCount + 7) / 8 : 0;

        int physicalCount = defs.Count + (needsNullFlags ? 1 : 0);

        // ---- geometry --------------------------------------------------------------
        int recordLength = 1; // leading delete flag
        foreach (var c in defs)
            recordLength += c.Length;
        if (needsNullFlags)
            recordLength += nullFlagsLen;

        int headerLength = CreateHeaderSize + physicalCount * CreateDescriptorWidth + 1 + CreateBacklinkSize;

        if (recordLength > ushort.MaxValue)
            throw new DbfSchemaException(
                $"The total record length ({recordLength}) exceeds the 65535-byte DBF limit.");

        // ---- assemble the header region (header + descriptors + 0x0D + backlink) ---
        var buffer = new byte[headerLength + 1]; // +1 for the trailing 0x1A EOF (VFP)

        buffer[0] = versionByte;
        var now = DateTime.Now;
        buffer[1] = (byte)(now.Year % 100);
        buffer[2] = (byte)now.Month;
        buffer[3] = (byte)now.Day;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 0u);                  // RecordCount 0
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), (ushort)recordLength);
        // bytes 12–27 reserved (0). Table flags @28: memo bit 0x02 (free table → no 0x01 cdx bit).
        buffer[28] = (byte)(hasMemo ? 0x02 : 0x00);
        buffer[29] = options.CodePage;
        // bytes 30–31 reserved (0).

        int descPos = CreateHeaderSize;
        int fieldOffset = 0; // prefix-sum of prior field lengths (record byte = offset + 1)
        for (int i = 0; i < defs.Count; i++)
        {
            var c = defs[i];
            WriteNullableName(buffer.AsSpan(descPos, 11), nameBytes[i]);
            buffer[descPos + 11] = (byte)c.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(descPos + 12, 4), (uint)(fieldOffset + 1)); // displacement
            buffer[descPos + 16] = (byte)c.Length;
            buffer[descPos + 17] = (byte)c.Decimal;

            byte flags = 0;
            if (c.Nullable) flags |= 0x02;
            // 0x04 (binary, no code-page translation): requested NOCPTRANS, and automatically every type VFP9
            // stores in binary form — I, B, Y, T, Q, W (probe 2026-10-22; C/N/F/D/L/M/G/V stay 0).
            if (c.Binary || char.ToUpperInvariant(c.Type) is 'I' or 'B' or 'Y' or 'T' or 'Q' or 'W') flags |= 0x04;
            if (c.AutoIncrement is not null) flags |= 0x08;
            buffer[descPos + 18] = flags;

            if (c.AutoIncrement is { } ai)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(descPos + 19, 4), ai.NextValue);
                buffer[descPos + 23] = ai.Step;
            }

            fieldOffset += c.Length;
            descPos += CreateDescriptorWidth;
        }

        if (needsNullFlags)
        {
            // The hidden _NullFlags system column: type '0', system+binary flags (0x05) — the
            // exact byte real VFP writes (verified against data/*.dbf fixtures).
            WriteNullableName(buffer.AsSpan(descPos, 11), "_NullFlags"u8.ToArray());
            buffer[descPos + 11] = (byte)'0';
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(descPos + 12, 4), (uint)(fieldOffset + 1));
            buffer[descPos + 16] = (byte)nullFlagsLen;
            buffer[descPos + 17] = 0;
            buffer[descPos + 18] = 0x05; // 0x01 system | 0x04 binary
            descPos += CreateDescriptorWidth;
        }

        buffer[descPos] = 0x0D; // descriptor-array terminator
        int backlinkPos = descPos + 1;

        // 263-byte backlink: all-zero = free table, or the relative .dbc path (table code page, NUL-padded).
        if (!string.IsNullOrEmpty(options.BacklinkPath))
        {
            byte[] link = CodePageText.Encode(options.BacklinkPath, nameEncoding, "Backlink path", m => new DbfSchemaException(m));
            if (link.Length > CreateBacklinkSize - 1)
                throw new DbfSchemaException(
                    $"Backlink path '{options.BacklinkPath}' needs {link.Length} bytes; the backlink holds {CreateBacklinkSize - 1}.");
            link.CopyTo(buffer.AsSpan(backlinkPos, link.Length));
        }

        // VFP 0x30/0x31/0x32 carry a trailing 0x1A EOF byte even when empty.
        buffer[headerLength] = 0x1A;

        return buffer;
    }

    /// <summary>
    /// Enforce the type-specific FIXED on-disk length for the fixed-width VFP field types
    /// (plan §A5b). A length the real VFP9 runtime would reject at <c>USE</c> time (e.g. an
    /// <c>I</c> field of 3 bytes, a <c>D</c> field of 4) is refused up-front with a typed
    /// <see cref="DbfSchemaException"/> instead of producing a structurally corrupt descriptor.
    /// Shared by §D4 Create and §D5 Alter. Variable-width types (<c>C</c>, <c>V</c>, <c>Q</c>)
    /// keep the generic 1–255 range already validated by the caller.
    /// </summary>
    private static void ValidateTypeLength(DbfColumnDef c)
    {
        static void Fixed(DbfColumnDef col, int required)
        {
            if (col.Length != required)
                throw new DbfSchemaException(
                    $"Column '{col.Name}' of type '{col.Type}' must have length {required} (got {col.Length}).");
        }

        switch (char.ToUpperInvariant(c.Type))
        {
            case 'I': // Integer
            case '+': // AutoIncrement integer
                Fixed(c, 4);
                break;
            case 'Y': // Currency
            case 'B': // Double
            case 'D': // Date
            case 'T': // DateTime
            case '@': // Timestamp
                Fixed(c, 8);
                break;
            case 'L': // Logical
                Fixed(c, 1);
                break;
            case 'M': // Memo
            case 'W': // Blob
            case 'G': // General / OLE
            case 'P': // Picture
                Fixed(c, 4);
                break;
            case 'N': // Numeric (ASCII)
            case 'F': // Float (ASCII)
                if (c.Length is < 1 or > 20)
                    throw new DbfSchemaException(
                        $"Column '{c.Name}' of type '{c.Type}' must have length 1–20 (got {c.Length}).");
                break;
        }
    }

    /// <summary>Test-only fault-injection seam: when non-null it is invoked immediately BEFORE the
    /// final, authoritative <c>.dbf</c> commit move (after the new <c>.fpt</c> has been committed and the
    /// original pair staged as backups), so regression tests can assert the rollback restores a
    /// consistent original <c>.dbf</c>/<c>.fpt</c> pair. <see cref="ThreadStaticAttribute"/> so a hook set
    /// by one test thread never trips a write running on another (xUnit parallelism). Always <c>null</c>
    /// in production.</summary>
    [ThreadStatic]
    internal static Action? FaultBeforeDbfCommit;

    /// <summary>
    /// Write the assembled <paramref name="dbfImage"/> (and a fresh empty <c>.fpt</c> when
    /// <paramref name="hasMemo"/>) to a SIBLING temp file in the same directory, then commit with an
    /// atomic <see cref="File.Move(string, string, bool)"/> so a disk-full / I/O fault mid-write
    /// leaves any pre-existing file byte-identical instead of a half-written partial header
    /// (§D-Leitplanken). The temp files are deleted in <c>finally</c> on any failure before commit.
    /// Refuses to clobber an existing file unless <paramref name="overwrite"/> is set
    /// (<c>CreateNew</c> semantics).
    /// </summary>
    private static void WriteTableImageAtomically(string path, byte[] dbfImage, bool hasMemo, int memoBlockSize, bool overwrite)
    {
        if (!overwrite && File.Exists(path))
            throw new DbfWriteException(
                $"A file already exists at '{path}'. Set DbfCreateOptions.Overwrite = true to replace it.");

        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full) is { Length: > 0 } d ? d : ".";
        string stem = Path.GetFileNameWithoutExtension(full) + "_" + Guid.NewGuid().ToString("N");
        string tmpDbf = Path.Combine(dir, stem + ".dbf.tmp");
        string tmpFpt = Path.Combine(dir, stem + ".fpt.tmp");
        string fptPath = SidecarFile.ChangeExtension(full, ".fpt");

        string bakDbf = full + ".bak_" + Guid.NewGuid().ToString("N");
        string bakFpt = fptPath + ".bak_" + Guid.NewGuid().ToString("N");
        bool dbfStaged = false, fptStaged = false, dbfBacked = false, fptBacked = false, fptMoved = false;
        try
        {
            File.WriteAllBytes(tmpDbf, dbfImage);
            dbfStaged = true;
            if (hasMemo)
            {
                WriteEmptyFpt(tmpFpt, memoBlockSize);
                fptStaged = true;
            }

            // Back up BOTH existing files of the prior pair so a mid-commit fault can restore them
            // intact — restoring only the .dbf would leave the OLD .dbf paired with the NEW .fpt
            // (the exact orphaned-memo inconsistency the staging is meant to prevent).
            if (overwrite && File.Exists(full))
            {
                File.Move(full, bakDbf);
                dbfBacked = true;
            }
            if (overwrite && File.Exists(fptPath))
            {
                File.Move(fptPath, bakFpt);
                fptBacked = true;
            }

            try
            {
                // Commit the .fpt FIRST, then the .dbf LAST (authoritative). A memo-bearing .dbf is thus
                // never committed without its sidecar already in place — no orphaned memo pairing.
                if (hasMemo)
                {
                    File.Move(tmpFpt, fptPath, overwrite: true);
                    fptStaged = false;
                    fptMoved = true;
                }
                FaultBeforeDbfCommit?.Invoke();
                File.Move(tmpDbf, full, overwrite: true);
                dbfStaged = false;
            }
            catch
            {
                // Roll the original pair back into place so a partial create cannot leave an inconsistent
                // .dbf/.fpt pair (best-effort): discard the half-committed new sidecar, then restore both
                // backups byte-identical to the pre-create state.
                try
                {
                    if (fptMoved && File.Exists(fptPath)) File.Delete(fptPath);
                    if (fptBacked && File.Exists(bakFpt)) File.Move(bakFpt, fptPath, overwrite: true);
                    if (dbfBacked && File.Exists(bakDbf)) File.Move(bakDbf, full, overwrite: true);
                }
                catch { /* best-effort rollback */ }
                throw;
            }
        }
        finally
        {
            if (dbfStaged) TryDelete(tmpDbf);
            if (fptStaged) TryDelete(tmpFpt);
            TryDelete(bakDbf); // on success the prior files are superseded; on rollback they were moved back.
            TryDelete(bakFpt);
        }
    }

    /// <summary>Best-effort delete that never throws (temp-file cleanup).</summary>
    private static void TryDelete(string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>Write the already-encoded (≤ 10 byte) <paramref name="name"/> NUL-padded into an 11-byte descriptor name slot.</summary>
    private static void WriteNullableName(Span<byte> slot, byte[] name)
    {
        slot.Clear();
        name.CopyTo(slot); // validated ≤ 10 bytes, so at least one NUL terminator remains
    }

    /// <summary>
    /// Create an empty FoxPro memo (<c>.fpt</c>) sidecar with a valid big-endian header:
    /// NextFree block (u32 @0) = 1 (the first data block follows the header block) and the
    /// block size (u16 @6). The file is exactly one block long.
    /// </summary>
    private static void WriteEmptyFpt(string fptPath, int blockSize)
    {
        if (blockSize <= 0)
            blockSize = 64;
        // Visual FoxPro reserves a full 512-byte FPT header (8 blocks at the 64-byte default);
        // memo blocks start at the first block boundary at or past byte 512, and the next-free
        // block points there. A shorter header is tolerated by lenient readers but VFP rejects
        // it ("memo file is missing or is invalid"). Round the reserved header up to a whole
        // number of blocks so a memo block never overlaps the header.
        uint nextFree = (uint)((512 + blockSize - 1) / blockSize);   // ceil(512 / blockSize)
        int headerLen = (int)nextFree * blockSize;
        var head = new byte[headerLen];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(0, 4), nextFree);            // next-free block
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(6, 2), (ushort)blockSize);   // block size
        File.WriteAllBytes(fptPath, head);
    }
}
