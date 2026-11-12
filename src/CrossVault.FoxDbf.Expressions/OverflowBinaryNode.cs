using System;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// Selected at parse time: ordinary arithmetic keeps BinaryNode's original hot path.
internal class OverflowBinaryNode(BinOp op, AstNode left, AstNode right, bool? constant = null) : BinaryNode(op, left, right)
{
    // Selected arms inherit the original expression's constant flag, rather than
    // redispatching its virtual property for each finite result.
    private readonly bool _arithmeticConstant = constant ?? (left.IsSqlConstant && right.IsSqlConstant);

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        if (SelectionCount > 0) return EvalWithSelections(row, ctx, null);
        return Apply(_left.Eval(row, ctx), _right.Eval(row, ctx), row, ctx);
    }

    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
    {
        if (SelectionCount > 0) return EvalWithSelections(row, ctx, resolver);
        return Apply(_left.Eval(row, ctx, resolver), _right.Eval(row, ctx, resolver), row, ctx);
    }

    public VfpValue Apply(VfpValue l, VfpValue r, IRowContext row, EvaluationContext ctx)
        => ApplySelected(l, r, row, ctx, default);

    // The caller already holds both values; readonly references avoid another 96-byte copy.
    internal VfpValue ApplySelected(in VfpValue l, in VfpValue r, IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections)
    {
        // Same finite numeric result as VfpRuntime. Avoid a second dispatch and a
        // post-result copy on the hot path; descriptor walking is below IsFinite.
        if (!VfpRuntime.IsNumeric(l) || !VfpRuntime.IsNumeric(r)
            || l.Type == VfpType.Currency || r.Type == VfpType.Currency)
            return VfpRuntime.Binary(_op, l, r, ctx, _arithmeticConstant);
        double left = l.AsDouble, right = r.AsDouble;
        double value = _op switch
        {
            BinOp.Div => left / (right == 0 ? 0d : right),
            BinOp.Add => left + right,
            BinOp.Sub => left - right,
            _ => left * right
        };
        if (double.IsFinite(value))
        {
            int ls = Math.Max(0, l.DisplayDecimals), rs = Math.Max(0, r.DisplayDecimals);
            int scale = _op == BinOp.Mul ? ls + rs : Math.Max(ls, rs);
            if (_op == BinOp.Div) scale = Math.Max(scale, ctx.NumericDecimals + (_arithmeticConstant ? 0 : 2));
            else if (_arithmeticConstant) scale = Math.Max(scale, ctx.NumericDecimals);
            return VfpValue.Number(value, scale);
        }
        return FormatOverflow(VfpRuntime.Binary(_op, l, r, ctx, _arithmeticConstant), row, ctx, selections, l, r);
    }

    public override Expression Build(BuildContext b)
        => SelectionCount > 0 ? BuildSelections(b)
            : Expression.Call(Expression.Constant(this), nameof(Apply), null, _left.Build(b), _right.Build(b), b.Row, b.Ctx);
}
