using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// §A8 / P3b — READ the DBC-enforced DEFAULT / RULE / TRIGGER metadata for a member table from its
/// <c>PROPERTY</c> blobs and expose it as <see cref="DbcTableRules"/> (field defaults/rules, the
/// record rule, the three triggers, the primary key). Each decoded string MUST equal what VFP9
/// <c>DBGETPROP()</c> returns (the authoritative oracle); the writer (a later task) feeds these to
/// microVFP so an ADO.NET write enforces them exactly like VFP.
/// </summary>
/// <remarks>
/// <para>
/// The Table / Field <c>PROPERTY</c> memo is a flat sequence of length-prefixed property entries,
/// each laid out as: <c>[u32 totalLen][u16 = 0x0001][u8 propertyId][value … (totalLen-7 bytes,
/// NUL-terminated for strings)]</c> — the same encoding <see cref="Write.DbfDatabaseBuilder"/>
/// writes for the path property (id <c>0x01</c>). Reverse-engineered against the committed
/// TasTrade sample / real-world customer containers and verified field-for-field against VFP9 <c>DBGETPROP()</c>:
/// </para>
/// <list type="bullet">
///   <item><description>Table record: <c>0x09</c>=RuleExpression, <c>0x0A</c>=RuleText,
///   <c>0x0E</c>=InsertTrigger, <c>0x0F</c>=UpdateTrigger, <c>0x10</c>=DeleteTrigger,
///   <c>0x14</c>=PrimaryKey (index tag).</description></item>
///   <item><description>Field record: <c>0x09</c>=RuleExpression, <c>0x0A</c>=RuleText,
///   <c>0x0B</c>=DefaultValue.</description></item>
/// </list>
/// </remarks>
public sealed partial class DbfDatabase
{
    // P3b property-entry IDs (see remarks above).
    private const byte PropRuleExpression = 0x09;
    private const byte PropRuleText       = 0x0A;
    private const byte PropDefaultValue   = 0x0B;
    private const byte PropInsertTrigger  = 0x0E;
    private const byte PropUpdateTrigger  = 0x0F;
    private const byte PropDeleteTrigger  = 0x10;
    private const byte PropPrimaryKey     = 0x14;

    /// <summary>Decoded rules cache, keyed by the DBC <c>OBJECTNAME</c> (case-insensitive).</summary>
    private readonly Dictionary<string, DbcTableRules> _rulesCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read the DBC DEFAULT / RULE / TRIGGER metadata for member table <paramref name="table"/>
    /// (its DBC <c>OBJECTNAME</c>), decoded from the Table and child Field <c>PROPERTY</c> blobs.
    /// The returned <see cref="DbcTableRules"/> carries the table rule + the three triggers + the
    /// primary key and one <see cref="DbcFieldRules"/> per field (DefaultValue / RuleExpression /
    /// RuleText). Every value equals the corresponding <c>DBGETPROP()</c> result; an unset property
    /// is <see langword="null"/>.
    /// </summary>
    public DbcTableRules GetTableRules(string table)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(table);

        if (!_byName.TryGetValue(table, out var node))
            throw new DbfFileNotFoundException($"No table named '{table}' in the database container.");

        if (_rulesCache.TryGetValue(node.ObjectName, out var cached))
            return cached;

        var rules = DecodeTableRules(node);
        _rulesCache[node.ObjectName] = rules;
        return rules;
    }

    /// <summary><see cref="GetTableRules"/> without the exception for a name that is not a member table (a
    /// free table or cursor written while a database is open), for callers on a per-row path.</summary>
    internal bool TryGetTableRules(string table, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DbcTableRules? rules)
    {
        rules = null;
        if (_disposed || table is null || !_byName.ContainsKey(table)) return false;
        rules = GetTableRules(table);
        return true;
    }

    private readonly Dictionary<string, DbcTableRules?> _rulesByPath = new(StringComparer.OrdinalIgnoreCase);

    // Metadata belongs to this open DBC snapshot; DDL opens a new snapshot after rewriting it.
    internal DbcTableRules? RulesForPath(string path)
    {
        if (_rulesByPath.TryGetValue(path, out var rules)) return rules;
        foreach (string member in TableNames)
            if (string.Equals(GetTablePath(member), path, StringComparison.OrdinalIgnoreCase))
                return _rulesByPath[path] = GetTableRules(member);
        _rulesByPath[path] = null;
        return null;
    }

    /// <summary>Decode one table node's Table + per-field <c>PROPERTY</c> blobs into a <see cref="DbcTableRules"/>.</summary>
    private DbcTableRules DecodeTableRules(TableNode node)
    {
        string? ruleExpr = null, ruleText = null, insert = null, update = null, delete = null, pk = null;

        var tableBlob = node.PropertyBlock > 0 ? _dbc.Memo?.ReadBytes(node.PropertyBlock) : null;
        if (tableBlob is not null)
            ForEachProperty(tableBlob, _dbc.Encoding, (id, value) =>
            {
                switch (id)
                {
                    case PropRuleExpression: ruleExpr = value; break;
                    case PropRuleText:       ruleText = value; break;
                    case PropInsertTrigger:  insert   = value; break;
                    case PropUpdateTrigger:  update   = value; break;
                    case PropDeleteTrigger:  delete   = value; break;
                    case PropPrimaryKey:     pk       = value; break;
                }
            });

        var fields = new List<DbcFieldRules>(node.LongFieldNames.Count);
        for (int i = 0; i < node.LongFieldNames.Count; i++)
        {
            string fieldName = node.LongFieldNames[i];
            int block = i < node.FieldPropertyBlocks.Count ? node.FieldPropertyBlocks[i] : 0;

            string? def = null, fre = null, frt = null;
            var fieldBlob = block > 0 ? _dbc.Memo?.ReadBytes(block) : null;
            if (fieldBlob is not null)
                ForEachProperty(fieldBlob, _dbc.Encoding, (id, value) =>
                {
                    switch (id)
                    {
                        case PropDefaultValue:   def = value; break;
                        case PropRuleExpression: fre = value; break;
                        case PropRuleText:       frt = value; break;
                    }
                });

            fields.Add(new DbcFieldRules
            {
                FieldName = fieldName,
                DefaultValue = def,
                RuleExpression = fre,
                RuleText = frt,
            });
        }

        return new DbcTableRules
        {
            TableName = node.ObjectName,
            RuleExpression = ruleExpr,
            RuleText = ruleText,
            InsertTrigger = insert,
            UpdateTrigger = update,
            DeleteTrigger = delete,
            PrimaryKey = pk,
            Fields = fields,
        };
    }

    /// <summary>
    /// Walk a <c>PROPERTY</c> blob's length-prefixed entries (see remarks on <see cref="DbfDatabase"/>)
    /// and invoke <paramref name="onString"/> with each entry's property id and its decoded string value
    /// (the value bytes minus the single trailing NUL, decoded with the container <paramref name="encoding"/>).
    /// Malformed/truncated entries stop the walk gracefully (read-only, never throws).
    /// </summary>
    internal static void ForEachProperty(ReadOnlySpan<byte> blob, Encoding encoding, Action<byte, string> onString)
    {
        int i = 0, n = blob.Length;
        while (i + 7 <= n)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(i, 4));
            if (len < 7 || (long)i + len > n)
                break;

            byte propId = blob[i + 6];           // [i+4..i+6] = u16 0x0001 marker; [i+6] = property id.
            int valStart = i + 7;
            int valLen = (int)len - 7;
            if (valLen > 0 && blob[valStart + valLen - 1] == 0)
                valLen--;                        // strip the single trailing NUL terminator.

            onString(propId, valLen > 0 ? encoding.GetString(blob.Slice(valStart, valLen)) : string.Empty);
            i += (int)len;
        }
    }
}
