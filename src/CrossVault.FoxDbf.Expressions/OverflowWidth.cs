using System;
using System.Globalization;

namespace CrossVault.FoxDbf.Expressions;

// Round8 and TransformWidthVfp9OracleTests: expression width and memory-variable
// width differ. Literals are cached at parse time; descriptors are constructed
// on overflow, or before a UDF can mutate the operands (SnapshotOverflowNode).
internal readonly record struct OverflowShape(int Width, int Scale);

internal abstract partial class AstNode
{
    internal int SelectionCount { get; init; }
    internal virtual bool ShapeReadsVariable => false;
    internal virtual bool ShapeNeedsSnapshot => false;
    internal virtual OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
        => default;
    internal virtual VfpValue EvalTracked(IRowContext row, EvaluationContext ctx,
        ExpressionFunctionResolver? resolver, Span<int> selections)
        => resolver is null ? Eval(row, ctx) : Eval(row, ctx, resolver);
}

internal sealed partial class LiteralNode
{
    private readonly OverflowShape _overflowShape;
    private static OverflowShape LiteralOverflowShape(VfpValue value)
    {
        if (!VfpRuntime.IsNumeric(value) || !double.IsFinite(value.AsDouble)) return new(10, 0);
        string text = value.AsNumber.ToString(CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        return new(text.Length, dot < 0 ? 0 : text.Length - dot - 1);
    }
    internal override OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
        => _overflowShape;
}

internal sealed partial class FieldNode
{
    internal override bool ShapeReadsVariable => true;
    internal override OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
    {
        if (row is ISchema schema && schema.TryGetColumn(_name, out char type, out int width, out int scale))
            return new(type == 'I' ? 11 : type == 'B' ? 21 : width, scale);
        // Memory-store reads happen only on this cold path; finite values carry no width.
        var value = sample.Type == VfpType.Unknown ? VfpRuntime.GetField(row, _name) : sample;
        int decimals = Math.Max(0, value.DisplayDecimals >= 0 ? value.DisplayDecimals : LiteralNode.Precision(value));
        return value.OverflowDisplayWidth > 0
            ? new(value.OverflowDisplayWidth, Math.Max(decimals, value.OverflowShapeScale))
            : new(VfpRuntime.NumericOverflowWidth(decimals), decimals);
    }
}

internal sealed partial class UnaryNode
{
    internal override bool ShapeReadsVariable => _operand.ShapeReadsVariable;
    internal override bool ShapeNeedsSnapshot => _operand.ShapeNeedsSnapshot;
    internal override OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
    {
        var shape = _operand.OverflowShape(row, ctx, selections);
        return shape.Width == 0 ? shape : shape with { Width = shape.Width + 1 };
    }
    internal override VfpValue EvalTracked(IRowContext row, EvaluationContext ctx,
        ExpressionFunctionResolver? resolver, Span<int> selections)
        => VfpRuntime.Unary(_op, _operand.EvalTracked(row, ctx, resolver, selections), ctx, HasQuotient);
}

internal partial class BinaryNode
{
    internal static BinaryNode Create(BinOp op, AstNode left, AstNode right)
    {
        if (op != BinOp.Div && !(op is BinOp.Add or BinOp.Sub or BinOp.Mul && (left.HasQuotient || right.HasQuotient)))
            return new BinaryNode(op, left, right);
        if (NeedsSnapshot(left, right)) return new SnapshotOverflowNode(op, left, right);
        if (left is FunctionNode selection && selection.CanMapSelectedValues)
            return selection.IsIif ? new IifOverflowNode(op, selection, right) : new SelectedOverflowNode(op, selection, right);
        return new OverflowBinaryNode(op, left, right);
    }

    internal override bool ShapeReadsVariable => _left.ShapeReadsVariable || _right.ShapeReadsVariable;
    internal override bool ShapeNeedsSnapshot => NeedsSnapshot(_left, _right);

    private static bool NeedsSnapshot(AstNode left, AstNode right)
        => left.ShapeNeedsSnapshot || right.ShapeNeedsSnapshot
            || left.ShapeReadsVariable && right.ContainsFunction(MayMutate);

    internal static bool MayMutate(string name, int arity)
        // These compatibility spellings are implemented by the engine, not UDF calls.
        => name is "EVALUATE" or "EVAL" || !VfpRuntime.EngineFunctionNames.Contains(name) && !VfpFunctionNames.IsBuiltin(name);

    // A stack-local record of branch choices belongs to THIS evaluation, never to an
    // AST node or a shared EvaluationContext. Only branch-bearing quotients need it.
    public VfpValue EvalWithSelections(IRowContext row, EvaluationContext ctx, ExpressionFunctionResolver? resolver)
    {
        Span<int> selections = SelectionCount <= 256 ? stackalloc int[SelectionCount] : new int[SelectionCount];
        return EvalTracked(row, ctx, resolver, selections);
    }

    internal override VfpValue EvalTracked(IRowContext row, EvaluationContext ctx,
        ExpressionFunctionResolver? resolver, Span<int> selections)
    {
        var l = _left.EvalTracked(row, ctx, resolver, selections[.._left.SelectionCount]);
        if (_op is BinOp.And or BinOp.Or && VfpRuntime.Decides(_op, l)) return l;
        var r = _right.EvalTracked(row, ctx, resolver, selections[_left.SelectionCount..]);
        if (this is OverflowBinaryNode overflow) return overflow.ApplySelected(l, r, row, ctx, selections);
        var value = VfpRuntime.Binary(_op, l, r, ctx, IsSqlConstant);
        return HasQuotient && value.Type == VfpType.Numeric && !double.IsFinite(value.AsDouble)
            ? FormatOverflow(value, row, ctx, selections) : value;
    }

    internal VfpValue FormatOverflow(VfpValue value,
        IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue left = default, VfpValue right = default)
    {
        var l = _left.OverflowShape(row, ctx, selections.IsEmpty ? default : selections[.._left.SelectionCount], left);
        var r = _right.OverflowShape(row, ctx, selections.IsEmpty ? default : selections[_left.SelectionCount..], right);
        return FormatOverflow(value, ctx, l, r);
    }

    internal VfpValue FormatOverflow(VfpValue value, EvaluationContext ctx, OverflowShape l, OverflowShape r)
    {
        var shape = CombineShape(l, r, ctx);
        if (shape.Width == 0) return value;
        int stored = value.OverflowDisplayWidth;
        if (_op == BinOp.Div)
        {
            // Assignment retains at least the memory numeric width and its sign.
            var dividend = l;
            stored = VfpRuntime.NumericOverflowWidth(Math.Min(18, Math.Max(ctx.NumericDecimals + 2, dividend.Scale)))
                + (value.AsDouble < 0 ? 1 : 0);
        }
        return value.WithOverflowWidths(Math.Max(stored, shape.Width), shape.Width, shape.Scale);
    }

    internal override OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
    {
        var l = _left.OverflowShape(row, ctx, selections.IsEmpty ? default : selections[.._left.SelectionCount]);
        var r = _right.OverflowShape(row, ctx, selections.IsEmpty ? default : selections[_left.SelectionCount..]);
        return CombineShape(l, r, ctx);
    }

    internal OverflowShape CombineShape(OverflowShape l, OverflowShape r, EvaluationContext ctx)
    {
        if (l.Width == 0 || r.Width == 0) return default;
        int scale = Math.Max(l.Scale, r.Scale);
        return _op switch
        {
            BinOp.Mul => new(l.Width + r.Width + (l.Scale > 0 && r.Scale > 0 ? 0 : 1), l.Scale + r.Scale),
            BinOp.Div => new(l.Width + r.Width - l.Scale - r.Scale + Math.Min(18, Math.Max(ctx.NumericDecimals + 2, scale))
                - (l.Scale > 0 || r.Scale > 0 ? 1 : 0), Math.Min(18, Math.Max(ctx.NumericDecimals + 2, scale))),
            BinOp.Mod => new(l.Width + 1, l.Scale),
            BinOp.Add or BinOp.Sub => new(Math.Max(l.Width - l.Scale, r.Width - r.Scale
                + (_op == BinOp.Sub && IsSqlConstant ? 1 : 0)) + scale + 1, scale),
            _ => default
        };
    }
}

internal sealed partial class FunctionNode
{
    private bool SelectsValue => _upper is "IIF" or "ICASE" or "NVL" or "EVL";
    private bool HasMeasuredShape => SelectsValue || VfpFunctionAbbreviations.Canonical(_upper) is "INT" or "ROUND" or "ABS";
    internal override bool ShapeReadsVariable => HasMeasuredShape && Array.Exists(_args, a => a.ShapeReadsVariable);
    internal override bool ShapeNeedsSnapshot
    {
        get
        {
            if (!HasMeasuredShape) return false;
            bool earlierVariable = false;
            for (int i = 0; i < _args.Length; i++)
            {
                // IIF/ICASE conditions precede the selected value; unselected values
                // cannot mutate it. Eager NVL/EVL and ROUND retain argument order.
                if (_upper == "IIF" && i == 0 || _upper == "ICASE" && i % 2 == 0 && i + 1 < _args.Length) continue;
                if (_args[i].ShapeNeedsSnapshot || earlierVariable && _args[i].ContainsFunction(BinaryNode.MayMutate)) return true;
                if (_upper is not ("IIF" or "ICASE")) earlierVariable |= _args[i].ShapeReadsVariable;
            }
            return false;
        }
    }

    private readonly int[] _selectionOffsets;
    private readonly ExpressionArgument[] _resolverArguments;
    private int SelectionOffset(int argument) => _selectionOffsets[argument];

    internal override VfpValue EvalTracked(IRowContext row, EvaluationContext ctx,
        ExpressionFunctionResolver? resolver, Span<int> selections)
    {
        if (SelectionCount == 0) return base.EvalTracked(row, ctx, resolver, selections);
        if (resolver is not null)
        {
            if (resolver(_upper, _resolverArguments, out var resolved)) return resolved;
        }
        if (_upper == "IIF" && _args.Length == 3)
        {
            int selected = VfpRuntime.AsCondition(TrackedArgument(0, row, ctx, resolver, selections)) ? 1 : 2;
            selections[0] = selected;
            return TrackedArgument(selected, row, ctx, resolver, selections);
        }
        if (_upper == "ICASE")
        {
            int i = 0;
            for (; i + 1 < _args.Length; i += 2)
                if (VfpRuntime.AsCondition(TrackedArgument(i, row, ctx, resolver, selections)))
                {
                    selections[0] = i + 1;
                    return TrackedArgument(i + 1, row, ctx, resolver, selections);
                }
            selections[0] = i;
            return i < _args.Length ? TrackedArgument(i, row, ctx, resolver, selections) : VfpValue.Null;
        }
        var values = new VfpValue[_args.Length];
        for (int i = 0; i < values.Length; i++) values[i] = TrackedArgument(i, row, ctx, resolver, selections);
        if (_upper == "NVL" && values.Length == 2) selections[0] = values[0].IsNull ? 1 : 0;
        if (_upper == "EVL" && values.Length == 2) selections[0] = VfpRuntime.Empty(values[0], ctx).AsLogical && !values[0].IsNull ? 1 : 0;
        PrepareOverflowFormat(values, row, ctx);
        return VfpRuntime.CallFunction(_upper, values, ctx, row);
    }

    private VfpValue TrackedArgument(int index, IRowContext row, EvaluationContext ctx,
        ExpressionFunctionResolver? resolver, Span<int> selections)
        => _args[index].EvalTracked(row, ctx, resolver, selections.Slice(SelectionOffset(index), _args[index].SelectionCount));

    internal override OverflowShape OverflowShape(IRowContext row, EvaluationContext ctx, ReadOnlySpan<int> selections, VfpValue sample = default)
    {
        if (SelectsValue && selections.IsEmpty) return default;
        int index = SelectsValue ? selections[0] : 0;
        if (index >= _args.Length) return default;
        var arg = _args[index].OverflowShape(row, ctx,
            selections.IsEmpty ? default : selections.Slice(SelectionOffset(index), _args[index].SelectionCount), SelectsValue ? sample : default);
        return FunctionShape(arg);
    }

    private OverflowShape FunctionShape(OverflowShape arg)
    {
        if (SelectsValue || arg.Width == 0) return arg;
        switch (VfpFunctionAbbreviations.Canonical(_upper))
        {
            case "INT": return arg with { Scale = 0 };
            case "ROUND":
                if (!_args[1].TryConstInt(out int d)) return default;
                int digits = Math.Max(0, d);
                return new(arg.Width + 1 + Math.Max(0, digits - arg.Scale), digits);
            // VFP ABS on an integer-scale operand produces a memory numeric.
            case "ABS": return arg.Scale == 0 ? new(10, 0) : arg;
            // No guessed descriptors for functions outside the measured rules.
            default: return default;
        }
    }
}
