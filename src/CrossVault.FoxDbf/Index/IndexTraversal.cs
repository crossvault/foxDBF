namespace CrossVault.FoxDbf.Index;

/// <summary>
/// Internal B-tree traversal shared by the tag directory and every CDX tag
/// (plan §C3/§C4). Descends from a root node down the LEFT-most child to the
/// left-most leaf, then walks the leaf RIGHT-sibling chain decoding each compact
/// leaf in order. Branch descent uses the BIG-endian child pointers; leaf decode
/// is delegated to <see cref="CompactLeaf"/>. Strictly defensive: a malformed
/// node, an out-of-range pointer, or a corrupt sibling cycle terminates the walk
/// instead of throwing or looping forever.
/// </summary>
internal static class IndexTraversal
{
    /// <summary>
    /// Enumerates a compact index (the tag directory or one tag) in ascending
    /// index order as <c>(recno, key)</c> pairs. <paramref name="pad"/> fills the trailing bytes of each
    /// key (<see cref="IndexKey.CharacterKeyPad"/> for a character tag, 0x20 for the tag directory, else 0x00).
    /// </summary>
    public static IEnumerable<(uint Recno, byte[] Key)> EnumerateCompact(
        IndexFile index, uint root, int keyLength, byte pad)
    {
        if (keyLength <= 0)
            yield break;

        // --- 1) Descend to the left-most leaf via the first branch child. ---
        long cur = root;
        var descended = new HashSet<long>();
        while (true)
        {
            if (!descended.Add(cur))
                yield break; // corrupt loop in the interior path

            var header = index.ReadNodeHeader(cur);
            if (header is null)
                yield break;
            if (header.Value.IsLeaf)
                break;

            var branch = index.ReadBranchEntries(cur, keyLength);
            if (branch.Count == 0)
                yield break; // branch with no children: nothing reachable

            cur = branch[0].ChildPointer; // left-most child (BIG-endian pointer)
        }

        // --- 2) Walk the leaf right-sibling chain, decoding each compact leaf. ---
        var visited = new HashSet<long>();
        while (true)
        {
            if (!visited.Add(cur))
                yield break; // corrupt sibling cycle

            foreach (var e in index.ReadLeafEntries(cur, keyLength, pad))
                yield return (e.RecordNumber, e.Key);

            var header = index.ReadNodeHeader(cur);
            if (header is null)
                break;
            var right = header.Value.RightSibling;
            if (right is null)
                break;
            cur = right.Value;
        }
    }
}
