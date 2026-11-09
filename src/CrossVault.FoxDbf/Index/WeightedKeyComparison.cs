namespace CrossVault.FoxDbf.Index;

// VFP's insertion comparison is directional: terminal 0x11 primary weights in
// the incoming key can match the end of the stored primary run. Secondary
// weights still compare at their own offsets. The reverse comparison remains
// bytewise; this is not a total-order comparer for sorting a bulk input run.
internal static class WeightedKeyComparison
{
    internal static int CompareIncoming(ReadOnlySpan<byte> incoming, ReadOnlySpan<byte> stored)
        => CompareIncoming(incoming, stored, out _);

    internal static int CompareIncoming(ReadOnlySpan<byte> incoming, ReadOnlySpan<byte> stored, out int matched)
    {
        matched = 0;
        int common = Math.Min(incoming.Length, stored.Length);
        for (int i = 0; i < common; i++)
        {
            int difference = incoming[i] - stored[i];
            if (difference == 0) { matched = i + 1; continue; }
            if (incoming[i] == 0x11 && stored[i] < 0x10)
            {
                int end = i;
                while (end < incoming.Length && incoming[end] == 0x11) end++;
                if (end == incoming.Length || incoming[end] < 0x10)
                {
                    int remaining = Math.Max(incoming.Length - end, stored.Length - i);
                    for (int j = 0; j < remaining; j++)
                    {
                        int left = end + j < incoming.Length ? incoming[end + j] : 0;
                        int right = i + j < stored.Length ? stored[i + j] : 0;
                        matched = i + j;
                        if (left != right) return left - right;
                    }
                    return 0;
                }
            }
            return difference;
        }
        for (int i = common; i < incoming.Length; i++)
            if (incoming[i] != 0) return incoming[i];
        for (int i = common; i < stored.Length; i++)
            if (stored[i] != 0) return -stored[i];
        return 0;
    }
}
