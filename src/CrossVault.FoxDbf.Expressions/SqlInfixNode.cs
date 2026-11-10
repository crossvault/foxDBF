using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// SQL-only syntax: keeping these nodes in the expression tree makes HAVING aggregate
// resolution, qualifier binding and hosted memvars/UDFs use the same paths as other operands.
internal sealed class SqlInfixNode(string kind, AstNode[] operands) : AstNode
{
    private readonly string? _field = operands[0] is FieldNode field &&
        (kind != "LIKE" || operands.Skip(1).All(o => LikeNullError(o) != 0)) ? field.Name : null;
    private readonly bool _constantMembers = kind == "IN" && operands.Skip(1).All(o => o.IsSqlConstant);

    internal override bool IsSqlCandidate(IRowContext row, EvaluationContext context)
    {
        if (!_constantMembers || _field is null || context.SqlIndexedFields?.Contains(_field) != true) return true;
        var value = operands[0].Eval(row, context);
        if (!VfpRuntime.IsNumeric(value)) return true;
        bool numericMember = false;
        for (int i = 1; i < operands.Length; i++)
        {
            var member = operands[i].Eval(row, context);
            if (!VfpRuntime.IsNumeric(member)) continue;
            numericMember = true;
            if (VfpRuntime.ValueEqSql(value, member, context)) return true;
        }
        // VFP9's numeric index narrows the candidate set before the typed residual runs.
        // A mixed member can still raise 107 on a surviving row; do not suppress that error.
        return !numericMember;
    }

    private readonly int _leftNullError = kind == "LIKE" ? LikeNullError(operands[0]) : 0;
    private readonly int _patternNullError = kind == "LIKE" ? LikeNullError(operands[1]) : 0;

    private static int LikeNullError(AstNode operand)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        operand.CollectFields(fields);
        if (fields.Count == 0) return 1833;
        return fields.All(f => f.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) ? 11 : 0;
    }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        var values = new VfpValue[operands.Length];
        for (int i = 0; i < values.Length; i++) values[i] = operands[i].Eval(row, ctx);
        return VfpRuntime.SqlInfix(kind, values, ctx, _leftNullError, _patternNullError, _field);
    }

    internal override VfpValue Eval(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver resolver)
    {
        var values = new VfpValue[operands.Length];
        for (int i = 0; i < values.Length; i++) values[i] = operands[i].Eval(row, ctx, resolver);
        return VfpRuntime.SqlInfix(kind, values, ctx, _leftNullError, _patternNullError, _field);
    }

    public override Expression Build(BuildContext context) => Expression.Call(
        typeof(VfpRuntime), nameof(VfpRuntime.SqlInfix), null, Expression.Constant(kind),
        Expression.NewArrayInit(typeof(VfpValue), operands.Select(o => o.Build(context))), context.Ctx,
        Expression.Constant(_leftNullError), Expression.Constant(_patternNullError), Expression.Constant(_field, typeof(string)));

    public override VfpTypeInfo Infer(ISchema schema) => new(VfpType.Logical, 1);

    internal override bool ContainsFunction(Func<string, int, bool> predicate)
        => operands.Any(o => o.ContainsFunction(predicate));

    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
    {
        foreach (var operand in operands) operand.CollectFields(into, skipFunction);
    }
}

internal static partial class VfpRuntime
{
    // Shared by IN lists and subqueries: SQL equality follows ANSI, never SET EXACT.
    internal static bool ValueEqSql(VfpValue left, VfpValue right, EvaluationContext ctx)
    {
        if (left.Type == VfpType.Character && right.Type == VfpType.Character)
            return StrEqSql(left.AsString, right.AsString, ctx);
        if (IsNumeric(left) && IsNumeric(right))
        {
            // Preserve the SQL subquery comparison's decimal/currency precision when sharing it.
            if (!double.IsFinite(left.AsDouble) || !double.IsFinite(right.AsDouble))
                return left.AsDouble.CompareTo(right.AsDouble) == 0;
            return left.AsNumber == right.AsNumber;
        }
        if (left.Type == VfpType.Logical && right.Type == VfpType.Logical)
            return left.AsLogical == right.AsLogical;
        if (left.Type is VfpType.Date or VfpType.DateTime && right.Type is VfpType.Date or VfpType.DateTime)
            return left.AsDateTime == right.AsDateTime;
        throw new ExpressionException("Operator/operand type mismatch.") { VfpErrorNumber = 107 };
    }

    internal static bool? InMembership(VfpValue value, IReadOnlyList<VfpValue> members, EvaluationContext ctx, int start = 0)
    {
        if (value.IsNull) return null;
        bool sawNull = false;
        for (int i = start; i < members.Count; i++)
        {
            var member = members[i];
            if (member.IsNull) { sawNull = true; continue; }
            if (ValueEqSql(value, member, ctx)) return true;
        }
        return sawNull ? null : false;
    }

    public static VfpValue SqlInfix(string kind, VfpValue[] operands, EvaluationContext ctx, int leftNullError, int patternNullError, string? field)
    {
        switch (kind)
        {
            case "IN":
                bool? membership = InMembership(operands[0], operands, ctx, start: 1);
                return membership.HasValue ? VfpValue.Logical(membership.Value) : VfpValue.Null;
            case "BETWEEN":
                // VFP9 uses two comparisons: FALSE AND NULL is FALSE, also under NOT BETWEEN.
                return Binary(BinOp.And, Binary(BinOp.Ge, operands[0], operands[1], ctx),
                    Binary(BinOp.Le, operands[0], operands[2], ctx), ctx);
            case "LIKE":
                // Rushmore validates a one-character ESCAPE when the value has a usable index.
                if (field is not null && ctx.SqlIndexedFields?.Contains(field) == true &&
                    operands.Length == 3 && operands[2].Type == VfpType.Character && operands[2].AsString.Length != 1)
                    throw new ExpressionException("LIKE ESCAPE must be one character.") { VfpErrorNumber = 11 };
                return SqlLike(operands, leftNullError, patternNullError);
            default:
                throw new InvalidOperationException("Unknown SQL infix predicate.");
        }
    }

    private static VfpValue SqlLike(VfpValue[] operands, int leftNullError, int patternNullError)
    {
        char escape = '\0';
        if (operands.Length == 3)
        {
            if (operands[2].Type != VfpType.Character)
                throw new ExpressionException("Function argument value, type, or count is invalid.") { VfpErrorNumber = 11 };
            if (operands[2].AsString.Length > 0) escape = operands[2].AsString[0];
        }
        if (operands[0].IsNull && leftNullError != 0)
            throw new ExpressionException("SQL: LIKE requires a character expression.") { VfpErrorNumber = leftNullError };
        if (operands[1].IsNull && patternNullError != 0)
            throw new ExpressionException("SQL: LIKE requires a character pattern.") { VfpErrorNumber = patternNullError };
        if (operands[0].IsNull || operands[1].IsNull) return VfpValue.Null;
        if (operands[0].Type != VfpType.Character || operands[1].Type != VfpType.Character)
            throw new ExpressionException("Function argument value, type, or count is invalid.") { VfpErrorNumber = 11 };
        return VfpValue.Logical(MatchesSqlLike(operands[0].AsString, operands[1].AsString, escape));
    }

    private static bool MatchesSqlLike(string value, string pattern, char escape)
    {
        int v = 0, p = 0, star = -1, retry = 0;
        while (true)
        {
            // Only the value may have excess trailing blanks. Pattern blanks and '_' consume
            // actual characters. ESCAPE quotes only '%' and '_' (not itself), as in VFP9.
            if (p == pattern.Length && value.AsSpan(v).TrimEnd(' ').IsEmpty) return true;
            bool quoted = p + 1 < pattern.Length && pattern[p] == escape && pattern[p + 1] is '%' or '_';
            if (!quoted && p < pattern.Length && pattern[p] == '%')
            {
                star = ++p;
                retry = v;
                continue;
            }
            int literal = quoted ? p + 1 : p;
            if (v < value.Length && literal < pattern.Length &&
                ((!quoted && pattern[literal] == '_') || pattern[literal] == value[v]))
            {
                v++;
                p = literal + 1;
                continue;
            }
            if (star < 0 || retry == value.Length) return false;
            v = ++retry;
            p = star;
        }
    }
}
