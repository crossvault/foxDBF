using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>Static result of <see cref="VfpExpression.InferType"/>: kind + length (+ decimals).</summary>
public readonly struct VfpTypeInfo : IEquatable<VfpTypeInfo>
{
    public VfpType Type { get; }
    public int Length { get; }
    public int Decimals { get; }
    // F and B share Numeric in the expression runtime but remain distinct SQL cursor types.
    internal char SqlStorageType { get; }

    public VfpTypeInfo(VfpType type, int length = 0, int decimals = 0)
    {
        Type = type; Length = length; Decimals = decimals;
    }

    internal VfpTypeInfo(VfpType type, int length, int decimals, char sqlStorageType)
        : this(type, length, decimals) => SqlStorageType = sqlStorageType;

    public bool Equals(VfpTypeInfo other)
        => Type == other.Type && Length == other.Length && Decimals == other.Decimals;
    public override bool Equals(object? obj) => obj is VfpTypeInfo o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Type, Length, Decimals);
    public override string ToString() => $"{Type}({Length},{Decimals})";
}

/// <summary>An opaque AST argument exposed only to internal expression function resolvers.</summary>
internal readonly struct ExpressionArgument
{
    private readonly AstNode _node;
    internal ExpressionArgument(AstNode node) => _node = node;
    internal VfpValue Evaluate(IRowContext row, EvaluationContext context) => _node.Eval(row, context);
}

/// <summary>Internal hook for hosts that need to replace selected function calls during tree evaluation.</summary>
internal delegate bool ExpressionFunctionResolver(
    string upperName, IReadOnlyList<ExpressionArgument> arguments, out VfpValue result);

/// <summary>
/// A parsed VFP/xBase expression. <see cref="Parse"/> builds the immutable AST;
/// <see cref="Compile"/> lowers it to a fast delegate via <c>System.Linq.Expressions</c>,
/// <see cref="Evaluate"/> tree-walks the AST, and <see cref="InferType"/> derives the
/// static result type from a schema.
/// </summary>
public sealed class VfpExpression
{
    /// <summary>The original source text.</summary>
    public string Text { get; }

    private readonly AstNode _root;

    private VfpExpression(string text, AstNode root) { Text = text; _root = root; }

    /// <summary>
    /// Parses <paramref name="text"/> into an immutable expression. Throws
    /// <see cref="ExpressionException"/> on malformed input.
    /// </summary>
    public static VfpExpression Parse(string text)
    {
        if (text is null) throw new ExpressionException("Expression text is null.");
        var root = ExpressionParser.Parse(text);
        return new VfpExpression(text, root);
    }

    /// <summary>Tree-walking interpreter. Never throws on bad/null data.</summary>
    public VfpValue Evaluate(IRowContext row, EvaluationContext? context = null)
        => _root.Eval(row, context ?? EvaluationContext.Default);

    internal bool ContainsFunction(Func<string, int, bool> predicate)
        => _root.ContainsFunction(predicate);

    internal VfpValue EvaluateWithFunctionResolver(
        IRowContext row, EvaluationContext context, ExpressionFunctionResolver functionResolver)
        => _root.Eval(row, context, functionResolver);

    /// <summary>
    /// Compiles the AST to a delegate over <see cref="IRowContext"/>. The context
    /// (SET EXACT / collation) is captured at compile time.
    /// </summary>
    public Func<IRowContext, VfpValue> Compile(EvaluationContext? context = null)
        => Compile(context, preferInterpretation: false);

    /// <summary>
    /// Compiles for AOT using <c>Expression.Compile(preferInterpretation: true)</c>.
    /// </summary>
    public Func<IRowContext, VfpValue> Compile(EvaluationContext? context, bool preferInterpretation)
    {
        var ctx = context ?? EvaluationContext.Default;
        var rowParam = Expression.Parameter(typeof(IRowContext), "row");
        var build = new BuildContext
        {
            Row = rowParam,
            Ctx = Expression.Constant(ctx, typeof(EvaluationContext)),
        };
        var body = _root.Build(build);
        var lambda = Expression.Lambda<Func<IRowContext, VfpValue>>(body, rowParam);
        return lambda.Compile(preferInterpretation);
    }

    /// <summary>Static type inference against a schema.</summary>
    public VfpTypeInfo InferType(ISchema schema)
        => _root.Infer(schema);

    internal VfpTypeInfo InferSqlType(ISchema schema) => _root.InferSql(schema);

    internal VfpTypeInfo InferSqlType(ISchema schema, IRowContext row, EvaluationContext context)
        => _root.InferSql(new SqlInferenceSchema(schema, row, context));

    internal static VfpExpression ParseSql(string text)
    {
        var (root, normalized) = ExpressionParser.ParseSql(text);
        return new VfpExpression(normalized, root);
    }

    /// <summary>
    /// Every distinct FIELD name this expression reads (case-insensitive; function names and literals
    /// excluded). Lets a host verify that a KEY/FOR expression only references columns that actually exist
    /// in a given physical schema — e.g. the write-path index maintenance rejects a tag whose expression
    /// names a field absent from the raw <c>.dbf</c> (a DBC long name) rather than derive wrong key bytes.
    /// </summary>
    public IReadOnlyCollection<string> ReferencedFields()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _root.CollectFields(set);
        return set;
    }
}
