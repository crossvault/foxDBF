namespace CrossVault.FoxDbf.Expressions;

internal sealed partial class LiteralNode
{
    internal override int? AggregateDecimals(ISchema schema, Func<string, VfpValue> memory, int decimals)
        => VfpRuntime.IsNumeric(_value) ? Precision(_value) : null;

    internal static int Precision(VfpValue value)
    {
        if (value.DisplayDecimals >= 0) return value.DisplayDecimals;
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value.AsNumber, bits);
        return (bits[3] >> 16) & 0xff;
    }
}

internal sealed partial class FieldNode
{
    internal override int? AggregateDecimals(ISchema schema, Func<string, VfpValue> memory, int decimals)
    {
        if (schema.TryGetColumn(_name, out _, out _, out int scale)) return scale;
        var value = memory(_name);
        return VfpRuntime.IsNumeric(value) ? LiteralNode.Precision(value) : null;
    }
}

internal sealed partial class UnaryNode
{
    internal override int? AggregateDecimals(ISchema schema, Func<string, VfpValue> memory, int decimals)
        => _operand.AggregateDecimals(schema, memory, decimals);
}

internal sealed partial class BinaryNode
{
    internal override int? AggregateDecimals(ISchema schema, Func<string, VfpValue> memory, int decimals)
    {
        int? left = _left.AggregateDecimals(schema, memory, decimals);
        int? right = _right.AggregateDecimals(schema, memory, decimals);
        if (left is null || right is null) return null;
        // VFP9 SUM/AVERAGE multiply operand scales, while division retains at least SET DECIMALS.
        return _op switch
        {
            BinOp.Mul => Math.Min(18, left.Value + right.Value),
            BinOp.Div => Math.Max(decimals, Math.Max(left.Value, right.Value)),
            _ => Math.Max(left.Value, right.Value)
        };
    }
}
