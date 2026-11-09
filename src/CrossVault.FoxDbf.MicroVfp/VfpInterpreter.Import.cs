using System.Globalization;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    private void ExecExternalAppend(AppendFromStmt statement)
    {
        var area = Session.AreaAt(Session.CurrentArea)
            ?? throw new MicroVfpRuntimeException("No table is open.", 52);
        if (Session.ReadOnly || area.NoUpdate) throw new MicroVfpRuntimeException("Cannot update the cursor.", 111);
        var fields = ScatterFields(area.Table, statement.Fields, memo: false, recordObject: true,
            pattern: statement.FieldPattern, except: statement.Except);
        IEnumerable<IReadOnlyList<object?>> rows;
        if (statement.Array)
        {
            rows = Session.MemoryBridge!.ReadArrayRows(NameOf(statement.Source))
                ?? throw new MicroVfpRuntimeException("Variable is not found.", 12);
        }
        else rows = ReadImportText(statement, fields, area.Table.Encoding);

        string path = BeginTxWrite(area.Table.SourcePath!);
        area = Session.AreaAt(area.Area)!;
        SnapshotForTxn(path);
        var keys = DmlExecutor.ImportKeys(Session, area.Alias, area.Table, path);
        int tally = 0;
        bool overflow = false;
        Memory.Set("_TALLY", VfpValue.Integer(0));
        try
        {
            foreach (var row in rows)
            {
                var map = DmlExecutor.ImportMap(Session, fields, row, statement.Array,
                    statement.Type?.Equals("SDF", StringComparison.OrdinalIgnoreCase) == true);
                using (var lease = LeaseWriter(path))
                {
                    var writer = lease.Writer;
                    var blank = DmlExecutor.OmittedColumnsBlankMask(Session, area.Alias,
                        writer.Schema.Columns, map, honourSetNull: false);
                    var values = DmlExecutor.ImportWriteValues(writer.Schema.Columns, map, blank);
                    var encoded = writer.EncodeImportRow(values, blank);
                    var record = new DbfRecord(writer.Schema, encoded);
                    var converted = DmlExecutor.StoredImportValues(writer.Schema, record, map);
                    int recno = writer.RecordCount + 1;
                    // FOR and rules see field-width truncation, rounded numbers and generated IDs,
                    // exactly as if the pending record were already stored.
                    if (!WithImportRow(area, recno, converted, () => statement.For is null || Truth(Eval(statement.For)))) continue;
                    WithImportRow(area, recno, converted, () =>
                    {
                        // Supplied field rules run before VFP counts the append. Omitted-field and
                        // record constraints run afterwards, and can reject an already-counted row.
                        DmlExecutor.ValidateImportRules(Session, area.Alias, writer.Schema.Columns, map, EvalTextLoud, evaluateFieldError: EvalFieldRuleError, suppliedFields: true);
                        Memory.Set("_TALLY", VfpValue.Integer(++tally));
                        DmlExecutor.ValidateImportRules(Session, area.Alias, writer.Schema.Columns, map, EvalTextLoud, evaluateFieldError: EvalFieldRuleError, suppliedFields: false);
                        return true;
                    });
                    DmlExecutor.CheckImportKeys(keys, record, recno);
                    foreach (var column in writer.Schema.Columns)
                        if (column.Type == 'I' && !column.IsAutoIncrement
                            && System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(column.Offset + 1, 4)) == int.MinValue)
                            overflow = true;
                    writer.AppendImportRows(new List<object?[]> { values }, new List<bool[]> { blank });
                    writer.Flush();
                }
                ReopenFileAreas(path);
                area = Session.AreaAt(area.Area)!;
            }
        }
        finally
        {
            ReopenFileAreas(path);
            var current = Session.AreaAt(area.Area)!;
            PositionPointer(Meta(area.Area), null, current.Table.RecordCount);
        }
        if (overflow && statement.Array) throw new MicroVfpRuntimeException("Numeric overflow. Data was lost.", 39);
    }

    private bool WithImportRow(VfpSession.WorkArea area, int recno, Dictionary<string, object?> map, Func<bool> action)
    {
        var previous = _defaultRow;
        int selected = Session.CurrentArea;
        Session.SelectArea(area.Area);
        var meta = Meta(area.Area);
        var saved = (meta.RecNo, meta.Eof, meta.Bof);
        _defaultRow = new DefaultRow(area.Area, recno, map, previous, false);
        meta.RecNo = recno;
        meta.Eof = meta.Bof = false;
        try { return action(); }
        finally
        {
            _defaultRow = previous;
            (meta.RecNo, meta.Eof, meta.Bof) = saved;
            Session.SelectArea(selected);
        }
    }

    private IEnumerable<IReadOnlyList<object?>> ReadImportText(AppendFromStmt statement, IReadOnlyList<DbfColumn> fields, Encoding tableEncoding)
    {
        string file = NameOf(statement.Source);
        if (!Path.HasExtension(file)) file += ".txt";
        if (!Path.IsPathRooted(file)) file = Path.Combine(Session.DataDirectory ?? Directory.GetCurrentDirectory(), file);
        if (!File.Exists(file)) throw new MicroVfpRuntimeException($"File '{file}' does not exist.", 1);
        Encoding encoding = _ctx.Encoding ?? Encoding.GetEncoding(1252);
        if (statement.CodePage is not null)
        {
            int codePage = (int)Eval(statement.CodePage).AsNumber;
            if (codePage <= 0) throw new MicroVfpRuntimeException("Code page number is invalid.", 1914);
            try { encoding = VfpSessionText.ResolveSession(codePage); }
            catch (ArgumentException) { throw new MicroVfpRuntimeException("Code page number is invalid.", 1914); }
        }
        byte[] bytes = File.ReadAllBytes(file);
        // Latin1 is a byte-preserving parsing view: SDF widths and delimiters count bytes, even for DBCS.
        string content = Encoding.Latin1.GetString(bytes);
        object TextValue(DbfColumn field, string value)
        {
            byte[] fieldBytes = Encoding.Latin1.GetBytes(value);
            // VFP9 preserves bytes across DBCS boundaries; its single-byte pages use best-fit maps.
            if (encoding.IsSingleByte && tableEncoding.IsSingleByte)
                fieldBytes = VfpSessionText.ConvertBytes(fieldBytes, encoding.CodePage, tableEncoding.CodePage);
            return field.Type is 'C' or 'V' ? new VfpImport.StoredText(fieldBytes) : tableEncoding.GetString(fieldBytes);
        }
        bool sdf = statement.Type?.Equals("SDF", StringComparison.OrdinalIgnoreCase) == true;
        char separator = ',', quote = '"';
        string option = statement.Delimiter?.Trim() ?? "";
        if (option.Equals("BLANK", StringComparison.OrdinalIgnoreCase)) { separator = ' '; quote = '\0'; }
        else if (option.Equals("TAB", StringComparison.OrdinalIgnoreCase)) { separator = '\t'; quote = '\0'; }
        else if (option.StartsWith("CHARACTER ", StringComparison.OrdinalIgnoreCase))
        {
            string s = EvalTextLoud(option[10..]).AsString;
            if (s.Length > 0) separator = s[0];
        }
        else if (option.Length > 0)
        {
            string s = EvalTextLoud(option).AsString;
            quote = s.Length > 0 ? s[0] : '\0';
        }
        if (statement.Separator is { } separatorExpression)
        {
            string s = EvalTextLoud(separatorExpression).AsString;
            if (s.Length > 0) separator = s[0];
        }
        // DOS EOF is a record terminator, not end-of-stream. Quoting never spans a line.
        string[] lines = content.TrimEnd('\x1a').Replace("\r\n", "\n").Replace('\r', '\n').Replace('\x1a', '\n').Split('\n');
        int end = lines.Length;
        if (end > 0 && lines[end - 1].Length == 0) end--;
        for (int i = 0; i < end; i++)
        {
            string line = lines[i];
            var values = new List<object?>();
            int offset = 0;
            foreach (var field in fields)
            {
                if (offset >= line.Length) break;
                if (sdf)
                {
                    int width = field.Type switch { 'D' => 8, 'I' => 11, 'B' => 20, 'Y' => 21, 'T' => 20, _ => field.Length };
                    int length = Math.Min(width, Math.Max(0, line.Length - offset));
                    values.Add(TextValue(field, length == 0 ? "" : line.Substring(offset, length)));
                    offset += width;
                }
                else
                {
                    int start = offset;
                    if (offset < line.Length && line[offset] == quote && quote != '\0')
                    {
                        start = ++offset;
                        while (offset < line.Length && line[offset] != quote) offset++;
                        values.Add(TextValue(field, line[start..offset]));
                        if (offset < line.Length) offset++;
                    }
                    else
                    {
                        while (offset < line.Length && line[offset] != separator) offset++;
                        values.Add(TextValue(field, line[start..offset]));
                    }
                    if (offset < line.Length && line[offset] == separator) offset++;
                }
            }
            yield return values;
        }
    }

    private void ExecSelectImport(InsertStatement statement)
    {
        FlushPendingAppends();
        var (fields, rows) = DmlExecutor.SelectImportSource(Session, statement, _row);
        var area = Session.FindInsertArea(statement.Table);
        if (area is null)
        {
            Session.Use(statement.Table, inArea: 0);
            area = Session.FindInsertArea(statement.Table)!;
        }
        if (Session.ReadOnly || area.NoUpdate) throw new MicroVfpRuntimeException("Cannot update the cursor.", 111);
        bool buffered = _anyBuffering && !_inBufferCommit && Meta(area.Area).Buffering > 1;
        bool trigger = EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Insert, area) is not null;
        string path = BeginTxWrite(area.Table.SourcePath!);
        area = Session.AreaAt(area.Area)!;
        SnapshotForTxn(path);
        var keys = DmlExecutor.ImportKeys(Session, statement.Table, area.Table, path);
        int tally = 0;
        Memory.Set("_TALLY", VfpValue.Integer(0));
        try
        {
            foreach (var row in rows)
            {
                var map = DmlExecutor.ImportMap(Session, fields, row);
                DmlExecutor.EvaluateDbcDefaults(Session, statement.Table, area.Table.Columns, map);
                if (buffered)
                {
                    WithImportRow(area, EffCount(area.Area, area) + 1, map, () =>
                    {
                        DmlExecutor.ValidateImportRules(Session, statement.Table, area.Table.Columns, map, EvalTextLoud, evaluateFieldError: EvalFieldRuleError);
                        return true;
                    });
                    string sql = $"INSERT INTO {statement.Table} ({string.Join(",", map.Keys)}) VALUES ({string.Join(",", map.Values.Select(ImportLiteral))})";
                    ExecInsert(new InsertStmt(sql, SqlParser.Parse(sql)));
                }
                else
                {
                    FileSnapshot? snapshot = trigger ? CaptureSnapshot(path) : null;
                    try
                    {
                        using (var lease = LeaseWriter(path))
                        {
                            var writer = lease.Writer;
                            var mask = DmlExecutor.OmittedColumnsBlankMask(Session, statement.Table, writer.Schema.Columns, map, honourSetNull: true);
                            var values = DmlExecutor.ImportWriteValues(writer.Schema.Columns, map, mask);
                            var record = new DbfRecord(writer.Schema, writer.EncodeImportRow(values, mask));
                            int recno = writer.RecordCount + 1;
                            WithImportRow(area, recno, DmlExecutor.StoredImportValues(writer.Schema, record, map), () =>
                            {
                                DmlExecutor.ValidateImportRules(Session, statement.Table, writer.Schema.Columns, map, EvalTextLoud, evaluateFieldError: EvalFieldRuleError);
                                return true;
                            });
                            DmlExecutor.CheckImportKeys(keys, record, recno);
                            writer.AppendImportRows(new List<object?[]> { values }, new List<bool[]> { mask });
                            writer.Flush();
                        }
                        ReopenFileAreas(path);
                        area = Session.AreaAt(area.Area)!;
                        if (trigger)
                        {
                            int selected = Session.CurrentArea;
                            bool allowed;
                            try
                            {
                                Session.SelectArea(area.Area);
                                GoRecord(area.Area, area.Table.RecordCount);
                                allowed = FireDmlTrigger(RiEvent.Insert, area.Area);
                            }
                            finally { Session.SelectArea(selected); }
                            if (!allowed)
                            {
                                RestoreSnapshot(snapshot!);
                                throw TriggerFailed(RiEvent.Insert, area.Area, area.Alias);
                            }
                        }
                    }
                    finally { snapshot?.Cleanup(); }
                }
                Memory.Set("_TALLY", VfpValue.Integer(++tally));
            }
        }
        finally { if (!buffered) ReopenFileAreas(path); }
    }

    private void SetImportCentury(string argument)
    {
        if (!argument.StartsWith("TO ", StringComparison.OrdinalIgnoreCase))
        {
            Session.ImportCentury = OnOff(argument);
            return;
        }
        var parts = System.Text.RegularExpressions.Regex.Split(argument[3..], @"\s+ROLLOVER\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int century = (int)EvalTextLoud(parts[0]).AsNumber;
        int rollover = parts.Length > 1 ? (int)EvalTextLoud(parts[1]).AsNumber : Session.ImportRollover;
        Session.ImportCenturyBase = century;
        Session.ImportRollover = rollover;
    }

    private static string ImportLiteral(object? value) => value switch
    {
        null => ".NULL.",
        bool b => b ? ".T." : ".F.",
        DateOnly d => d == default ? "CTOD('')" : "{^" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "}",
        DateTime d => d == default ? "CTOT('')" : "{^" + d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "}",
        string s => s.Length == 0 ? "''" : string.Join("+", s.Select(c => "CHR(" + (int)c + ")")),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
