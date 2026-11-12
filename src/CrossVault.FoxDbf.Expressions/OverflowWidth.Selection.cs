using System;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// Distribute a quotient over a lazy value selection at parse time. Conditions,
// selected operands and the divisor keep their original order, without a per-row
// selection buffer or a second arithmetic dispatch.
internal class SelectedOverflowNode : OverflowBinaryNode
{
    private readonly FunctionNode _selection;
    protected readonly FunctionNode _evaluation;
    internal SelectedOverflowNode(BinOp op, FunctionNode left, AstNode right) : base(op, left, right)
    {
        _selection = left;
        _evaluation = left.MapSelectedValues(value => new SelectedArmNode(op, value, right, IsSqlConstant));
    }
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx) => _evaluation.Eval(row, ctx);
    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
        => _selection.EvalMapped(row, ctx, resolver, _evaluation, this, _right);
    public override Expression Build(BuildContext b) => _evaluation.Build(b);
}

// IIF is the common row-level quotient: avoid redispatching its function name
// and walking a second FunctionNode before applying the selected arithmetic.
internal sealed class IifOverflowNode : SelectedOverflowNode
{
    private readonly AstNode _condition;
    private readonly SelectedArmNode _whenTrue, _whenFalse;
    internal IifOverflowNode(BinOp op, FunctionNode left, AstNode right) : base(op, left, right)
    {
        _condition = left.Argument(0);
        _whenTrue = (SelectedArmNode)_evaluation.Argument(1);
        _whenFalse = (SelectedArmNode)_evaluation.Argument(2);
    }
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => (VfpRuntime.AsCondition(_condition.Eval(row, ctx)) ? _whenTrue : _whenFalse).Eval(row, ctx);
}

internal sealed class SelectedArmNode(BinOp op, AstNode left, AstNode right, bool constant) : OverflowBinaryNode(op, left, right, constant)
{
    internal override bool IsSqlConstant => constant;
}

internal sealed partial class FunctionNode
{
    internal AstNode Argument(int index) => _args[index];
    internal bool IsIif => _upper == "IIF" && _args.Length == 3;
    internal bool CanMapSelectedValues => _upper == "IIF" && _args.Length == 3 || _upper == "ICASE" && _args.Length > 0;
    internal FunctionNode MapSelectedValues(Func<AstNode, AstNode> map)
    {
        var args = (AstNode[])_args.Clone();
        if (_upper == "ICASE" && (args.Length & 1) == 0)
        {
            // A missing match returns NULL, but the original quotient still
            // evaluates its divisor (including errors and RAND side effects).
            Array.Resize(ref args, args.Length + 1);
            args[^1] = new LiteralNode(VfpValue.Null);
        }
        for (int i = 0; i < args.Length; i++)
            if (_upper == "IIF" ? i > 0 : i % 2 == 1 || i == args.Length - 1)
                args[i] = map(args[i]);
        return new FunctionNode(_upper, args);
    }
    internal VfpValue EvalMapped(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver,
        FunctionNode mapped, OverflowBinaryNode quotient, AstNode right)
    {
        if (resolver(_upper, _resolverArguments, out var value))
            return quotient.Apply(value, right.Eval(row, ctx, resolver), row, ctx);
        if (_upper == "IIF")
            return mapped._args[VfpRuntime.AsCondition(_args[0].Eval(row, ctx, resolver)) ? 1 : 2].Eval(row, ctx, resolver);
        return mapped.EvalIcase(row, ctx, resolver);
    }
}
