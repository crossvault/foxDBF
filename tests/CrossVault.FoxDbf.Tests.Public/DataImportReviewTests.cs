using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

// Captures from DataImportReviewVfp9OracleTests, using only synthetic input.
public sealed class DataImportReviewTests
{
    [Theory]
    [InlineData("SDF")]
    [InlineData("DELIMITED")]
    public void SpacesInNumeric_AreStoredZero(string format)
    {
        using var temp = new MicroVfpTestSupport.TempDir("importblank");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        File.WriteAllText(temp.File("input.txt"), "   \r\n");
        interpreter.Execute("CREATE TABLE t(n N(3))\nAPPEND FROM input.txt " + format);
        Assert.False(interpreter.EvalExpression("ISBLANK(n)").AsLogical);
        interpreter.Execute("USE");
        using var table = DbfTable.Open(temp.File("t.dbf"));
        Assert.Equal("20202030", Convert.ToHexString(table.GetRecord(0)!.Value.Raw));
    }

    [Theory]
    [InlineData(false, "OK:3|3|1;***********;3;")]
    [InlineData(true, "E39|3|1;***********;3;")]
    public void LargeIntegerImport_PreservesSentinelAndPrefix(bool array, string expected)
    {
        using var temp = new MicroVfpTestSupport.TempDir("importlarge");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        File.WriteAllText(temp.File("input.txt"), "1\r\n1e30\r\n3\r\n");
        string setup = "CREATE TABLE t(n I)\nDIMENSION aa[3,1]\naa[1,1]=1\naa[2,1]=1e30\naa[3,1]=3";
        interpreter.Execute(DataImportTests.Script(setup, array ? "APPEND FROM ARRAY aa" : "APPEND FROM input.txt DELIMITED", "TRANSFORM(n)"));
        Assert.Equal(expected, interpreter.EvalExpression("result").AsString);
        interpreter.Execute("USE");
        using var table = DbfTable.Open(temp.File("t.dbf"));
        Assert.Equal("2000000080", Convert.ToHexString(table.GetRecord(1)!.Value.Raw));
    }

    [Theory]
    [InlineData("SET DATE TO GERMAN", "03.02.2024", "20240203")]
    [InlineData("SET CENTURY TO 21 ROLLOVER 40", "02/03/39", "22390203")]
    [InlineData("SET CENTURY TO 21 ROLLOVER 40", "02/03/40", "21400203")]
    public void ImportDate_HonorsToAndRollover(string setting, string input, string expected)
    {
        using var temp = new MicroVfpTestSupport.TempDir("importdate");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        File.WriteAllText(temp.File("input.txt"), input + "\r\n");
        interpreter.Execute("CREATE TABLE t(d D)\n" + setting + "\nAPPEND FROM input.txt DELIMITED");
        Assert.Equal(expected, interpreter.EvalExpression("DTOS(d)").AsString);
    }

    [Theory]
    [InlineData(932)]
    [InlineData(1252)]
    public void Sdf_UsesSourceByteWidths(int tablePage)
    {
        using var temp = new MicroVfpTestSupport.TempDir("importdbcs");
        using var session = new VfpSession();
        session.OpenDirectory(temp.Path);
        using var interpreter = new VfpInterpreter(session);
        File.WriteAllBytes(temp.File("input.txt"), [0x82, 0xa0, 32, 32, 32, 32, 32, 32, 32, 32, 52, 50, 13, 10]);
        interpreter.Execute($"CREATE TABLE t CODEPAGE={tablePage} (c C(10),n N(2))\nAPPEND FROM input.txt SDF AS 932");
        Assert.Equal(42, interpreter.EvalExpression("n").AsNumber);
        interpreter.Execute("USE");
        using var table = DbfTable.Open(temp.File("t.dbf"));
        Assert.Equal("2082A020202020202020203432", Convert.ToHexString(table.GetRecord(0)!.Value.Raw));
    }
}
