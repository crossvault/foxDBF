using System.Runtime.Intrinsics.X86;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// Decoder for a COMPACT LEAF node of a FoxPro compact index (plan §C4).
///
/// A compact leaf packs three things into a 512-byte page:
/// <list type="number">
///   <item>the 12-byte node header (see <see cref="IndexNodeHeader"/>);</item>
///   <item>the <see cref="LeafInfo"/> block (@12..23) holding the bit widths and
///   masks used to unpack each entry;</item>
///   <item>a fixed-width entry-info array growing FORWARD from offset 24, and the
///   distinct key bytes growing BACKWARD from the page end (the 488-byte tail
///   region).</item>
/// </list>
///
/// For each entry the <see cref="LeafInfo.BytesPerEntry"/>-byte record is read as a
/// LITTLE-endian unsigned integer <c>V</c>:
/// <code>
///   recordNumber = V &amp; RecnoMask
///   dup          = (V >> RecnoBits) &amp; DupCountMask           // 0 for entry 0
///   trail        = (V >> (RecnoBits + DupBits)) &amp; TrailCountMask
/// </code>
/// The key is then rebuilt as <c>dup</c> prefix bytes copied from the PREVIOUS
/// reconstructed key, followed by <c>keyLength - dup - trail</c> fresh bytes taken
/// backward from the tail cursor, followed by <c>trail</c> pad bytes
/// (<c>0x20</c> for a MACHINE character tag, otherwise <c>0x00</c> — a GENERAL / SPANISH key is
/// padded with 0x00, see <see cref="IndexKey.CharacterKeyPad"/>).
/// </summary>
public static class CompactLeaf
{
    /// <summary>Byte offset where the fixed-width entry-info array begins.</summary>
    public const int EntryArrayOffset = LeafInfo.EntryArrayOffset;

    /// <summary>
    /// Selects which kernel extracts the fixed-width <c>recno / dup / trail</c> fields
    /// from each entry-info integer (Highlike Phase B-4). EXPOSED to tests so the
    /// portable scalar fallback and the BMI2 <c>PEXT</c> fast path can be exercised and
    /// proven BYTE-IDENTICAL on the SAME (BMI2-capable) machine — the field format,
    /// front-coding and the higher-level reader are unchanged either way.
    /// </summary>
    internal enum DecodePath
    {
        /// <summary>Pick the BMI2 <c>PEXT</c> kernel when the CPU supports it, else scalar.</summary>
        Auto,

        /// <summary>Force the portable scalar shift/mask loop (also simulates "no BMI2").</summary>
        Scalar,

        /// <summary>Force the BMI2 <c>PEXT</c> kernel; falls back to scalar on an unsupported CPU (never throws).</summary>
        Bmi2,
    }

    /// <summary>
    /// True when the BMI2 <c>PEXT</c> (ParallelBitExtract) fast path can run on this CPU
    /// (in-box <see cref="Bmi2.X64"/>; no extra dependency). When false every path uses the
    /// scalar fallback.
    /// </summary>
    internal static bool IsBmi2Available => Bmi2.X64.IsSupported;

    /// <summary>
    /// Decodes the compact leaf page into its ordered <see cref="LeafEntry"/>
    /// list. <paramref name="keyLength"/> is the owning tag's key length;
    /// <paramref name="isCharacter"/> selects the trailing pad byte
    /// (<c>0x20</c> when true, else <c>0x00</c>). Returns an empty list for an
    /// empty leaf and never throws on a malformed page.
    /// </summary>
    public static IReadOnlyList<LeafEntry> Decode(ReadOnlySpan<byte> page, int keyLength, bool isCharacter)
        => Decode(page, keyLength, isCharacter ? (byte)0x20 : (byte)0x00, DecodePath.Auto);

    /// <summary>
    /// As <see cref="Decode(ReadOnlySpan{byte},int,bool)"/> with the trailing <paramref name="pad"/> byte given
    /// directly (a weighted character tag pads with <c>0x00</c>, a MACHINE one with <c>0x20</c>).
    /// </summary>
    internal static IReadOnlyList<LeafEntry> Decode(ReadOnlySpan<byte> page, int keyLength, byte pad)
        => Decode(page, keyLength, pad, DecodePath.Auto);

    /// <summary>
    /// As <see cref="Decode(ReadOnlySpan{byte},int,bool)"/> but with an explicit
    /// field-extraction <paramref name="path"/> (Highlike Phase B-4). Only the unpack
    /// kernel differs between paths; the decoded result is byte-identical.
    /// </summary>
    internal static IReadOnlyList<LeafEntry> Decode(ReadOnlySpan<byte> page, int keyLength, bool isCharacter, DecodePath path)
        => Decode(page, keyLength, isCharacter ? (byte)0x20 : (byte)0x00, path);

    private static IReadOnlyList<LeafEntry> Decode(ReadOnlySpan<byte> page, int keyLength, byte pad, DecodePath path)
    {
        if (page.Length < LeafInfo.EntryArrayOffset || keyLength <= 0)
            return Array.Empty<LeafEntry>();

        var header = IndexNodeHeader.Parse(page);

        // A BRANCH page has no LeafInfo block: bytes @12-23 are the middle of the
        // first branch entry's (big-endian) key/recno/child. Reinterpreting them as
        // a LeafInfo block can pass every numeric bounds guard below yet yield
        // plausible-but-garbage entries. Honour the codebase's 'return empty on
        // malformed' convention and refuse anything without the leaf bit set.
        if (!header.IsLeaf)
            return Array.Empty<LeafEntry>();

        int keyCount = header.KeyCount;
        if (keyCount <= 0)
            return Array.Empty<LeafEntry>();

        var info = LeafInfo.Parse(page);
        int kBy = info.BytesPerEntry;
        if (kBy <= 0 || kBy > 8)
            return Array.Empty<LeafEntry>();

        // Cap to what can physically fit in the 488-byte entry region so a
        // crafted KeyCount cannot drive an out-of-bounds read or huge alloc.
        keyCount = CapKeyCount(keyCount, kBy);

        // Resolve the field-extraction kernel ONCE for the whole page. The BMI2 path is
        // only ever taken when the CPU actually supports it, so an unsupported CPU (ARM,
        // older x86) silently uses the scalar fallback — it never throws.
        bool useBmi2 = path switch
        {
            DecodePath.Scalar => false,
            // Auto and Bmi2 both pick the PEXT kernel iff this CPU supports it,
            // otherwise the proven scalar fallback (byte-identical either way).
            _ => IsBmi2Available,
        };

        var entries = new List<LeafEntry>(keyCount);
        int entryOffset = LeafInfo.EntryArrayOffset;   // entry-info array grows FORWARD
        int tail = IndexFile.PageSize;                 // key bytes consumed BACKWARD from page end
        byte[]? previous = null;

        for (int i = 0; i < keyCount; i++)
        {
            if (entryOffset + kBy > IndexFile.PageSize)
                break; // truncated / malformed node: stop rather than throw

            // Read kBy bytes of the entry-info record as a LITTLE-endian integer V.
            ulong v = 0;
            for (int b = 0; b < kBy; b++)
                v |= (ulong)page[entryOffset + b] << (8 * b);
            entryOffset += kBy;

            Unpack(v, info, useBmi2, out uint recordNumber, out int dup, out int trail);

            if (i == 0)
                dup = 0; // the first entry of a node has no predecessor

            // Defensive clamping so a corrupt record never overruns the key.
            if (dup > keyLength) dup = keyLength;
            if (trail > keyLength - dup) trail = keyLength - dup;
            int fresh = keyLength - dup - trail;
            if (fresh < 0) fresh = 0;

            var key = new byte[keyLength];

            // 1) dup prefix bytes copied from the PREVIOUS reconstructed key.
            if (dup > 0 && previous is not null)
            {
                int copy = Math.Min(dup, previous.Length);
                Array.Copy(previous, 0, key, 0, copy);
            }

            // 2) `fresh` distinct bytes taken from the tail cursor. The block
            //    occupies [tail-fresh, tail) and is read FORWARD within itself.
            int freshStart = tail - fresh;
            for (int j = 0; j < fresh; j++)
            {
                int src = freshStart + j;
                if ((uint)src < (uint)page.Length)
                    key[dup + j] = page[src];
            }
            tail -= fresh;

            // 3) `trail` pad bytes (0x20 for MACHINE character tags, else 0x00).
            for (int j = 0; j < trail; j++)
                key[dup + fresh + j] = pad;

            entries.Add(new LeafEntry(recordNumber, key));
            previous = key;
        }

        return entries;
    }

    internal static int DuplicatePrefix(ReadOnlySpan<byte> page, LeafInfo info, int slot)
    {
        if (slot == 0) return 0;
        int offset = LeafInfo.EntryArrayOffset + slot * info.BytesPerEntry;
        if (info.BytesPerEntry is < 1 or > 8 || offset + info.BytesPerEntry > page.Length) return 0;
        ulong bits = 0;
        for (int b = 0; b < info.BytesPerEntry; b++) bits |= (ulong)page[offset + b] << (b * 8);
        return (int)((bits >> info.RecnoBits) & info.DupCountMask);
    }

    /// <summary>Caps a declared leaf key count to the physical entry-info capacity of one page.</summary>
    internal static int CapKeyCount(int declaredKeyCount, int bytesPerEntry)
    {
        if (declaredKeyCount <= 0 || bytesPerEntry <= 0)
            return 0;
        int maxEntries = (IndexFile.PageSize - LeafInfo.EntryArrayOffset) / bytesPerEntry;
        return Math.Min(declaredKeyCount, maxEntries);
    }

    /// <summary>
    /// Extracts the three fixed-width fields (record number, dup count, trail count)
    /// from the little-endian entry-info integer <paramref name="v"/>. Dispatches to the
    /// BMI2 <c>PEXT</c> kernel when <paramref name="useBmi2"/> is set, otherwise the scalar
    /// shift/mask kernel. Both kernels are required to be byte-identical.
    /// </summary>
    private static void Unpack(ulong v, in LeafInfo info, bool useBmi2,
        out uint recordNumber, out int dup, out int trail)
    {
        if (useBmi2)
        {
            UnpackBmi2(v, info, out recordNumber, out dup, out trail);
            return;
        }

        // Scalar fallback: shift the field down to bit 0 and mask off its width.
        recordNumber = (uint)(v & info.RecnoMask);
        dup = (int)((v >> info.RecnoBits) & info.DupCountMask);
        trail = (int)((v >> (info.RecnoBits + info.DupBits)) & info.TrailCountMask);
    }

    /// <summary>
    /// BMI2 <c>PEXT</c> (ParallelBitExtract) fast path for the fixed-width field unpack.
    /// </summary>
    /// <remarks>
    /// One <see cref="Bmi2.X64.ParallelBitExtract(ulong,ulong)"/> per field. Each PEXT mask
    /// is the field's stored mask shifted to the field's bit offset:
    /// <list type="bullet">
    ///   <item><c>recno</c> = PEXT(v, RecnoMask) — offset 0</item>
    ///   <item><c>dup</c>   = PEXT(v, DupCountMask &lt;&lt; RecnoBits)</item>
    ///   <item><c>trail</c> = PEXT(v, TrailCountMask &lt;&lt; (RecnoBits + DupBits))</item>
    /// </list>
    /// Because the stored masks are contiguous low bits, PEXT(v, mask &lt;&lt; shift) compacts to
    /// exactly <c>(v &gt;&gt; shift) &amp; mask</c> — i.e. byte-identical to the scalar kernel for every
    /// entry. Callers only reach here when <see cref="Bmi2.X64.IsSupported"/> is true, so this
    /// never throws on an unsupported CPU (the dispatch in <c>Decode</c> picks scalar).
    /// </remarks>
    private static void UnpackBmi2(ulong v, in LeafInfo info,
        out uint recordNumber, out int dup, out int trail)
    {
        ulong recnoMask = info.RecnoMask;
        ulong dupMask = (ulong)info.DupCountMask << info.RecnoBits;
        ulong trailMask = (ulong)info.TrailCountMask << (info.RecnoBits + info.DupBits);

        recordNumber = (uint)Bmi2.X64.ParallelBitExtract(v, recnoMask);
        dup = (int)Bmi2.X64.ParallelBitExtract(v, dupMask);
        trail = (int)Bmi2.X64.ParallelBitExtract(v, trailMask);
    }
}
