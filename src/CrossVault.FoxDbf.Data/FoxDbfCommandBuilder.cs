using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// Auto-generates INSERT / UPDATE / DELETE commands from a <see cref="FoxDbfDataAdapter"/>'s
/// <see cref="FoxDbfDataAdapter.SelectCommand"/>. A DBF has no declared primary key, so row identity
/// for the UPDATE / DELETE WHERE is VFP's natural key, <c>RECNO()</c>: the generated DML is
/// <c>UPDATE t SET … WHERE RECNO()=?</c> / <c>DELETE FROM t WHERE RECNO()=?</c>, with the WHERE
/// parameter sourced (SourceVersion = Original) from the <c>RECNO()</c> column the SELECT projects
/// (e.g. <c>SELECT RECNO() AS recno, … FROM t</c>). RECNO()-targeting flows through the existing DML
/// executor unchanged (the WHERE text is handed to the VFP expression engine, which evaluates
/// <c>RECNO()</c> per record), so we do NOT fall back to optimistic all-columns concurrency.
/// <para>
/// The SELECT is parsed once (via <see cref="SqlParser"/>) to discover the single base table, the
/// writable physical columns (simple or alias-qualified field references), and the projected
/// <c>RECNO()</c> identity column. Alias-qualified projections (<c>p.id</c>, <c>p.id AS pid</c>) are
/// resolved to their PHYSICAL field exactly as <c>SelectExecutor.BuildColumns</c> does (strip the
/// alias prefix), and the generated parameter's <c>SourceColumn</c> is the DataTable column name
/// (<c>item.Alias ?? field</c>) so binding always finds the value.
/// </para>
/// <para>
/// MEMO / BINARY COLUMNS ARE EXCLUDED. FPT-backed fields (Memo <c>M</c> and binary <c>G/P/Q/W</c>)
/// are deliberately omitted from the generated INSERT column list and UPDATE SET list, because
/// (1) binary values surface as <c>byte[]</c>, which <see cref="FoxDbfCommand"/> cannot render as a
/// VFP literal (it throws), and (2) re-SETting a memo on every UPDATE defeats the DML executor's
/// <c>KeepValue</c> design (which preserves the existing .fpt block for FPT columns NOT in the SET
/// list) and would grow the .fpt file unboundedly. Consequently the auto-generated commands do not
/// write memo/binary content: on UPDATE the existing .fpt block is preserved verbatim; on INSERT the
/// new row gets the field's empty default. Supply an explicit InsertCommand/UpdateCommand to write
/// memo/binary data. Determining a column's type requires an OPEN connection (the table schema is read
/// via <see cref="FoxDbfConnection.GetSchema(string, string?[])"/>), so command generation throws if
/// the SelectCommand's connection is not open.
/// </para>
/// <para>
/// The parsed SELECT (reference + CommandText) is cached and re-derived whenever either changes, and
/// the cache is cleared when the builder's <see cref="DataAdapter"/> is reassigned, so a reused or
/// reassigned builder can never emit DML against a stale table.
/// </para>
/// </summary>
public sealed class FoxDbfCommandBuilder : DbCommandBuilder
{
    // The same identifier rule as the expression lexer / SQL executor: any Unicode letter (VFP9 accepts
    // razónSoc as a field name), decimal digits and '_'.
    private static readonly Regex s_identifier = new(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.Compiled);
    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.Compiled);

    private bool _schemaParsed;
    private FoxDbfCommand? _parsedSelect;                   // the SelectCommand the cache was derived from
    private string? _parsedText;                            // its CommandText at derivation time
    private string _table = string.Empty;
    private string? _recnoColumn;                          // result name of the RECNO() identity column
    private readonly List<(string Result, string Field)> _columns = new(); // writable physical columns

    private FoxDbfCommand? _insert;
    private FoxDbfCommand? _update;
    private FoxDbfCommand? _delete;

    public FoxDbfCommandBuilder() { }

    public FoxDbfCommandBuilder(FoxDbfDataAdapter adapter)
    {
        DataAdapter = adapter;
    }

    /// <summary>The adapter whose <c>SelectCommand</c> drives command generation.</summary>
    public new FoxDbfDataAdapter? DataAdapter
    {
        get => (FoxDbfDataAdapter?)base.DataAdapter;
        // Reassigning the adapter invalidates everything derived from the old SelectCommand, so the
        // builder can never emit DML against a stale table (a fixture-mutation hazard).
        set { ResetCache(); base.DataAdapter = value; }
    }

    /// <summary>The generated INSERT command (Added rows).</summary>
    public new FoxDbfCommand GetInsertCommand() => _insert ??= BuildInsert();

    /// <summary>The generated UPDATE command (Modified rows), keyed on <c>RECNO()</c>.</summary>
    public new FoxDbfCommand GetUpdateCommand() => _update ??= BuildUpdate();

    /// <summary>The generated DELETE command (Deleted rows), keyed on <c>RECNO()</c>.</summary>
    public new FoxDbfCommand GetDeleteCommand() => _delete ??= BuildDelete();

    public override void RefreshSchema()
    {
        ResetCache();
        base.RefreshSchema();
    }

    private void ResetCache()
    {
        _schemaParsed = false;
        _parsedSelect = null;
        _parsedText = null;
        _recnoColumn = null;
        _columns.Clear();
        _insert = _update = _delete = null;
    }

    // ---- command generation ----------------------------------------------------------------

    private void EnsureSchema()
    {
        var adapter = DataAdapter
            ?? throw new InvalidOperationException("The command builder has no DataAdapter.");
        var select = adapter.SelectCommand
            ?? throw new InvalidOperationException("The DataAdapter's SelectCommand is not set.");
        if (string.IsNullOrWhiteSpace(select.CommandText))
            throw new InvalidOperationException("The SelectCommand text is empty.");

        // Re-derive when the SelectCommand reference OR its text changed (the standard SqlCommandBuilder
        // auto-detects a changed SelectCommand); otherwise reuse the cached schema.
        if (_schemaParsed && ReferenceEquals(_parsedSelect, select) && _parsedText == select.CommandText)
            return;

        // (Re)deriving: drop any commands cached against the previous schema so they rebuild.
        _insert = _update = _delete = null;
        _columns.Clear();
        _recnoColumn = null;

        if (SqlParser.Parse(select.CommandText) is not SelectStatement sel)
            throw new InvalidOperationException("The SelectCommand must be a SELECT statement.");
        if (sel.From.Count != 1 || sel.Joins.Count > 0)
            throw new InvalidOperationException("Dynamic SQL generation requires a single-table SELECT.");

        _table = sel.From[0].Table;

        // FPT-backed (memo/binary) columns are excluded from the generated DML — needs the table type
        // map, which requires an open connection.
        var fieldTypes = ReadFieldTypes(select);

        foreach (var item in sel.Items)
        {
            if (item.IsStar)
                throw new InvalidOperationException(
                    "Dynamic SQL generation requires an explicit column list (no '*'); project the columns and RECNO() AS recno.");
            if (item.Expression is null)
                continue; // aggregate-star etc. — not a writable column.

            string text = item.Expression.Text.Trim();
            string normalized = s_whitespace.Replace(text, "").ToUpperInvariant();

            if (normalized == "RECNO()")
            {
                _recnoColumn = item.Alias ?? text; // computed identity column — never written.
                continue;
            }

            // A writable column is a bare ('name') or alias-qualified ('p.name') field reference; the
            // physical field is the part after the last dot, matching SelectExecutor.StripAlias. Anything
            // else (expression, function) is read-only and excluded from INSERT / UPDATE.
            string? field = PhysicalField(text);
            if (field is null)
                continue;

            // Exclude FPT-backed columns (M memo, G/P/Q/W binary): see the type-level docs.
            if (fieldTypes.TryGetValue(field, out char tc) && IsFptType(tc))
                continue;

            // SourceColumn is the DataTable column name the SELECT produced: item.Alias when aliased,
            // else the PHYSICAL field (NOT the dotted text — that would not match the DataTable column).
            _columns.Add((item.Alias ?? field, field));
        }

        if (_columns.Count == 0)
            throw new InvalidOperationException("Dynamic SQL generation found no updatable columns in the SelectCommand.");

        _parsedSelect = select;
        _parsedText = select.CommandText;
        _schemaParsed = true;
    }

    // Resolve a projected expression's PHYSICAL field name, or null if it is not a plain field
    // reference. Mirrors SelectExecutor: a bare identifier passes through; an alias-qualified reference
    // ('p.name', 'a.b.name') is reduced to the part after the last dot when that part is an identifier.
    private static string? PhysicalField(string text)
    {
        if (s_identifier.IsMatch(text))
            return text;

        string stripped = text.Replace(" ", "");
        int dot = stripped.LastIndexOf('.');
        if (dot >= 0)
            stripped = stripped[(dot + 1)..];
        return s_identifier.IsMatch(stripped) ? stripped : null;
    }

    private static bool IsFptType(char typeChar)
        => typeChar is 'M' or 'G' or 'P' or 'Q' or 'W';

    // Read the base table's field-type map (FIELD -> DBF type char) via the connection's schema. An open
    // connection is required because FPT-backed columns must be excluded from the generated DML, and the
    // builder otherwise has no schema knowledge.
    private Dictionary<string, char> ReadFieldTypes(FoxDbfCommand select)
    {
        if (select.Connection is not FoxDbfConnection conn || conn.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Generating INSERT / UPDATE / DELETE commands requires the SelectCommand's connection to be open " +
                "(the table schema is read to exclude memo/binary columns).");

        var map = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        var schema = conn.GetSchema("Columns", new string?[] { null, null, _table });
        foreach (DataRow r in schema.Rows)
        {
            if (r["COLUMN_NAME"] is string name && !string.IsNullOrEmpty(name) &&
                r["DATA_TYPE"] is string dt && dt.Length > 0)
                map[name] = char.ToUpperInvariant(dt[0]);
        }
        return map;
    }

    private void RequireRecno()
    {
        if (string.IsNullOrEmpty(_recnoColumn))
            throw new InvalidOperationException(
                "RECNO()-based row identity requires the SelectCommand to project 'RECNO() AS <alias>' " +
                "(a DBF has no primary key). Add it to enable UPDATE / DELETE generation.");
    }

    private FoxDbfCommand BuildInsert()
    {
        EnsureSchema();
        string cols = string.Join(", ", _columns.Select(c => c.Field));
        string marks = string.Join(", ", _columns.Select(_ => "?"));
        var cmd = NewCommand($"INSERT INTO {_table} ({cols}) VALUES ({marks})");
        foreach (var c in _columns)
            cmd.Parameters.Add(MakeParam(c.Result, DataRowVersion.Current));
        return cmd;
    }

    private FoxDbfCommand BuildUpdate()
    {
        EnsureSchema();
        RequireRecno();
        string sets = string.Join(", ", _columns.Select(c => $"{c.Field}=?"));
        var cmd = NewCommand($"UPDATE {_table} SET {sets} WHERE RECNO()=?");
        foreach (var c in _columns)
            cmd.Parameters.Add(MakeParam(c.Result, DataRowVersion.Current));
        cmd.Parameters.Add(MakeParam(_recnoColumn!, DataRowVersion.Original));
        return cmd;
    }

    private FoxDbfCommand BuildDelete()
    {
        EnsureSchema();
        RequireRecno();
        var cmd = NewCommand($"DELETE FROM {_table} WHERE RECNO()=?");
        cmd.Parameters.Add(MakeParam(_recnoColumn!, DataRowVersion.Original));
        return cmd;
    }

    private FoxDbfCommand NewCommand(string text)
    {
        var select = DataAdapter!.SelectCommand!;
        return new FoxDbfCommand(text, (FoxDbfConnection?)select.Connection)
        {
            CommandTimeout = select.CommandTimeout,
            UpdatedRowSource = UpdateRowSource.None,
        };
    }

    // Positional (?) parameter: empty name so FoxDbfCommand binds it by order. The base DbDataAdapter
    // populates Value from the row using SourceColumn + SourceVersion before each ExecuteNonQuery.
    private static FoxDbfParameter MakeParam(string sourceColumn, DataRowVersion version)
        => new()
        {
            ParameterName = string.Empty,
            SourceColumn = sourceColumn,
            SourceVersion = version,
        };

    // ---- DbCommandBuilder plumbing ---------------------------------------------------------
    // We generate the commands ourselves (above), so these abstract hooks only need to be coherent.
    // Parameters are positional '?' markers (no names), and per-row value/type comes from binding.

    protected override void ApplyParameterInfo(
        DbParameter parameter, DataRow row, StatementType statementType, bool whereClause)
    {
        // FoxDbfCommand renders the VFP literal from the runtime value at bind time, so there is no
        // provider-specific type info to stamp onto the parameter here.
    }

    protected override string GetParameterName(int parameterOrdinal) => "?";

    protected override string GetParameterName(string parameterName) => parameterName;

    protected override string GetParameterPlaceholder(int parameterOrdinal) => "?";

    public override string QuoteIdentifier(string unquotedIdentifier) => unquotedIdentifier;

    public override string UnquoteIdentifier(string quotedIdentifier) => quotedIdentifier;

    // Subscribe / unsubscribe our row-updating handler on the adapter (standard DbCommandBuilder
    // pattern). On add the call arrives while base.DataAdapter is still the OLD value (null here),
    // on remove it arrives while base.DataAdapter still equals the adapter being detached.
    protected override void SetRowUpdatingHandler(DbDataAdapter adapter)
    {
        var a = (FoxDbfDataAdapter)adapter;
        if (ReferenceEquals(adapter, base.DataAdapter))
            a.RowUpdating -= RowUpdatingHandler;
        else
            a.RowUpdating += RowUpdatingHandler;
    }

    private void RowUpdatingHandler(object? sender, RowUpdatingEventArgs e)
    {
        if (e.Status != UpdateStatus.Continue) return;
        if (e.Command is not null) return; // a user-supplied command wins.

        try
        {
            var cmd = e.StatementType switch
            {
                StatementType.Insert => GetInsertCommand(),
                StatementType.Update => GetUpdateCommand(),
                StatementType.Delete => GetDeleteCommand(),
                _ => null,
            };
            if (cmd is null) return;

            // The base DbDataAdapter binds parameter VALUES from the row BEFORE this event supplies the
            // command, so an event-supplied command's parameters would otherwise stay unbound. Populate
            // them here from the row using each parameter's SourceColumn + SourceVersion.
            if (e.Row is { } row)
                PopulateParameters(cmd, row);

            e.Command = cmd;
        }
        catch (Exception ex)
        {
            e.Status = UpdateStatus.ErrorsOccurred;
            e.Errors = ex;
        }
    }

    private static void PopulateParameters(FoxDbfCommand cmd, DataRow row)
    {
        var columns = row.Table.Columns;
        foreach (DbParameter p in cmd.Parameters)
        {
            if (string.IsNullOrEmpty(p.SourceColumn)) continue;
            if (p.Direction is not (ParameterDirection.Input or ParameterDirection.InputOutput)) continue;

            var col = columns[p.SourceColumn];
            if (col is null)
            {
                // A missing RECNO() identity column (Original version) would otherwise bind .NULL. into
                // 'WHERE RECNO()=?', silently matching NO records (0-row UPDATE/DELETE). Fail loudly.
                // Triggers: RECNO() projected WITHOUT an alias (the DataTable names it 'EXP_n', not
                // 'RECNO()'), or the recno column removed after Fill.
                if (p.SourceVersion == DataRowVersion.Original)
                    throw new InvalidOperationException(
                        $"RECNO() identity column '{p.SourceColumn}' not found in the DataTable; " +
                        "project RECNO() AS <alias> and do not remove it after Fill.");
                p.Value = null;
                continue;
            }

            object raw = row[col, p.SourceVersion];
            p.Value = raw is DBNull ? null : raw; // FoxDbfCommand renders null as .NULL.
        }
    }
}
