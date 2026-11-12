using System;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// UDF-bearing arithmetic can change an operand before the overflow is formatted.
// This parse-time specialization samples descriptors alongside values; ordinary
// field arithmetic and branch quotients retain their allocation-free hot path.
internal sealed class SnapshotOverflowNode(BinOp op, AstNode left, AstNode right) : OverflowBinaryNode(op, left, right)
{
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx) => EvalSnapshot(row, ctx, null).Value;
    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
        => EvalSnapshot(row, ctx, resolver).Value;
    internal override VfpValue EvalTracked(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver, Span<int> selections)
        => EvalSnapshot(row, ctx, resolver).Value;
    public override Expression Build(BuildContext b)
        => Expression.Field(BuildSnapshot(b), "Item1");
    internal override Expression BuildTracked(BuildContext b, ReadOnlySpan<ParameterExpression> selections) => Build(b);
}

internal abstract partial class AstNode
{
    internal virtual (VfpValue Value, OverflowShape Shape) EvalSnapshot(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver)
    {
        var value = resolver is null ? Eval(row, ctx) : Eval(row, ctx, resolver);
        return (value, OverflowShape(row, ctx, default, value));
    }
}

internal sealed partial class UnaryNode
{
    internal override (VfpValue Value, OverflowShape Shape) EvalSnapshot(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver)
    {
        var operand = _operand.EvalSnapshot(row, ctx, resolver);
        return (VfpRuntime.Unary(_op, operand.Value, ctx, HasQuotient),
            operand.Shape.Width == 0 ? default : operand.Shape with { Width = operand.Shape.Width + 1 });
    }
}

internal partial class BinaryNode
{
    internal override (VfpValue Value, OverflowShape Shape) EvalSnapshot(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver)
    {
        var left = _left.EvalSnapshot(row, ctx, resolver);
        if (_op is BinOp.And or BinOp.Or && VfpRuntime.Decides(_op, left.Value)) return left;
        var right = _right.EvalSnapshot(row, ctx, resolver);
        var value = VfpRuntime.Binary(_op, left.Value, right.Value, ctx, IsSqlConstant);
        if (HasQuotient && value.Type == VfpType.Numeric && !double.IsFinite(value.AsDouble))
            value = FormatOverflow(value, ctx, left.Shape, right.Shape);
        return (value, CombineShape(left.Shape, right.Shape, ctx));
    }
}

internal sealed partial class FunctionNode
{
    internal override (VfpValue Value, OverflowShape Shape) EvalSnapshot(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver)
    {
        // An opaque function contributes no measured descriptor. Its argument values
        // still evaluate normally, but their shapes cannot affect this quotient.
        if (!HasMeasuredShape)
            return (resolver is null ? Eval(row, ctx) : Eval(row, ctx, resolver), default);
        if (resolver is not null && resolver(_upper, _resolverArguments, out var resolved)) return (resolved, default);
        if (_upper == "IIF" && _args.Length == 3)
        {
            var condition = _args[0].EvalSnapshot(row, ctx, resolver);
            return _args[VfpRuntime.AsCondition(condition.Value) ? 1 : 2].EvalSnapshot(row, ctx, resolver);
        }
        if (_upper == "ICASE")
        {
            int i = 0;
            for (; i + 1 < _args.Length; i += 2)
                if (VfpRuntime.AsCondition(_args[i].EvalSnapshot(row, ctx, resolver).Value))
                    return _args[i + 1].EvalSnapshot(row, ctx, resolver);
            return i < _args.Length ? _args[i].EvalSnapshot(row, ctx, resolver) : (VfpValue.Null, default);
        }
        var values = new VfpValue[_args.Length];
        var shapes = new OverflowShape[_args.Length];
        for (int i = 0; i < values.Length; i++)
            (values[i], shapes[i]) = _args[i].EvalSnapshot(row, ctx, resolver);
        int selected = _upper == "NVL" && values.Length == 2 && values[0].IsNull
            || _upper == "EVL" && values.Length == 2 && !values[0].IsNull && VfpRuntime.Empty(values[0], ctx).AsLogical ? 1 : 0;
        PrepareOverflowFormat(values, row, ctx);
        var value = VfpRuntime.CallFunction(_upper, values, ctx, row);
        return (value, shapes.Length == 0 ? default : FunctionShape(shapes[selected]));
    }
}
