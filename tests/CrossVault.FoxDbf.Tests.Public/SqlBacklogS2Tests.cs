using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlBacklogS2Tests
{
    [Theory]
    [InlineData("id=3.AND.name='x'")]
    [InlineData("id=3.OR.id=4")]
    [InlineData("id=3.AND..NOT.name='y'")]
    public void S6_DottedBooleanAfterNumber_UsesIndexAndMatchesFullScan(string filter)
    {
        using var dir = new MicroVfpTestSupport.TempDir("s6_tokens");
        string path = dir.File("ct.dbf");
        using (var writer = DbfWriter.Create(path, new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 4) }))
        {
            for (int i = 1; i <= 100; i++) writer.AppendRecord(i, i == 3 ? "x" : "y");
            writer.CreateTag(new CdxTagDefinition("idtag", "id"));
        }
        using var table = DbfTable.Open(path);
        using var cdx = CdxFile.Open(Path.ChangeExtension(path, ".cdx"), table);
        var scan = QueryOptimizer.FindRecords(table, cdx, filter, new EvaluationContext { Optimize = false });
        var indexed = QueryOptimizer.FindRecords(table, cdx, filter, new EvaluationContext { Optimize = true });
        Assert.Equal(scan.RecordNumbers, indexed.RecordNumbers);
        Assert.True(indexed.Optimized);
        Assert.True(indexed.RecordsScanned < scan.RecordsScanned);
        Assert.Contains("idtag", QueryOptimizer.Explain(table, cdx, filter).UsedTags, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT g,COUNT(*) FROM ct GROUP BY g HAVING id>0", 1803)]
    [InlineData("SELECT g,COUNT(*) FROM ct GROUP BY g HAVING cnt>0", 1806)]
    [InlineData("SELECT g+1,COUNT(*) FROM ct GROUP BY 1 HAVING exp_1>0", 1806)]
    [InlineData("SELECT DISTINCT g FROM ct ORDER BY id", 1808)]
    [InlineData("SELECT g,COUNT(*) FROM ct GROUP BY g HAVING g IN (SELECT g FROM ct)", 1810)]
    public void S5_InvalidQueries_RejectBeforeCreatingCursor_EvenWithNoRows(string sql, int number)
    {
        using var dir = new MicroVfpTestSupport.TempDir("s5_errors");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE ct (id I, g I)");
        for (int rows = 0; rows <= 1; rows++)
        {
            var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(sql + " INTO CURSOR rejected"));
            Assert.Equal(number, error.VfpErrorNumber);
            Assert.Null(session.FindAreaByAlias("rejected"));
            session.Execute("INSERT INTO ct VALUES (1,1)");
        }
    }

    [Theory]
    [InlineData("SUM", 16)]
    [InlineData("AVG", 16)]
    [InlineData("MIN", 8)]
    [InlineData("MAX", 8)]
    public void EmptyAggregate_HasOneNullRow_OrNoGroupedRows(string function, int width)
    {
        using var dir = new MicroVfpTestSupport.TempDir("s2_empty");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE ct (id I, n N(8,2))");
        var result = session.Execute($"SELECT {function}(n) AS x FROM ct")!;
        Assert.Null(Assert.Single(result.Rows)[0]);
        var col = Assert.Single(result.Columns);
        Assert.Equal(('N', width, 2), (col.VfpType, col.Length, col.Decimals));
        Assert.Empty(session.Execute($"SELECT {function}(n) AS x FROM ct GROUP BY id")!.Rows);
    }

    [Theory]
    [InlineData("m.threshold")]
    [InlineData("threshold")]
    [InlineData("m.g")]
    public void HavingValidation_PreservesBoundMemoryVariables(string variable)
    {
        using var dir = new MicroVfpTestSupport.TempDir("s5_memory");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE ct (id I, g I)");
        session.Execute("INSERT INTO ct VALUES (1,1)");
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("threshold=0\ng=0");
        interpreter.Execute($"SELECT g,COUNT(*) AS total FROM ct GROUP BY g HAVING total>{variable} INTO CURSOR result");
        Assert.Single(session.Execute("SELECT * FROM result")!.Rows);
    }

    [Fact]
    public void NullFirstVal_SizesBeforeWhere_AndStoresOverflowAsStars()
    {
        using var dir = new MicroVfpTestSupport.TempDir("s2_val");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE ct (id I, n N(8,2) NULL)");
        session.Execute("INSERT INTO ct VALUES (1,.NULL.)");
        session.Execute("INSERT INTO ct VALUES (2,12.34)");
        const string sql = "SELECT VAL(STR(n)) AS x FROM ct WHERE id>1";
        var result = session.Execute(sql)!;
        var column = Assert.Single(result.Columns);
        Assert.Equal(('N', 1, 0), (column.VfpType, column.Length, column.Decimals));
        Assert.Equal(double.PositiveInfinity, Assert.Single(result.Rows)[0]);
        session.Execute(sql + " INTO CURSOR stored");
        var table = session.FindAreaByAlias("stored")!.Table;
        Assert.Equal("*"u8.ToArray(), table.GetRecord(0)!.Value.GetRawField(table.Columns[0]).ToArray());
        Assert.Equal(double.PositiveInfinity, Assert.Single(session.Execute("SELECT x FROM stored")!.Rows)[0]);
        Assert.Equal("*****", Assert.Single(session.Execute("SELECT STR(x,5) FROM stored")!.Rows)[0]);
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("rendered=TRANSFORM(stored.x)");
        // Round3StoredField_TransformMatchesVfp9: the field retains N(1,0),
        // while assignment to a memory variable widens it to ten columns.
        Assert.Equal("*", interpreter.Memory.Get("rendered").AsString);
        interpreter.Execute("v=stored.x\nrendered=TRANSFORM(v)");
        Assert.Equal("**********", interpreter.Memory.Get("rendered").AsString);
    }

    [Theory]
    [InlineData("DATETIME(2024,2,29)", 0, 0, 0)]
    [InlineData("DATETIME(2024,2,29,12)", 12, 0, 0)]
    [InlineData("DATETIME(2024,2,29,12,34)", 12, 34, 0)]
    [InlineData("DATETIME(2024,2,29,12,34,56)", 12, 34, 56)]
    [InlineData("DATETIME(2024.9,2.9,29.9,12.9,34.9,56.9)", 12, 34, 56)]
    public void DateTimeArguments_InterpretCompileSqlAndMicroVfp(string expression, int hour, int minute, int second)
    {
        var expected = new DateTime(2024, 2, 29, hour, minute, second);
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(expected, parsed.Evaluate(TestRow.Empty).AsDateTime);
        Assert.Equal(expected, parsed.Compile()(TestRow.Empty).AsDateTime);
        using var dir = new MicroVfpTestSupport.TempDir("s2_datetime");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        session.Execute("CREATE TABLE ct (id I)");
        session.Execute("INSERT INTO ct VALUES (1)");
        var result = session.Execute($"SELECT {expression} AS x FROM ct")!;
        Assert.Equal(expected, Assert.Single(result.Rows)[0]);
        Assert.Equal(('T', 8, 0), (result.Columns[0].VfpType, result.Columns[0].Length, result.Columns[0].Decimals));
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("value=" + expression);
        Assert.Equal(expected, interpreter.Memory.Get("value").AsDateTime);
        interpreter.Execute($"SELECT {expression} AS x FROM ct INTO CURSOR stored");
        Assert.Equal(expected, Assert.Single(session.Execute("SELECT x FROM stored")!.Rows)[0]);
    }

    [Theory]
    [InlineData("DATETIME(24,1,1)", 11)]
    [InlineData("DATETIME(2024,13,1)", 11)]
    [InlineData("DATETIME(2024,1,1,24)", 11)]
    [InlineData("DATETIME(2024,1,1,0,60)", 11)]
    [InlineData("DATETIME(2024,1,1,0,0,60)", 11)]
    [InlineData("DATETIME('2024',1,1)", 11)]
    [InlineData("DATETIME(2024)", 1229)]
    [InlineData("DATETIME(2024,1)", 1229)]
    [InlineData("DATETIME(2024,1,1,0,0,0,0)", 1230)]
    public void DateTimeInvalidArguments_ReportVfpNumber(string expression, int number)
    {
        // VFP9 rejects excess arguments while compiling; component/count errors can also occur at runtime.
        Assert.Equal(number, Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expression).Evaluate(TestRow.Empty)).VfpErrorNumber);
        Assert.Equal(number, Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expression).Compile()(TestRow.Empty)).VfpErrorNumber);
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute($"TRY\nvalue={expression}\nCATCH TO err\nnumber=err.ErrorNo\nENDTRY");
        Assert.Equal(number, interpreter.Memory.Get("number").AsInteger);
    }

    [Fact]
    public void DateTimeNullComponentsAndImpossibleCalendarDay_MatchVfp9()
    {
        DateTime today = DateTime.Today;
        Assert.Equal(new DateTime(today.Year, 1, 1), VfpExpression.Parse("DATETIME(.NULL.,1,1)").Evaluate(TestRow.Empty).AsDateTime);
        Assert.Equal(new DateTime(2024, today.Month, 1), VfpExpression.Parse("DATETIME(2024,.NULL.,1)").Evaluate(TestRow.Empty).AsDateTime);
        Assert.Equal(new DateTime(2024, 1, today.Day), VfpExpression.Parse("DATETIME(2024,1,.NULL.)").Evaluate(TestRow.Empty).AsDateTime);
        Assert.Equal(new DateTime(2024, 1, 1), VfpExpression.Parse("DATETIME(2024,1,1,.NULL.)").Evaluate(TestRow.Empty).AsDateTime);
        Assert.Equal(default, VfpExpression.Parse("DATETIME(2023,2,29)").Evaluate(TestRow.Empty).AsDateTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoubleDdl_DecimalsDoNotChangeStorageWidth(bool alter)
    {
        using var dir = new MicroVfpTestSupport.TempDir("s2_double");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        if (alter)
        {
            session.Execute("CREATE TABLE ct (id I)");
            session.Execute("ALTER TABLE ct ADD COLUMN x B(3)");
        }
        else session.Execute("CREATE TABLE ct (x B(3))");
        using var table = DbfTable.Open(dir.File("ct.dbf"));
        var c = table.Columns.Single(c => c.Name.Equals("x", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(('B', 8, 3), (c.Type, c.Length, c.Decimal));
    }
}
