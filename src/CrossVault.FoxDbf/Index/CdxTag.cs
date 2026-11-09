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
    /// True when the key is a CHARACTER key; false for numeric/date/datetime/integer keys. Derived from the
    /// KEY expression + optional DBF schema. A character key pads with 0x20 under MACHINE and with 0x00 under a
    /// weighted sequence (see <see cref="KeyPad"/>); every other key pads with 0x00.
    /// </summary>
    public bool IsCharacterKey { get; }

    /// <summary>
    /// The byte the stored keys of this tag are padded with up to <see cref="KeyLength"/>: 0x20 for a MACHINE
    /// character key, 0x00 for a GENERAL / SPANISH (any weighted) character key and for every non-character key.
    /// Taken from the sort sequence NAME in the tag header, so a tag under a sequence we cannot weight (GERMAN) still
    /// reads with VFP's pad.
    /// </summary>
    internal byte KeyPad { get; }

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
    private readonly string? _unsupportedKeyFunction;

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
        KeyPad = isCharacterKey ? IndexKey.CharacterKeyPad(collation) : (byte)0x00;
        KeyType = keyType;
        CharacterEncoding = characterEncoding ?? IndexKey.ResolveCharacterEncoding(null, null);
        _collationSupport = VfpCollations.Resolve(collation, tableCodePage, out _collation);
        _unsupportedKeyFunction = VfpRuntime.UnsupportedIndexFunction(keyExpression);
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
        if (_collationSupport != CollationSupport.Exact)
            throw new NotSupportedException(
                $"Index tag '{Name}' uses collating sequence '{Collation}' with code page {CharacterEncoding.CodePage}, which cannot be encoded exactly for SEEK.");
        return IndexKey.EncodeCharacterKey(s.AsSpan(), _collation, CharacterEncoding, KeyLength);
    }

    /// <summary>
    /// The width in characters of a weighted (GENERAL / SPANISH) key: VFP9 stores two weight bytes per character,
    /// so it is half the key length. VFP9 rejects expressions over 120 characters with error 112 rather than
    /// capping their keys; the CDX builder enforces that same limit.
    /// </summary>
    private int WeightedKeyWidth => KeyLength / 2;

    /// <summary>
    /// The needle VFP9's SEEK (SEEK(), KEYMATCH(), INDEXSEEK(), the SET RELATION reposition) uses for the character
    /// value <paramref name="s"/> in this tag, with <paramref name="exact"/> = SET EXACT (VFP9-verified, see
    /// <see cref="CharacterSeekNeedle"/>):
    /// <list type="bullet">
    ///   <item>MACHINE: the raw bytes, never trimmed or cut; EXACT ON matches the key equal to them plus blanks,
    ///   EXACT OFF every key they are a prefix of.</item>
    ///   <item>weighted, EXACT ON, or EXACT OFF with <paramref name="s"/> exactly as wide as the key: the full stored
    ///   key of the trimmed value, matched with the rest of the key 0x00 (SEEK('Muller    ') does not find
    ///   'Müller').</item>
    ///   <item>weighted, EXACT OFF otherwise: the head weights only, a prefix (the tails are ignored: SEEK('áa') finds
    ///   'Aa', SEEK('Müller') finds 'Muller' when that sorts first); trailing needle blanks compare with the heads of
    ///   the blank-padded value (SEEK('Ab ') finds 'Ab' or 'Ab  c', SEEK('Stra  ') not 'Strab').</item>
    /// </list>
    /// </summary>
    /// <exception cref="NotSupportedException">The tag's sort sequence is unknown (see <see cref="EncodeCharacterKey"/>).</exception>
    internal CharacterSeekNeedle CreateCharacterSeekNeedle(string s, bool exact)
    {
        RequireSupportedKeyFunctions();
        if (_collationSupport != CollationSupport.Exact || _collation is not GeneralCollation general)
            return new CharacterSeekNeedle(EncodeCharacterKey(s), exact, KeyPad);
        if (exact || s.Length == WeightedKeyWidth)
            return new CharacterSeekNeedle(EncodeCharacterKey(s), exact: true, KeyPad, blankHead: general.BlankHead);
        int trimmed = s.Length;
        while (trimmed > 0 && s[trimmed - 1] == ' ') trimmed--;
        var heads = general.GetHeadKey(s.AsSpan(0, trimmed));
        if (heads.Length >= KeyLength)
            return new CharacterSeekNeedle(heads.AsSpan(0, KeyLength).ToArray(), exact: false, KeyPad);
        int headLength = heads.Length;
        while (headLength > 0 && heads[headLength - 1] == general.BlankHead)
            headLength--;
        int blanks = s.Length - trimmed + heads.Length - headLength;
        // VFP compares terminal blank WEIGHTS, including TAB/NBSP primary heads, with padding.
        return new CharacterSeekNeedle(heads.AsSpan(0, headLength).ToArray(),
            exact: false, KeyPad, blanks, general.BlankHead);
    }

    /// <summary>
    /// The bound of a single-value SET KEY TO <paramref name="s"/> under SET EXACT <paramref name="exact"/>
    /// (VFP9-verified). With <paramref name="equality"/> the stored key must equal the returned bytes, which are
    /// padded with <see cref="KeyPad"/> to <see cref="KeyLength"/> (EXACT ON, and under a weighted sequence also a
    /// value exactly as wide as the key); otherwise the returned bytes are a byte prefix of the stored key. Unlike
    /// SEEK the bound is cut to the key length (MACHINE SET KEY TO 'Ab'+SPACE(20) scopes to 'Ab') and a weighted
    /// prefix keeps its trailing blanks as blank heads with no pad equivalence (SET KEY TO 'Ab ' leaves out 'Ab').
    /// </summary>
    /// <exception cref="NotSupportedException">The tag's sort sequence is unknown (see <see cref="EncodeCharacterKey"/>).</exception>
    internal byte[] EncodeCharacterScopeKey(string s, bool exact, out bool equality)
    {
        byte[] natural;
        if (_collation is GeneralCollation general && _collationSupport == CollationSupport.Exact
            && !exact && s.Length != WeightedKeyWidth)
        {
            int trimmed = s.Length;
            while (trimmed > 0 && s[trimmed - 1] == ' ') trimmed--;
            var heads = general.GetHeadKey(s.AsSpan(0, trimmed));
            natural = new byte[heads.Length + s.Length - trimmed];
            Array.Fill(natural, general.BlankHead);
            heads.CopyTo(natural, 0);
            equality = false;
        }
        else
        {
            natural = EncodeCharacterKey(s);
            equality = exact || _collation is GeneralCollation;
        }
        if (!equality)
            return natural.Length <= KeyLength ? natural : natural.AsSpan(0, KeyLength).ToArray();
        var key = new byte[KeyLength];
        Array.Fill(key, KeyPad);
        natural.AsSpan(0, Math.Min(natural.Length, KeyLength)).CopyTo(key);
        return key;
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
    /// equal the needle padded with the tag's <see cref="KeyPad"/> to the key length (blanks under MACHINE, 0x00
    /// under a weighted sequence) — so a short value still matches a single-field key (the pad fills the field
    /// width) but a PREFIX of a COMPOSITE key does NOT (the following key bytes are not pad), landing the caller
    /// at EOF.
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
            int cmp = ComparePrefix(needle, entry.Key, exact, KeyPad);
            if (cmp == 0)
                return entry.RecordNumber;   // (prefix) match → first matching record
            if (cmp < 0)
                break;                        // passed where the key would sort: absent
        }

        return NotFound;
    }

    /// <summary>
    /// Seeks the first key in stored ascending byte order that matches <paramref name="needle"/> (see
    /// <see cref="CreateCharacterSeekNeedle"/>); an empty needle matches the first key, as VFP9's SEEK('') does.
    /// Like <see cref="Seek(ReadOnlySpan{byte}, bool)"/> it is not order-aware for a <see cref="Descending"/> tag.
    /// Never throws.
    /// </summary>
    internal uint? Seek(CharacterSeekNeedle needle)
    {
        // VFP descends with the literal blank heads, then scans the leaf with blank/pad
        // equivalence. Control heads between a short key and its blank-suffixed equivalent
        // make that distinction observable (including on native bulk-compressed keys).
        byte[] lowerBound = needle.TrailingBlanks > 0 ? needle.SortKey(KeyLength) : needle.Bytes;
        foreach (var entry in EnumerateFrom(lowerBound))
        {
            int cmp = needle.CompareForSeek(entry.Key);
            if (cmp < 0)
                break;
            if (cmp == 0 && needle.TrailingBlanksMatch(entry.Key))
                return entry.RecordNumber;
        }
        return NotFound;
    }

    /// <summary>
    /// Unsigned byte comparison of <paramref name="needle"/> against the start of
    /// <paramref name="key"/>: returns 0 when they match, negative when the needle sorts
    /// before the key, positive after. When <paramref name="exact"/> is false a plain
    /// prefix is a match; when true, any key bytes beyond the needle must all be the tag's
    /// <paramref name="pad"/> (VFP EXACT-ON padded equality), otherwise the needle sorts by that pad byte.
    /// </summary>
    private static int ComparePrefix(byte[] needle, byte[] key, bool exact, byte pad)
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
        // Exact seek: the needle is padded to the key length, so the trailing key
        // bytes must all be pad to be equal; otherwise compare that pad vs the byte.
        for (int i = needle.Length; i < key.Length; i++)
            if (key[i] != pad)
                return pad - key[i];
        return 0;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> to key bytes via this tag's
    /// <see cref="KeyType"/> transform, then seeks it (see <see cref="Seek(ReadOnlySpan{byte})"/>).
    /// A character value seeks like VFP9's SEEK under SET EXACT OFF (see <see cref="CreateCharacterSeekNeedle"/>):
    /// under a weighted sequence (GENERAL / SPANISH) a prefix seek on the value's head weights only, so
    /// <c>Seek("áa")</c> lands on the first key whose heads start with those of <c>áa</c>, which may be <c>Aa</c>; a
    /// value exactly as wide as the key seeks its full key. An empty value is not found.
    /// Returns <see cref="NotFound"/> for an absent value, an unencodable type or a character key under an
    /// unsupported collating sequence. An approximate collation throws rather than searching with wrong weights.
    /// </summary>
    public uint? Seek(object value)
    {
        RequireSupportedKeyFunctions();
        // A character tag whose expression could not be typed (e.g. a UDF) still holds character keys.
        if ((KeyType == IndexKeyType.Character || (KeyType == IndexKeyType.Unknown && IsCharacterKey)) && value is string s)
        {
            if (_collationSupport == CollationSupport.Unsupported)
                return NotFound;
            var needle = CreateCharacterSeekNeedle(s, exact: false);
            return needle.Bytes.Length == 0 && needle.TrailingBlanks == 0 ? NotFound : Seek(needle);
        }

        var keyBytes = IndexKey.Encode(value, KeyType);
        return keyBytes is null ? NotFound : Seek(keyBytes.AsSpan());
    }

    private void RequireSupportedKeyFunctions()
    {
        if (_unsupportedKeyFunction is { } function
            && VfpFunctionNames.IsBuiltin(function))
            throw new NotSupportedException($"Index tag '{Name}': function {function}() is not supported by the index engine for SEEK.");
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
            .EnumerateCompact(_index, RootPageOffset, KeyLength, KeyPad)
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

            foreach (var e in _index.ReadLeafEntries(cur, KeyLength, KeyPad))
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
