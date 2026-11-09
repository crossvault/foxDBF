using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Sql;

internal sealed partial class DmlExecutor
{
    internal static (IReadOnlyList<DbfColumn> Fields, List<object?[]> Rows) SelectImportSource(
        VfpSession session, InsertStatement st, IRowContext? host = null)
    {
        if (st.Query is null || st.Query.Into is not null)
            throw new FoxDbfSqlException("INSERT SELECT requires a query without INTO.") { VfpErrorNumber = 10 };
        // Materialize before opening the writer: SELECT may read the destination itself.
        var result = new SelectExecutor(session, host) { VfpBlankFields = true }.Run(st.Query);
        var rows = result.Rows.ToList();
        var (table, index) = session.OpenNamedTable(st.Table);
        try
        {
            var fields = st.Columns is null ? table.Columns.Where(c => !c.IsSystem).ToArray()
                : st.Columns.Select(name => table.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FoxDbfSqlException($"Variable '{name}' is not found.") { VfpErrorNumber = 12 }).ToArray();
            if (fields.Length != result.Columns.Count)
                throw new FoxDbfSqlException("SELECT and INSERT column counts do not match.") { VfpErrorNumber = 2119 };
            return (fields, rows);
        }
        finally { index?.Dispose(); table.Dispose(); }
    }

    internal static Dictionary<string, object?> ImportMap(VfpSession session, IReadOnlyList<DbfColumn> fields,
        IReadOnlyList<object?> row, bool array = false, bool sdf = false)
    {
        var map = new ImportValues();
        for (int i = 0; i < Math.Min(fields.Count, row.Count); i++)
        {
            var col = fields[i];
            if (col.IsAutoIncrement)
            {
                if (session.ImportAutoIncError)
                    throw new FoxDbfSqlException("Field is read-only.") { VfpErrorNumber = 2088 };
                continue;
            }
            if (row[i] is null && !col.IsNullable)
                throw new FoxDbfSqlException($"Field {col.Name} does not accept null values.") { VfpErrorNumber = 1581 };
            map[col.Name] = VfpImport.ConvertValue(row[i], col, session, array, sdf);
            if (VfpImport.IsBlankConversion(row[i], col)) map.BlankFields.Add(col.Name);
        }
        return map;
    }

    internal SqlResult InsertSelect(InsertStatement st, IRowContext? host = null)
    {
        var (fields, rows) = SelectImportSource(_session, st, host);
        var target = _session.OpenWritableTarget(st.Table);
        int count = 0;
        _session.MemoryBridge?.SetImportTally(0);
        try
        {
            var columns = target.Writer.Schema.Columns;
            var keys = ImportKeys(_session, st.Table, target.Writer.Schema, target.WritePath);
            bool hasDefaults = DbcDefaultExpressions(_session, st.Table, columns, target.SourcePath) is not null;
            foreach (var row in rows)
            {
                var map = ImportMap(_session, fields, row);
                if (hasDefaults && _session.DbcDefaultEvaluator is not null)
                    FillDbcDefaults(_session, st.Table, ref target, map);
                else FillImportDefaults(_session, st.Table, columns, map);
                var mask = OmittedColumnsBlankMask(_session, st.Table, columns, map, honourSetNull: true);
                var values = ImportWriteValues(columns, map, mask);
                var record = new DbfRecord(target.Writer.Schema, target.Writer.EncodeImportRow(values, mask));
                int recno = target.Writer.RecordCount + 1;
                ValidateImportRules(_session, st.Table, columns, StoredImportValues(target.Writer.Schema, record, map), recno: recno);
                CheckImportKeys(keys, record, recno);
                // Writer-side constraints can fail too: retain each accepted prefix row, as VFP9 does.
                target.Writer.AppendImportRows(new List<object?[]> { values }, new List<bool[]> { mask });
                count++;
                _session.MemoryBridge?.SetImportTally(count);
            }
        }
        finally { target.Dispose(); }
        return SqlResult.Dml(count);
    }

    internal static Dictionary<string, object?> StoredImportValues(DbfTable schema, DbfRecord record, Dictionary<string, object?> map)
        => schema.Columns.Where(c => !c.IsSystem).ToDictionary(c => c.Name,
            c => record.IsNullFlagSet(c) ? null
                : c.Type is 'M' or 'W' or 'G' ? map.GetValueOrDefault(c.Name, "") : record.GetVfpValue(c.Name), StringComparer.OrdinalIgnoreCase);

    private sealed class ImportValues : Dictionary<string, object?>
    {
        internal HashSet<string> BlankFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal ImportValues() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    internal static object?[] ImportWriteValues(IReadOnlyList<DbfColumn> columns, Dictionary<string, object?> map, bool[] mask)
    {
        var values = new object?[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            string name = columns[i].Name;
            if (map is ImportValues imported && imported.BlankFields.Contains(name)) mask[i] = true;
            values[i] = mask[i] ? null : map.GetValueOrDefault(name);
        }
        return values;
    }

    internal static void FillImportDefaults(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        Dictionary<string, object?> map)
    {
        var defaults = DbcDefaultExpressions(session, table, columns);
        if (defaults is null) return;
        for (int i = 0; i < columns.Count; i++)
            if (!map.ContainsKey(columns[i].Name) && defaults[i] is { } expression)
                map[columns[i].Name] = VfpExpression.Parse(expression).Evaluate(new ImportRow(columns, map), session.Context).ToClr();
    }

    internal static void ValidateImportRules(VfpSession session, string table, IReadOnlyList<DbfColumn> columns,
        Dictionary<string, object?> map, Func<string, VfpValue>? evaluate = null, int recno = 0, bool? suppliedFields = null,
        ISet<string>? fieldsToValidate = null, bool validateRecord = true, DbcTableRules? rules = null)
    {
        rules ??= TableRules(session, table);
        if (rules is null) return;
        VfpValue Eval(string expression) => evaluate is not null ? evaluate(expression)
            : VfpExpression.Parse(expression).Evaluate(new ImportRow(columns, map, recno), session.Context);
        int field = 0;
        foreach (var col in columns)
        {
            if (col.IsSystem) continue;
            var rule = FieldRules(rules, columns, col, field++);
            if ((!suppliedFields.HasValue || map.ContainsKey(col.Name) == suppliedFields.Value)
                && (fieldsToValidate is null || fieldsToValidate.Contains(col.Name))
                && !string.IsNullOrEmpty(rule?.RuleExpression))
            {
                var value = Eval(rule.RuleExpression);
                if (value.Type != VfpType.Logical || !value.AsLogical)
                    throw new FoxDbfSqlException("Field validation rule is violated.") { VfpErrorNumber = 1582 };
            }
        }
        if (validateRecord && suppliedFields != true && !string.IsNullOrEmpty(rules.RuleExpression))
        {
            var value = Eval(rules.RuleExpression);
            if (value.Type != VfpType.Logical || !value.AsLogical)
                throw new FoxDbfSqlException("Table validation rule is violated.") { VfpErrorNumber = 1583 };
        }
    }

    internal static List<(CdxIndexBuilder.TagKeyComputer Computer, HashSet<string> Keys)> ImportKeys(
        VfpSession session, string table, DbfTable schema, string path)
    {
        string livePath = session.TxLivePath?.Invoke(path) ?? path;
        var names = new HashSet<string>(session.ImportCandidateTags?.Invoke(livePath) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (TableRules(session, table, livePath)?.PrimaryKey is { Length: > 0 } primary) names.Add(primary);
        var result = new List<(CdxIndexBuilder.TagKeyComputer, HashSet<string>)>();
        string file = SidecarFile.ChangeExtension(path, ".cdx");
        if (!File.Exists(file)) return result;
        using var index = CdxFile.Open(file, schema);
        var header = index.Index.ReadCdxHeader();
        foreach (var entry in IndexTraversal.EnumerateCompact(index.Index, header.Root, header.KeyLength, 0x20))
        {
            // VFP9 candidate tag headers carry 0x04 (measured header: 0x64).
            if ((index.Index.ReadCdxHeader(entry.Recno).Options & 0x04) != 0)
                names.Add(schema.Encoding.GetString(entry.Key).TrimEnd(' ', '\0'));
        }
        foreach (string name in names)
        {
            var tag = index.Tag(name);
            if (tag is null) continue;
            var definition = new CdxTagDefinition(tag.Name, tag.KeyExpression, tag.ForExpression, tag.Descending, tag.Collation);
            var computer = CdxIndexBuilder.CreateKeyComputer(schema, definition, session.Context);
            var existing = tag.EnumerateEntries().Select(e => Convert.ToHexString(e.Key)).ToHashSet();
            result.Add((computer, existing));
        }
        return result;
    }

    internal static void CheckImportKeys(List<(CdxIndexBuilder.TagKeyComputer Computer, HashSet<string> Keys)> keys,
        DbfRecord record, int recno)
    {
        var additions = new List<(HashSet<string> Set, string Key)>();
        foreach (var (computer, existing) in keys)
        {
            if (!computer.TryComputeKey(record, recno, recno, out var bytes)) continue;
            string key = Convert.ToHexString(bytes);
            if (existing.Contains(key))
                throw new FoxDbfSqlException("Uniqueness of index is violated.") { VfpErrorNumber = 1884 };
            additions.Add((existing, key));
        }
        foreach (var (set, key) in additions) set.Add(key);
    }

    private sealed class ImportRow(IReadOnlyList<DbfColumn> columns, Dictionary<string, object?> values, int recno = 0) : IRowContext
    {
        public int RecNo => recno;
        public int RecCount => recno;
        public bool Deleted => false;
        public object? GetField(string name)
        {
            name = name[(name.LastIndexOf('.') + 1)..];
            if (values.TryGetValue(name, out var value)) return value;
            var column = columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return column?.Type switch
            {
                'C' or 'V' or 'M' => "", 'L' => false, 'D' => default(DateOnly), 'T' => default(DateTime), _ => 0m,
            };
        }
    }
}
