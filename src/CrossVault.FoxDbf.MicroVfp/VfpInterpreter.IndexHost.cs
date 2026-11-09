using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    private IRowContext? _indexRow;

    private void ValidateIndexFunction(string name)
    {
        if (!VfpFunctionNames.IsBuiltin(name) && !_procs.ContainsKey(name) && Memory.FindArray(name) is null)
            UnknownFunction(name);
    }

    private void ResolveHostedTag(Index.CdxTag tag, DbfTable table)
    {
        if (tag.NeedsHostResolution)
            tag.ResolveHostedKey(Write.CdxIndexBuilder.ResolveKey(table, tag.KeyExpression, _ctx).Type);
    }

    // A writer may be evaluating the old or proposed row, or rebuilding while its work area is closed.
    // UDF field reads and RECNO must see that exact row, never the interpreter's navigation cursor.
    private VfpValue EvaluateIndex(VfpExpression expression, IRowContext row, EvaluationContext context)
    {
        var saved = _indexRow;
        _indexRow = row;
        try
        {
            var normalized = ParseExpressionCached(MicroVfpExprRewrite.Normalize(expression.Text));
            return normalized.Evaluate(new IndexHostRow(this, row), context);
        }
        finally { _indexRow = saved; }
    }

    private sealed class IndexHostRow(VfpInterpreter interpreter, IRowContext row) : IRowContext, IVfpFunctionHost
    {
        public object? GetField(string name) => interpreter.ResolveName(name);
        public int RecNo => row.RecNo;
        public int RecCount => row.RecCount;
        public bool Deleted => row.Deleted;
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue result)
            => interpreter.HostInvoke(name, args, context, out result);
        public VfpValue UnknownFunction(string name, VfpValue[] args) => interpreter.UnknownFunction(name);
    }
}
