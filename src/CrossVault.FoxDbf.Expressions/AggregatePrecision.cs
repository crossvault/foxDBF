namespace CrossVault.FoxDbf.Expressions;

internal sealed partial class LiteralNode
{
    internal static int Precision(VfpValue value)
    {
        if (value.DisplayDecimals >= 0) return value.DisplayDecimals;
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value.AsNumber, bits);
        return (bits[3] >> 16) & 0xff;
    }
}

// VFP9 discovers aggregate type/precision by sampling the expression tree at the first
// qualifying record (or the final pointer). Each parent samples its children before itself;
// this is observable with RAND/UDFs. It runs once per command, never per scanned row.
internal sealed partial class UnaryNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var operand = _operand.ProbeAggregate(schema, row, context);
        return (Eval(row, context), operand.Decimals);
    }
}

internal sealed partial class BinaryNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var left = _left.ProbeAggregate(schema, row, context);
        var right = _right.ProbeAggregate(schema, row, context);
        int? scale = left.Decimals is int l && right.Decimals is int r ? _op switch
        {
            BinOp.Mul => Math.Min(18, l + r),
            BinOp.Div => Math.Max(context.NumericDecimals, Math.Max(l, r)),
            _ => Math.Max(l, r)
        } : null;
        return (Eval(row, context), scale);
    }
}

internal sealed partial class FunctionNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var samples = new (VfpValue Value, int? Decimals)[_args.Length];
        bool branch = _upper is "IIF" or "NVL" or "EVL";
        for (int i = 0; i < _args.Length; i++)
        {
            // VFP's branch metadata probing may visit an unchosen value, but does not
            // surface its error. The oracle observes two samples of branch arguments.
            if (branch)
            {
                try
                {
                    _args[i].ProbeAggregate(schema, row, context);
                    samples[i] = _args[i].ProbeAggregate(schema, row, context);
                }
                catch { }
            }
            else samples[i] = _args[i].ProbeAggregate(schema, row, context);
        }
        var value = Eval(row, context);
        int? scale = _upper switch
        {
            "RAND" => context.NumericDecimals,
            "ABS" when samples.Length == 1 => samples[0].Decimals,
            "SQRT" when samples.Length == 1 => Math.Max(context.NumericDecimals, samples[0].Decimals ?? 0),
            "ROUND" when samples.Length == 2 => Math.Max(0, samples[1].Value.AsInteger),
            _ => VfpRuntime.IsNumeric(value) ? LiteralNode.Precision(value) : null
        };
        return (value, scale);
    }
}
