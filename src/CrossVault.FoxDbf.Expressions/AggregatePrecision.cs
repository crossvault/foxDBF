using System;

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
    internal override (VfpValue Value, int? Decimals, int Width) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var operand = _operand.ProbeAggregate<TVfpError>(schema, row, context);
        return (Eval(row, context), operand.Decimals, operand.Width == 0 ? 0 : operand.Width + 1);
    }
}

internal partial class BinaryNode
{
    internal override int AggregateOverflowWidth(int setting, int sampled, bool calculate, int expressionWidth = 0)
    {
        // Round6 aggregate matrix: SUM/AVERAGE retain quotient guard places; CALCULATE
        // constant quotients use the sampled scale, and powers reserve at least two places.
        if (HasAggregateQuotient)
            return calculate
                ? expressionWidth == 0 ? VfpRuntime.NumericOverflowWidth(setting)
                    : Math.Max(VfpRuntime.NumericOverflowWidth(setting), expressionWidth + 3)
                : Math.Max(VfpRuntime.NumericOverflowWidth(Math.Max(setting + 2, sampled)), expressionWidth);
        if (HasQuotient && expressionWidth > 0)
            return Math.Max(VfpRuntime.NumericOverflowWidth(Math.Max(setting, sampled)), expressionWidth + (calculate ? 3 : 0));
        if (calculate && _op == BinOp.Pow) return VfpRuntime.NumericOverflowWidth(Math.Max(2, Math.Max(setting, sampled)));
        return base.AggregateOverflowWidth(setting, sampled, calculate);
    }

    internal override (VfpValue Value, int? Decimals, int Width) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var left = _left.ProbeAggregate<TVfpError>(schema, row, context);
        var right = _right.ProbeAggregate<TVfpError>(schema, row, context);
        int? scale = left.Decimals is int l && right.Decimals is int r ? _op switch
        {
            BinOp.Mul => Math.Min(18, l + r),
            BinOp.Div => Math.Max(context.NumericDecimals + (IsSqlConstant ? 0 : 2), Math.Min(18, l + r)),
            _ => Math.Max(l, r)
        } : null;
        // The existing metadata evaluation also captures the expression descriptor.
        // No width tracking is needed in the subsequent per-record SUM loop.
        var sample = EvalSnapshot(row, context, null);
        var shape = !double.IsFinite(sample.Value.AsDouble) ? AggregateShape(row, context) : sample.Shape;
        return (sample.Value, scale, shape.Width > 0 ? shape.Width : sample.Shape.Width);
    }
}

internal sealed partial class FunctionNode
{
    // Round6 CALCULATE SUM/AVG(EXP(1000)) pins three additional overflow columns
    // at SET DECIMALS 0/2/4/10; ordinary SUM/AVERAGE retain the sampled scale.
    internal override int AggregateOverflowWidth(int setting, int sampled, bool calculate, int expressionWidth = 0)
    {
        if (expressionWidth > 0)
        {
            if (calculate) return Math.Max(VfpRuntime.NumericOverflowWidth(setting), expressionWidth + 3);
            int floor = HasAggregateQuotient ? setting + 2 : setting;
            return Math.Max(VfpRuntime.NumericOverflowWidth(Math.Max(floor, sampled)), expressionWidth);
        }
        return calculate && _upper == "EXP" ? VfpRuntime.NumericOverflowWidth(setting) + 3 : base.AggregateOverflowWidth(setting, sampled, calculate);
    }

    internal override (VfpValue Value, int? Decimals, int Width) ProbeAggregate<TVfpError>(ISchema schema, IRowContext row, EvaluationContext context)
    {
        var samples = new (VfpValue Value, int? Decimals, int Width)[_args.Length];
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
        int width = value.ImmediateOverflowWidth > 0 ? value.ImmediateOverflowWidth : value.OverflowDisplayWidth;
        if (VfpRuntime.IsNumeric(value) && !double.IsFinite(value.AsDouble))
        {
            var shape = AggregateShape(row, context);
            if (shape.Width > 0)
            {
                width = shape.Width;
                if (_upper == "SQRT") scale = shape.Scale;
            }
        }
        return (value, scale, width);
    }
}

// Command-only overflow descriptors. Unlike format propagation, these include the
// measured SQRT/MAX operand widths (Round4); no finite per-record evaluation uses them.
internal abstract partial class AstNode
{
    internal virtual OverflowShape AggregateShape(IRowContext row, EvaluationContext context)
        => OverflowShape(row, context, default);
}
internal partial class BinaryNode
{
    internal override OverflowShape AggregateShape(IRowContext row, EvaluationContext context)
        => CombineShape(_left.AggregateShape(row, context), _right.AggregateShape(row, context), context);
}
internal sealed partial class FunctionNode
{
    internal override OverflowShape AggregateShape(IRowContext row, EvaluationContext context)
    {
        if (_args.Length == 0) return default;
        int index = 0;
        // Dynamic selection already has a descriptor in the evaluation snapshot. Never
        // re-evaluate its condition (or guess that the first argument was selected).
        if (SelectsValue)
        {
            if (_upper == "IIF" && _args.Length == 3 && _args[0] is LiteralNode condition)
                index = VfpRuntime.AsCondition(condition.ConstantValue) ? 1 : 2;
            else if (_upper is "NVL" or "EVL" && _args.Length == 2 && _args[0] is LiteralNode literal)
                index = (_upper == "NVL" ? literal.ConstantValue.IsNull
                    : !literal.ConstantValue.IsNull && VfpRuntime.Empty(literal.ConstantValue, context).AsLogical) ? 1 : 0;
            else return default;
        }
        var shape = _args[index].AggregateShape(row, context);
        if (shape.Width == 0) return default;
        if (_upper == "SQRT")
        {
            int scale = Math.Max(context.NumericDecimals, shape.Scale);
            return new(shape.Width + scale - shape.Scale + (shape.Scale == 0 ? 1 : 0), scale);
        }
        if (_upper is "MAX" or "MIN") return shape;
        return FunctionShape(shape);
    }
}
