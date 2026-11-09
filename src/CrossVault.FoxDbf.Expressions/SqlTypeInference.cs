using System;
using System.Globalization;

namespace CrossVault.FoxDbf.Expressions;

// SQL chooses an IIF branch's storage width/scale from the first source record,
// before WHERE or ORDER BY. Keep that sampling out of index type inference.
internal sealed class SqlInferenceSchema(ISchema schema, IRowContext? row, EvaluationContext context) : ISchema
{
    internal IRowContext? Row => row;
    internal EvaluationContext Context => context;
    public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        => schema.TryGetColumn(name, out type, out length, out decimals);
}

internal sealed partial class FieldNode
{
    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        var info = Infer(schema);
        return schema.TryGetColumn(_name, out char type, out _, out _) && type is 'F' or 'B'
            ? new(info.Type, info.Length, info.Decimals, type)
            : info;
    }
}

internal sealed partial class LiteralNode
{
    internal VfpValue ConstantValue => _value;
    internal override bool IsSqlConstant => true;

    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        if (_value.Type == VfpType.Currency) return new(VfpType.Currency, 8, 4);
        if (_value.Type != VfpType.Numeric) return Infer(schema);
        string text = _value.AsNumber.ToString(CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        return new(VfpType.Numeric, text.Length, dot < 0 ? 0 : text.Length - dot - 1);
    }
}

internal sealed partial class UnaryNode
{
    internal override bool IsSqlConstant => _operand.IsSqlConstant;
    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        var info = _operand.InferSql(schema);
        if (_op == UnOp.Not) return new(VfpType.Logical, 1);
        return new(info.Type, info.Length + (IsSqlConstant ? 1 : 0), info.Decimals);
    }
}

internal sealed partial class BinaryNode
{
    internal override bool IsSqlConstant => _constant;

    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        var basic = Infer(schema);
        var l = _left.InferSql(schema);
        var r = _right.InferSql(schema);
        if (basic.Type == VfpType.Character)
            return new(VfpType.Character, SaturatingLengthAdd(l.Length, r.Length));
        if (basic.Type != VfpType.Numeric) return basic;
        if (l.Type is VfpType.Date or VfpType.DateTime && r.Type is VfpType.Date or VfpType.DateTime)
            return new(VfpType.Numeric, 10);
        if (l.Type == VfpType.Currency || r.Type == VfpType.Currency)
            return new(VfpType.Currency, 8, 4);
        l = NumericStorage(l);
        r = NumericStorage(r);
        int decimals = Math.Max(l.Decimals, r.Decimals);
        int setting = schema is SqlInferenceSchema sample ? sample.Context.NumericDecimals : 2;
        // VFP retains the literal scale for a constant quotient (1/2 is N(1,0)),
        // whereas a quotient involving a field gets at least four decimals.
        if (IsSqlConstant && _op == BinOp.Div)
        {
            decimal value = Eval(null!, EvaluationContext.Default).AsNumber;
            int width = Math.Round(value, decimals, MidpointRounding.AwayFromZero)
                .ToString("F" + decimals, CultureInfo.InvariantCulture).Length;
            return new(VfpType.Numeric, Math.Max(width, Math.Max(l.Length, r.Length)), decimals);
        }
        // Subtraction reserves the sign of a negated numeric literal on the right.
        if (_op == BinOp.Sub && _right.IsSqlConstant && _right.Eval(null!, EvaluationContext.Default).AsNumber >= 0)
            r = new(r.Type, r.Length + 1, r.Decimals);
        int length;
        switch (_op)
        {
            case BinOp.Mul:
                length = l.Length + r.Length + (l.Decimals > 0 && r.Decimals > 0 ? 0 : 1);
                decimals = l.Decimals + r.Decimals;
                break;
            case BinOp.Div:
                decimals = Math.Max(setting + 2, decimals);
                length = l.Length + r.Length - l.Decimals - r.Decimals + decimals
                    - (l.Decimals > 0 || r.Decimals > 0 ? 1 : 0);
                break;
            case BinOp.Pow:
                decimals = Math.Max(setting, decimals);
                length = l.Length + 5 + decimals - l.Decimals;
                break;
            case BinOp.Mod:
                decimals = l.Decimals;
                length = l.Length + 1;
                break;
            default:
                length = Math.Max(l.Length - l.Decimals, r.Length - r.Decimals) + decimals + 1;
                break;
        }
        return new(VfpType.Numeric, Math.Min(20, length), Math.Min(19, decimals));
    }

    internal static VfpTypeInfo NumericStorage(VfpTypeInfo info) => info.SqlStorageType == 'B'
        ? new(VfpType.Numeric, 20, info.Decimals) : info.Type switch
    {
        VfpType.Integer => new(VfpType.Numeric, 11),
        VfpType.Currency => new(VfpType.Numeric, 20, 4),
        _ => info
    };
}

internal sealed partial class FunctionNode
{
    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        string name = VfpFunctionAbbreviations.Canonical(_upper);
        var arg = _args.Length == 0 ? default : _args[0].InferSql(schema);
        var number = BinaryNode.NumericStorage(arg);
        switch (name)
        {
            case "LEN": case "AT": case "RAT": case "RECNO": case "RECCOUNT": case "COUNT":
                return new(VfpType.Numeric, 10);
            case "ASC": return new(VfpType.Numeric, 4);
            case "DAY": case "MONTH": case "DOW": return new(VfpType.Numeric, 3);
            case "YEAR": return new(VfpType.Numeric, 5);
            case "VAL":
                // A NULL first source argument fixes VAL's cursor column at N(1,0), even if
                // WHERE skips that row. Other numeric functions retain their static widths.
                if (_args.Length > 0 && schema is SqlInferenceSchema valSample
                    && valSample.Row is not null
                    && _args[0].Eval(valSample.Row, valSample.Context).IsNull)
                    return new(VfpType.Numeric, 1);
                int valScale = schema is SqlInferenceSchema valContext ? valContext.Context.NumericDecimals : 2;
                return new(VfpType.Numeric, Math.Min(20, arg.Length + valScale + 1), valScale);
            case "SQRT":
                int sqrtScale = Math.Max(number.Decimals, schema is SqlInferenceSchema sqrtContext ? sqrtContext.Context.NumericDecimals : 2);
                return new(VfpType.Numeric, Math.Min(20, number.Length + sqrtScale - number.Decimals), sqrtScale);
            case "ABS": case "MIN": case "MAX": case "SUM": case "AVG":
                // Within a larger expression SUM/AVG have their argument's width. Only a root
                // aggregate expands N/F storage to 16 columns (handled by the SQL executor).
                return arg;
            case "INT": return new(number.Type, number.Length);
            case "ROUND":
                int decimals = _args.Length > 1 && _args[1].TryConstInt(out int d) ? Math.Max(0, d) : number.Decimals;
                return new(VfpType.Numeric, Math.Min(20, number.Length + 1 + Math.Max(0, decimals - number.Decimals)), decimals);
            case "MOD":
                var other = _args.Length > 1 ? BinaryNode.NumericStorage(_args[1].InferSql(schema)) : number;
                int scale = Math.Max(number.Decimals, other.Decimals);
                return new(VfpType.Numeric, Math.Min(20, Math.Max(number.Length - number.Decimals, other.Length - other.Decimals) + scale + 1), scale);
            case "IIF":
                if (_args.Length == 3)
                {
                    VfpValue? condition = _args[0] is LiteralNode literal ? literal.ConstantValue : null;
                    if (condition is null && schema is SqlInferenceSchema sample && sample.Row is not null
                        && !_args[0].ContainsFunction(static (n, a) => a == 1 && n is "SUM" or "AVG" or "COUNT" or "MIN" or "MAX"))
                        condition = _args[0].Eval(sample.Row, sample.Context);
                    if (condition is { } value)
                        return _args[VfpRuntime.AsCondition(value) ? 1 : 2].InferSql(schema);
                }
                return _args.Length > 1 ? _args[1].InferSql(schema) : Infer(schema);
            case "ICASE":
                return _args.Length > 1 ? _args[1].InferSql(schema) : Infer(schema);
            case "UPPER": case "LOWER": case "TRIM": case "RTRIM": case "LTRIM": case "ALLTRIM":
                return new(VfpType.Character, arg.Length);
            case "SUBSTR":
                if (_args.Length == 2 && _args[1].TryConstInt(out int start))
                    return new(VfpType.Character, Math.Max(1, arg.Length - Math.Max(1, start) + 1));
                break;
        }
        return Infer(schema);
    }
}
