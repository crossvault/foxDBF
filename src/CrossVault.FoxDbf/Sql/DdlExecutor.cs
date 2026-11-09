using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
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
/// CREATE without FREE registers the table, fields, rules and keys in the current database.
/// ALTER / DROP of database members and RENAME COLUMN retain their existing unsupported boundary.
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
        if (st.Database is not null)
            throw new FoxDbfSqlException("Object name is invalid.") { VfpErrorNumber = 1575 };
        if (st.FromArray is not null) st = ResolveArray(st);
        byte codePage = CreateCodePage(st);
        if (st.LongName is not null && st.FromArray is null && _session.Database is null)
            throw new FoxDbfSqlException("No database is open or set as the current database.")
                { VfpErrorNumber = 1520 };
        if (st.RequiresDatabase && (st.Free || _session.Database is null))
            throw new FoxDbfSqlException("Feature is not supported for non-.DBC tables.")
                { VfpErrorNumber = 1580 };
        bool member = !st.Free && _session.Database is not null;
        var logicalFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in st.Columns)
        {
            if (!logicalFields.Add(column.Name) || (!member && column.Name.Length > 10))
                throw new FoxDbfSqlException("Field name is a duplicate or invalid.") { VfpErrorNumber = 1712 };
            if (column.AutoIncrement is not null && column.Type != 'I')
                throw new FoxDbfSqlException("Invalid autoincrement field type.") { VfpErrorNumber = 1804 };
            if (column.AutoIncrement is not null && column.Nullable == true)
                throw new FoxDbfSqlException("Function argument value, type, or count is invalid.") { VfpErrorNumber = 11 };
            if (column.NoCpTrans && column.Type is not ('C' or 'V' or 'M'))
                throw new FoxDbfSqlException("Syntax error.") { VfpErrorNumber = 10 };
        }
        if (st.Keys.Any(k => k.Tag.Length > 10))
            throw new FoxDbfSqlException("Tag name is too long.") { VfpErrorNumber = 1888 };
        if (st.Keys.Count(k => k.Kind == "PRIMARY") > 1)
            throw new FoxDbfSqlException("A primary key already exists.") { VfpErrorNumber = 1883 };
        string logicalName = st.LongName ?? Path.GetFileNameWithoutExtension(st.Table);
        if (member && _session.Database!.TableNames.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
            throw new FoxDbfSqlException($"Table '{logicalName}' already exists.") { VfpErrorNumber = 1571 };

        string path = TargetPath(st.Table);
        if (File.Exists(path))
            throw new FoxDbfSqlException($"Table '{st.Table}' already exists at '{path}'.");

        // Inside a transaction: record the to-be-created table so a Rollback deletes the new files
        // (without this, CREATE would silently survive a rollback). No-op in autocommit.
        _session.SnapshotForDdl(path);

        var columns = st.Columns.Select(c => c with
        {
            Default = Normalize(c.Default), Check = Normalize(c.Check), Error = Normalize(c.Error)
        }).ToArray();
        var defs = new List<DbfColumnDef>(columns.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
        {
            string name = col.Name[..Math.Min(10, col.Name.Length)];
            for (int suffix = 2; !names.Add(name); suffix++)
            {
                string number = suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                name = col.Name[..Math.Min(10 - number.Length, col.Name.Length)] + number;
            }
            defs.Add(ToColumnDef(col with { Name = name }));
        }
        var keys = ResolveReferences(st.Keys, logicalName);
        string? backlink = member ? Path.GetRelativePath(Path.GetDirectoryName(path)!, _session.DatabasePath!).ToLowerInvariant() : null;
        string[] createdFiles = [path, SidecarFile.ChangeExtension(path, ".fpt"), SidecarFile.ChangeExtension(path, ".cdx")];
        if (createdFiles.Skip(1).Any(File.Exists))
            throw new FoxDbfSqlException("A sidecar for the new table already exists.");
        string stagedPath = Path.Combine(Path.GetDirectoryName(path)!, ".foxdbf-create-" + Guid.NewGuid().ToString("N") + ".dbf");
        string[] stagedFiles = [stagedPath, Path.ChangeExtension(stagedPath, ".fpt"), Path.ChangeExtension(stagedPath, ".cdx")];
        var published = new List<string>();
        bool registered = false;
        try
        {
            using (DbfWriter.Create(stagedPath, defs, _session.NewTableOptions with { CodePage = codePage, BacklinkPath = backlink })) { }
            if (keys.Length > 0)
            {
                using var writer = DbfWriter.Open(stagedPath, new DbfOptions { LockMode = LockMode.Exclusive },
                    physical => DbfDatabase.ApplyLongNames(physical, columns.Select(c => c.Name).ToArray()));
                foreach (var key in keys)
                    writer.CreateTag(new CdxTagDefinition(key.Tag.ToUpperInvariant(), key.Expression, collation: _session.Context.Collation.Name)
                        { Candidate = key.Kind is "PRIMARY" or "UNIQUE" }, _session.SqlContext());
            }
            // VFP CREATE leaves an empty DBF at the end of its header; the first append writes EOF.
            using (var empty = new FileStream(stagedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                empty.SetLength(empty.Length - 1);

            // No-overwrite moves also protect files created by another session since validation.
            foreach (int i in new[] { 1, 2, 0 })
            {
                if (!File.Exists(stagedFiles[i])) continue;
                File.Move(stagedFiles[i], createdFiles[i]);
                published.Add(createdFiles[i]);
            }
            if (member)
            {
                _session.SnapshotForDdl(_session.DatabasePath!);
                _session.RewriteDatabase(dbc =>
                {
                    DbfDatabaseBuilder.AddTable(dbc, logicalName,
                        Path.GetRelativePath(Path.GetDirectoryName(dbc)!, path), columns, keys, Normalize(st.Check), Normalize(st.Error),
                        Normalize(st.InsertTrigger), Normalize(st.UpdateTrigger), Normalize(st.DeleteTrigger), st.Comment);
                    registered = true;
                });
            }
        }
        catch
        {
            if (!registered)
                foreach (string file in published) File.Delete(file);
            throw;
        }
        finally
        {
            foreach (string file in stagedFiles) File.Delete(file);
        }

        return SqlResult.Dml(0);
    }

    private CreateTableKey[] ResolveReferences(IReadOnlyList<CreateTableKey> keys, string tableName)
    {
        var resolved = new List<CreateTableKey>();
        foreach (var key in keys)
        {
            if (key.ReferencedTable is null)
            {
                resolved.Add(key);
                continue;
            }
            string? parentTag = key.ReferencedTag;
            if (string.Equals(key.ReferencedTable, tableName, StringComparison.OrdinalIgnoreCase))
            {
                // VFP resolves keys in declaration order, including self references.
                parentTag ??= resolved.LastOrDefault(k => k.Kind == "PRIMARY")?.Tag;
                if (parentTag is null)
                    throw new FoxDbfSqlException("No primary key is defined for the table.") { VfpErrorNumber = 1879 };
                if (resolved.LastOrDefault(k => string.Equals(k.Tag, parentTag, StringComparison.OrdinalIgnoreCase))?.Kind is not ("PRIMARY" or "UNIQUE"))
                    throw new FoxDbfSqlException("Index tag is not found.") { VfpErrorNumber = 1882 };
            }
            else
            {
                if (!_session.Database!.TryGetTableRules(key.ReferencedTable, out var rules))
                    throw new FoxDbfSqlException($"Table '{key.ReferencedTable}' is not in the database.") { VfpErrorNumber = 1880 };
                parentTag ??= rules.PrimaryKey;
                if (parentTag is null)
                    throw new FoxDbfSqlException("No primary key is defined for the table.") { VfpErrorNumber = 1879 };
                string? parentPath = _session.Database.GetTablePath(key.ReferencedTable);
                string? cdxPath = parentPath is null ? null : SidecarFile.ChangeExtension(parentPath, ".cdx");
                using var cdx = cdxPath is not null && File.Exists(cdxPath) ? CdxFile.Open(cdxPath) : null;
                if (cdx?.Tag(parentTag)?.IsCandidate != true)
                    throw new FoxDbfSqlException("Index tag is not found.") { VfpErrorNumber = 1882 };
            }
            resolved.Add(key with { ReferencedTag = parentTag });
        }
        return resolved.ToArray();
    }

    private static string? Normalize(string? expression)
        => expression is null ? null : VfpExpressionText.Canonicalize(expression);

    private CreateTableStatement ResolveArray(CreateTableStatement statement)
    {
        var array = _session.DdlArrayResolver?.Invoke(statement.FromArray!)
            ?? throw new FoxDbfSqlException($"Variable '{statement.FromArray}' is not found.") { VfpErrorNumber = 12 };
        int width = array.GetLength(1);
        if (width is < 4 or > 18)
            throw new FoxDbfSqlException("Syntax error.") { VfpErrorNumber = 10 };
        var columns = new List<ColumnDefinition>();
        string? TableText(int column)
        {
            if (column > width) return null;
            var value = array[0, column - 1];
            if (value.Type != VfpType.Character) throw new FoxDbfSqlException("Data type mismatch.") { VfpErrorNumber = 9 };
            return value.AsString.Length == 0 ? null : value.AsString;
        }
        for (int row = 0; row < array.GetLength(0); row++)
        {
            string? Text(int column)
            {
                if (column > width) return null;
                var value = array[row, column - 1];
                if (value.Type != VfpType.Character) throw new FoxDbfSqlException("Data type mismatch.") { VfpErrorNumber = 9 };
                return value.AsString.Length == 0 ? null : value.AsString;
            }
            int Number(int column)
            {
                if (column > width) return 0;
                var value = array[row, column - 1];
                if (value.Type is not (VfpType.Numeric or VfpType.Integer))
                    throw new FoxDbfSqlException("Data type mismatch.") { VfpErrorNumber = 9 };
                return (int)value.AsDouble;
            }
            bool? Logical(int column)
            {
                if (column > width) return null;
                var value = array[row, column - 1];
                if (value.Type != VfpType.Logical) throw new FoxDbfSqlException("Data type mismatch.") { VfpErrorNumber = 9 };
                return value.AsLogical;
            }
            string name = Text(1) ?? "";
            string type = Text(2) ?? "";
            if (type.Length != 1) throw new FoxDbfSqlException("Invalid field type.") { VfpErrorNumber = 9 };
            columns.Add(new ColumnDefinition(name, type[0], Number(3), Number(4), Logical(5))
            {
                NoCpTrans = Logical(6) ?? false,
                Check = Text(7), Error = Text(8), Default = Text(9),
                AutoIncrement = width >= 18 && Number(18) != 0
                    ? new DbfAutoIncrement(unchecked((uint)Number(17)), checked((byte)Number(18))) : null
            });
        }
        return statement with
        {
            Columns = columns,
            LongName = statement.LongName ?? TableText(12)?.ToLowerInvariant(),
            Check = TableText(10), Error = TableText(11), InsertTrigger = TableText(13),
            UpdateTrigger = TableText(14), DeleteTrigger = TableText(15), Comment = TableText(16),
            RequiresDatabase = columns.Any(c => c.Check is not null || c.Default is not null) ||
                Enumerable.Range(10, 7).Any(c => TableText(c) is not null)
        };
    }

    private byte CreateCodePage(CreateTableStatement st)
    {
        if (st.CodePage is null) return _session.NewTableOptions.CodePage;   // VFP9: CPCURRENT()
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
        bool binary = c.NoCpTrans || type is 'Q' or 'W';
        return new DbfColumnDef(
            name: c.Name,
            type: type,
            length: c.Length ?? 0,
            decimalCount: DefaultDecimals(type, c.Decimals),
            nullable: c.AutoIncrement is null && (c.Nullable ?? _session.Context.NullSetting),
            binary: binary,
            autoIncrement: c.AutoIncrement);
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

    /// <summary>Protect ALTER / DROP of a database member: these operations also need to update
    /// its catalog records. CREATE uses the member-registration path above.</summary>
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
