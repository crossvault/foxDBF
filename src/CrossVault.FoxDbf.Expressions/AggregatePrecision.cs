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

// VFP9's command-level metadata walk precedes the ordinary per-row evaluations.
// Round4 AggregateMatrix pins the recursive visits, including IIF depths 1/2/3/5:
// ordinary operands and conditions once, conditional value arguments twice, then Eval.
// Sampling starts at the first qualifying record (or final pointer if none qualifies).
internal sealed partial class UnaryNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var operand = _operand.ProbeAggregate<TVfpError>(schema, row, context);
        return (Eval(row, context), operand.Decimals);
    }
}

internal sealed partial class BinaryNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var left = _left.ProbeAggregate<TVfpError>(schema, row, context);
        var right = _right.ProbeAggregate<TVfpError>(schema, row, context);
        int? scale = left.Decimals is int l && right.Decimals is int r ? _op switch
        {
            BinOp.Mul => Math.Min(18, l + r),
            BinOp.Div => Math.Max(context.NumericDecimals + (IsSqlConstant ? 0 : 2), Math.Min(18, l + r)),
            _ => Math.Max(l, r)
        } : null;
        return (Eval(row, context), scale);
    }
}

internal sealed partial class FunctionNode
{
    internal override (VfpValue Value, int? Decimals) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var samples = new (VfpValue Value, int? Decimals)[_args.Length];
        bool branch = _upper is "IIF" or "ICASE" or "NVL" or "EVL";
        for (int i = 0; i < _args.Length; i++)
        {
            // Metadata visits even unchosen branches, suppressing only VFP errors.
            // Conditions have one recursive visit; value arguments have two. The final
            // Eval below still uses the function's normal lazy/eager error semantics.
            if (branch)
            {
                try
                {
                    bool condition = _upper == "IIF" && i == 0
                        || _upper == "ICASE" && i % 2 == 0 && i + 1 < _args.Length;
                    if (!condition) _args[i].ProbeAggregate<TVfpError>(schema, row, context);
                    samples[i] = _args[i].ProbeAggregate<TVfpError>(schema, row, context);
                }
                catch (TVfpError) { }
                catch (ExpressionException) { }
            }
            else samples[i] = _args[i].ProbeAggregate<TVfpError>(schema, row, context);
        }
        var value = Eval(row, context);
        int? scale = _upper switch
        {
            "RAND" => context.NumericDecimals,
            "ABS" when samples.Length == 1 => samples[0].Decimals,
            "SQRT" when samples.Length == 1 => Math.Max(context.NumericDecimals, samples[0].Decimals ?? 0),
            // Round5: metadata uses the same argument conversion and 18-place cap as ROUND.
            "ROUND" when samples.Length == 2 => Math.Clamp(VfpRuntime.RoundDigits(samples[1].Value), 0, 18),
            _ => VfpRuntime.IsNumeric(value) ? LiteralNode.Precision(value) : null
        };
        return (value, scale);
    }
}
