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

    internal void SetOpenCompactIndexes(IEnumerable<string> paths, EvaluationContext context)
    {
        _openCompactIndexes.Clear();
        foreach (string path in paths)
        {
            using var idx = IdxFile.Open(path, _schema.Encoding);
            if (idx.Header.IsCompact)
                _openCompactIndexes[Path.GetFullPath(path)] = context;
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
            (FieldsResolve(text) && !VfpExpression.Parse(text).ContainsFunction((name, _) =>
                !VfpRuntime.EngineFunctionNames.Contains(name)));
        if ((header.Options & 0xE0) != 0x20 || !Supported(header.KeyExpression) || !Supported(header.ForExpression)
            || VfpCollations.Resolve(header.SortOrder, _schema.Encoding.CodePage, out _) != CollationSupport.Exact)
            throw new DbfWriteException($"Cannot maintain compact index '{path}' (KEY '{header.KeyExpression}', FOR '{header.ForExpression}', collation '{header.SortOrder}', code page {_schema.Encoding.CodePage}).");
        var def = new CdxTagDefinition(Path.GetFileNameWithoutExtension(path), header.KeyExpression,
            header.HasFor ? header.ForExpression : null, header.Descending, header.SortOrder, header.IsUnique);
        var computer = CdxIndexBuilder.CreateKeyComputer(_schema, def, context);
        if (computer.KeyLen != header.KeyLength || computer.KeyType == IndexKeyType.Unknown)
            throw new DbfWriteException($"Cannot derive the stored key of compact index '{path}' (collation '{header.SortOrder}', code page {_schema.Encoding.CodePage}).");
        return (def, computer);
    }

    private void ValidateCompactIndexes()
    {
        foreach (var (path, context) in _openCompactIndexes)
            ReadCompactIndexPlan(path, context);
    }

    private void MaintainCompactIndexes(int recNo, DbfRecord? before, DbfRecord after)
    {
        foreach (var (path, context) in _openCompactIndexes)
        {
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
                editor.Insert(0, newKey, (uint)recNo, computer.KeyLen, computer.Pad);
            editor.Flush();
        }
    }

    private void RebuildCompactIndexes()
    {
        if (_openCompactIndexes.Count == 0) return;
        var rows = MaterializeRows();
        foreach (var (path, context) in _openCompactIndexes)
        {
            var (definition, _) = ReadCompactIndexPlan(path, context);
            CdxIndexBuilder.BuildCompactIdx(path, _schema, rows, definition, context, includeDeleted: true);
        }
    }
}
