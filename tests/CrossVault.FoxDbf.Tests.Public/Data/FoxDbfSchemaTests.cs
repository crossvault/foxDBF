using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// TDD (RED) for the ADO.NET schema-metadata surface: <see cref="DbConnection.GetSchema()"/> and its
/// overloads on <see cref="FoxDbfConnection"/>. These assert the standard ADO.NET DataTable schema
/// collections (MetaDataCollections / Restrictions / DataSourceInformation / DataTypes / Tables /
/// Columns) with the conventional collection + column NAMES so generic tooling (schema explorers,
/// EF scaffolding, Dapper) sees a familiar shape. Read-only: free tables run against a temp COPY of
/// PERSON; the .dbc case opens the committed Tastrade container for reading only (never mutated).
/// </summary>
public sealed class FoxDbfSchemaTests
{
    // ---- (1) MetaDataCollections -----------------------------------------------------------

    [Fact]
    public void GetSchema_NoArgs_ReturnsMetaDataCollections_ListingCoreCollections()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema();

        Assert.Equal("MetaDataCollections", t.TableName);
        Assert.Contains("CollectionName", t.Columns.Cast<DataColumn>().Select(c => c.ColumnName));

        var names = t.Rows.Cast<DataRow>()
            .Select(r => (string)r["CollectionName"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var required in new[]
                 {
                     "MetaDataCollections", "Restrictions", "DataSourceInformation",
                     "DataTypes", "Tables", "Columns",
                 })
            Assert.Contains(required, names);

        // GetSchema(MetaDataCollections) must return the same collection as the no-arg overload.
        DataTable t2 = conn.GetSchema(DbMetaDataCollectionNames.MetaDataCollections);
        Assert.Equal(t.Rows.Count, t2.Rows.Count);
    }

    // ---- (2) Tables: free-table directory --------------------------------------------------

    [Fact]
    public void GetSchema_Tables_FreeTableDirectory_ListsDbfFiles()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema("Tables");

        foreach (var col in new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME", "TABLE_TYPE" })
            Assert.Contains(col, t.Columns.Cast<DataColumn>().Select(c => c.ColumnName));

        var tableNames = t.Rows.Cast<DataRow>()
            .Select(r => (string)r["TABLE_NAME"])
            .ToList();

        // The temp dir holds exactly one .dbf (person.dbf); the sidecar .cdx must NOT be a table.
        Assert.Single(tableNames);
        Assert.Contains("person", tableNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetSchema_Tables_RestrictionFiltersToOneTable()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        // Positional restriction array: [catalog, schema, table_name, table_type].
        DataTable match = conn.GetSchema("Tables", new string?[] { null, null, "person", null });
        Assert.Single(match.Rows);
        Assert.Equal("person", (string)match.Rows[0]["TABLE_NAME"], ignoreCase: true);

        DataTable none = conn.GetSchema("Tables", new string?[] { null, null, "does_not_exist", null });
        Assert.Empty(none.Rows);
    }

    [Fact]
    public void GetSchema_Tables_EmptyRestrictionArray_TreatsMissingEntriesAsNoFilter()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema("Tables", Array.Empty<string?>());

        var tableNames = t.Rows.Cast<DataRow>()
            .Select(r => (string)r["TABLE_NAME"])
            .ToList();

        Assert.Single(tableNames);
        Assert.Contains("person", tableNames, StringComparer.OrdinalIgnoreCase);
    }

    // ---- (3) Tables: a .dbc container ------------------------------------------------------

    [Fact]
    public void GetSchema_Tables_Dbc_ListsContainerTables()
    {
        string dbc = Fixtures.Tastrade("tastrade.dbc");
        if (!File.Exists(dbc)) return; // committed silver fixture; skip if absent.

        using var conn = new FoxDbfConnection($"Data Source={dbc};ReadOnly=true");
        conn.Open();

        DataTable t = conn.GetSchema("Tables");

        var names = t.Rows.Cast<DataRow>()
            .Select(r => (string)r["TABLE_NAME"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(names);
        Assert.Contains("customer", names);  // a DBC OBJECTTYPE=="Table" member.
    }

    // ---- (4) Columns ----------------------------------------------------------------------

    [Fact]
    public void GetSchema_Columns_MatchesDbfTableColumns()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema("Columns", new string?[] { null, null, "person", null });

        foreach (var col in new[]
                 {
                     "TABLE_NAME", "COLUMN_NAME", "ORDINAL_POSITION", "DATA_TYPE",
                     "CHARACTER_MAXIMUM_LENGTH", "NUMERIC_PRECISION", "NUMERIC_SCALE", "IS_NULLABLE",
                 })
            Assert.Contains(col, t.Columns.Cast<DataColumn>().Select(c => c.ColumnName));

        // Ground truth: the actual DBF descriptors.
        using var dbf = DbfTable.Open(Path.Combine(db.Path, "person.dbf"));
        Assert.Equal(dbf.Columns.Count, t.Rows.Count);

        for (int i = 0; i < dbf.Columns.Count; i++)
        {
            var expected = dbf.Columns[i];
            var row = t.Rows.Cast<DataRow>().Single(r =>
                string.Equals((string)r["COLUMN_NAME"], expected.Name, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(i + 1, Convert.ToInt32(row["ORDINAL_POSITION"]));            // 1-based.
            Assert.Equal(expected.Type.ToString(), (string)row["DATA_TYPE"], ignoreCase: true);
            // As VFPOLEDB: a character length only for C/V/Q (field width) and M/G/P/W (int.MaxValue).
            if (expected.Type is 'C' or 'V' or 'Q')
                Assert.Equal(expected.Length, Convert.ToInt32(row["CHARACTER_MAXIMUM_LENGTH"]));
            else if (expected.Type is 'M' or 'G' or 'P' or 'W')
                Assert.Equal(int.MaxValue, Convert.ToInt32(row["CHARACTER_MAXIMUM_LENGTH"]));
            else
                Assert.Equal(DBNull.Value, row["CHARACTER_MAXIMUM_LENGTH"]);
            Assert.Equal(expected.Decimal, Convert.ToInt32(row["NUMERIC_SCALE"]));
        }

        // ORDINAL_POSITION is dense & 1-based across the whole set.
        var ordinals = t.Rows.Cast<DataRow>().Select(r => Convert.ToInt32(r["ORDINAL_POSITION"])).OrderBy(x => x);
        Assert.Equal(Enumerable.Range(1, dbf.Columns.Count), ordinals);
    }

    [Fact]
    public void GetSchema_Columns_ShortRestrictionArrays_TreatMissingEntriesAsNoFilter()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable noTableFilter = conn.GetSchema("Columns", Array.Empty<string?>());
        Assert.Empty(noTableFilter.Rows);

        DataTable columns = conn.GetSchema("Columns", new string?[] { null, null, "person" });

        using var dbf = DbfTable.Open(Path.Combine(db.Path, "person.dbf"));
        Assert.Equal(dbf.Columns.Count, columns.Rows.Count);
        Assert.Contains(columns.Rows.Cast<DataRow>(),
            r => string.Equals((string)r["COLUMN_NAME"], "name", StringComparison.OrdinalIgnoreCase));
    }

    // ---- (5) DataTypes --------------------------------------------------------------------

    [Fact]
    public void GetSchema_DataTypes_CoverDbfFieldTypes_WithReaderClrMapping()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema("DataTypes");

        foreach (var col in new[] { "TypeName", "DataType" })
            Assert.Contains(col, t.Columns.Cast<DataColumn>().Select(c => c.ColumnName));

        var byType = t.Rows.Cast<DataRow>()
            .ToDictionary(r => (string)r["TypeName"], r => (string)r["DataType"], StringComparer.OrdinalIgnoreCase);

        // Every DBF field type the provider exposes must appear.
        foreach (var type in new[] { "C", "N", "F", "I", "Y", "B", "D", "T", "L", "M", "V", "G", "Q", "W" })
            Assert.True(byType.ContainsKey(type), $"DataTypes is missing field type '{type}'.");

        // The CLR mapping MUST match the DataReader's GetFieldType (single source of truth):
        // C/M/V -> string, N/F/Y -> decimal, I -> int, B -> double, L -> bool,
        // D/T -> DateTime (D surfaces as DateTime for ADO.NET), G/Q/W -> byte[].
        Assert.Equal(typeof(string).FullName, byType["C"]);
        Assert.Equal(typeof(string).FullName, byType["M"]);
        Assert.Equal(typeof(decimal).FullName, byType["N"]);
        Assert.Equal(typeof(decimal).FullName, byType["Y"]);
        Assert.Equal(typeof(int).FullName, byType["I"]);
        Assert.Equal(typeof(double).FullName, byType["B"]);
        Assert.Equal(typeof(bool).FullName, byType["L"]);
        Assert.Equal(typeof(DateTime).FullName, byType["D"]);
        Assert.Equal(typeof(DateTime).FullName, byType["T"]);
        Assert.Equal(typeof(byte[]).FullName, byType["G"]);
    }

    // ---- DataSourceInformation ------------------------------------------------------------

    [Fact]
    public void GetSchema_DataSourceInformation_HasProductName()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        DataTable t = conn.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);

        Assert.Single(t.Rows);
        Assert.Contains("DataSourceProductName",
            t.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
        Assert.False(string.IsNullOrEmpty((string)t.Rows[0]["DataSourceProductName"]));
    }

    // ---- (6) Unknown collection -----------------------------------------------------------

    [Fact]
    public void GetSchema_UnknownCollection_ThrowsArgumentException()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        Assert.Throws<ArgumentException>(() => conn.GetSchema("NoSuchCollection"));
    }
}
