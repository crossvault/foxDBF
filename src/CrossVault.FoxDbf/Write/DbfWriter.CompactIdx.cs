using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

public sealed partial class DbfWriter
{
    private readonly Dictionary<string, EvaluationContext?> _openCompactIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keep this standalone compact IDX current on append, update, delete/recall and PACK/ZAP.
    /// Opening an IdxFile reader alone does not register it for writing. The registration lasts until
    /// CloseCompactIdx or disposal; unsupported definitions are refused before changing table data.</summary>
    public void OpenCompactIdx(string path, EvaluationContext? evalContext = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string full = Path.GetFullPath(path);
        ReadCompactIndexPlan(full, evalContext);
        _openCompactIndexes[full] = evalContext;
    }

    /// <summary>Stop maintaining a standalone IDX, as SET INDEX TO does when closing it.</summary>
    public void CloseCompactIdx(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _openCompactIndexes.Remove(Path.GetFullPath(path));
    }

    internal void SetOpenAdditionalIndexes(IEnumerable<string> paths, EvaluationContext context)
    {
        if (paths is ICollection<string> { Count: 0 } && !HasAdditionalIndexes) return;
        var wanted = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string old in _additionalIndexPlans.Keys.ToArray())
            if (!wanted.Contains(old) || !_openAdditionalIndexes.TryGetValue(old, out var prior) || !ReferenceEquals(prior, context))
                _additionalIndexPlans.Remove(old);
        _openCompactIndexes.Clear();
        _openAdditionalIndexes.Clear();
        foreach (string path in wanted)
        {
            if (Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                using var idx = IdxFile.Open(path, _schema.Encoding);
                if (idx.Header.IsCompact)
                {
                    _openCompactIndexes[Path.GetFullPath(path)] = context;
                    continue;
                }
            }
            _openAdditionalIndexes[Path.GetFullPath(path)] = context;
        }
    }

    private (CdxTagDefinition Definition, CdxIndexBuilder.TagKeyComputer Computer) ReadCompactIndexPlan(
        string path, EvaluationContext? context)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var file = IndexFile.Open(stream, leaveOpen: true);
        file.TextEncoding = _schema.Encoding;
        var header = file.ReadCdxHeader(0);
        bool Supported(string? text) => string.IsNullOrWhiteSpace(text) ||
            (FieldsResolve(text, context) && (context?.IndexEvaluator is not null || !VfpExpression.Parse(text).ContainsFunction((name, _) =>
                !VfpRuntime.EngineFunctionNames.Contains(VfpFunctionAbbreviations.Canonical(name)))));
        if ((header.Options & 0xE0) != 0x20 || !Supported(header.KeyExpression) || !Supported(header.ForExpression)
            || VfpCollations.Resolve(header.SortOrder, _schema.Encoding.CodePage, out _) != CollationSupport.Exact)
        {
            string? function = VfpRuntime.UnsupportedIndexFunction(header.KeyExpression)
                ?? VfpRuntime.UnsupportedIndexFunction(header.ForExpression);
            throw new DbfWriteException($"Cannot maintain compact index '{path}' (KEY '{header.KeyExpression}', FOR '{header.ForExpression}', collation '{header.SortOrder}', code page {_schema.Encoding.CodePage})."
                + (function is null ? "" : $" Function {function}() is not supported by the index engine."));
        }
        var def = new CdxTagDefinition(Path.GetFileNameWithoutExtension(path), header.KeyExpression,
            header.HasFor ? header.ForExpression : null, header.Descending, header.SortOrder, header.IsUnique);
        var computer = CdxIndexBuilder.CreateKeyComputer(_schema, def, context);
        if (computer.KeyLen != header.KeyLength || computer.KeyType == IndexKeyType.Unknown)
            throw new DbfWriteException($"Cannot derive the stored key of compact index '{path}' (collation '{header.SortOrder}', code page {_schema.Encoding.CodePage}).");
        computer.ValidateHost?.Invoke();
        return (def, computer);
    }

    private bool SkipUnavailableCompactIndex(string path, EvaluationContext? context)
    {
        if (context?.SkipUnavailableIndexExpression is not { } skip) return false;
        using var index = IdxFile.Open(path, _schema);
        return skip(index.Header.KeyExpression) || skip(index.Header.ForExpression);
    }

    private void ValidateCompactIndexes()
    {
        ValidateAdditionalIndexes();
        foreach (var (path, context) in _openCompactIndexes)
            if (!SkipUnavailableCompactIndex(path, context)) ReadCompactIndexPlan(path, context);
    }

    private void MaintainCompactIndexes(int recNo, DbfRecord? before, DbfRecord after)
    {
        MaintainAdditionalIndexes(recNo, before, after);
        foreach (var (path, context) in _openCompactIndexes)
        {
            if (SkipUnavailableCompactIndex(path, context)) continue;
            var (_, computer) = ReadCompactIndexPlan(path, context);
            byte[] oldKey = [];
            bool had = before is { } old && computer.TryComputeKey(old, recNo, _recordCount, out oldKey);
            bool has = computer.TryComputeKey(after, recNo, _recordCount, out var newKey);
            if (had == has && (!had || oldKey.AsSpan().SequenceEqual(newKey)))
                continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using var editor = new CdxTreeEditor(stream, _schema.Encoding);
            // A compact IDX is one CDX-format tag whose header is at offset zero.
            if (had) editor.Delete(0, oldKey, (uint)recNo, computer.KeyLen, computer.Pad);
            if (has && (!computer.Unique || !editor.ContainsKey(0, newKey, computer.KeyLen, computer.Pad)))
                editor.Insert(0, newKey, (uint)recNo, computer.KeyLen, computer.Pad, computer.IsChar && computer.Collation is GeneralCollation { BlankHead: 0x11 });
            editor.Flush();
        }
    }

    private void RebuildCompactIndexes()
    {
        RebuildAdditionalIndexes();
        if (_openCompactIndexes.Count == 0) return;
        var rows = MaterializeRows();
        foreach (var (path, context) in _openCompactIndexes)
        {
            var (definition, _) = ReadCompactIndexPlan(path, context);
            CdxIndexBuilder.BuildCompactIdx(path, _schema, rows, definition, context, includeDeleted: true);
        }
    }
}
