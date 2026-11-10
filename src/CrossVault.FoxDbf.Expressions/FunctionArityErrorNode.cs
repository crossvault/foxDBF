using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// Only runtime-invalid counts use this node. Normal function calls keep their existing hot path.
// VFP9 compiles these calls even inside an unselected IIF/ICASE branch.
internal sealed class FunctionArityErrorNode(
    FunctionNode function, AstNode[] args, int error, bool propagateNull, int position) : AstNode
{
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        var values = new VfpValue[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Eval(row, ctx);
        return Raise(values, error, propagateNull, position);
    }

    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
    {
        var values = new VfpValue[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Eval(row, ctx, resolver);
        return Raise(values, error, propagateNull, position);
    }

    public override Expression Build(BuildContext b)
    {
        var values = new Expression[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Build(b);
        return Expression.Call(typeof(FunctionArityErrorNode), nameof(Raise), null,
            Expression.NewArrayInit(typeof(VfpValue), values), Expression.Constant(error),
            Expression.Constant(propagateNull), Expression.Constant(position));
    }

    public static VfpValue Raise(VfpValue[] values, int error, bool propagateNull, int position)
    {
        if (propagateNull)
            foreach (var value in values)
                if (value.IsNull) return VfpValue.Null;
        throw VfpFunctionArity.Error(error, position);
    }

    public override VfpTypeInfo Infer(ISchema schema) => function.Infer(schema);
    internal override VfpTypeInfo InferSql(ISchema schema) => function.InferSql(schema);
    internal override bool ContainsFunction(Func<string, int, bool> predicate) => function.ContainsFunction(predicate);
    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
        => function.CollectFields(into, skipFunction);
}
