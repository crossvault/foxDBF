using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

public sealed partial class DbfWriter
{
    // Additional files are maintained only while explicitly open, as SET INDEX TO in VFP.
    private readonly Dictionary<string, EvaluationContext> _openAdditionalIndexes = new(StringComparer.OrdinalIgnoreCase);
    private sealed record AdditionalIndexPlan(CdxTagDefinition[] Definitions,
        Dictionary<string, CdxIndexBuilder.TagKeyComputer> Computers);
    private readonly Dictionary<string, AdditionalIndexPlan> _additionalIndexPlans = new(StringComparer.OrdinalIgnoreCase);

    private AdditionalIndexPlan GetAdditionalIndexPlan(string path, EvaluationContext context)
    {
        bool cacheable = context.SkipUnavailableIndexExpression is null;
        if (cacheable && _additionalIndexPlans.TryGetValue(path, out var cached)) return cached;
        var definitions = ReadAdditionalDefinitions(path, context, allowSkipped: true);
        var computers = definitions.ToDictionary(d => d.Name,
            d => CdxIndexBuilder.CreateKeyComputer(_schema, d, context), StringComparer.OrdinalIgnoreCase);
        var plan = new AdditionalIndexPlan(definitions, computers);
        if (cacheable) _additionalIndexPlans.Add(path, plan);
        return plan;
    }

    private bool HasAdditionalIndexes => _openCompactIndexes.Count > 0 || _openAdditionalIndexes.Count > 0;

    private CdxTagDefinition[] ReadAdditionalDefinitions(string path, EvaluationContext context, bool allowSkipped = false)
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
        var available = new List<CdxTagDefinition>();
        for (int i = 0; i < definitions.Length; i++)
        {
            var def = definitions[i];
            if (allowSkipped && context.SkipUnavailableIndexExpression is { } skip
                && (skip(def.KeyExpression) || skip(def.ForExpression))) continue;
            if (!FieldsResolve(def.KeyExpression, context) || !FieldsResolve(def.ForExpression, context)
                || VfpCollations.Resolve(def.Collation, _schema.Encoding.CodePage, out _) != CollationSupport.Exact)
                throw new DbfWriteException($"Cannot maintain index '{path}' (KEY '{def.KeyExpression}', FOR '{def.ForExpression}').");
            var computer = CdxIndexBuilder.CreateKeyComputer(_schema, def, context);
            computer.ValidateHost?.Invoke();
            if (computer.KeyLen != lengths[i] || computer.KeyType == IndexKeyType.Unknown)
                throw new DbfWriteException($"Cannot derive the stored key of index '{path}'.");
            available.Add(def);
        }
        return available.ToArray();
    }

    private void ValidateAdditionalIndexes()
    {
        foreach (var (path, context) in _openAdditionalIndexes)
            foreach (var computer in GetAdditionalIndexPlan(path, context).Computers.Values)
                computer.ValidateHost?.Invoke();
    }

    private void MaintainAdditionalIndexes(int recNo, DbfRecord? before, DbfRecord after)
    {
        foreach (var (path, context) in _openAdditionalIndexes)
        {
            var plan = GetAdditionalIndexPlan(path, context);
            if (plan.Definitions.Length == 0) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (Path.GetExtension(path).Equals(".idx", StringComparison.OrdinalIgnoreCase))
            {
                var computer = plan.Computers[plan.Definitions[0].Name];
                var editor = new IdxTreeEditor(stream, computer.KeyLen);
                Change(computer, (key, rec) => editor.Delete(key, rec),
                    (key, rec) => editor.Insert(key, rec), editor.ContainsKey);
                editor.Flush();
            }
            else
            {
                using var editor = new CdxTreeEditor(stream, _schema.Encoding);
                foreach (var tag in editor.ReadTags())
                {
                    if (!plan.Computers.TryGetValue(tag.Name, out var computer)) continue;
                    Change(computer,
                        (key, rec) => editor.Delete(tag.HeaderOffset, key, rec, computer.KeyLen, computer.Pad),
                        (key, rec) => editor.Insert(tag.HeaderOffset, key, rec, computer.KeyLen, computer.Pad),
                        key => editor.ContainsKey(tag.HeaderOffset, key, computer.KeyLen, computer.Pad));
                }
                editor.Flush();
            }
        }

        void Change(CdxIndexBuilder.TagKeyComputer computer, Action<byte[], uint> delete,
            Action<byte[], uint> insert, Func<byte[], bool> contains)
        {
            byte[] oldKey = [];
            bool had = before is { } old && computer.TryComputeKey(old, recNo, _recordCount, out oldKey);
            bool has = computer.TryComputeKey(after, recNo, _recordCount, out var newKey);
            if (had == has && (!had || oldKey.AsSpan().SequenceEqual(newKey))) return;
            if (had) delete(oldKey, (uint)recNo);
            if (has && (!computer.Unique || !contains(newKey))) insert(newKey, (uint)recNo);
        }
    }

    private void RebuildAdditionalIndexes()
    {
        _additionalIndexPlans.Clear();
        if (_openAdditionalIndexes.Count == 0) return;
        var rows = MaterializeRows();
        foreach (var (path, context) in _openAdditionalIndexes)
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

