using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// VFP9 SQL evaluates ISBLANK on the scan value, including for bare fields. In contrast,
// xBase ISBLANK(field) can distinguish raw blank numeric/logical bytes from written 0/.F..
internal sealed class SqlIsBlankNode(AstNode argument) : AstNode
{
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => IsBlank(argument.Eval(row, ctx));

    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
        => IsBlank(argument.Eval(row, ctx, resolver));

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(SqlIsBlankNode), nameof(IsBlank), null, argument.Build(b));

    public static VfpValue IsBlank(VfpValue value) => VfpValue.Logical(value.Type switch
    {
        VfpType.Character => value.AsString.Trim(' ').Length == 0,
        VfpType.Date => value.AsDate == default,
        VfpType.DateTime => value.AsDateTime == default,
        _ => false,
    });

    public override VfpTypeInfo Infer(ISchema schema) => new(VfpType.Logical, 1);
    internal override bool ContainsFunction(Func<string, int, bool> predicate)
        => predicate("ISBLANK", 1) || argument.ContainsFunction(predicate);
    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
    {
        if (skipFunction?.Invoke("ISBLANK", 1) != true) argument.CollectFields(into, skipFunction);
    }
}
