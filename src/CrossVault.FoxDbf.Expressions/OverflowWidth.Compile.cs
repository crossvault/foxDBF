using System;
using System.Linq;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

internal abstract partial class AstNode
{
    internal virtual Expression BuildTracked(BuildContext b, ReadOnlySpan<ParameterExpression> selections) => Build(b);
}

internal sealed partial class UnaryNode
{
    internal override Expression BuildTracked(BuildContext b, ReadOnlySpan<ParameterExpression> selections)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Unary), null,
            Expression.Constant(_op), _operand.BuildTracked(b, selections), b.Ctx, Expression.Constant(HasQuotient));
}

internal partial class BinaryNode
{
    protected Expression BuildSelections(BuildContext b)
    {
        var selections = Enumerable.Range(0, SelectionCount).Select(i => Expression.Variable(typeof(int), "choice" + i)).ToArray();
        return Expression.Block(selections, BuildTracked(b, selections));
    }

    public VfpValue FormatSelectedOverflow(VfpValue value, IRowContext row, EvaluationContext ctx, int[] selections)
        => FormatOverflow(value, row, ctx, selections);

    internal override Expression BuildTracked(BuildContext b, ReadOnlySpan<ParameterExpression> selections)
    {
        if (SelectionCount == 0) return Build(b);
        var left = _left.BuildTracked(b, selections[.._left.SelectionCount]);
        var right = _right.BuildTracked(b, selections[_left.SelectionCount..]);
        Expression Call(Expression l) => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Binary), null,
            Expression.Constant(_op), l, right, b.Ctx, Expression.Constant(IsSqlConstant));
        Expression result;
        if (_op is BinOp.And or BinOp.Or)
        {
            var l = Expression.Variable(typeof(VfpValue), "left");
            result = Expression.Block(new[] { l }, Expression.Assign(l, left),
                Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Decides), null, Expression.Constant(_op), l), l, Call(l)));
        }
        else result = Call(left);
        if (!HasQuotient) return result;
        var value = Expression.Variable(typeof(VfpValue), "result");
        // The compiled path records only integer branch indices. It materializes the
        // cold-path array only after a non-finite result, never on finite arithmetic.
        return Expression.Block(new[] { value }, Expression.Assign(value, result),
            Expression.Condition(
                Expression.AndAlso(Expression.Equal(Expression.Property(value, nameof(VfpValue.Type)), Expression.Constant(VfpType.Numeric)),
                    Expression.Not(Expression.Call(typeof(double), nameof(double.IsFinite), null, Expression.Property(value, nameof(VfpValue.AsDouble))))),
                Expression.Call(Expression.Constant(this), nameof(FormatSelectedOverflow), null, value, b.Row, b.Ctx,
                    Expression.NewArrayInit(typeof(int), selections.ToArray())), value));
    }
}

internal sealed partial class FunctionNode
{
    internal override Expression BuildTracked(BuildContext b, ReadOnlySpan<ParameterExpression> selections)
    {
        if (SelectionCount == 0) return Build(b);
        var args = new Expression[_args.Length];
        for (int i = 0; i < args.Length; i++)
            args[i] = _args[i].BuildTracked(b, selections.Slice(SelectionOffset(i), _args[i].SelectionCount));
        if (_upper == "IIF" && args.Length == 3)
            return Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, args[0]),
                Selected(1, args[1], selections[0]), Selected(2, args[2], selections[0]));
        if (_upper == "ICASE")
        {
            int end = args.Length;
            Expression result = Expression.Constant(VfpValue.Null);
            if ((end & 1) != 0) { end--; result = Selected(end, args[end], selections[0]); }
            for (int i = end - 2; i >= 0; i -= 2)
                result = Expression.Condition(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, args[i]),
                    Selected(i + 1, args[i + 1], selections[0]), result);
            return result;
        }
        var values = Expression.Variable(typeof(VfpValue[]), "arguments");
        Expression call = Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.CallFunction), null,
            Expression.Constant(_upper), values, b.Ctx, b.Row);
        if (_formatOverflow)
            call = Expression.Block(Expression.Call(Expression.Constant(this), nameof(PrepareOverflowFormat), null, values, b.Row, b.Ctx), call);
        if (_upper is "NVL" or "EVL" && args.Length == 2)
        {
            var first = Expression.ArrayIndex(values, Expression.Constant(0));
            Expression selectSecond = Expression.Property(first, nameof(VfpValue.IsNull));
            if (_upper == "EVL")
                selectSecond = Expression.AndAlso(Expression.Not(selectSecond),
                    Expression.Property(Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Empty), null, first, b.Ctx), nameof(VfpValue.AsLogical)));
            call = Expression.Block(Expression.Assign(selections[0], Expression.Condition(selectSecond, Expression.Constant(1), Expression.Constant(0))), call);
        }
        return Expression.Block(new[] { values }, Expression.Assign(values, Expression.NewArrayInit(typeof(VfpValue), args)), call);
    }

    private static Expression Selected(int index, Expression value, ParameterExpression selection)
        => Expression.Block(Expression.Assign(selection, Expression.Constant(index)), value);
}
