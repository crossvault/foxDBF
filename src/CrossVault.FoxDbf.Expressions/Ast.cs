using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>Compile-time helpers shared while lowering the AST to a delegate.</summary>
internal sealed class BuildContext
{
    public required ParameterExpression Row { get; init; }
    public required Expression Ctx { get; init; }   // ConstantExpression of EvaluationContext
}

/// <summary>Immutable AST node base. Provides interpret, compile and infer paths.</summary>
internal abstract class AstNode
{
    /// <summary>Tree-walking interpretation.</summary>
    public abstract VfpValue Eval(IRowContext row, EvaluationContext ctx);

    internal virtual VfpValue Eval(
        IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver functionResolver)
        => Eval(row, ctx);

    internal virtual bool ContainsFunction(Func<string, int, bool> predicate) => false;

    /// <summary>Lowers this node to a <see cref="Expression"/> of type <see cref="VfpValue"/>.</summary>
    public abstract Expression Build(BuildContext b);

    /// <summary>Static type inference against a schema.</summary>
    public abstract VfpTypeInfo Infer(ISchema schema);

    // SQL cursor storage has different sizing rules from index expressions.
    internal virtual VfpTypeInfo InferSql(ISchema schema) => Infer(schema);
    internal virtual int? AggregateDecimals(ISchema schema, Func<string, VfpValue> memory, int decimals)
    {
        var info = Infer(schema);
        return info.Length > 0 ? info.Decimals : null;
    }
    internal virtual bool IsSqlConstant => false;
    internal virtual bool IsSqlCandidate(IRowContext row, EvaluationContext context, bool negated = false) => true;

    /// <summary>Add every FIELD name this node (transitively) reads to <paramref name="into"/>. Only bare
    /// field references count — function names, literals and operators contribute nothing. Used by the
    /// write-path index maintenance to decide whether a tag's KEY/FOR expression resolves against a table's
    /// physical schema before it edits that tag's on-disk tree.</summary>
    public virtual void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null) { }

    /// <summary>If this node is a numeric literal, returns its integer value.</summary>
    public virtual bool TryConstInt(out int value) { value = 0; return false; }

    /// <summary>If this node is a character literal, returns its string value.</summary>
    public virtual bool TryConstString(out string value) { value = string.Empty; return false; }
}

internal sealed partial class LiteralNode : AstNode
{
    private readonly VfpValue _value;
    private readonly bool _byteLiteral;
    public LiteralNode(VfpValue value)
    {
        _value = value;
        _byteLiteral = value.Type == VfpType.Character && !value.IsBinary
            && value.AsString.AsSpan().IndexOfAnyInRange('\u0080', '\u009f') >= 0;
    }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => _byteLiteral ? VfpRuntime.DecodeByteLiteral(_value, ctx) : _value;

    public override Expression Build(BuildContext b)
        => _byteLiteral
            ? Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.DecodeByteLiteral), null,
                Expression.Constant(_value, typeof(VfpValue)), b.Ctx)
            : Expression.Constant(_value, typeof(VfpValue));

    public override VfpTypeInfo Infer(ISchema schema) => _value.Type switch
    {
        VfpType.Character => new VfpTypeInfo(VfpType.Character, _value.AsString.Length) { IsBinary = _value.IsBinary },
        VfpType.Logical => new VfpTypeInfo(VfpType.Logical, 1),
        VfpType.Date => new VfpTypeInfo(VfpType.Date, 8),
        VfpType.DateTime => new VfpTypeInfo(VfpType.DateTime, 8),
        VfpType.Null => new VfpTypeInfo(VfpType.Null),
        _ => new VfpTypeInfo(VfpType.Numeric),
    };

    public override bool TryConstInt(out int value)
    {
        if (VfpRuntime.IsNumeric(_value)) { value = _value.AsInteger; return true; }
        value = 0;
        return false;
    }

    public override bool TryConstString(out string value)
    {
        if (_value.Type == VfpType.Character) { value = _value.AsString; return true; }
        value = string.Empty;
        return false;
    }
}

internal sealed partial class FieldNode : AstNode
{
    private readonly string _name;
    public FieldNode(string name) => _name = name;
    internal string Name => _name;

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => VfpRuntime.GetField(row, _name);

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.GetField), null,
            b.Row, Expression.Constant(_name));

    public override VfpTypeInfo Infer(ISchema schema)
    {
        if (schema.TryGetColumn(_name, out char type, out int length, out int decimals))
        {
            // DBF memo descriptors expose only their four-byte FPT pointer as a structural length.
            // Treat an expression reading M as unbounded; propagating C(4) or an arbitrary C(254)
            // would silently truncate STRTRAN/other string results, while promoting the expression
            // to an unpinned memo field would change the result contract. INTO therefore fails before
            // target creation, whereas direct SELECT evaluation can still return the actual value.
            if (char.ToUpperInvariant(type) == 'M')
                return new VfpTypeInfo(VfpType.Character, int.MaxValue, decimals);
            return new VfpTypeInfo(MapColumnType(type), length, decimals) { IsBinary = char.ToUpperInvariant(type) == 'Q' };
        }
        return new VfpTypeInfo(VfpType.Unknown);
    }

    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null) => into.Add(_name);

    private static VfpType MapColumnType(char t) => char.ToUpperInvariant(t) switch
    {
        'C' or 'M' or 'V' or 'Q' => VfpType.Character,
        'N' or 'F' or 'B' => VfpType.Numeric,
        'I' => VfpType.Integer,
        'Y' => VfpType.Currency,
        'D' => VfpType.Date,
        'T' or '@' => VfpType.DateTime,
        'L' => VfpType.Logical,
        _ => VfpType.Unknown,
    };
}

internal sealed partial class UnaryNode : AstNode
{
    private readonly UnOp _op;
    private readonly AstNode _operand;
    public UnaryNode(UnOp op, AstNode operand) { _op = op; _operand = operand; }

    internal override bool IsSqlCandidate(IRowContext row, EvaluationContext context, bool negated = false)
        => _op != UnOp.Not || _operand.IsSqlCandidate(row, context, !negated);

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => VfpRuntime.Unary(_op, _operand.Eval(row, ctx), ctx);

    internal override VfpValue Eval(
        IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver functionResolver)
        => VfpRuntime.Unary(_op, _operand.Eval(row, ctx, functionResolver), ctx);

    internal override bool ContainsFunction(Func<string, int, bool> predicate)
        => _operand.ContainsFunction(predicate);

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Unary), null,
            Expression.Constant(_op), _operand.Build(b), b.Ctx);

    public override VfpTypeInfo Infer(ISchema schema)
        => _op == UnOp.Not ? new VfpTypeInfo(VfpType.Logical, 1) : _operand.Infer(schema);

    public override bool TryConstInt(out int value)
    {
        if (_op != UnOp.Neg || !_operand.TryConstInt(out int operand))
        {
            value = 0;
            return false;
        }

        value = operand == int.MinValue ? int.MaxValue : -operand;
        return true;
    }

    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null) => _operand.CollectFields(into, skipFunction);
}

internal sealed partial class BinaryNode : AstNode
{
    private readonly BinOp _op;
    private readonly AstNode _left, _right;
    private readonly bool _constant;
    public BinaryNode(BinOp op, AstNode left, AstNode right)
    { _op = op; _left = left; _right = right; _constant = left.IsSqlConstant && right.IsSqlConstant; }

    internal override bool IsSqlCandidate(IRowContext row, EvaluationContext context, bool negated = false)
        // NOT (a OR b) has the same candidate intersection as NOT a AND NOT b.
        => _op != (negated ? BinOp.Or : BinOp.And) ||
            (_left.IsSqlCandidate(row, context, negated) && _right.IsSqlCandidate(row, context, negated));

    // AND / OR short-circuit like VFP9: a logical .F. on the left of AND (.T. on the left of OR) decides the
    // result and the right operand is NOT evaluated — no UDF call, no error from it (VFP9-verified:
    // `.F. AND NoSuchFn()` is .F., `.F. AND 1` is .F.). A .NULL. left operand still evaluates the right one.
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        var l = _left.Eval(row, ctx);
        if (_op is BinOp.And or BinOp.Or && VfpRuntime.Decides(_op, l)) return l;
        return VfpRuntime.Binary(_op, l, _right.Eval(row, ctx), ctx, IsSqlConstant);
    }

    internal override VfpValue Eval(
        IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver functionResolver)
    {
        var l = _left.Eval(row, ctx, functionResolver);
        if (_op is BinOp.And or BinOp.Or && VfpRuntime.Decides(_op, l)) return l;
        return VfpRuntime.Binary(_op, l, _right.Eval(row, ctx, functionResolver), ctx, IsSqlConstant);
    }

    internal override bool ContainsFunction(Func<string, int, bool> predicate)
        => _left.ContainsFunction(predicate) || _right.ContainsFunction(predicate);

    public override Expression Build(BuildContext b)
    {
        if (_op is not (BinOp.And or BinOp.Or))
            return Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Binary), null,
                Expression.Constant(_op), _left.Build(b), _right.Build(b), b.Ctx, Expression.Constant(IsSqlConstant));

        // Same short-circuit as Eval: the left value is computed once into a local, the right operand only
        // when the left one does not decide.
        var l = Expression.Variable(typeof(VfpValue), "l");
        return Expression.Block(typeof(VfpValue), new[] { l },
            Expression.Assign(l, _left.Build(b)),
            Expression.Condition(
                Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Decides), null, Expression.Constant(_op), l),
                l,
                Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Binary), null,
                    Expression.Constant(_op), l, _right.Build(b), b.Ctx, Expression.Constant(false))));
    }

    public override VfpTypeInfo Infer(ISchema schema)
    {
        switch (_op)
        {
            case BinOp.Eq: case BinOp.ExactEq: case BinOp.Ne:
            case BinOp.Lt: case BinOp.Le: case BinOp.Gt: case BinOp.Ge:
            case BinOp.Dollar: case BinOp.And: case BinOp.Or:
                return new VfpTypeInfo(VfpType.Logical, 1);
        }

        var l = _left.Infer(schema);
        var r = _right.Infer(schema);

        if (_op == BinOp.Add)
        {
            if (l.Type == VfpType.Character || r.Type == VfpType.Character)
                return new VfpTypeInfo(VfpType.Character, SaturatingLengthAdd(l.Length, r.Length)) { IsBinary = l.IsBinary };
            if (l.Type is VfpType.Date or VfpType.DateTime) return l;
            if (r.Type is VfpType.Date or VfpType.DateTime) return r;
            return new VfpTypeInfo(VfpType.Numeric);
        }
        if (_op == BinOp.Sub)
        {
            if (l.Type == VfpType.Character || r.Type == VfpType.Character)
                return new VfpTypeInfo(VfpType.Character, SaturatingLengthAdd(l.Length, r.Length));
            if (l.Type is VfpType.Date or VfpType.DateTime &&
                r.Type is VfpType.Date or VfpType.DateTime)
                return new VfpTypeInfo(VfpType.Numeric);
            if (l.Type is VfpType.Date or VfpType.DateTime) return l;
            return new VfpTypeInfo(VfpType.Numeric);
        }
        return new VfpTypeInfo(VfpType.Numeric);
    }

    private static int SaturatingLengthAdd(int left, int right)
    {
        long sum = (long)left + right;
        if (sum > int.MaxValue) return int.MaxValue;
        if (sum < int.MinValue) return int.MinValue;
        return (int)sum;
    }

    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
    {
        _left.CollectFields(into, skipFunction);
        _right.CollectFields(into, skipFunction);
    }
}

internal sealed partial class FunctionNode : AstNode
{
    private readonly string _name;
    // Uppercased ONCE at parse/build time so the per-record hot path never calls
    // ToUpperInvariant. Both the interpreter and the compiled delegate dispatch on this.
    private readonly string _upper;
    private readonly AstNode[] _args;
    public FunctionNode(string name, AstNode[] args)
    {
        _name = name;
        _upper = VfpFunctionNames.EvaluationName(name.ToUpperInvariant());
        // ICAS is VFP9's abbreviation of ICASE. ICASE is a lazy special form, so it is named here rather than
        // resolved after its arguments were evaluated (VfpRuntime's unknown-function path does that for others).
        if (_upper == "ICAS") _upper = "ICASE";
        _args = args;
    }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        // ICASE(c1,v1[,c2,v2…][,default]) is a LAZY special form (no hackfox entry — pinned to the VFP9
        // runtime): conditions are evaluated left→right, the FIRST true one's value is returned, and NOTHING
        // else is evaluated (unmatched conditions AND unmatched value expressions are skipped). No match with
        // an odd trailing arg ⇒ that default; no match with no default ⇒ .NULL. (NOT .F.). So it cannot go
        // through the eager arg-array path below.
        if (_upper == "ICASE") return EvalIcase(row, ctx);
        if (_upper == "IIF" && _args.Length == 3)
            return VfpRuntime.AsCondition(_args[0].Eval(row, ctx))
                ? _args[1].Eval(row, ctx)
                : _args[2].Eval(row, ctx);
        var values = new VfpValue[_args.Length];
        for (int i = 0; i < _args.Length; i++) values[i] = _args[i].Eval(row, ctx);
        return VfpRuntime.CallFunction(_upper, values, ctx, row);
    }

    internal override VfpValue Eval(
        IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver functionResolver)
    {
        var arguments = new ExpressionArgument[_args.Length];
        for (int i = 0; i < _args.Length; i++) arguments[i] = new ExpressionArgument(_args[i]);
        if (functionResolver(_upper, arguments, out var resolved)) return resolved;

        if (_upper == "ICASE") return EvalIcase(row, ctx, functionResolver);
        if (_upper == "IIF" && _args.Length == 3)
            return VfpRuntime.AsCondition(_args[0].Eval(row, ctx, functionResolver))
                ? _args[1].Eval(row, ctx, functionResolver)
                : _args[2].Eval(row, ctx, functionResolver);
        var values = new VfpValue[_args.Length];
        for (int i = 0; i < _args.Length; i++) values[i] = _args[i].Eval(row, ctx, functionResolver);
        return VfpRuntime.CallFunction(_upper, values, ctx, row);
    }

    internal override bool ContainsFunction(Func<string, int, bool> predicate)
    {
        if (predicate(_upper, _args.Length)) return true;
        foreach (var argument in _args)
            if (argument.ContainsFunction(predicate)) return true;
        return false;
    }

    private VfpValue EvalIcase(IRowContext row, EvaluationContext ctx)
    {
        int n = _args.Length;
        int i = 0;
        for (; i + 1 < n; i += 2)
            if (VfpRuntime.AsCondition(_args[i].Eval(row, ctx)))
                return _args[i + 1].Eval(row, ctx);
        return i < n ? _args[i].Eval(row, ctx) : VfpValue.Null;   // odd trailing arg = default; else .NULL.
    }

    private VfpValue EvalIcase(
        IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver functionResolver)
    {
        int n = _args.Length;
        int i = 0;
        for (; i + 1 < n; i += 2)
            if (VfpRuntime.AsCondition(_args[i].Eval(row, ctx, functionResolver)))
                return _args[i + 1].Eval(row, ctx, functionResolver);
        return i < n ? _args[i].Eval(row, ctx, functionResolver) : VfpValue.Null;
    }

    public override Expression Build(BuildContext b)
    {
        if (_upper == "ICASE") return BuildIcase(b);
        if (_upper == "IIF" && _args.Length == 3)
            return Expression.Condition(
                Expression.Call(
                    typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, _args[0].Build(b)),
                _args[1].Build(b),
                _args[2].Build(b));
        var elems = new Expression[_args.Length];
        for (int i = 0; i < _args.Length; i++) elems[i] = _args[i].Build(b);
        Expression array = Expression.NewArrayInit(typeof(VfpValue), elems);
        // Pass the already-uppercased name as a constant: zero string work per record.
        return Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.CallFunction), null,
            Expression.Constant(_upper), array, b.Ctx, b.Row);
    }

    // Compiled ICASE: nested Expression.Condition so only the taken branch (and the conditions up to the
    // first true one) are evaluated — same laziness as the tree-walk EvalIcase. Each condition expression is
    // built ONCE and passed through VfpRuntime.AsCondition (single evaluation, no double-eval of side effects).
    private Expression BuildIcase(BuildContext b)
    {
        int n = _args.Length;
        Expression acc;
        int pairEnd;
        if ((n & 1) == 1) { acc = _args[n - 1].Build(b); pairEnd = n - 1; }   // odd trailing arg = default
        else { acc = Expression.Constant(VfpValue.Null, typeof(VfpValue)); pairEnd = n; }
        for (int i = pairEnd - 2; i >= 0; i -= 2)
        {
            Expression isTrue = Expression.Call(
                typeof(VfpRuntime), nameof(VfpRuntime.AsCondition), null, _args[i].Build(b));
            acc = Expression.Condition(isTrue, _args[i + 1].Build(b), acc);
        }
        return acc;
    }

    public override VfpTypeInfo Infer(ISchema schema)
    {
        string n = VfpFunctionAbbreviations.Canonical(_upper);   // SUBS(…) is typed like SUBSTR(…).
        switch (n)
        {
            case "UPPER": case "LOWER": case "ALLTRIM":
            case "TRIM": case "RTRIM": case "LTRIM":
                return ArgInfer(schema, 0) is var a0 && a0.Type == VfpType.Character
                    ? a0 with { IsBinary = false } : new VfpTypeInfo(VfpType.Character, a0.Length);
            case "LEFT": case "RIGHT":
                // LEFT/RIGHT length is argument index 1 (LEFT(str, n)).
                return new VfpTypeInfo(VfpType.Character, LiteralLen(1, ArgInfer(schema, 0).Length)) { IsBinary = ArgInfer(schema, 0).IsBinary };
            case "SUBSTR":
                // SUBSTR length is argument index 2 (SUBSTR(str, start, n)).
                return new VfpTypeInfo(VfpType.Character, LiteralLen(2, ArgInfer(schema, 0).Length)) { IsBinary = ArgInfer(schema, 0).IsBinary };
            case "STR": return new VfpTypeInfo(VfpType.Character, ExpansionLength(1, 10));
            case "STRZERO": return new VfpTypeInfo(VfpType.Character, ExpansionLength(1, 10));
            case "PADL": case "PADR": case "PADC":
                return new VfpTypeInfo(VfpType.Character,
                    ExpansionLength(1, ArgInfer(schema, 0).Length));
            case "SPACE": return new VfpTypeInfo(VfpType.Character, ExpansionLength(0, 0));
            case "REPLICATE": return InferReplicate(schema);
            case "CHRTRAN": return ArgInfer(schema, 0) with { IsBinary = false };
            case "STUFF": return InferStuff(schema);
            case "STRTRAN": return InferStrtran(schema);
            case "DTOC": return new VfpTypeInfo(VfpType.Character, 10);
            case "DTOS": return new VfpTypeInfo(VfpType.Character, 8);
            case "TTOC": return new VfpTypeInfo(VfpType.Character, LiteralLen(1, 0) == 1 ? 14 : LiteralLen(1, 0) == 2 ? 8 : 22);
            case "CDOW": return new VfpTypeInfo(VfpType.Character, 9);
            case "CMONTH": return new VfpTypeInfo(VfpType.Character, 9);
            case "TIME": return new VfpTypeInfo(VfpType.Character, LiteralLen(0, 0) == 1 ? 11 : 8);
            case "CHR": return new VfpTypeInfo(VfpType.Character, 1);

            case "BINTOC":
            {
                string flags = TryConstString(1, out string format) ? format.ToUpperInvariant() : LiteralLen(1, 4).ToString();
                return new VfpTypeInfo(VfpType.Character, flags.Contains('8') || flags.Contains('B') ? 8
                    : flags.Contains('F') ? 4 : flags.Contains('2') ? 2 : flags.Contains('1') ? 1 : 4);
            }
            case "ANSITOOEM": case "OEMTOANSI": return ArgInfer(schema, 0) with { IsBinary = false };
            case "CREATEBINARY": return new VfpTypeInfo(VfpType.Character, ArgInfer(schema, 0).Length) { IsBinary = true };
            case "DMY": case "MDY": case "ADDBS": case "DEFAULTEXT": case "FORCEEXT": case "FORCEPATH":
            case "JUSTDRIVE": case "JUSTEXT": case "JUSTFNAME": case "JUSTPATH": case "JUSTSTEM":
                return new VfpTypeInfo(VfpType.Character);
            case "ATN2": case "LOG10":
            case "ACOS": case "ASIN": case "ATAN": case "BITAND": case "BITCLEAR": case "BITLSHIFT": case "BITNOT": case "BITOR": case "BITRSHIFT": case "BITSET": case "BITXOR": case "CEILING": case "COS": case "CTOBIN": case "DTOR": case "EXP": case "FLOOR": case "FV": case "HOUR": case "LOG": case "MINUTE": case "MTON": case "NTOM": case "PAYMENT": case "PI": case "PV": case "QUARTER": case "RGB": case "RTOD": case "SEC": case "SIGN": case "SIN": case "SQRT": case "TAN": case "WEEK":
            case "RAND": case "FLOAT": case "FIXED":
            case "VAL": case "LEN": case "AT": case "RAT": case "ASC":
            case "INT": case "ROUND": case "ABS": case "MOD":
            case "DAY": case "MONTH": case "YEAR": case "DOW":
            case "RECNO": case "RECCOUNT":
                return new VfpTypeInfo(VfpType.Numeric);
            case "MAX": case "MIN": return ArgInfer(schema, 0);

            case "TTOD":
            case "CTOD": return new VfpTypeInfo(VfpType.Date, 8);
            case "DATE": return new VfpTypeInfo(VfpType.Date, 8);
            case "GOMONTH": return ArgInfer(schema, 0);
            case "CTOT": case "DTOT": case "DATETIME": return new VfpTypeInfo(VfpType.DateTime, 8);

            case "UNPACK": case "BITTEST":
            case "EMPTY": case "ISNULL": case "DELETED":
            case "BETWEEN": case "INLIST":
                return new VfpTypeInfo(VfpType.Logical, 1);

            case "NVL": case "EVL": return ArgInfer(schema, 0).Type is VfpType.Null or VfpType.Unknown ? ArgInfer(schema, 1) : ArgInfer(schema, 0);
            case "IIF":
            {
                var first = ArgInfer(schema, 1);
                var second = ArgInfer(schema, 2);
                // Index storage must accommodate either character branch. SQL has its own
                // first-row/selected-branch sizing in InferSql and deliberately does not use this.
                return first.Type == VfpType.Character && second.Type == VfpType.Character
                    ? new VfpTypeInfo(first.Type, Math.Max(first.Length, second.Length), first.Decimals) { IsBinary = first.IsBinary }
                    : first;
            }
            case "ICASE": return ArgInfer(schema, 1);   // result type ≈ the first pair's value expression
            default: return new VfpTypeInfo(VfpType.Unknown);
        }
    }

    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
    {
        if (skipFunction?.Invoke(_upper, _args.Length) == true) return;
        foreach (var a in _args) a.CollectFields(into, skipFunction);
    }

    private VfpTypeInfo ArgInfer(ISchema schema, int i)
        => i < _args.Length ? _args[i].Infer(schema) : new VfpTypeInfo(VfpType.Unknown);

    private int LiteralLen(int argIndex, int fallback)
        => argIndex < _args.Length && _args[argIndex].TryConstInt(out int v) ? v : fallback;

    private int ExpansionLength(int argIndex, int fallback)
    {
        if (argIndex >= _args.Length) return fallback;
        if (!_args[argIndex].TryConstInt(out int length)) return UnknownCharacterLength;
        return NonNegativeLength(length);
    }

    private VfpTypeInfo InferReplicate(ISchema schema)
    {
        if (_args.Length < 2) return new VfpTypeInfo(VfpType.Character, 0);
        if (!_args[1].TryConstInt(out int count))
            return new VfpTypeInfo(VfpType.Character, UnknownCharacterLength);

        int sourceLength = ArgInfer(schema, 0).Length;
        return new VfpTypeInfo(VfpType.Character,
            SaturatingLengthMultiply(NonNegativeLength(sourceLength), NonNegativeLength(count)));
    }

    private VfpTypeInfo InferStuff(ISchema schema)
    {
        int sourceLength = CharacterOperandLength(schema, 0);
        int replacementLength = _args.Length < 4 ? 0 : CharacterOperandLength(schema, 3);

        if (TryConstString(0, out string source) &&
            TryConstString(3, out string replacement) &&
            _args.Length > 2 &&
            _args[1].TryConstInt(out int start) &&
            _args[2].TryConstInt(out int length))
        {
            return new VfpTypeInfo(VfpType.Character,
                ExactStuffLength(source, start, length, replacement));
        }

        return new VfpTypeInfo(VfpType.Character,
            SaturatingLengthAdd(sourceLength, replacementLength));
    }

    private int CharacterOperandLength(ISchema schema, int index)
    {
        if (index >= _args.Length) return 0;
        if (_args[index].TryConstString(out string value)) return value.Length;
        var info = ArgInfer(schema, index);
        return info.Type == VfpType.Character ? info.Length : UnknownCharacterLength;
    }

    private static int ExactStuffLength(string source, int start, int length, string replacement)
    {
        long i = (long)start - 1;
        if (i < 0) i = 0;
        if (i > source.Length) i = source.Length;

        int removed = NonNegativeLength(length);
        long end = i + removed;
        if (end > source.Length) removed = source.Length - (int)i;

        return SaturatingLengthAdd(source.Length - removed, replacement.Length);
    }

    private static int SaturatingLengthAdd(int left, int right)
    {
        long sum = (long)left + right;
        if (sum >= int.MaxValue) return int.MaxValue;
        if (sum <= int.MinValue) return int.MinValue;
        return (int)sum;
    }

    private static int NonNegativeLength(int value) => value < 0 ? 0 : value;

    private static int SaturatingLengthMultiply(int length, int multiplier)
    {
        long product = (long)length * multiplier;
        if (product > int.MaxValue) return int.MaxValue;
        if (product < int.MinValue) return int.MinValue;
        return (int)product;
    }

    // VFP character fields are capped at 254 bytes, but inference must retain widths above that
    // threshold so the SQL materializer can reject them before creating a target. Never wrap a
    // computed width back to a small/zero value when a dynamic operand can expand the result.
    private const int UnknownCharacterLength = int.MaxValue;

    private VfpTypeInfo InferStrtran(ISchema schema)
    {
        bool sourceLiteral = TryConstString(0, out string sourceText);
        bool soughtLiteral = TryConstString(1, out string soughtText);
        string replacementText = string.Empty;
        bool replacementLiteral = _args.Length < 3 || TryConstString(2, out replacementText);

        int sourceLength = sourceLiteral
            ? sourceText.Length
            : DynamicCharacterLength(ArgInfer(schema, 0));
        int soughtLength = soughtLiteral ? soughtText.Length : 1; // a dynamic search can be one char.
        int replacementLength = _args.Length < 3
            ? 0
            : replacementLiteral
                ? replacementText.Length
                : DynamicCharacterLength(ArgInfer(schema, 2));

        if (sourceLiteral && soughtLiteral && replacementLiteral)
        {
            int exact = soughtLength == 0
                ? sourceLength
                : sourceText.Replace(soughtText, replacementText, StringComparison.Ordinal).Length;
            return new VfpTypeInfo(VfpType.Character, AtLeastOne(exact));
        }

        return new VfpTypeInfo(VfpType.Character,
            AtLeastOne(ConservativeStrtranLength(sourceLength, soughtLength, replacementLength)));
    }

    private bool TryConstString(int index, out string value)
    {
        if (index < _args.Length) return _args[index].TryConstString(out value);
        value = string.Empty;
        return false;
    }

    private static int DynamicCharacterLength(VfpTypeInfo info)
        => info.Type == VfpType.Character && info.Length > 0 ? info.Length : UnknownCharacterLength;

    private static int ConservativeStrtranLength(int sourceLength, int soughtLength, int replacementLength)
    {
        if (sourceLength <= 0 || soughtLength <= 0 || replacementLength <= soughtLength)
            return Math.Max(0, sourceLength);

        long occurrences = sourceLength / (long)soughtLength;
        long result = sourceLength + occurrences * (replacementLength - (long)soughtLength);
        return result >= int.MaxValue ? int.MaxValue : (int)result;
    }

    private static int AtLeastOne(int length) => length > 0 ? length : 1;
}

// SQL owns DISTINCT's grouping semantics; this marker preserves the argument's ordinary value and type.
internal sealed class SqlDistinctNode(AstNode argument) : AstNode
{
    public override VfpValue Eval(IRowContext row, EvaluationContext ctx) => argument.Eval(row, ctx);
    public override Expression Build(BuildContext context) => argument.Build(context);
    public override VfpTypeInfo Infer(ISchema schema) => argument.Infer(schema);
    internal override VfpTypeInfo InferSql(ISchema schema) => argument.InferSql(schema);
    internal override bool ContainsFunction(Func<string, int, bool> predicate) => argument.ContainsFunction(predicate);
    public override void CollectFields(ICollection<string> into, Func<string, int, bool>? skipFunction = null)
        => argument.CollectFields(into, skipFunction);
}
