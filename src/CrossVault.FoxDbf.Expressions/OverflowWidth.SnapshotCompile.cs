using System;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

internal abstract partial class AstNode
{
    internal virtual Expression BuildSnapshot(BuildContext b)
        => Expression.Call(Expression.Constant(this), nameof(SnapshotValue), null, Build(b), b.Row, b.Ctx);

    public (VfpValue, OverflowShape) SnapshotValue(VfpValue value, IRowContext row, EvaluationContext ctx)
        => (value, OverflowShape(row, ctx, default, value));

    internal static Expression SnapshotPair(Expression value, Expression shape)
        => Expression.New(typeof(ValueTuple<VfpValue, OverflowShape>).GetConstructor(new[] { typeof(VfpValue), typeof(OverflowShape) })!, value, shape);
}

internal sealed partial class UnaryNode
{
    internal override Expression BuildSnapshot(BuildContext b)
        => Expression.Call(Expression.Constant(this), nameof(UnarySnapshot), null, _operand.BuildSnapshot(b), b.Ctx);

    public (VfpValue, OverflowShape) UnarySnapshot((VfpValue Value, OverflowShape Shape) operand, EvaluationContext ctx)
        => (VfpRuntime.Unary(_op, operand.Value, ctx, HasQuotient),
            operand.Shape.Width == 0 ? default : operand.Shape with { Width = operand.Shape.Width + 1 });
}

internal partial class BinaryNode
{
    internal override Expression BuildSnapshot(BuildContext b)
    {
        var left = Expression.Variable(typeof(ValueTuple<VfpValue, OverflowShape>), "leftSnapshot");
        Expression call = Expression.Call(Expression.Constant(this), nameof(BinarySnapshot), null, left, _right.BuildSnapshot(b), b.Ctx);
        if (_op is BinOp.And or BinOp.Or)
            call = Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Decides), null,
                Expression.Constant(_op), Expression.Field(left, "Item1")), left, call);
        return Expression.Block(new[] { left }, Expression.Assign(left, _left.BuildSnapshot(b)), call);
    }

    public (VfpValue, OverflowShape) BinarySnapshot((VfpValue Value, OverflowShape Shape) left,
        (VfpValue Value, OverflowShape Shape) right, EvaluationContext ctx)
    {
        var value = VfpRuntime.Binary(_op, left.Value, right.Value, ctx, IsSqlConstant);
        if (HasQuotient && value.Type == VfpType.Numeric && !double.IsFinite(value.AsDouble))
            value = FormatOverflow(value, ctx, left.Shape, right.Shape);
        return (value, CombineShape(left.Shape, right.Shape, ctx));
    }
}

internal sealed partial class FunctionNode
{
    internal override Expression BuildSnapshot(BuildContext b)
    {
        if (!HasMeasuredShape) return SnapshotPair(Build(b), Expression.Default(typeof(OverflowShape)));
        if (_upper == "IIF" && _args.Length == 3)
            return Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, _args[0].Build(b)),
                _args[1].BuildSnapshot(b), _args[2].BuildSnapshot(b));
        if (_upper == "ICASE")
        {
            int end = _args.Length;
            Expression result = SnapshotPair(Expression.Constant(VfpValue.Null), Expression.Default(typeof(OverflowShape)));
            if ((end & 1) != 0) result = _args[--end].BuildSnapshot(b);
            for (int i = end - 2; i >= 0; i -= 2)
                result = Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, _args[i].Build(b)),
                    _args[i + 1].BuildSnapshot(b), result);
            return result;
        }
        var args = new Expression[_args.Length];
        for (int i = 0; i < args.Length; i++) args[i] = _args[i].BuildSnapshot(b);
        return Expression.Call(Expression.Constant(this), nameof(FunctionSnapshot), null,
            Expression.NewArrayInit(typeof(ValueTuple<VfpValue, OverflowShape>), args), b.Row, b.Ctx);
    }

    public (VfpValue, OverflowShape) FunctionSnapshot((VfpValue Value, OverflowShape Shape)[] args, IRowContext row, EvaluationContext ctx)
    {
        var values = new VfpValue[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Value;
        int selected = _upper == "NVL" && values.Length == 2 && values[0].IsNull
            || _upper == "EVL" && values.Length == 2 && !values[0].IsNull && VfpRuntime.Empty(values[0], ctx).AsLogical ? 1 : 0;
        var value = VfpRuntime.CallFunction(_upper, values, ctx, row);
        return (value, args.Length == 0 ? default : FunctionShape(args[selected].Shape));
    }
}
