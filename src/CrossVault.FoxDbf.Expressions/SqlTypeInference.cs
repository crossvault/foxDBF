using System;
using System.Globalization;

namespace CrossVault.FoxDbf.Expressions;

// SQL chooses an IIF branch's storage width/scale from the first source record,
// before WHERE or ORDER BY. Keep that sampling out of index type inference.
internal sealed class SqlInferenceSchema(ISchema? schema, IRowContext? row, EvaluationContext context, IRowContext? memory = null) : ISchema
{
    internal IRowContext? Row => row;
    internal IRowContext? Memory => memory ?? row;
    internal EvaluationContext Context => context;
    public bool TryGetColumn(string name, out char type, out int length, out int decimals)
    {
        type = default; length = 0; decimals = 0;
        return schema is not null && schema.TryGetColumn(name, out type, out length, out decimals);
    }
}

internal sealed partial class FieldNode
{
    internal override VfpTypeInfo InferSql(ISchema schema)
    {
        var info = Infer(schema);
        if (info.Type == VfpType.Unknown && schema is SqlInferenceSchema { Memory: not null } sample)
        {
            var value = Eval(sample.Memory, sample.Context);
            if (VfpRuntime.IsNumeric(value))
            {
                // Round4 memvar AFIELDS: retain the assigned scale, sign and large integer digits.
                int scale = Math.Max(0, LiteralNode.Precision(value));
                int width = VfpRuntime.NumericOverflowWidth(scale);
                if (double.IsFinite(value.AsDouble))
                {
                    int digits = decimal.Truncate(Math.Abs(value.AsNumber)).ToString(CultureInfo.InvariantCulture).Length;
                    width = Math.Max(10, digits) + (scale > 0 ? scale + 1 : 0) + (value.AsDouble < 0 ? 1 : 0);
                }
                return new(VfpType.Numeric, Math.Min(20, width), scale);
            }
        }
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

internal partial class BinaryNode
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
        if (_op != BinOp.Pow && (l.Type == VfpType.Currency || (_op != BinOp.Mod && r.Type == VfpType.Currency)))
            return new(VfpType.Currency, 8, 4);
        bool currencyPower = _op == BinOp.Pow && l.Type == VfpType.Currency;
        l = NumericStorage(l);
        r = NumericStorage(r);
        int decimals = Math.Max(l.Decimals, r.Decimals);
        int setting = schema is SqlInferenceSchema sample ? sample.Context.NumericDecimals : 2;
        // VFP retains the literal scale for a constant quotient (1/2 is N(1,0)),
        // whereas a quotient involving a field gets at least four decimals.
        if (IsSqlConstant && _op == BinOp.Div)
        {
            var evaluated = Eval(null!, schema is SqlInferenceSchema context ? context.Context : EvaluationContext.Default);
            if (!double.IsFinite(evaluated.AsDouble))
                return new(VfpType.Numeric, Math.Min(20, evaluated.ImmediateOverflowWidth), Math.Min(18, evaluated.OverflowShapeScale));
            decimal value = evaluated.AsNumber;
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
                length = (currencyPower ? 10 : l.Length) + 5 + decimals - l.Decimals;
                break;
            case BinOp.Mod:
                decimals = l.Decimals;
                length = l.Length + 1;
                break;
            default:
                length = Math.Max(l.Length - l.Decimals, r.Length - r.Decimals) + decimals + 1;
                break;
        }
        return new(VfpType.Numeric, Math.Min(20, length), Math.Min(18, decimals));
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
        var info = InferSqlStorage(schema);
        // VFP9 AFIELDS (SqlFloatResultTypeVfp9OracleTests): a projected F field and a root SUM/AVG/MAX/MIN
        // of it stay F, but ABS, IIF, ICASE, NVL and EVL over an F field give an N column.
        return info.SqlStorageType == 'F' && VfpFunctionAbbreviations.Canonical(_upper) is "ABS" or "IIF" or "ICASE" or "NVL" or "EVL"
            ? new(info.Type, info.Length, info.Decimals)
            : info;
    }

    private VfpTypeInfo InferSqlStorage(ISchema schema)
    {
        string name = VfpFunctionAbbreviations.Canonical(_upper);
        var arg = _args.Length == 0 ? default : _args[0].InferSql(schema);
        var number = BinaryNode.NumericStorage(arg);
        switch (name)
        {
            case "LOG" when _args.Length == 1 && _args[0].HasQuotient:
                int logScale = Math.Max(number.Decimals, schema is SqlInferenceSchema logContext ? logContext.Context.NumericDecimals : 2);
                return new(VfpType.Numeric, 20, Math.Min(18, logScale));
            case "EXP" when _args.Length == 1 && _args[0].HasQuotient:
                int expScale = Math.Max(number.Decimals, schema is SqlInferenceSchema expContext ? expContext.Context.NumericDecimals : 2);
                return new(VfpType.Numeric, Math.Min(20, VfpRuntime.NumericOverflowWidth(expScale)), expScale);
            case "NVL" when _args.Length == 2 && _args[0] is LiteralNode nvl:
                return nvl.ConstantValue.IsNull ? _args[1].InferSql(schema) : arg;
            case "EVL" when _args.Length == 2 && _args[0] is LiteralNode evl:
                return !evl.ConstantValue.IsNull && VfpRuntime.Empty(evl.ConstantValue, EvaluationContext.Default).AsLogical
                    ? _args[1].InferSql(schema) : arg;
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
                return new(VfpType.Numeric, Math.Min(20, Math.Min(20, arg.Length) + valScale + 1), valScale);
            case "SQRT":
                int sqrtScale = Math.Max(number.Decimals, schema is SqlInferenceSchema sqrtContext ? sqrtContext.Context.NumericDecimals : 2);
                return new(VfpType.Numeric, Math.Min(20, number.Length + sqrtScale - number.Decimals + (number.Decimals == 0 ? 1 : 0)), sqrtScale);
            case "ABS": case "MIN": case "MAX": case "SUM": case "AVG":
                // Within a larger expression SUM/AVG have their argument's width. Only a root
                // aggregate expands N/F storage to 16 columns (handled by the SQL executor).
                return arg;
            case "MTON": return arg.Type == VfpType.Currency ? new(VfpType.Numeric, 20, 4) : arg;
            case "NTOM": return new(VfpType.Currency, 8, 4);
            case "INT": return arg.Type == VfpType.Currency ? arg : new(number.Type, number.Length);
            case "ROUND":
                if (arg.Type == VfpType.Currency) return arg;
                int decimals = _args.Length > 1 && _args[1].TryConstInt(out int d) ? Math.Max(0, d) : number.Decimals;
                return new(VfpType.Numeric, Math.Min(20, number.Length + 1 + Math.Max(0, decimals - number.Decimals)), decimals);
            case "MOD":
                // The divisor does not widen MOD's scale or promote its result to currency.
                return arg.Type == VfpType.Currency ? arg
                    : new(VfpType.Numeric, Math.Min(20, number.Length + 1), number.Decimals);
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
            case "NVL":
                // Round10: retain the selected quotient's SQL width before outer arithmetic.
                return _args.Length == 2 && _args[0].InferSql(schema).Type is VfpType.Null or VfpType.Unknown
                    ? _args[1].InferSql(schema) : arg;
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
