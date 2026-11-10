using System;
using CrossVault.FoxDbf.Expressions;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// A command over a <see cref="FoxDbfConnection"/>. <see cref="DbCommand.CommandText"/> is either a
/// VFP-SQL statement (SELECT / INSERT / UPDATE / DELETE) or a work-area command (USE / SELECT &lt;area&gt;),
/// parsed with the <c>SqlParser</c>; <see cref="CommandType.TableDirect"/> treats CommandText as a bare
/// table name (<c>SELECT * FROM &lt;name&gt;</c>).
/// <para>
/// PARAMETER BINDING (approach + limits): parameters are bound by SAFE typed-literal substitution into
/// the SQL text. A single literal-aware scan (mirroring <c>SqlLexer</c>'s rules) skips markers that sit
/// inside string / bracket / date literals and <c>&amp;&amp;</c> comments, and emits each value verbatim
/// (never via a regex replacement template). VFP quoting:
/// <list type="bullet">
/// <item>strings: delimited with the first of <c>'</c> / <c>"</c> / <c>[ ]</c> the value does NOT
/// contain (doubling is unsafe — the lexer does no un-escaping); a value containing all three falls
/// back to <c>CHR()</c>-concatenation; a NUL byte is rejected;</item>
/// <item>DateTime: always a VFP datetime/T literal <c>{^yyyy-mm-dd HH:mm:ss}</c>, including midnight;
/// DateOnly alone uses the date/D literal <c>{^yyyy-mm-dd}</c>;</item>
/// <item>numbers: invariant culture; booleans: <c>.T.</c>/<c>.F.</c>; null: <c>.NULL.</c>;</item>
/// <item>byte[] (binary): NOT supported — throws, since no faithful VFP literal exists.</item>
/// </list>
/// Markers: positional <c>?</c> (bound by order) and named <c>@name</c> / <c>:name</c> (longest match wins).
/// </para>
/// </summary>
public sealed class FoxDbfCommand : DbCommand
{
    private readonly FoxDbfParameterCollection _parameters = new();
    private FoxDbfConnection? _connection;
    private string _commandText = string.Empty;

    public FoxDbfCommand() { }

    public FoxDbfCommand(string? commandText) => _commandText = commandText ?? string.Empty;

    public FoxDbfCommand(string? commandText, FoxDbfConnection? connection)
    {
        _commandText = commandText ?? string.Empty;
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    public override bool DesignTimeVisible { get; set; } = true;

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (FoxDbfConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() { /* no-op */ }

    public override void Prepare() { /* no-op */ }

    protected override DbParameter CreateDbParameter() => new FoxDbfParameter();

    public override int ExecuteNonQuery()
    {
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        if (CommandType == CommandType.StoredProcedure)
            return ExecuteStoredProcedureNonQuery();
        if (TryGetAdHocExpression(_commandText, out var expr))
        {
            EvaluateAdHocExpression(expr);
            return -1;
        }

        string sql = BindParameters(_commandText);

        if (CommandType == CommandType.TableDirect)
        {
            sql = $"SELECT * FROM {CommandText}";
        }

        if (ShouldEnforce(sql))
            return ExecuteEnforcedDml(sql);

        EnsureMemoryBridgeFor(sql);
        var result = ExecuteSql(sql);
        return result?.AffectedRecords ?? -1;
    }

    public override object? ExecuteScalar()
    {
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        if (CommandType == CommandType.StoredProcedure)
            return ExecuteStoredProcedureScalar();
        if (TryGetAdHocExpression(_commandText, out var expr))
            return EvaluateAdHocExpression(expr);

        string sql = BindParameters(_commandText);

        if (CommandType == CommandType.TableDirect)
        {
            sql = $"SELECT * FROM {CommandText}";
        }

        if (ShouldEnforce(sql))
        {
            ExecuteEnforcedDml(sql);
            return null;
        }

        EnsureMemoryBridgeFor(sql);
        var result = ExecuteSql(sql);
        if (result?.Rows == null) return null;

        foreach (var row in result.Rows)
        {
            if (row != null && row.Length > 0)
                return row[0] ?? DBNull.Value;
        }
        return null;
    }

    private SqlResult? ExecuteSql(string sql)
    {
        try { return _connection!.Session.Execute(sql); }
        catch (Exception error) when (error is not DbException
            && (error is IVfpErrorCode { VfpErrorNumber: 39 or 1988 }
                || error is ExpressionException { VfpErrorNumber: 39 or 1988 }))
        {
            int code = error is IVfpErrorCode coded ? coded.VfpErrorNumber!.Value
                : ((ExpressionException)error).VfpErrorNumber!.Value;
            throw new FoxDbfException(error.Message, code, error);
        }
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        _connection.ReserveActiveReaderSlot();
        try
        {
            var result = BuildResultSet(behavior);
            var reader = new FoxDbfDataReader(result, _connection, behavior);
            _connection.CommitActiveReaderSlot(reader);
            return reader;
        }
        catch
        {
            _connection.SetActiveReader(null);
            throw;
        }
    }

    /// <summary>Run this command and return its single <see cref="SqlResult"/> WITHOUT wrapping it in a
    /// reader or registering it as the connection's active reader — the seam <see cref="FoxDbfBatch"/> uses
    /// to collect one result set per batch command and stitch them behind a single multi-result reader.</summary>
    internal SqlResult BuildResultSet(CommandBehavior behavior)
    {
        if (_connection is null || _connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        if (CommandType == CommandType.StoredProcedure)
            return SingleValueResult(
                _connection.Interpreter.Call(_commandText.Trim(), CollectStoredProcedureArgs()).ToClr());
        if (TryGetAdHocExpression(_commandText, out var adHoc))
            return SingleValueResult(_connection.Interpreter.EvalExpression(adHoc).ToClr());

        string sql = BindParameters(_commandText);

        if (CommandType == CommandType.TableDirect)
        {
            sql = $"SELECT * FROM {CommandText}";
        }

        // DML via ExecuteReader is a caller error — and under EnforceRules it would otherwise SILENTLY skip
        // all DEFAULT/RULE/TRIGGER enforcement (the raw Session.Execute below never enforces). Reject it.
        if (ShouldEnforce(sql))
            throw new FoxDbfException("ExecuteReader cannot be used with DML statements; use ExecuteNonQuery.");

        EnsureMemoryBridgeFor(sql);
        var result = ExecuteSql(sql);
        if (result == null)
            throw new FoxDbfException("ExecuteReader can only be used with SELECT statements.");

        return result;
    }

    // ---- microVFP seams (SP / UDF + EnforceRules write-model) ----------------------------------
    //
    // These route through the connection's shared VfpInterpreter (loaded from the open DBC's
    // StoredProceduresSource, sharing the session's work areas). The RAW DML / SELECT paths above are
    // untouched when EnforceRules is off and CommandType is Text.

    /// <summary>An ad-hoc microVFP expression command — CommandText trimmed begins with <c>?</c> or <c>=</c>
    /// (e.g. <c>?UPPER('x')='X'</c> / <c>=UPPER('abc')</c>); the remainder is the expression to evaluate.</summary>
    private static bool TryGetAdHocExpression(string? text, out string expression)
    {
        expression = string.Empty;
        string t = (text ?? string.Empty).TrimStart();
        if (t.Length == 0) return false;
        if (t[0] is '?' or '=') { expression = t.Substring(1).Trim(); return true; }
        return false;
    }

    /// <summary>
    /// Ensure the connection's shared <c>VfpInterpreter</c> exists BEFORE a raw <c>Session.Execute</c> that
    /// reaches the memvar store — <c>SELECT … INTO ARRAY</c> (writes an array) or <c>INSERT … FROM
    /// ARRAY|MEMVAR</c> (reads one). Touching the interpreter registers its <c>IVfpMemoryBridge</c> on the
    /// session, so the SQL engine can land / read the active-session arrays. A cheap keyword sniff; a false
    /// positive merely instantiates the (cached, idempotent) interpreter a little earlier — harmless.
    /// </summary>
    private void EnsureMemoryBridgeFor(string? sql)
    {
        // Precise word-boundary match on the actual clauses (INTO ARRAY / FROM ARRAY / FROM MEMVAR) so an
        // unrelated statement — a column named `arrayfield`, a table `arrays` — does NOT needlessly create the
        // interpreter (which shares + adjusts the session's SET state).
        if (sql is not null && MemorySourceRx.IsMatch(sql))
            _ = _connection!.Interpreter;
    }

    private static readonly System.Text.RegularExpressions.Regex MemorySourceRx = new(
        @"\b(INTO\s+ARRAY|FROM\s+ARRAY|FROM\s+MEMVAR)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the opt-in VFP write-model must run for <paramref name="sql"/>: EnforceRules is on,
    /// a DBC is open (enforcement is a DBC concept — a free-table / .dbf-only connection has no DEFAULT/RULE/
    /// TRIGGER metadata, so its writes stay on the raw DML path), AND the statement is a writing DML.</summary>
    private bool ShouldEnforce(string? sql)
        => _connection!.EnforceRules && _connection.Session.Database is not null && IsWriteDml(sql);

    /// <summary>True when <paramref name="sql"/>'s leading keyword is a writing DML (INSERT/UPDATE/DELETE) —
    /// the statements the opt-in EnforceRules path runs through the VFP write-model.</summary>
    private static bool IsWriteDml(string? sql)
    {
        string t = (sql ?? string.Empty).TrimStart();
        return t.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collect the command's parameters as positional microVFP arguments (insertion order), so a
    /// stored procedure / UDF receives them BY VALUE in declaration order.</summary>
    private CrossVault.FoxDbf.Expressions.VfpValue[] CollectStoredProcedureArgs()
    {
        var args = new CrossVault.FoxDbf.Expressions.VfpValue[_parameters.Count];
        for (int i = 0; i < _parameters.Count; i++)
            args[i] = CrossVault.FoxDbf.Expressions.VfpValue.FromClr(((DbParameter)_parameters[i]).Value);
        return args;
    }

    private object? ExecuteStoredProcedureScalar()
    {
        var result = _connection!.Interpreter.Call(_commandText.Trim(), CollectStoredProcedureArgs());
        return result.ToClr();
    }

    private int ExecuteStoredProcedureNonQuery()
    {
        _connection!.Interpreter.Call(_commandText.Trim(), CollectStoredProcedureArgs());
        return -1;   // a void-style proc call: no row count.
    }

    private object? EvaluateAdHocExpression(string expression)
        => _connection!.Interpreter.EvalExpression(expression).ToClr();

    /// <summary>Wrap a single scalar microVFP result (a SP RETURN value / an ad-hoc expression) as a
    /// one-row, one-column <c>"result"</c> <see cref="SqlResult"/>, so an idiomatic <c>ExecuteReader</c>
    /// works too.</summary>
    private static SqlResult SingleValueResult(object? value)
    {
        var (vfpType, length, decimals, clrType) = value switch
        {
            bool => ('L', 1, 0, typeof(bool)),
            int => ('I', 4, 0, typeof(int)),
            long => ('N', 20, 0, typeof(decimal)),
            decimal => ('N', 20, 4, typeof(decimal)),
            double or float => ('B', 8, 0, typeof(double)),
            DateTime => ('T', 8, 0, typeof(DateTime)),
            DateOnly => ('D', 8, 0, typeof(DateOnly)),
            _ => ('C', 254, 0, typeof(string)),
        };
        object? boxed = clrType == typeof(string) ? value?.ToString() ?? string.Empty : value;
        var columns = new[] { new SqlColumn("result", vfpType, length, decimals, clrType) };
        return new SqlResult(columns, new[] { new[] { boxed } });
    }

    private int ExecuteEnforcedDml(string sql)
        => new FoxDbfEnforcedWriteModel(_connection!, _connection!.Interpreter).Execute(sql);

    // ---- parameter binding ---------------------------------------------------

    private string BindParameters(string sql)
    {
        // Collect positional (?) and named (@name, :name) parameters
        var positionalParams = new List<DbParameter>();
        var namedParams = new Dictionary<string, DbParameter>(StringComparer.OrdinalIgnoreCase);

        foreach (DbParameter p in _parameters)
        {
            string name = p.ParameterName?.TrimStart('@', ':') ?? "";
            if (name.Length == 0)
                positionalParams.Add(p);
            else
                namedParams[name] = p;
        }

        // Single left-to-right scan that mirrors SqlLexer's literal rules: string ('…' / "…"),
        // bracket-string ([…]), date ({…}) literals and && line comments are copied verbatim so a
        // ?, @name or :name appearing INSIDE a literal is never treated as a parameter marker. This
        // also makes the positional count match only the real markers (no false "Too many"/"Not
        // enough"). The literal text we emit per parameter is produced verbatim — never re-parsed as
        // a regex replacement template — so values containing $, &, ] etc. cannot inject.
        var sb = new System.Text.StringBuilder(sql.Length);
        int posIndex = 0;
        int i = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];

            // && line comment
            if (c == '&' && i + 1 < n && sql[i + 1] == '&')
            {
                int start = i;
                i += 2;
                while (i < n && sql[i] != '\n') i++;
                sb.Append(sql, start, i - start);
                continue;
            }

            // 'string' / "string" literal: scan to the first matching quote (SqlLexer rule).
            if (c == '\'' || c == '"')
            {
                char q = c;
                int start = i;
                i++;
                while (i < n && sql[i] != q) i++;
                if (i < n) i++; // closing quote
                sb.Append(sql, start, i - start);
                continue;
            }

            // [bracket-string] literal: scan to the first ].
            if (c == '[')
            {
                int start = i;
                i++;
                while (i < n && sql[i] != ']') i++;
                if (i < n) i++; // closing bracket
                sb.Append(sql, start, i - start);
                continue;
            }

            // {date} literal: scan to the first }.
            if (c == '{')
            {
                int start = i;
                i++;
                while (i < n && sql[i] != '}') i++;
                if (i < n) i++; // closing brace
                sb.Append(sql, start, i - start);
                continue;
            }

            // positional marker
            if (c == '?')
            {
                if (posIndex >= positionalParams.Count)
                    throw new FoxDbfException("Not enough positional parameters supplied.");
                sb.Append(ToVfpLiteral(positionalParams[posIndex].Value));
                posIndex++;
                i++;
                continue;
            }

            // named marker @name / :name (longest identifier wins naturally, so @id never clobbers @id2)
            if (c == '@' || c == ':')
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(sql[j]) || sql[j] == '_')) j++;
                if (j > i + 1)
                {
                    string name = sql.Substring(i + 1, j - i - 1);
                    if (namedParams.TryGetValue(name, out var prm))
                    {
                        sb.Append(ToVfpLiteral(prm.Value));
                        i = j;
                        continue;
                    }
                }
                // Not a known named parameter -> copy the marker char verbatim.
                sb.Append(c);
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        if (posIndex < positionalParams.Count)
            throw new FoxDbfException("Too many positional parameters supplied.");

        return sb.ToString();
    }

    internal static string ToVfpLiteral(object? value)
    {
        if (value == null) return ".NULL.";
        if (value is string s) return EscapeString(s);
        if (value is bool b) return b ? ".T." : ".F.";
        if (value is DateTime dt)
            // A CLR DateTime is always a VFP T value, including midnight; DateOnly is the D form.
            return $"{{^{dt:yyyy-MM-dd HH:mm:ss}}}";
        if (value is DateOnly d) return $"{{^{d:yyyy-MM-dd}}}";
        if (value is decimal d1) return d1.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is double d2) return d2.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is int i) return i.ToString();
        if (value is long l) return l.ToString();
        if (value is byte[] ba) return ToVfpBinaryLiteral(ba);
        // Fallback: convert to string and escape
        return EscapeString(value.ToString() ?? "");
    }

    private static string EscapeString(string s)
    {
        // SqlLexer scans 'string' / "string" / [string] to the FIRST closing delimiter with NO
        // un-escaping (SqlLexer.cs:62-84). So "doubling" is unsafe; the only safe move is to pick a
        // delimiter the value does NOT contain. Try the three VFP string delimiters in turn; if the
        // value contains all of ' " and ], fall back to CHR()-concatenation. VFP cannot represent a
        // NUL byte in a string literal, so reject it.
        if (s.IndexOf('\0') >= 0)
            throw new NotSupportedException("String parameters cannot contain a NUL (CHR(0)) character; VFP string literals cannot represent it.");

        if (!s.Contains('\'')) return "'" + s + "'";
        if (!s.Contains('"')) return "\"" + s + "\"";
        if (!s.Contains(']')) return "[" + s + "]";
        return ChrConcat(s);
    }

    // Build a VFP expression for a string that contains all three delimiters, by emitting runs of
    // non-quote chars as 'segments' and each embedded single-quote as CHR(39). Embedded " and ] are
    // safe inside a single-quoted segment (the lexer scans to the first '). Round-trips exactly.
    private static string ChrConcat(string s)
    {
        var parts = new List<string>();
        var seg = new System.Text.StringBuilder();
        void Flush() { if (seg.Length > 0) { parts.Add("'" + seg + "'"); seg.Clear(); } }
        foreach (char ch in s)
        {
            if (ch == '\'') { Flush(); parts.Add("CHR(39)"); }
            else seg.Append(ch);
        }
        Flush();
        return parts.Count == 0 ? "''" : string.Join("+", parts);
    }

    private static string ToVfpBinaryLiteral(byte[] data)
        // A byte[] has no faithful VFP literal form: rendering it as hex TEXT would silently compare
        // hex against binary and never match a G/Q/W/M field. Reject rather than be silently wrong.
        => throw new NotSupportedException("Binary (byte[]) parameters cannot be represented as VFP literals.");
}
