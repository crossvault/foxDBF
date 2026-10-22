using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// An ADO.NET connection over a FoxPro / dBase data source — the thin <see cref="DbConnection"/> layer
/// (Microsoft.Data.Sqlite posture) that owns a <c>CrossVault.FoxDbf.Sql.VfpSession</c>.
/// <para>
/// The <see cref="DbConnection.ConnectionString"/> is parsed via <see cref="FoxDbfConnectionStringBuilder"/>.
/// <see cref="Open"/> opens the <c>Data Source</c> (a <c>.dbc</c>, a free-table directory, or a single
/// <c>.dbf</c>) into the session and applies Collate / Deleted / Ansi / Exclusive / ReadOnly / Accelerator;
/// <see cref="Close"/> / <see cref="Dispose(bool)"/> dispose it. Exactly ONE open data reader is allowed
/// at a time.
/// </para>
/// </summary>
public sealed class FoxDbfConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private string? _dataSource;
    private ConnectionState _state = ConnectionState.Closed;
    private VfpSession? _session;
    private CrossVault.FoxDbf.Highlike.HighlikeEngine? _accelerator;
    private bool _disposed;
    private bool _enforceRules;
    private CrossVault.FoxDbf.MicroVfp.VfpInterpreter? _interpreter;
    private FoxDbfDataReader? _activeReader;
    private bool _activeReaderReserved;
    private readonly object _activeReaderGate = new();
    private FoxDbfTransaction? _activeTransaction;

    public FoxDbfConnection() { }

    public FoxDbfConnection(string? connectionString) => _connectionString = connectionString ?? string.Empty;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set => _connectionString = value ?? string.Empty;
    }

    public override string Database => _dataSource ?? "";

    public override string DataSource => _dataSource ?? "";

    public override string ServerVersion => "CrossVault.FoxDbf/1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("ChangeDatabase is not supported.");
    }

    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == ConnectionState.Open) return;

        VfpSession? session = null;
        CrossVault.FoxDbf.Highlike.HighlikeEngine? accelerator = null;

        try
        {
            var builder = new FoxDbfConnectionStringBuilder(_connectionString);
            _dataSource = builder.DataSource;
            _enforceRules = builder.EnforceRules;

            if (string.IsNullOrEmpty(_dataSource))
                throw new FoxDbfException("Connection string must specify 'Data Source'.");

            var context = new EvaluationContext
            {
                Collation = ParseCollation(builder.Collate),
                Deleted = builder.Deleted,
                Ansi = builder.Ansi,
            };

            session = new VfpSession(context)
            {
                DefaultExclusive = builder.Exclusive,
                ReadOnly = builder.ReadOnly,
            };

            // Opt-in query accelerator: Accelerator=Highlike routes SELECT / DML candidate discovery
            // through the Highlike engine (same result set, faster plan); None = the plain optimizer.
            if (builder.Accelerator == FoxDbfAccelerator.Highlike)
            {
                accelerator = new CrossVault.FoxDbf.Highlike.HighlikeEngine();
                session.Accelerator = accelerator;
            }

            // Open the data source: .dbc, directory, or .dbf file
            string fullPath = Path.GetFullPath(_dataSource);
            if (fullPath.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase))
            {
                session.OpenDatabase(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                session.OpenDirectory(fullPath);
            }
            else if (fullPath.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            {
                // Single .dbf file: open its directory
                string? dirPath = Path.GetDirectoryName(fullPath);
                if (dirPath is not null)
                    session.OpenDirectory(dirPath);
                else
                    throw new FoxDbfException($"Cannot determine directory for .dbf file '{fullPath}'.");
            }
            else
            {
                throw new FoxDbfException($"Data Source '{_dataSource}' not found or is not a valid .dbc, directory, or .dbf file.");
            }
        }
        catch (FoxDbfException)
        {
            try { session?.Dispose(); } catch { }
            try { accelerator?.Dispose(); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            try { session?.Dispose(); } catch { }
            try { accelerator?.Dispose(); } catch { }
            throw new FoxDbfException($"Failed to open connection: {ex.Message}", ex);
        }

        _session = session;
        _accelerator = accelerator;
        try
        {
            SetState(ConnectionState.Open);
        }
        catch (FoxDbfException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FoxDbfException($"Failed to open connection: {ex.Message}", ex);
        }
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed) return;
        // A still-open transaction rolls back when the connection closes (DbTransaction convention).
        if (_activeTransaction is { IsCompleted: false } tx)
        {
            try { tx.Rollback(); } catch { /* close must not throw */ }
        }
        _activeTransaction = null;
        SetState(ConnectionState.Closed);
        SetActiveReader(null);
        _interpreter = null;
        // Symmetric with Open(): release the session's open DBF handles + the accelerator so a
        // Close -> Open -> Close cycle (or Close() on an IDbConnection handle) never leaks them.
        _session?.Dispose();
        _session = null;
        _accelerator?.Dispose();
        _accelerator = null;
    }

    // ---- schema metadata (ADO.NET GetSchema) ----------------------------------
    // Standard ADO.NET DataTable schema collections: MetaDataCollections, Restrictions,
    // DataSourceInformation, DataTypes, Tables, Columns (with optional Indexes/IndexColumns).
    // Read-only: only enumerate schema, never mutate data or fixtures.

    public override DataTable GetSchema()
        => GetSchema(DbMetaDataCollectionNames.MetaDataCollections, null);

    public override DataTable GetSchema(string collectionName)
        => GetSchema(collectionName, null);

    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
    {
        if (string.IsNullOrEmpty(collectionName))
            throw new ArgumentException("Collection name cannot be null or empty.", nameof(collectionName));

        if (string.Equals(collectionName, DbMetaDataCollectionNames.MetaDataCollections, StringComparison.OrdinalIgnoreCase))
            return GetMetaDataCollections();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.Restrictions, StringComparison.OrdinalIgnoreCase))
            return GetRestrictions();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.DataSourceInformation, StringComparison.OrdinalIgnoreCase))
            return GetDataSourceInformation();
        if (string.Equals(collectionName, DbMetaDataCollectionNames.DataTypes, StringComparison.OrdinalIgnoreCase))
            return GetDataTypes();
        if (string.Equals(collectionName, "Tables", StringComparison.OrdinalIgnoreCase))
            return GetTables(restrictionValues);
        if (string.Equals(collectionName, "Columns", StringComparison.OrdinalIgnoreCase))
            return GetColumns(restrictionValues);

        throw new ArgumentException($"Unsupported collection '{collectionName}'.", nameof(collectionName));
    }

    private DataTable GetMetaDataCollections()
    {
        var t = new DataTable(DbMetaDataCollectionNames.MetaDataCollections)
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("CollectionName", typeof(string));
        t.Columns.Add("NumberOfRestrictions", typeof(int));
        t.Columns.Add("NumberOfIdentifierParts", typeof(int));

        var rows = new (string name, int restrictions, int identifierParts)[]
        {
            (DbMetaDataCollectionNames.MetaDataCollections, 0, 0),
            (DbMetaDataCollectionNames.Restrictions, 0, 0),
            (DbMetaDataCollectionNames.DataSourceInformation, 0, 0),
            (DbMetaDataCollectionNames.DataTypes, 0, 0),
            ("Tables", 4, 3), // catalog, schema, name, type
            ("Columns", 4, 2), // table catalog, table schema, table name, column name
        };

        foreach (var (name, restrictions, identifierParts) in rows)
        {
            var row = t.NewRow();
            row["CollectionName"] = name;
            row["NumberOfRestrictions"] = restrictions;
            row["NumberOfIdentifierParts"] = identifierParts;
            t.Rows.Add(row);
        }

        return t;
    }

    private DataTable GetRestrictions()
    {
        var t = new DataTable(DbMetaDataCollectionNames.Restrictions)
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("CollectionName", typeof(string));
        t.Columns.Add("RestrictionName", typeof(string));
        t.Columns.Add("RestrictionDefault", typeof(string));
        t.Columns.Add("RestrictionNumber", typeof(int));

        var restrictions = new (string collection, string restriction, object defaultValue, int number)[]
        {
            ("Tables", "Catalog", DBNull.Value, 0),
            ("Tables", "Schema", DBNull.Value, 1),
            ("Tables", "TableName", DBNull.Value, 2),
            ("Tables", "TableType", DBNull.Value, 3),
            ("Columns", "Catalog", DBNull.Value, 0),
            ("Columns", "Schema", DBNull.Value, 1),
            ("Columns", "TableName", DBNull.Value, 2),
            ("Columns", "ColumnName", DBNull.Value, 3),
        };

        foreach (var (collection, restriction, defaultValue, number) in restrictions)
        {
            var row = t.NewRow();
            row["CollectionName"] = collection;
            row["RestrictionName"] = restriction;
            row["RestrictionDefault"] = defaultValue;
            row["RestrictionNumber"] = number;
            t.Rows.Add(row);
        }

        return t;
    }

    private DataTable GetDataSourceInformation()
    {
        var t = new DataTable(DbMetaDataCollectionNames.DataSourceInformation)
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("DataSourceProductName", typeof(string));
        t.Columns.Add("DataSourceProductVersion", typeof(string));
        t.Columns.Add("DataSourceProductMajorVersion", typeof(int));
        t.Columns.Add("DataSourceProductMinorVersion", typeof(int));
        t.Columns.Add("DataSourceProductBuildVersion", typeof(int));
        t.Columns.Add("DataSourceProductComments", typeof(string));
        t.Columns.Add("IdentifierPattern", typeof(string));
        t.Columns.Add("IdentifierCase", typeof(int)); // 1=Sensitive, 2=Insensitive, 4=Mixed
        t.Columns.Add("InitialCatalog", typeof(string));
        t.Columns.Add("SchemaPattern", typeof(string));
        t.Columns.Add("SchemaSeparator", typeof(string));
        t.Columns.Add("CatalogPattern", typeof(string));
        t.Columns.Add("CatalogSeparator", typeof(string));
        t.Columns.Add("MaximumIdentifierLength", typeof(int));
        t.Columns.Add("MaximumRowSize", typeof(int));
        t.Columns.Add("MaximumRowSizeIncludesBLOB", typeof(bool));
        t.Columns.Add("MaximumTableNameLength", typeof(int));
        t.Columns.Add("MaximumColumnsInTable", typeof(int));
        t.Columns.Add("MaximumColumnsInIndex", typeof(int));
        t.Columns.Add("MaximumColumnsInOrderBy", typeof(int));
        t.Columns.Add("MaximumColumnsInGroupBy", typeof(int));
        t.Columns.Add("MaximumColumnsInSelect", typeof(int));
        t.Columns.Add("MaximumIndexSize", typeof(int));
        t.Columns.Add("MaximumBytesForGroupBy", typeof(int));
        t.Columns.Add("MaximumCharacterLength", typeof(int));
        t.Columns.Add("MaximumNumericPrecision", typeof(int));
        t.Columns.Add("MaximumNumericScale", typeof(int));
        t.Columns.Add("MaximumOpenCursors", typeof(int));
        t.Columns.Add("MaximumColumnsInGroupByExpression", typeof(int));
        t.Columns.Add("MaximumOrderByItemsInSelectStatement", typeof(int));
        t.Columns.Add("MaximumTablesInSelectStatement", typeof(int));
        t.Columns.Add("MaximumTablesInSelectStatementForJoin", typeof(int));
        t.Columns.Add("MaximumStatementComplexity", typeof(int));
        t.Columns.Add("MaximumClustered IndexSize", typeof(int));
        t.Columns.Add("MaximumNonClusteredIndexSize", typeof(int));
        t.Columns.Add("MaximumColumnsInPrimaryKey", typeof(int));
        t.Columns.Add("MaximumColumnsInForeignKey", typeof(int));
        t.Columns.Add("MaximumColumnsInUniqueIndex", typeof(int));
        t.Columns.Add("MaximumColumnsInGroupByClause", typeof(int));
        t.Columns.Add("BooleanFunctionsSupported", typeof(int));
        t.Columns.Add("ConditionalFunctionsSupported", typeof(int));
        t.Columns.Add("DateTimeFunctionsSupported", typeof(int));
        t.Columns.Add("EncryptionAlgorithmsSupported", typeof(string));
        t.Columns.Add("GroupByBehavior", typeof(int)); // DBNull=0, NotSupported=1, MustContainAll=2, Unknown=3
        t.Columns.Add("IdentifierBehavior", typeof(int)); // CaseSensitive=1, CaseInsensitive=2, MixedCase=4
        t.Columns.Add("IndexBehavior", typeof(int)); // NotSupported=0, Clustered=1, NonClustered=2, Primary=4, Hashed=8
        t.Columns.Add("ILike Operator Supported", typeof(bool));
        t.Columns.Add("LikeEscapeClauseSupported", typeof(bool));
        t.Columns.Add("LogicalOperatorsSupported", typeof(int));
        t.Columns.Add("MathFunctionsSupported", typeof(int));
        t.Columns.Add("NumericFunctionsSupported", typeof(int));
        t.Columns.Add("OrderByColumnsInSelectListRequired", typeof(bool));
        t.Columns.Add("ParameterMarkerFormat", typeof(string));
        t.Columns.Add("ParameterMarkerPattern", typeof(string));
        t.Columns.Add("ParameterNameMaxLength", typeof(int));
        t.Columns.Add("ParameterNamePattern", typeof(string));
        t.Columns.Add("ParameterNameCharacters", typeof(string));
        t.Columns.Add("ProcedureCallFormat", typeof(string));
        t.Columns.Add("ProcedureCallFormatRules", typeof(string));
        t.Columns.Add("ProcedureCallRulesStyle", typeof(int));
        t.Columns.Add("QuotedIdentifierCase", typeof(int)); // CaseSensitive=1, CaseInsensitive=2, MixedCase=4
        t.Columns.Add("QuotedIdentifierPattern", typeof(string));
        t.Columns.Add("QuotedIdentifierCaseStyle", typeof(int));
        t.Columns.Add("StatementTypes", typeof(int));
        t.Columns.Add("StringFunctionsSupported", typeof(int));
        t.Columns.Add("StringLiteralCharacters", typeof(string));
        t.Columns.Add("SupportedJoinOperators", typeof(int));
        t.Columns.Add("SupportsGroupByBeyondSelect", typeof(bool));
        t.Columns.Add("SupportsMultipleResultSets", typeof(bool));
        t.Columns.Add("SupportsMultipleTransactions", typeof(bool));
        t.Columns.Add("SupportsMultipleStatements", typeof(bool));
        t.Columns.Add("SupportsOrderByUnrelatedSelectExpression", typeof(bool));
        t.Columns.Add("SupportsOuterJoins", typeof(bool));
        t.Columns.Add("SupportsSubqueries", typeof(bool));
        t.Columns.Add("SupportsCorrelatedSubqueries", typeof(bool));
        t.Columns.Add("SupportsUnion", typeof(bool));
        t.Columns.Add("SupportsUnionAll", typeof(bool));
        t.Columns.Add("SupportsSchemasInDml", typeof(bool));
        t.Columns.Add("SupportsSchemasInIndexCreation", typeof(bool));
        t.Columns.Add("SupportsSchemasInTableCreation", typeof(bool));
        t.Columns.Add("SupportsSchemasInDataManipulation", typeof(bool));
        t.Columns.Add("SupportsIndexRelatedDBObjects", typeof(bool));
        t.Columns.Add("SupportsReferentialIntegrity", typeof(bool));
        t.Columns.Add("SupportsTransactions", typeof(bool));
        t.Columns.Add("SupportsTransactedDdl", typeof(bool));
        t.Columns.Add("TransactionIsolationLevel", typeof(int)); // Unspecified=0, ReadUncommitted=1, ReadCommitted=2, RepeatableRead=4, Serializable=8
        t.Columns.Add("TransactionDdlCommitted", typeof(bool));
        t.Columns.Add("TransactionDdlRolledBack", typeof(bool));
        t.Columns.Add("TypeConversionSupported", typeof(bool));
        t.Columns.Add("UserDefinedTypesSupported", typeof(bool));

        var row = t.NewRow();
        row["DataSourceProductName"] = "CrossVault.FoxDbf";
        row["DataSourceProductVersion"] = ServerVersion;
        row["DataSourceProductMajorVersion"] = 1;
        row["DataSourceProductMinorVersion"] = 0;
        row["DataSourceProductBuildVersion"] = 0;
        row["DataSourceProductComments"] = "A .NET 10 Visual FoxPro / dBase file format provider";
        row["IdentifierPattern"] = @"^[\p{L}_][\p{L}\p{Nd}_]*$";   // Unicode letters, as VFP9 field names allow
        row["IdentifierCase"] = 2; // Case insensitive
        row["InitialCatalog"] = _dataSource ?? "";
        row["SchemaPattern"] = DBNull.Value;
        row["SchemaSeparator"] = ".";
        row["CatalogPattern"] = DBNull.Value;
        row["CatalogSeparator"] = ".";
        row["MaximumIdentifierLength"] = 254; // VFP long name limit
        row["MaximumRowSize"] = 65535; // DBF record limit
        row["MaximumRowSizeIncludesBLOB"] = true;
        row["MaximumTableNameLength"] = 254;
        row["MaximumColumnsInTable"] = 254; // DBF field limit
        row["MaximumColumnsInIndex"] = 254;
        row["MaximumColumnsInOrderBy"] = 254;
        row["MaximumColumnsInGroupBy"] = 254;
        row["MaximumColumnsInSelect"] = 254;
        row["MaximumIndexSize"] = DBNull.Value;
        row["MaximumBytesForGroupBy"] = DBNull.Value;
        row["MaximumCharacterLength"] = 254;
        row["MaximumNumericPrecision"] = 20;
        row["MaximumNumericScale"] = 15;
        row["MaximumOpenCursors"] = DBNull.Value;
        row["MaximumColumnsInGroupByExpression"] = DBNull.Value;
        row["MaximumOrderByItemsInSelectStatement"] = DBNull.Value;
        row["MaximumTablesInSelectStatement"] = DBNull.Value;
        row["MaximumTablesInSelectStatementForJoin"] = DBNull.Value;
        row["MaximumStatementComplexity"] = DBNull.Value;
        row["MaximumClustered IndexSize"] = DBNull.Value;
        row["MaximumNonClusteredIndexSize"] = DBNull.Value;
        row["MaximumColumnsInPrimaryKey"] = DBNull.Value;
        row["MaximumColumnsInForeignKey"] = DBNull.Value;
        row["MaximumColumnsInUniqueIndex"] = DBNull.Value;
        row["MaximumColumnsInGroupByClause"] = DBNull.Value;
        row["BooleanFunctionsSupported"] = DBNull.Value;
        row["ConditionalFunctionsSupported"] = DBNull.Value;
        row["DateTimeFunctionsSupported"] = DBNull.Value;
        row["EncryptionAlgorithmsSupported"] = DBNull.Value;
        row["GroupByBehavior"] = 3; // Unknown
        row["IdentifierBehavior"] = 2; // Case insensitive
        row["IndexBehavior"] = 2 | 8; // NonClustered | Hashed (CDX)
        row["ILike Operator Supported"] = false;
        row["LikeEscapeClauseSupported"] = false;
        row["LogicalOperatorsSupported"] = DBNull.Value;
        row["MathFunctionsSupported"] = DBNull.Value;
        row["NumericFunctionsSupported"] = DBNull.Value;
        row["OrderByColumnsInSelectListRequired"] = false;
        row["ParameterMarkerFormat"] = "?";
        row["ParameterMarkerPattern"] = @"\?";
        row["ParameterNameMaxLength"] = 0;
        row["ParameterNamePattern"] = DBNull.Value;
        row["ParameterNameCharacters"] = DBNull.Value;
        row["ProcedureCallFormat"] = DBNull.Value;
        row["ProcedureCallFormatRules"] = DBNull.Value;
        row["ProcedureCallRulesStyle"] = DBNull.Value;
        row["QuotedIdentifierCase"] = 2; // Case insensitive
        row["QuotedIdentifierPattern"] = @"^\[.*\]$|^\`.*\`$|^"".*""$";
        row["QuotedIdentifierCaseStyle"] = 2; // Case insensitive
        row["StatementTypes"] = DBNull.Value;
        row["StringFunctionsSupported"] = DBNull.Value;
        row["StringLiteralCharacters"] = "'";
        row["SupportedJoinOperators"] = DBNull.Value;
        row["SupportsGroupByBeyondSelect"] = false;
        row["SupportsMultipleResultSets"] = false;
        row["SupportsMultipleTransactions"] = false;
        row["SupportsMultipleStatements"] = false;
        row["SupportsOrderByUnrelatedSelectExpression"] = true;
        row["SupportsOuterJoins"] = true;
        row["SupportsSubqueries"] = true;
        row["SupportsCorrelatedSubqueries"] = false;
        row["SupportsUnion"] = true;
        row["SupportsUnionAll"] = true;
        row["SupportsSchemasInDml"] = false;
        row["SupportsSchemasInIndexCreation"] = false;
        row["SupportsSchemasInTableCreation"] = false;
        row["SupportsSchemasInDataManipulation"] = false;
        row["SupportsIndexRelatedDBObjects"] = true;
        row["SupportsReferentialIntegrity"] = false;
        row["SupportsTransactions"] = true;
        row["SupportsTransactedDdl"] = false;
        row["TransactionIsolationLevel"] = 0; // Unspecified
        row["TransactionDdlCommitted"] = false;
        row["TransactionDdlRolledBack"] = false;
        row["TypeConversionSupported"] = false;
        row["UserDefinedTypesSupported"] = false;
        t.Rows.Add(row);

        return t;
    }

    private DataTable GetDataTypes()
    {
        var t = new DataTable(DbMetaDataCollectionNames.DataTypes)
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("TypeName", typeof(string));
        t.Columns.Add("ProviderDbType", typeof(int));
        t.Columns.Add("ColumnSize", typeof(long));
        t.Columns.Add("CreateFormat", typeof(string));
        t.Columns.Add("CreateParameters", typeof(string));
        t.Columns.Add("DataType", typeof(string)); // CLR type FullName
        t.Columns.Add("IsAutoIncrementable", typeof(bool));
        t.Columns.Add("IsBestMatch", typeof(bool));
        t.Columns.Add("IsCaseSensitive", typeof(bool));
        t.Columns.Add("IsFixedLength", typeof(bool));
        t.Columns.Add("IsFixedPrecisionScale", typeof(bool));
        t.Columns.Add("IsLong", typeof(bool));
        t.Columns.Add("IsNullable", typeof(bool));
        t.Columns.Add("IsSearchable", typeof(bool));
        t.Columns.Add("IsSearchableWithLike", typeof(bool));
        t.Columns.Add("IsUnsigned", typeof(bool));
        t.Columns.Add("MaximumScale", typeof(short));
        t.Columns.Add("MinimumScale", typeof(short));
        t.Columns.Add("IsConcurrencyType", typeof(bool));
        t.Columns.Add("IsLiteralSupported", typeof(bool));
        t.Columns.Add("LiteralPrefix", typeof(string));
        t.Columns.Add("LiteralSuffix", typeof(string));
        t.Columns.Add("NativeDataType", typeof(string));

        // Define all DBF field types and their CLR mappings (matching FoxDbfDataReader)
        var types = new (char code, string typeName, Type clrType, string createFormat, bool isFixedLength, bool isLong)[]
        {
            ('C', "C", typeof(string), "C({0})", true, false), // Character
            ('M', "M", typeof(string), "M", false, true),       // Memo
            ('V', "V", typeof(string), "V", true, false),       // Varchar
            ('N', "N", typeof(decimal), "N({0},{1})", true, false), // Numeric
            ('F', "F", typeof(decimal), "F({0},{1})", true, false), // Float (decimal variant)
            ('Y', "Y", typeof(decimal), "Y", true, false),      // Currency
            ('I', "I", typeof(int), "I", true, false),          // Integer
            ('B', "B", typeof(double), "B", true, false),       // Double
            ('D', "D", typeof(DateTime), "D", true, false),     // Date
            ('T', "T", typeof(DateTime), "T", true, false),     // Timestamp
            ('L', "L", typeof(bool), "L", true, false),         // Logical
            ('G', "G", typeof(byte[]), "G", false, true),       // General (binary)
            ('P', "P", typeof(byte[]), "P", false, true),       // Picture
            ('Q', "Q", typeof(byte[]), "Q", false, true),       // VarBinary
            ('W', "W", typeof(byte[]), "W", false, true),       // Blob
        };

        foreach (var (code, typeName, clrType, createFormat, isFixedLength, isLong) in types)
        {
            var row = t.NewRow();
            row["TypeName"] = typeName;
            row["ProviderDbType"] = (int)code; // Use ASCII code as provider type
            row["ColumnSize"] = isLong ? long.MaxValue : 254;
            row["CreateFormat"] = createFormat;
            row["CreateParameters"] = code switch
            {
                'C' or 'V' => "length",
                'N' or 'F' => "precision,scale",
                _ => DBNull.Value,
            };
            row["DataType"] = clrType.FullName;
            row["IsAutoIncrementable"] = false;
            row["IsBestMatch"] = true;
            row["IsCaseSensitive"] = false;
            row["IsFixedLength"] = isFixedLength;
            row["IsFixedPrecisionScale"] = code is 'N' or 'F' or 'Y';
            row["IsLong"] = isLong;
            row["IsNullable"] = true;
            row["IsSearchable"] = true;
            row["IsSearchableWithLike"] = code is 'C' or 'M' or 'V';
            row["IsUnsigned"] = code is 'I' or 'B' or 'Y'; // Not exactly, but simplification
            row["MaximumScale"] = code is 'N' or 'F' ? (short)15 : (short)0;
            row["MinimumScale"] = code is 'N' or 'F' ? (short)0 : (short)0;
            row["IsConcurrencyType"] = false;
            row["IsLiteralSupported"] = true;
            row["LiteralPrefix"] = code is 'C' or 'M' or 'V' ? "'" : (code is 'D' ? "{d'" : (code is 'T' ? "{ts'" : DBNull.Value));
            row["LiteralSuffix"] = code is 'C' or 'M' or 'V' ? "'" : (code is 'D' ? "'}" : (code is 'T' ? "'}" : DBNull.Value));
            row["NativeDataType"] = typeName;
            t.Rows.Add(row);
        }

        return t;
    }

    private DataTable GetTables(string?[]? restrictionValues)
    {
        var t = new DataTable("Tables")
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_SCHEMA", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("TABLE_TYPE", typeof(string));

        // Extract restrictions: [catalog, schema, table_name, table_type]
        string? catalogFilter = RestrictionValue(restrictionValues, 0);
        string? schemaFilter = RestrictionValue(restrictionValues, 1);
        string? tableNameFilter = RestrictionValue(restrictionValues, 2);
        string? tableTypeFilter = RestrictionValue(restrictionValues, 3);

        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        // Enumerate tables from either a DBC or the free-table directory
        var tableNames = new List<string>();

        if (_session?.Database is { } dbc)
        {
            // DBC mode: enumerate from the database container
            tableNames.AddRange(dbc.TableNames);
        }
        else if (_session?.DataDirectory is { } dir && Directory.Exists(dir))
        {
            // Free-table mode: enumerate .dbf files in the directory
            foreach (var file in Directory.EnumerateFiles(dir, "*.dbf", SearchOption.TopDirectoryOnly))
            {
                string filename = Path.GetFileNameWithoutExtension(file);
                tableNames.Add(filename);
            }
        }

        // Apply table name filter if specified
        if (!string.IsNullOrEmpty(tableNameFilter))
        {
            tableNames = tableNames
                .Where(n => string.Equals(n, tableNameFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Add rows for each table
        foreach (var tableName in tableNames)
        {
            var row = t.NewRow();
            row["TABLE_CATALOG"] = _dataSource ?? "";
            row["TABLE_SCHEMA"] = DBNull.Value;
            row["TABLE_NAME"] = tableName;
            row["TABLE_TYPE"] = "TABLE"; // Always return "TABLE" for now
            t.Rows.Add(row);
        }

        return t;
    }

    private DataTable GetColumns(string?[]? restrictionValues)
    {
        var t = new DataTable("Columns")
        {
            Locale = System.Globalization.CultureInfo.InvariantCulture,
        };
        t.Columns.Add("TABLE_CATALOG", typeof(string));
        t.Columns.Add("TABLE_SCHEMA", typeof(string));
        t.Columns.Add("TABLE_NAME", typeof(string));
        t.Columns.Add("COLUMN_NAME", typeof(string));
        t.Columns.Add("ORDINAL_POSITION", typeof(int));
        t.Columns.Add("COLUMN_DEFAULT", typeof(string));
        t.Columns.Add("IS_NULLABLE", typeof(string)); // "YES" or "NO"
        t.Columns.Add("DATA_TYPE", typeof(string));
        t.Columns.Add("CHARACTER_MAXIMUM_LENGTH", typeof(int));
        t.Columns.Add("CHARACTER_OCTET_LENGTH", typeof(int));
        t.Columns.Add("NUMERIC_PRECISION", typeof(int));
        t.Columns.Add("NUMERIC_SCALE", typeof(int));
        t.Columns.Add("DATETIME_PRECISION", typeof(int));

        // Extract restrictions: [catalog, schema, table_name, column_name]
        string? catalogFilter = RestrictionValue(restrictionValues, 0);
        string? schemaFilter = RestrictionValue(restrictionValues, 1);
        string? tableNameFilter = RestrictionValue(restrictionValues, 2);
        string? columnNameFilter = RestrictionValue(restrictionValues, 3);

        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");

        // Open the specified table
        if (string.IsNullOrEmpty(tableNameFilter))
            return t; // No table name specified, return empty

        DbfTable? dbf = null;
        try
        {
            if (_session?.Database is { } dbc)
            {
                // Try to open from DBC
                dbf = dbc.OpenTable(tableNameFilter);
            }
            else if (_session?.DataDirectory is { } dir)
            {
                // Try to open from free-table directory
                string dbfPath = Path.Combine(dir, tableNameFilter + ".dbf");
                if (File.Exists(dbfPath))
                    dbf = DbfTable.Open(dbfPath);
            }

            if (dbf is not null)
            {
                int ordinal = 1;
                foreach (var col in dbf.Columns)
                {
                    // Apply column name filter if specified
                    if (!string.IsNullOrEmpty(columnNameFilter) &&
                        !string.Equals(col.Name, columnNameFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var row = t.NewRow();
                    row["TABLE_CATALOG"] = _dataSource ?? "";
                    row["TABLE_SCHEMA"] = DBNull.Value;
                    row["TABLE_NAME"] = tableNameFilter;
                    row["COLUMN_NAME"] = col.Name;
                    row["ORDINAL_POSITION"] = ordinal++;
                    row["COLUMN_DEFAULT"] = DBNull.Value;
                    row["IS_NULLABLE"] = "YES"; // DBF fields are always nullable
                    row["DATA_TYPE"] = col.Type.ToString();
                    row["CHARACTER_MAXIMUM_LENGTH"] = col.Length;
                    row["CHARACTER_OCTET_LENGTH"] = col.Length; // Approximation
                    row["NUMERIC_PRECISION"] = col.Type is 'N' or 'F' or 'Y' ? col.Length : (object)DBNull.Value;
                    row["NUMERIC_SCALE"] = col.Decimal;
                    row["DATETIME_PRECISION"] = DBNull.Value;
                    t.Rows.Add(row);
                }
            }
        }
        finally
        {
            dbf?.Dispose();
        }

        return t;
    }

    private static string? RestrictionValue(string?[]? restrictionValues, int index)
        => restrictionValues is not null && index < restrictionValues.Length ? restrictionValues[index] : null;

    protected override DbCommand CreateDbCommand() => new FoxDbfCommand(null, this);

    /// <summary>Multi-command batches are supported (a local-file convenience — no round-trip saving).</summary>
    public override bool CanCreateBatch => true;

    /// <summary>Create a <see cref="FoxDbfBatch"/> bound to this connection.</summary>
    protected override DbBatch CreateDbBatch() => new FoxDbfBatch(this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");
        // Only ONE active transaction per connection: a nested BeginTransaction on a busy connection throws.
        if (_activeTransaction is { IsCompleted: false })
            throw new InvalidOperationException(
                "A transaction is already active on this connection; only one transaction per connection is supported.");
        var tx = new FoxDbfTransaction(this, isolationLevel);
        _activeTransaction = tx;
        // Route the session's reads + writes through the transaction's COPY-ON-WRITE redirect: the first
        // write to each table takes a private working copy (recording the live change-token), and the
        // transacting connection then reads + writes that copy for the rest of the transaction (ISOLATION).
        // DDL still snapshots the live files so a rollback restores them.
        _session!.TxRedirectReadPath = tx.RedirectReadPath;
        _session!.TxBeginWritePath = tx.BeginWritePath;
        _session!.TxLivePath = tx.LivePathOf;
        _session!.TxDdlSnapshot = tx.DdlSnapshot;
        return tx;
    }

    /// <summary>The transaction currently active on this connection (snapshot-before-first-write
    /// target for the DML path), or <see langword="null"/> when autocommit. Cleared once the
    /// transaction commits / rolls back.</summary>
    internal FoxDbfTransaction? ActiveTransaction
        => _activeTransaction is { IsCompleted: false } tx ? tx : null;

    /// <summary>Called by <see cref="FoxDbfTransaction"/> on commit / rollback so a fresh
    /// BeginTransaction is allowed again.</summary>
    internal void ClearTransaction(FoxDbfTransaction tx)
    {
        if (ReferenceEquals(_activeTransaction, tx))
        {
            _activeTransaction = null;
            // Back to autocommit: reads + writes go straight to the live files again.
            if (_session is not null)
            {
                _session.TxRedirectReadPath = null;
                _session.TxBeginWritePath = null;
                _session.TxLivePath = null;
                _session.TxDdlSnapshot = null;
            }
        }
    }

    /// <summary>Called by <see cref="FoxDbfTransaction.Rollback"/> just before it restores snapshotted
    /// files over the live ones: release every open session handle (work-area tables + indexes) and the
    /// active reader so the underlying files can be overwritten without a sharing violation. Handles
    /// re-open lazily on the next access.</summary>
    internal void QuiesceForRollback()
    {
        SetActiveReader(null);
        _session?.CloseAllHandles();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            try { Close(); }
            catch { }
            _session?.Dispose();
            _session = null;
            _accelerator?.Dispose();
            _accelerator = null;
        }

        base.Dispose(disposing);
    }

    // ---- internal methods for the provider ------------------------------------

    internal VfpSession Session
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != ConnectionState.Open)
                throw new InvalidOperationException("Connection is not open.");
            return _session!;
        }
    }

    /// <summary>Opt-in DBC rule/RI enforcement on writes (the <c>EnforceRules</c> / <c>EnforceRI</c>
    /// connection-string flag). When <see langword="false"/> (default) the provider's INSERT/UPDATE/DELETE
    /// take the existing RAW DML path, byte-for-byte unchanged.</summary>
    internal bool EnforceRules => _enforceRules;

    /// <summary>
    /// The shared microVFP interpreter over this connection's <see cref="VfpSession"/>, lazily created and
    /// loaded from the open DBC's <c>StoredProceduresSource</c> (a no-op when the data source is a free-table
    /// directory / single .dbf with no container). <see cref="FoxDbfCommand"/> uses it to (a) CALL stored
    /// procedures / UDFs and evaluate ad-hoc expressions, and (b) enforce the DBC DEFAULT/RULE/TRIGGER on
    /// writes when <see cref="EnforceRules"/> is on. Because it is bound to the live session it sees the same
    /// open work areas. Cleared on <see cref="Close"/> so a reopened connection rebuilds it.
    /// </summary>
    internal CrossVault.FoxDbf.MicroVfp.VfpInterpreter Interpreter
    {
        get
        {
            var session = Session;   // asserts the connection is open (and not disposed).
            if (_interpreter is null)
            {
                var interp = new CrossVault.FoxDbf.MicroVfp.VfpInterpreter(session);
                interp.LoadStoredProceduresFromDatabase();   // RI procs + business UDFs from the .dbc (no-op if none).
                interp.EnforceReferentialIntegrity = _enforceRules;
                _interpreter = interp;
            }
            return _interpreter;
        }
    }

    internal void ReserveActiveReaderSlot()
    {
        lock (_activeReaderGate)
        {
            if (_activeReader is not null || _activeReaderReserved)
                throw new InvalidOperationException("A data reader is already open.");
            _activeReaderReserved = true;
        }
    }

    internal void SetActiveReader(FoxDbfDataReader? reader)
    {
        lock (_activeReaderGate)
        {
            if (reader is not null && (_activeReader is not null || _activeReaderReserved))
                throw new InvalidOperationException("A data reader is already open.");
            _activeReader = reader;
            _activeReaderReserved = false;
        }
    }

    internal void CommitActiveReaderSlot(FoxDbfDataReader reader)
    {
        lock (_activeReaderGate)
        {
            if (_activeReader is not null || !_activeReaderReserved)
                throw new InvalidOperationException("A data reader is already open.");
            _activeReader = reader;
            _activeReaderReserved = false;
        }
    }

    private void SetState(ConnectionState newState)
    {
        if (_state != newState)
        {
            var old = _state;
            _state = newState;
            OnStateChange(new StateChangeEventArgs(old, newState));
        }
    }

    private static IVfpCollation ParseCollation(string collateStr)
    {
        return collateStr.Equals("general", StringComparison.OrdinalIgnoreCase)
            ? VfpCollations.General
            : VfpCollations.Machine;
    }
}
