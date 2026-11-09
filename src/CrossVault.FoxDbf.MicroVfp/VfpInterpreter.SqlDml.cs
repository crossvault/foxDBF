using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    private void ExecSqlDml(SqlDmlStmt statement)
    {
        var parsed = SafeParseSql(statement.Sql) ?? ThrowSqlParseError(statement.Sql);
        var update = parsed as UpdateStatement;
        var delete = parsed as DeleteStatement;
        string table = update?.Table ?? statement.DeleteTarget ?? delete!.Table;
        var where = update?.Where ?? delete?.Where;
        int selected = Session.CurrentArea;
        var oldTally = Memory.Get("_TALLY");
        bool requireNames = _requireDmlNames;
        bool completed = false;
        int tally = 0;
        DbfTable? source = null;
        Index.CdxFile? sourceIndex = null;
        try
        {
            FlushPendingAppends();
            if (statement.DeleteTarget is not null && !table.Equals(delete!.Table, StringComparison.OrdinalIgnoreCase))
                (source, sourceIndex) = Session.OpenNamedTable(delete.Table);
            var wa = Session.FindInsertArea(table);
            if (wa is null)
            {
                Session.Use(table, inArea: 0);
                wa = Session.FindInsertArea(table)!;
            }
            int area = wa.Area;
            Session.SelectArea(area);
            var sqlRow = source is null ? null : new SqlDeleteSourceRow(this, wa, source, delete!.Table);
            if (Session.ReadOnly || wa.NoUpdate)
                throw new MicroVfpRuntimeException("Cannot update the cursor.", 111);

            var assignments = new List<(string Field, PrgExpr Value)>();
            if (update is not null)
            {
                foreach (var assignment in update.Assignments)
                {
                    string field = assignment.Column;
                    int dot = field.IndexOf('.');
                    if (dot >= 0)
                    {
                        if (!field[..dot].Equals(table, StringComparison.OrdinalIgnoreCase))
                            throw new MicroVfpRuntimeException("SQL: Invalid SET expression.", 2149);
                        field = field[(dot + 1)..];
                    }
                    int column = ColumnIndex(wa.Table, field);
                    if (column < 0)
                        throw new MicroVfpRuntimeException($"SQL: Column '{field}' is not found.", 1806);
                    assignments.Add((wa.Table.Columns[column].Name, PrgExpr.Parse(assignment.Value.Text)));
                }
            }

            _requireDmlNames = true;
            var predicate = where is null ? null : PrgExpr.Parse(where.Text);
            foreach (var expression in assignments.Select(a => a.Value).Append(predicate))
            {
                if (expression?.Parsed is not { } tree) continue;
                foreach (string name in tree.ReferencedFields())
                {
                    try { if (sqlRow is null) ResolveName(name); else sqlRow.GetField(name); }
                    catch (MicroVfpRuntimeException ex) when (ex.VfpErrorNumber == 12 && !name.StartsWith("m.", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new MicroVfpRuntimeException($"SQL: Column '{name}' is not found.", 1806);
                    }
                }
            }
            if (predicate?.Parsed is { } condition)
            {
                var type = condition.InferType(new SelectExecutor.TableSchema(wa.Table, wa.Alias)).Type;
                if (type is not (VfpType.Unknown or VfpType.Null or VfpType.Logical))
                    throw new MicroVfpRuntimeException("SQL: WHERE clause is invalid.", 1833);
            }
            // SQL ignores FILTER/KEY/order, honours DELETED, and leaves the pointer on the last
            // examined physical row. GoRecord retains row-buffer commits and transaction seams.
            int count = EffCount(area, wa);
            if (update is not null)
                ScanRows(() => SqlUpdateRow(Session.AreaAt(area)!, assignments));
            else
                // Share xBase's statement writer/refresh while retaining SQL's matching-row tally.
                ExecDeleteRecall(null, null, null, null, 0, recall: false, scanRows: ScanRows);
            MaybeAutoCommitRow(area);
            completed = true;

            void ScanRows(Action mark)
            {
                for (int rec = 1; rec <= count; rec++)
                {
                    GoRecord(area, rec);
                    wa = Session.AreaAt(area)!;
                    if (_ctx.Deleted && _row.Deleted) continue;
                    if (sqlRow is not null)
                    {
                        bool matched = false;
                        for (int sourceRec = 0; sourceRec < source!.RecordCount; sourceRec++)
                        {
                            if (_ctx.Deleted && source.IsRecordDeleted(sourceRec)) continue;
                            sqlRow.Record = source.GetRecord(sourceRec, includeDeleted: true);
                            if (predicate is null || Truth(predicate.Parsed!.Evaluate(sqlRow, _ctx))) { matched = true; break; }
                        }
                        if (!matched) continue;
                    }
                    else if (predicate is not null && !Truth(Eval(predicate))) continue;
                    mark();
                    // Unlike xBase DELETE, SQL counts matches even when their deletion flag was set.
                    tally++;
                }
            }
        }
        finally
        {
            _requireDmlNames = requireNames;
            sourceIndex?.Dispose();
            source?.Dispose();
            Session.SelectArea(selected);
            // VFP9 preserves the preceding _TALLY if a SQL statement fails, including trigger errors.
            Memory.Set("_TALLY", completed ? VfpValue.Integer(tally) : oldTally);
        }
    }

    // The optional DELETE target FROM source form is a two-table predicate. Its source scan
    // must not move the caller's live source pointer or leave a temporary source work area open.
    // VFP uses the source's disk values even when its open work area has pending buffered edits.
    private sealed class SqlDeleteSourceRow(VfpInterpreter owner, VfpSession.WorkArea target, DbfTable source, string alias)
        : IRowContext, IVfpFunctionHost
    {
        public DbfRecord? Record { get; set; }
        public int RecNo => owner._row.RecNo;
        public int RecCount => owner._row.RecCount;
        public bool Deleted => owner._row.Deleted;
        public object? GetField(string name)
        {
            int dot = name.IndexOf('.');
            string field = dot < 0 ? name : name[(dot + 1)..];
            int column = ColumnIndex(source, field);
            if (dot < 0 && column >= 0 && ColumnIndex(target.Table, field) >= 0)
                throw new MicroVfpRuntimeException($"SQL: Column '{field}' is ambiguous.", 1832);
            if (column >= 0 && (dot < 0 || name[..dot].Equals(alias, StringComparison.OrdinalIgnoreCase)))
                return Record is null ? BlankFor(source.Columns[column]) : Record.Value[source.Columns[column].Name];
            return owner.ResolveName(name);
        }
        public bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext context, out VfpValue result)
            => owner._row.TryInvoke(upperName, args, context, out result);
        public VfpValue UnknownFunction(string upperName, VfpValue[] args) => owner.UnknownFunction(upperName);
    }

    private void SqlUpdateRow(VfpSession.WorkArea wa, IReadOnlyList<(string Field, PrgExpr Value)> assignments)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in wa.Table.Columns)
            if (!column.IsSystem) values[column.Name] = ReadField(wa, column.Name);
        var clauses = new List<ReplaceClause>();
        WithImportRow(wa, Meta(wa.Area).RecNo, values, () =>
        {
            // VFP9 evaluates SET clauses left to right: SET a=b,b=a gives both fields old b.
            foreach (var (field, expression) in assignments)
            {
                var value = Eval(expression);
                var column = wa.Table.Columns[ColumnIndex(wa.Table, field)];
                bool compatible = value.IsNull || column.Type switch
                {
                    'C' or 'V' or 'M' => value.Type == VfpType.Character || value.IsBinary,
                    'I' or 'N' or 'F' or 'B' or 'Y' => value.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency,
                    'L' => value.Type == VfpType.Logical,
                    'D' => value.Type == VfpType.Date,
                    'T' => value.Type == VfpType.DateTime,
                    _ => true
                };
                if (!compatible) throw new MicroVfpRuntimeException("Data type mismatch.", 9);
                values[field] = value.ToClr();
                // VFP checks each SET field before evaluating the next clause, even without buffering.
                if (EnforceReferentialIntegrity && Meta(wa.Area).Buffering <= 1)
                    DmlExecutor.ValidateImportRules(Session, wa.Alias, wa.Table.Columns, values, EvalTextLoud,
                        fieldsToValidate: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { field }, validateRecord: false);
            }
            if (EnforceReferentialIntegrity && Meta(wa.Area).Buffering <= 1)
                DmlExecutor.ValidateImportRules(Session, wa.Alias, wa.Table.Columns, values, EvalTextLoud,
                    fieldsToValidate: new HashSet<string>(), validateRecord: true);
            return true;
        });
        foreach (string field in assignments.Select(a => a.Field).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            clauses.Add(new ReplaceClause(NameRef.OfName(field), PrgExpr.Constant(VfpValue.FromClr(values[field])), false));
        }
        // Keep all writes on the interpreter's writer: cached handles, CDX/IDX, triggers, buffers
        // and PRG/COW transactions therefore share precisely the REPLACE path.
        ExecReplace(new ReplaceStmt(clauses, null, null, null, null), rulesValidated: true);
        // VFP retains an invalid SQL candidate in a row/table buffer so it can be corrected or
        // reverted. The same failure must still prevent a later TABLEUPDATE from publishing it.
        if (EnforceReferentialIntegrity && Meta(wa.Area).Buffering > 1)
        {
            var meta = Meta(wa.Area);
            if (meta.Buf?.Rows.TryGetValue(meta.RecNo, out var edit) == true)
                ValidateBufferedRow(wa, meta.RecNo, edit);
            else
                WithImportRow(wa, meta.RecNo, values, () =>
                {
                    DmlExecutor.ValidateImportRules(Session, wa.Alias, wa.Table.Columns, values, EvalTextLoud);
                    return true;
                });
        }
    }
}
