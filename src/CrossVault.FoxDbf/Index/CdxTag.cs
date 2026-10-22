using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// A single tag of a compound index (<c>.cdx</c>) — plan §C1/§C2/§C3. A tag is a
/// complete compact B-tree: its <see cref="RootPageOffset"/> node descends to an
/// ordered, doubly-linked list of compact leaves. <see cref="EnumerateEntries"/>
/// yields the tag's <see cref="IndexEntry"/> pairs in INDEX ORDER (honouring
/// <see cref="Descending"/>).
/// </summary>
/// <remarks>
/// The DESCENDING on-disk effect is verified against <c>idxtest</c>: a DESC tag
/// stores the SAME ascending key bytes as the matching ASC tag — VFP realises
/// descending purely by REVERSE TRAVERSAL, not by pre-inverting the key bytes.
/// Never throws on a malformed tag — yields what is readable.
/// </remarks>
public sealed class CdxTag
{
    private readonly IndexFile _index;

    /// <summary>The tag name as stored in the tag directory (trimmed).</summary>
    public string Name { get; }

    /// <summary>The tag's key length in bytes (from its own tag header).</summary>
    public int KeyLength { get; }

    /// <summary>The uncompiled KEY expression text.</summary>
    public string KeyExpression { get; }

    /// <summary>The uncompiled FOR expression text (empty when none).</summary>
    public string ForExpression { get; }

    /// <summary>
    /// True when the tag was created UNIQUE (header Options bit <c>0x01</c>): the
    /// index keeps only one entry (lowest recno) per distinct key, so it
    /// UNDER-represents duplicate-key records and is unsafe for index-driven
    /// candidate sets on positive equality/range/IN/BETWEEN leaves.
    /// </summary>
    public bool IsUnique { get; }

    /// <summary>True when the tag was created DESCENDING (header @502).</summary>
    public bool Descending { get; }

    /// <summary>The collation name (e.g. "MACHINE"/"GENERAL").</summary>
    public string Collation { get; }

    /// <summary>The byte offset of this tag's root node.</summary>
    public uint RootPageOffset { get; }

    /// <summary>
    /// True when the key is a CHARACTER key (compact-leaf trailing pad = 0x20);
    /// false for numeric/date/datetime/integer keys (pad = 0x00). Derived from the
    /// KEY expression + optional DBF schema.
    /// </summary>
    public bool IsCharacterKey { get; }

    /// <summary>
    /// The logical key type (plan §C5), resolved from the <see cref="KeyExpression"/>
    /// against the DBF schema supplied to <see cref="CdxFile"/>. Drives
    /// <see cref="DecodeKey"/> / <see cref="Seek(object)"/>. <see cref="IndexKeyType.Unknown"/>
    /// when no schema was supplied or the expression is not a bare field name.
    /// </summary>
    public IndexKeyType KeyType { get; }

    /// <summary>
    /// The code page a MACHINE character key of this tag is stored in: the table encoding (Latin-1 for a
    /// bare binary/NOCPTRANS field), or the library default (CP1252) when no table was supplied. See
    /// <see cref="IndexKey.ResolveCharacterEncoding"/>.
    /// </summary>
    internal System.Text.Encoding CharacterEncoding { get; }

    private readonly IVfpCollation _collation;
    private readonly CollationSupport _collationSupport;

    /// <summary>
    /// The collation that produced this tag's CHARACTER keys: <see cref="Collation"/> resolved for the TABLE code
    /// page (VFP9 weights GENERAL / SPANISH per code page). MACHINE for an unsupported sort sequence.
    /// </summary>
    internal IVfpCollation KeyCollation => _collation;

    /// <summary>
    /// True when <see cref="KeyCollation"/> reproduces VFP9's stored keys byte-for-byte (MACHINE; GENERAL / SPANISH
    /// on CP1252 or CP850). False for GENERAL / SPANISH on another code page (CP1252 weights stand in — right for
    /// ASCII only) and for an unknown sort sequence: a range over such keys is no proven superset, so Rushmore
    /// does not narrow on the tag.
    /// </summary>
    internal bool IsKeyCollationExact => _collationSupport == CollationSupport.Exact;

    /// <summary>Sentinel meaning "no matching record" for <see cref="Seek(object)"/> — <see langword="null"/>.</summary>
    public static readonly uint? NotFound = null;

    internal CdxTag(
        IndexFile index,
        string name,
        int keyLength,
        string keyExpression,
        string forExpression,
        bool descending,
        string collation,
        uint rootPageOffset,
        bool isCharacterKey,
        IndexKeyType keyType,
        bool isUnique = false,
        System.Text.Encoding? characterEncoding = null,
        int tableCodePage = 1252)
    {
        _index = index;
        Name = name;
        KeyLength = keyLength;
        KeyExpression = keyExpression;
        ForExpression = forExpression;
        IsUnique = isUnique;
        Descending = descending;
        Collation = collation;
        RootPageOffset = rootPageOffset;
        IsCharacterKey = isCharacterKey;
        KeyType = keyType;
        CharacterEncoding = characterEncoding ?? IndexKey.ResolveCharacterEncoding(null, null);
        _collationSupport = VfpCollations.Resolve(collation, tableCodePage, out _collation);
    }

    /// <summary>
    /// The NATURAL (unpadded) stored bytes of the character value <paramref name="s"/> in this tag: the
    /// raw <see cref="CharacterEncoding"/> bytes under MACHINE, the collation weight key otherwise (cut to
    /// <see cref="KeyLength"/> like the stored key) — exactly what the CDX builder wrote, so it is the needle for
    /// SEEK / Rushmore / SET KEY.
    /// </summary>
    /// <exception cref="NotSupportedException">The tag's sort sequence is unknown (e.g. GERMAN): no needle can
    /// match its stored keys, so this refuses instead of silently seeking MACHINE bytes.</exception>
    internal byte[] EncodeCharacterKey(string s)
    {
        if (_collationSupport == CollationSupport.Unsupported)
            throw new NotSupportedException(
                $"Index tag '{Name}' uses collating sequence '{Collation}', which is not supported for SEEK (MACHINE, GENERAL and SPANISH are).");
        return IndexKey.EncodeCharacterKey(s.AsSpan(), _collation, CharacterEncoding, KeyLength);
    }

    /// <summary>
    /// Decodes raw key bytes into an <see cref="IndexKey"/> using this tag's
    /// <see cref="KeyType"/> (plan §C5). Never throws.
    /// </summary>
    public IndexKey DecodeKey(ReadOnlySpan<byte> keyBytes)
        => KeyType == IndexKeyType.Character && IndexKey.IsMachineCollation(_collation)
            ? IndexKey.DecodeCharacter(keyBytes, CharacterEncoding)
            : IndexKey.Decode(keyBytes, KeyType);

    /// <summary>
    /// Seeks a record whose key matches <paramref name="keyBytes"/> by
    /// descending the B-tree with UNSIGNED byte comparison (prefix seek allowed);
    /// returns the first matching record in stored ascending byte order, or
    /// <see cref="NotFound"/> (<see langword="null"/>) when no key shares the prefix.
    /// This is not order-aware for <see cref="Descending"/> positioning. Never throws.
    /// </summary>
    public uint? Seek(ReadOnlySpan<byte> keyBytes) => Seek(keyBytes, exact: false);

    /// <summary>
    /// Raw-byte seek. With <paramref name="exact"/> <c>false</c> (the default) this is a PREFIX seek: the
    /// first key that carries <paramref name="keyBytes"/> as a prefix wins (VFP SET EXACT OFF). With
    /// <paramref name="exact"/> <c>true</c> it is an EXACT seek matching VFP SET EXACT ON: the key must
    /// equal the needle padded with trailing BLANKS to the key length — so a short value still matches a
    /// single-field key (the pad-blanks fill the field width) but a PREFIX of a COMPOSITE key does NOT
    /// (the following key bytes are non-blank), landing the caller at EOF.
    /// </summary>
    public uint? Seek(ReadOnlySpan<byte> keyBytes, bool exact)
    {
        // An empty prefix would match everything → treat as "not found".
        if (keyBytes.Length == 0)
            return NotFound;

        // O(log n) SEEK: descend the B-tree to the leaf that holds the first key >= the needle
        // (EnumerateFrom → DescendToLeaf), then walk the ascending leaf chain FORWARD from there
        // comparing with UNSIGNED byte order; the first key that carries the seek bytes as a prefix
        // is the answer (prefix seek allowed). This yields byte-for-byte the SAME match the earlier
        // left-most-leaf full scan produced: every key BEFORE the descent leaf sorts strictly below
        // the needle (DescendToLeaf lands on the first subtree whose MAX key >= the needle, so all
        // earlier keys are < needle and could never match), and the forward walk decodes+compares
        // the same entries the old scan would have — it merely SKIPS the O(n) leading run instead of
        // decoding every entry from key 1. DescendToLeaf errs left (never past a match), so the walk
        // is complete. SEEK matches ascending key BYTES regardless of the tag's logical order, so a
        // DESCENDING tag returns some ascending-first matching recno and is NOT order-aware for
        // controlling-order positioning. Strictly defensive:
        // EnumerateFrom terminates (never throws) on a malformed node or a corrupt sibling cycle.
        var needle = keyBytes.ToArray();
        foreach (var entry in EnumerateFrom(needle))
        {
            int cmp = ComparePrefix(needle, entry.Key, exact);
            if (cmp == 0)
                return entry.RecordNumber;   // (prefix) match → first matching record
            if (cmp < 0)
                break;                        // passed where the key would sort: absent
        }

        return NotFound;
    }

    /// <summary>
    /// Unsigned byte comparison of <paramref name="needle"/> against the start of
    /// <paramref name="key"/>: returns 0 when they match, negative when the needle sorts
    /// before the key, positive after. When <paramref name="exact"/> is false a plain
    /// prefix is a match; when true, any key bytes beyond the needle must all be BLANKS
    /// (VFP EXACT-ON blank-padded equality), otherwise the needle sorts by that blank.
    /// </summary>
    private static int ComparePrefix(byte[] needle, byte[] key, bool exact = false)
    {
        int n = Math.Min(needle.Length, key.Length);
        for (int i = 0; i < n; i++)
        {
            int diff = needle[i] - key[i];
            if (diff != 0)
                return diff;
        }
        if (needle.Length > key.Length)
            return 1;             // key is a prefix of needle → needle sorts after.
        if (!exact)
            return 0;             // prefix seek: needle ⊑ key is a match.
        // Exact seek: the needle is blank-padded to the key length, so the trailing key
        // bytes must all be spaces to be equal; otherwise compare that blank vs the byte.
        for (int i = needle.Length; i < key.Length; i++)
            if (key[i] != (byte)' ')
                return (byte)' ' - key[i];
        return 0;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> to key bytes via this tag's
    /// <see cref="KeyType"/> transform, then seeks it (see <see cref="Seek(ReadOnlySpan{byte})"/>).
    /// Returns <see cref="NotFound"/> for an absent value, an unencodable type or a character key under an
    /// unsupported collating sequence. Never throws.
    /// </summary>
    public uint? Seek(object value)
    {
        // A character tag whose expression could not be typed (e.g. a UDF) still holds character keys.
        if ((KeyType == IndexKeyType.Character || (KeyType == IndexKeyType.Unknown && IsCharacterKey)) && value is string s)
            return _collationSupport == CollationSupport.Unsupported ? NotFound : Seek(EncodeCharacterKey(s).AsSpan());

        var keyBytes = IndexKey.Encode(value, KeyType);
        return keyBytes is null ? NotFound : Seek(keyBytes.AsSpan());
    }

    /// <summary>
    /// Bridges the index to the table (plan §C5): yields the <see cref="DbfRecord"/>s
    /// in index order by mapping each entry's record number through
    /// <see cref="DbfTable.GetRecord(int)"/> (1-based recno → 0-based index). Deleted /
    /// out-of-range records are skipped. Never throws.
    /// </summary>
    public IEnumerable<DbfRecord> EnumerateRecords(DbfTable table)
    {
        if (table is null)
            return Array.Empty<DbfRecord>();
        return EnumerateRecordsCore(table);
    }

    private IEnumerable<DbfRecord> EnumerateRecordsCore(DbfTable table)
    {
        foreach (var entry in EnumerateEntries())
        {
            // Index recnos are 1-based; GetRecord is a 0-based physical index and
            // returns null for a deleted record. Skip deleted / out-of-range; never throw.
            long index = (long)entry.RecordNumber - 1;
            if (index < 0 || index >= table.RecordCount)
                continue;

            DbfRecord? record;
            try
            {
                record = table.GetRecord((int)index);
            }
            catch
            {
                continue;
            }

            if (record is not null)
                yield return record.Value;
        }
    }

    /// <summary>
    /// Enumerates this tag's entries in index order: left-most descent then a walk
    /// of the leaf right-sibling chain. When <see cref="Descending"/> the ascending
    /// stream is materialised and reversed (the bytes are stored ascending; VFP
    /// realises DESC by reverse traversal).
    /// </summary>
    public IEnumerable<IndexEntry> EnumerateEntries()
    {
        var ascending = IndexTraversal
            .EnumerateCompact(_index, RootPageOffset, KeyLength, IsCharacterKey)
            .Select(t => new IndexEntry(t.Recno, t.Key));

        if (!Descending)
            return ascending;

        // DESC == ASC reversed, recno-for-recno (verified against idxtest tnamed).
        return ReverseMaterialised(ascending);
    }

    private static IEnumerable<IndexEntry> ReverseMaterialised(IEnumerable<IndexEntry> source)
    {
        var list = source.ToList();
        for (int i = list.Count - 1; i >= 0; i--)
            yield return list[i];
    }

    /// <summary>
    /// Rushmore SEEK enumeration (perf §D8): yields this tag's entries in ASCENDING index order
    /// starting from the leaf that contains the first key &gt;= <paramref name="lowerBoundKey"/>,
    /// then walking the leaf right-sibling chain forward. A <see langword="null"/> bound starts at
    /// the left-most leaf (equivalent to <see cref="EnumerateEntries"/>). The descent is O(log n):
    /// it navigates branch nodes by their separator keys (each = the MAXIMUM key of its child
    /// subtree) without reading every leaf, so a caller that stops once it passes an upper bound
    /// examines only O(log n + matches) entries.
    /// <para>
    /// ASCENDING tags only: a DESCENDING tag stores ascending bytes but is logically reversed, so
    /// this forward walk would not match the seek caller's order — the caller MUST guard
    /// <see cref="Descending"/> and fall back to a full scan. Never throws; a malformed node, an
    /// out-of-range pointer, or a corrupt sibling cycle terminates the walk.
    /// </para>
    /// </summary>
    internal IEnumerable<IndexEntry> EnumerateFrom(byte[]? lowerBoundKey)
    {
        long leaf = DescendToLeaf(lowerBoundKey);
        if (leaf < 0)
            yield break;

        var visited = new HashSet<long>();
        long cur = leaf;
        while (true)
        {
            if (!visited.Add(cur))
                yield break; // corrupt sibling cycle

            foreach (var e in _index.ReadLeafEntries(cur, KeyLength, IsCharacterKey))
                yield return new IndexEntry(e.RecordNumber, e.Key);

            var header = _index.ReadNodeHeader(cur);
            if (header is null)
                break;
            var right = header.Value.RightSibling;
            if (right is null)
                break;
            cur = right.Value;
        }
    }

    /// <summary>
    /// Descends the B-tree to the byte offset of the leaf that holds the first key
    /// &gt;= <paramref name="lowerBoundKey"/> (or the left-most leaf when the bound is
    /// <see langword="null"/>). At each branch node the separator key is the MAXIMUM key of its
    /// child subtree, so the first child whose key &gt;= the bound is the one that contains (or
    /// first follows) the bound; descending one child too early is harmless (the forward walk and
    /// the caller's predicate filter the surplus), so this never lands PAST a true match. Returns
    /// <c>-1</c> on a malformed / unreadable tree. Never throws.
    /// </summary>
    private long DescendToLeaf(byte[]? lowerBoundKey)
    {
        long cur = RootPageOffset;
        var seen = new HashSet<long>();
        while (true)
        {
            if (!seen.Add(cur))
                return -1; // corrupt interior loop

            var header = _index.ReadNodeHeader(cur);
            if (header is null)
                return -1;
            if (header.Value.IsLeaf)
                return cur;

            var branch = _index.ReadBranchEntries(cur, KeyLength);
            if (branch.Count == 0)
                return -1;

            if (lowerBoundKey is null)
            {
                cur = branch[0].ChildPointer; // -inf → left-most child
                continue;
            }

            // First child whose MAX key (separator) is >= the bound contains the first match.
            int j = 0;
            while (j < branch.Count && CompareUnsigned(branch[j].Key, lowerBoundKey) < 0)
                j++;
            if (j >= branch.Count)
                j = branch.Count - 1; // every key < bound: take the right-most child
            cur = branch[j].ChildPointer;
        }
    }

    /// <summary>Unsigned, shorter-sorts-first byte comparison (the CDX key sort order).</summary>
    private static int CompareUnsigned(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }
}
