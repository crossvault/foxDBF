using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

public sealed partial class DbfWriter
{
    // Structural CDX and compact IDX retain their incremental editors. Legacy IDX and named
    // CDX use the existing bulk builders; this list is empty on the ordinary write hot path.
    private readonly Dictionary<string, EvaluationContext> _openRebuiltIndexes = new(StringComparer.OrdinalIgnoreCase);
    private bool HasAdditionalIndexes => _openCompactIndexes.Count > 0 || _openRebuiltIndexes.Count > 0;

    private CdxTagDefinition[] ReadAdditionalDefinitions(string path, EvaluationContext context)
    {
        CdxTagDefinition[] definitions;
        int[] lengths;
        if (Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase))
        {
            using var idx = IdxFile.Open(path, _schema);
            var h = idx.Header;
            definitions = [new(Path.GetFileNameWithoutExtension(path), h.KeyExpression,
                h.HasFor ? h.ForExpression : null, unique: h.IsUnique)];
            lengths = [h.KeyLength];
        }
        else
        {
            using var cdx = CdxFile.Open(path, _schema);
            var tags = cdx.TagNames.Select(n => cdx.Tag(n)!).ToArray();
            definitions = tags.Select(t => new CdxTagDefinition(t.Name, t.KeyExpression,
                t.ForExpression, t.Descending, t.Collation, t.IsUnique)).ToArray();
            lengths = tags.Select(t => t.KeyLength).ToArray();
        }
        for (int i = 0; i < definitions.Length; i++)
        {
            var def = definitions[i];
            if (!FieldsResolve(def.KeyExpression, context) || !FieldsResolve(def.ForExpression, context)
                || VfpCollations.Resolve(def.Collation, _schema.Encoding.CodePage, out _) != CollationSupport.Exact)
                throw new DbfWriteException($"Cannot maintain index '{path}' (KEY '{def.KeyExpression}', FOR '{def.ForExpression}').");
            var computer = CdxIndexBuilder.CreateKeyComputer(_schema, def, context);
            computer.ValidateHost?.Invoke();
            if (computer.KeyLen != lengths[i] || computer.KeyType == IndexKeyType.Unknown)
                throw new DbfWriteException($"Cannot derive the stored key of index '{path}'.");
        }
        return definitions;
    }

    private void ValidateRebuiltIndexes()
    {
        foreach (var (path, context) in _openRebuiltIndexes)
            ReadAdditionalDefinitions(path, context);
    }

    private void RebuildAdditionalIndexes()
    {
        if (_openRebuiltIndexes.Count == 0) return;
        var rows = MaterializeRows();
        foreach (var (path, context) in _openRebuiltIndexes)
        {
            var definitions = ReadAdditionalDefinitions(path, context);
            if (Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                var def = definitions[0];
                IdxIndexBuilder.Build(path, _schema, rows, def.KeyExpression, def.ForExpression,
                    def.Unique, context, includeDeleted: true);
            }
            else CdxIndexBuilder.Build(path, _schema, rows, definitions, context, includeDeleted: true);
        }
    }
}

