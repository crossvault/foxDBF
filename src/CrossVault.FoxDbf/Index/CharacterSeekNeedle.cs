namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The character needle of a VFP9 SEEK (and SEEK(), KEYMATCH(), INDEXSEEK(), the SET RELATION reposition) together
/// with the rule a stored key must meet to match it. Built by <see cref="CdxTag.CreateCharacterSeekNeedle"/> (or, for a
/// MACHINE IDX, from the raw bytes):
/// <list type="bullet">
///   <item><see cref="Exact"/>: the key equals <see cref="Bytes"/> followed by <see cref="Pad"/> up to the key length
///   (SET EXACT ON, and under a weighted sequence also a needle exactly as wide as the key);</item>
///   <item>otherwise a prefix match on <see cref="Bytes"/>, followed by <see cref="TrailingBlanks"/> needle blanks
///   that each match a key byte that is the <see cref="BlankHead"/> or lies past the value's heads (below 0x10: a
///   tail or the pad). That compares the needle with the heads of the value padded with blanks, as VFP9 does
///   under a weighted sequence: <c>'Ab '</c> finds <c>Ab</c> and <c>Ab  c</c>, not <c>Abc</c>.</item>
/// </list>
/// A needle longer than the key never matches.
/// </summary>
internal sealed class CharacterSeekNeedle
{
    internal CharacterSeekNeedle(byte[] bytes, bool exact, byte pad, int trailingBlanks = 0, byte blankHead = 0)
    {
        Bytes = bytes;
        Exact = exact;
        Pad = pad;
        TrailingBlanks = exact ? 0 : trailingBlanks;
        BlankHead = blankHead;
    }

    /// <summary>The bytes a matching key starts with.</summary>
    internal byte[] Bytes { get; }

    /// <summary>The rest of a matching key must be all <see cref="Pad"/>.</summary>
    internal bool Exact { get; }

    /// <summary>The byte the tag pads its stored keys with.</summary>
    internal byte Pad { get; }

    /// <summary>Needle blanks after <see cref="Bytes"/> (weighted prefix needles only).</summary>
    internal int TrailingBlanks { get; }

    /// <summary>The head weight of a blank in the tag's collation.</summary>
    internal byte BlankHead { get; }

    /// <summary>Where the needle sorts among the stored keys (for SET NEAR): <see cref="Bytes"/> followed by the
    /// trailing blank heads, and for an exact needle the pad.</summary>
    internal byte[] SortKey(int keyLength)
    {
        if (Exact)
        {
            var padded = new byte[Math.Max(keyLength, Bytes.Length)];
            Array.Fill(padded, Pad);
            Bytes.CopyTo(padded, 0);
            return padded;
        }
        if (TrailingBlanks == 0)
            return Bytes;
        var key = new byte[Math.Min(keyLength, Bytes.Length + TrailingBlanks)];
        Array.Fill(key, BlankHead);
        Bytes.AsSpan(0, Math.Min(Bytes.Length, key.Length)).CopyTo(key);
        return key;
    }

    /// <summary>
    /// Unsigned compare of <see cref="Bytes"/> with the start of <paramref name="key"/> (exact: with the whole key,
    /// <see cref="Bytes"/> padded with <see cref="Pad"/>): 0 when the key carries the needle bytes (exact: equals
    /// it), negative when the needle sorts before the key, positive after. Exact weighted needles also compare
    /// their terminal blank heads with padding. Prefix trailing blanks are handled by <see cref="Matches"/>.
    /// </summary>
    internal int CompareBytes(ReadOnlySpan<byte> key)
    {
        int n = Math.Min(Bytes.Length, key.Length);
        int blankSuffix = Bytes.Length;
        if (Exact && BlankHead != 0)
            while (blankSuffix > 0 && Bytes[blankSuffix - 1] == BlankHead)
                blankSuffix--;
        for (int i = 0; i < n; i++)
        {
            if (i >= blankSuffix && key[i] == Pad)
                continue;
            int diff = Bytes[i] - key[i];
            if (diff != 0)
                return diff;
        }
        if (Bytes.Length > key.Length)
            return 1;
        if (!Exact)
            return 0;
        for (int i = Bytes.Length; i < key.Length; i++)
            if (key[i] != Pad)
                return Pad - key[i];
        return 0;
    }

    internal int CompareForSeek(ReadOnlySpan<byte> key)
    {
        int compare = CompareBytes(key);
        if (compare != 0)
            return compare;
        int end = Math.Min(key.Length, Bytes.Length + TrailingBlanks);
        for (int i = Bytes.Length; i < end; i++)
        {
            if (key[i] < 0x10)
                continue;
            int difference = BlankHead - key[i];
            if (difference != 0)
                return difference;
        }
        return 0;
    }

    /// <summary>True when the stored <paramref name="key"/> matches this needle.</summary>
    internal bool Matches(ReadOnlySpan<byte> key)
        => CompareBytes(key) == 0 && TrailingBlanksMatch(key);

    /// <summary>The needle blanks after <see cref="Bytes"/> against <paramref name="key"/>, which already carries
    /// <see cref="Bytes"/>: each matches the blank head or a byte past the value's heads; beyond the key length the
    /// needle is cut.</summary>
    internal bool TrailingBlanksMatch(ReadOnlySpan<byte> key)
    {
        int end = Math.Min(key.Length, Bytes.Length + TrailingBlanks);
        for (int i = Bytes.Length; i < end; i++)
            if (key[i] != BlankHead && key[i] >= 0x10)
                return false;
        return true;
    }
}
