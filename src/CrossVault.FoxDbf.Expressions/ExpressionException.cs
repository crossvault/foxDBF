using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Thrown for malformed expression text or invalid function arguments, as in VFP9.
/// </summary>
public sealed class ExpressionException : Exception
{
    /// <summary>0-based character position in the source text, when known.</summary>
    public int Position { get; }

    /// <summary>The VFP error number for a runtime argument error, when known.</summary>
    public int? VfpErrorNumber { get; init; }

    public ExpressionException(string message, int position = -1) : base(message)
        => Position = position;

    public ExpressionException(string message, Exception inner, int position = -1)
        : base(message, inner) => Position = position;
}
