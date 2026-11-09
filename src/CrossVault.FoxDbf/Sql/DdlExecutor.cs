using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// The VFP-SQL DDL executor (CREATE TABLE / ALTER TABLE / DROP TABLE). It maps the parsed DDL AST
/// onto the Core write surface — <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
/// for CREATE, the Core ALTER primitives (<see cref="DbfWriter.AddColumn"/>, <see cref="DbfWriter.DropColumn"/>,
/// <see cref="DbfWriter.ModifyColumn"/>) for ALTER, and file removal for DROP — resolving the data
/// directory / current DBC context through the owning <see cref="VfpSession"/>.
/// <para>
/// A DDL statement returns a <see cref="SqlResult.Dml"/> with a zero affected-record count, so the
/// ADO.NET <c>ExecuteNonQuery</c> reports <c>0</c> for a successful DDL command.
/// </para>
/// </summary>
/// <remarks>
/// The current FoxPro DBC reader is read-only (it carries no member add/remove primitive), so a
/// <c>db!name</c> form (CREATE / ALTER / DROP of a DATABASE-CONTAINED member) is rejected up-front
/// with a clear <see cref="NotSupportedException"/> rather than silently producing a free table.
/// CREATE without FREE also requires membership whenever a database is current.
/// Likewise RENAME COLUMN — which the Core ALTER cannot perform without discarding the column's data —
/// is rejected as not yet supported.
/// </remarks>
internal sealed class DdlExecutor
{
    private readonly VfpSession _session;

    public DdlExecutor(VfpSession session) => _session = session;

    /// <summary>Executes one DDL statement, returning a zero-count <see cref="SqlResult.Dml"/>.</summary>
    public SqlResult Run(SqlStatement statement) => statement switch
    {
        CreateTableStatement c => CreateTable(c),
        AlterTableStatement a => AlterTable(a),
        DropTableStatement d => DropTable(d),
        _ => throw new NotSupportedException($"'{statement.GetType().Name}' is not a DDL statement."),
    };

    // =====================================================================================
    //  CREATE TABLE
    // =====================================================================================

    private SqlResult CreateTable(CreateTableStatement st)
    {
        RejectDbcMember(st.Database, "CREATE TABLE");
        byte codePage = CreateCodePage(st);
        if (st.LongName is not null && _session.Database is null)
            throw new FoxDbfSqlException("No database is open or set as the current database.")
                { VfpErrorNumber = 1520 };
        if (st.RequiresDatabase && (st.Free || _session.Database is null))
            throw new FoxDbfSqlException("Feature is not supported for non-.DBC tables.")
                { VfpErrorNumber = 1580 };
        // VFP9 implicitly registers a new table in the current DBC unless FREE is stated.
        // Reject before creating files: an unregistered free table would silently lose that contract.
        if (!st.Free && _session.Database is not null)
            throw new NotSupportedException(
                "CREATE TABLE in the current database is not supported: DBC member registration is not implemented. " +
                "Use FREE to explicitly create a free table.");
        if (st.UnsupportedClause is { } clause)
            throw new NotSupportedException($"CREATE TABLE ... {clause} is not yet supported; no table was created.");
        RejectBareMember(st.Table);

        string path = TargetPath(st.Table);
        if (File.Exists(path))
            throw new FoxDbfSqlException($"Table '{st.Table}' already exists at '{path}'.");

        // Inside a transaction: record the to-be-created table so a Rollback deletes the new files
        // (without this, CREATE would silently survive a rollback). No-op in autocommit.
        _session.SnapshotForDdl(path);

        var defs = new List<DbfColumnDef>(st.Columns.Count);
        foreach (var col in st.Columns)
            defs.Add(ToColumnDef(col));

        // DbfWriter.Create writes the .dbf (+ .fpt) and reopens it as a ready writer; close it
        // immediately — the new table is then resolved on disk by subsequent INSERT / SELECT.
        using (DbfWriter.Create(path, defs, new DbfCreateOptions { CodePage = codePage }))
        {
        }

        return SqlResult.Dml(0);
    }

    private byte CreateCodePage(CreateTableStatement st)
    {
        if (st.CodePage is null) return 0x03;
        var value = _session.DdlExpressionEvaluator is { } evaluate
            ? evaluate(st.CodePage)
            : st.CodePage.Evaluate(ConstantRow.Instance, _session.SqlContext());
        if (value.Type is not (VfpType.Numeric or VfpType.Integer or VfpType.Currency))
            throw new FoxDbfSqlException("Syntax error.") { VfpErrorNumber = 10 };

        // VFP9 truncates fractional numeric values and uses these canonical language-driver
        // bytes. Reader aliases (e.g. CP860/1255/1256) are NOT accepted by CREATE TABLE.
        double cp = Math.Truncate(value.AsDouble);
        return cp switch
        {
            0 => 0x00, 437 => 0x01, 850 => 0x02, 1252 => 0x03, 10000 => 0x04,
            852 => 0x64, 866 => 0x65, 865 => 0x66, 861 => 0x67, 895 => 0x68,
            620 => 0x69, 737 => 0x6a, 857 => 0x6b, 863 => 0x6c,
            950 => 0x78, 949 => 0x79, 936 => 0x7a, 932 => 0x7b, 874 => 0x7c,
            10007 => 0x96, 10029 => 0x97, 10006 => 0x98,
            1250 => 0xc8, 1251 => 0xc9, 1254 => 0xca, 1253 => 0xcb, 1257 => 0xcc,
            _ => throw new FoxDbfSqlException("Code page number is invalid.") { VfpErrorNumber = 1914 },
        };
    }

    private sealed class ConstantRow : IRowContext
    {
        public static readonly ConstantRow Instance = new();
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name)
            => throw new FoxDbfSqlException($"Variable '{name}' is not found.") { VfpErrorNumber = 12 };
    }

    /// <summary>Map a parsed VFP <see cref="ColumnDefinition"/> onto a Core <see cref="DbfColumnDef"/>.
    /// Fixed-width types (I/B/Y/D/T/L/M/G/P) get their canonical width auto-filled by the column-def
    /// constructor when the syntax states no length. Nullability follows the column's explicit NULL /
    /// NOT NULL clause; absent one, it defaults to the ambient <c>SET NULL</c> (VFP default OFF ⇒ NOT NULL).</summary>
    private DbfColumnDef ToColumnDef(ColumnDefinition c)
    {
        char type = char.ToUpperInvariant(c.Type);
        // Varbinary (Q) and Blob (W) are NOCPTRANS binary types in VFP.
        bool binary = type is 'Q' or 'W';
        return new DbfColumnDef(
            name: c.Name,
            type: type,
            length: c.Length ?? 0,
            decimalCount: DefaultDecimals(type, c.Decimals),
            nullable: c.Nullable ?? _session.Context.NullSetting,
            binary: binary);
    }

    /// <summary>The descriptor decimal-count for a DDL column, matching the AUTHORITATIVE VFP9
    /// behaviour (verified against the VFP9 runtime): Currency (<c>Y</c>) always stores <c>4</c>; Double
    /// (<c>B</c>) stores the ambient <c>SET DECIMALS</c> (VFP default <c>2</c>) when the syntax
    /// states none; every other type keeps the stated decimals (or <c>0</c>).</summary>
    private static int DefaultDecimals(char type, int? stated) => type switch
    {
        'Y' => 4,             // Currency: VFP writes a fixed 4 to the descriptor.
        'B' => stated ?? 2,   // Double: VFP writes SET DECIMALS (default 2) when unspecified.
        _ => stated ?? 0,
    };

    // =====================================================================================
    //  ALTER TABLE
    // =====================================================================================

    private SqlResult AlterTable(AlterTableStatement st)
    {
        RejectDbcMember(st.Database, "ALTER TABLE");
        RejectBareMember(st.Table);

        string path = ExistingPath(st.Table);

        // Inside a transaction: snapshot the live .dbf + sidecars before the schema rewrite so a
        // Rollback restores them byte-for-byte (the Core ALTER rewrites the file irreversibly). No-op
        // in autocommit.
        _session.SnapshotForDdl(path);

        // Release any work area that holds a (stale) handle on this file, then re-open them after the
        // in-place rewrite — otherwise DbfWriter.Open collides with the open read handle and the area
        // would keep a view of the pre-ALTER structure.
        var reopen = _session.CloseAreasForPath(Path.GetFullPath(path));
        try
        {
            using var writer = DbfWriter.Open(path);
            var action = st.Action;
            switch (action.Kind)
            {
            case AlterTableActionKind.AddColumn:
                writer.AddColumn(ToColumnDef(action.Column!));
                break;
            case AlterTableActionKind.AlterColumn:
                writer.ModifyColumn(action.Column!.Name, ToColumnDef(action.Column!));
                break;
            case AlterTableActionKind.DropColumn:
                writer.DropColumn(action.DropName!);
                break;
            case AlterTableActionKind.RenameColumn:
                throw new NotSupportedException(
                    "ALTER TABLE ... RENAME COLUMN is not yet supported (the Core ALTER cannot rename a " +
                    "column without discarding its data).");
            default:
                throw new NotSupportedException($"ALTER TABLE action '{action.Kind}' is not supported.");
            }
        }
        finally
        {
            // Re-open the areas we closed (best-effort) so the session keeps the same aliases it had.
            _session.ReopenAreas(reopen);
        }

        return SqlResult.Dml(0);
    }

    // =====================================================================================
    //  DROP TABLE
    // =====================================================================================

    private SqlResult DropTable(DropTableStatement st)
    {
        RejectDbcMember(st.Database, "DROP TABLE");
        RejectBareMember(st.Table);

        string path = TargetPath(st.Table);
        if (!File.Exists(path))
        {
            if (st.IfExists) return SqlResult.Dml(0);
            throw new FoxDbfSqlException($"Table '{st.Table}' was not found in the data directory.") { VfpErrorNumber = 1 }; // VFP err 1 "does not exist".
        }

        // Inside a transaction: snapshot the live .dbf + sidecars BEFORE deleting them so a Rollback
        // restores the table (without this, DROP would be permanent, unrecoverable data loss even after
        // a rollback). No-op in autocommit.
        _session.SnapshotForDdl(path);

        // Release any work area still holding a handle on the target BEFORE deleting (Core opens
        // without FileShare.Delete, so an open area would make File.Delete throw a sharing violation).
        // The table is going away, so these areas are NOT re-opened.
        _session.CloseAreasForPath(Path.GetFullPath(path));

        // Remove the .dbf and its companion side files (.fpt memo, .cdx structural index).
        Delete(path);
        Delete(SidecarFile.ChangeExtension(path, ".fpt"));
        Delete(SidecarFile.ChangeExtension(path, ".cdx"));

        return SqlResult.Dml(0);
    }

    // =====================================================================================
    //  helpers
    // =====================================================================================

    private static void RejectDbcMember(string? database, string op)
    {
        if (database is not null)
            throw new NotSupportedException(
                $"{op} for a database-contained member ('{database}!...') is not supported: the FoxPro " +
                "database container (.dbc) is opened read-only and has no member add/remove primitive.");
    }

    /// <summary>Rejects a BARE-name DDL target that is actually a member of the open <c>.dbc</c>.
    /// Without this guard a bare CREATE/ALTER/DROP would silently operate on free files in the .dbc's
    /// directory — DROP would orphan the member record (DBC corruption), ALTER would rewrite the file
    /// behind the DBC's back, and CREATE would add an unregistered free file where VFP adds a member.
    /// The reader-only DBC has no member add/remove primitive, so this is a clear NotSupported.</summary>
    private void RejectBareMember(string tableName)
    {
        if (_session.Database is { } db &&
            db.TableNames.Any(n => StringComparer.OrdinalIgnoreCase.Equals(n, tableName)))
            throw new NotSupportedException(
                $"DDL on the database-contained member '{tableName}' is not supported (the .dbc is opened " +
                "read-only and has no member add/remove primitive); close the database to operate on a free table.");
    }

    /// <summary>The on-disk <c>.dbf</c> path for <paramref name="name"/> in the session data directory
    /// (whether or not it exists yet).</summary>
    private string TargetPath(string name)
    {
        string dir = _session.DataDirectory
                     ?? throw new FoxDbfSqlException("No data directory is open; DDL needs an open directory or database.");
        string file = name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) ? name : name + ".dbf";
        return Path.Combine(dir, file);
    }

    /// <summary>The on-disk <c>.dbf</c> path for an EXISTING table; throws when it is not found.</summary>
    private string ExistingPath(string name)
    {
        string path = TargetPath(name);
        if (!File.Exists(path))
            throw new FoxDbfSqlException($"Table '{name}' was not found in the data directory.") { VfpErrorNumber = 1 }; // VFP err 1 "does not exist".
        return path;
    }

    private static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (IOException ex)
        {
            // Another process (e.g. a VFP session) still holds the file open — surface a clear DDL
            // error instead of a raw sharing-violation IOException bubbling out of DROP TABLE.
            throw new FoxDbfSqlException(
                $"Cannot drop '{Path.GetFileName(path)}': the file is in use by another process.", ex);
        }
    }
}
