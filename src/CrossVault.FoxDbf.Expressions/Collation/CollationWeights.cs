namespace CrossVault.FoxDbf.Expressions;

/// <summary>Precomputed primary and secondary weights. Empty tails suppress secondary weights;
/// explicit zero tails retain their position before a later accent (VFP9 GENERAL and GERMAN).</summary>
internal sealed record CollationWeights(byte[] Heads, byte[] Tails);

/// <summary>A byte sequence replaced by weights. Longest matching input wins. ASCII case folding is
/// explicit per rule; case-sensitive or non-ASCII variants can be supplied as separate data rows.</summary>
internal sealed record CollationContraction(byte[] Input, CollationWeights Weights, bool FoldAsciiCase = true);
