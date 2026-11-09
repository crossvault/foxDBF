using System.Diagnostics;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

// Expectations are pinned by MicroVfpDeleteRecallVfp9OracleTests (four rows: 3, 1, 4, 2).
public sealed class MicroVfpDeleteRecallTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("", 2, 0, "0101")]
    [InlineData("ALL", 5, 2, "1111")]
    [InlineData("REST", 5, 1, "0111")]
    [InlineData("NEXT 2", 3, 1, "0111")]
    [InlineData("NEXT 0", 2, 0, "0101")]
    [InlineData("RECORD 3", 3, 1, "0111")]
    [InlineData("FOR id > 1", 5, 2, "1111")]
    [InlineData("WHILE id < 4", 3, 0, "0101")]
    [InlineData("ALL WHILE id < 4", 3, 1, "1101")]
    [InlineData("ALL NOOPTIMIZE", 5, 2, "1111")]
    public void Delete_ScopesCountTransitionsAndLeaveVfpPointer(string scope, int recno, int tally, string flags)
    {
        using var f = new Fixture();
        f.Run("GO 2\nDELETE\nGO 4\nDELETE\nGO 2\nDELETE " + scope);
        f.AssertState(recno, tally, flags);
    }

    [Theory]
    [InlineData("ALL", "", 5, 2, "0000")]
    [InlineData("FOR id > 1", "", 5, 1, "0100")]
    [InlineData("ALL", "SET FILTER TO id > 1", 5, 1, "0100")]
    [InlineData("NEXT 2", "INDEX ON id TAG id", 4, 2, "0000")]
    [InlineData("WHILE id < 4", "INDEX ON id TAG id", 3, 2, "0000")]
    public void Recall_SeesDeletedRowsWithDeletedOn(string scope, string setup, int recno, int tally, string flags)
    {
        using var f = new Fixture();
        f.Run("GO 2\nDELETE\nGO 4\nDELETE\n" + setup + "\nSET DELETED ON\nGO 2\nRECALL " + scope);
        f.AssertState(recno, tally, flags);
    }

    [Fact]
    public void InAlias_MovesTargetAndEvaluatesTargetFields()
    {
        using var f = new Fixture();
        f.Run("CREATE TABLE other (id I)\nINSERT INTO other VALUES (99)\nSELECT items\nGO 2\nSELECT other\nDELETE ALL FOR id < 4 IN items");
        Assert.Equal("OTHER", f.Interpreter.EvalExpression("ALIAS()").AsString);
        Assert.Equal(1, f.Num("RECNO()"));
        Assert.Equal(5, f.Num("RECNO('items')"));
        Assert.Equal("1101", f.Flags());
        f.Run("RECALL ALL IN items");
        Assert.Equal("0000", f.Flags());
        Assert.Equal(3, f.Num("_TALLY"));
    }

    [Theory]
    [InlineData("BOGUS", 36)]
    [InlineData("ALL xyz", 36)]
    [InlineData("ALL FOR", 152)]
    [InlineData("ALL FOR id >", 1231)]
    [InlineData("ALL NOOPTIMIZE garbage", 36)]
    [InlineData("ALL FOR .T. FOR .F.", 36)]
    [InlineData("ALL IN", 36)]
    [InlineData("ALL IN nosuch", 13)]
    [InlineData("NEXT absent", 12)]
    [InlineData("NEXT 'two'", 10)]
    [InlineData("RECORD 99", 5)]
    public void InvalidCommand_LeavesBytesPointerAndTallyUntouched(string tail, int number)
    {
        using var f = new Fixture();
        foreach (string command in new[] { "DELETE", "RECALL" })
        {
            f.Run("GO 2\n_TALLY=77");
            byte[] before = f.Bytes();
            var error = Assert.Throws<MicroVfpRuntimeException>(() => f.Run(command + " " + tail));
            Assert.Equal(number, error.VfpErrorNumber);
            Assert.Equal(before, f.Bytes());
            Assert.Equal(2, f.Num("RECNO()"));
            Assert.Equal(77, f.Num("_TALLY"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InScope_UsesTargetFieldsAndRestoresCallerOnError(bool recall)
    {
        using var f = new Fixture();
        f.Run("CREATE TABLE other (n I)\nINSERT INTO other VALUES (99)\nSELECT items\nGO 1");
        if (recall) f.Run("DELETE ALL\nGO 1");
        string verb = recall ? "RECALL" : "DELETE";
        f.Run($"SELECT other\n{verb} RECORD id IN items");
        Assert.Equal("OTHER", f.Interpreter.EvalExpression("ALIAS()").AsString);
        Assert.Equal(3, f.Num("RECNO('items')"));
        Assert.Equal(recall ? "1101" : "0010", f.Flags());
        f.Run("SELECT items\nGO 1\nSELECT other\n_TALLY=77");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => f.Run($"{verb} RECORD (id+99) IN items"));
        Assert.Equal(5, error.VfpErrorNumber);
        Assert.Equal("OTHER", f.Interpreter.EvalExpression("ALIAS()").AsString);
        Assert.Equal(1, f.Num("RECNO('items')"));
        Assert.Equal(77, f.Num("_TALLY"));
    }

    [Theory]
    [InlineData("NEXT", 10, "Syntax error.")]
    [InlineData("RECORD", 10, "Syntax error.")]
    [InlineData("ALL FOR", 152, "Missing expression.")]
    [InlineData("ALL WHILE", 152, "Missing expression.")]
    [InlineData("ALL FOR id >", 1231, "Missing operand.")]
    [InlineData("ALL FOR .T. FOR .F.", 36, "Command contains unrecognized phrase/keyword.")]
    [InlineData("ALL FOR 1", 1127, "You must use a logical expression with a FOR or WHILE clause.")]
    [InlineData("ALL WHILE 1", 1127, "You must use a logical expression with a FOR or WHILE clause.")]
    public void InvalidScope_ReportsVfpNumberMessageAndAerror(string tail, int number, string message)
    {
        using var f = new Fixture();
        foreach (string verb in new[] { "DELETE", "RECALL" })
        {
            f.Run($"DIMENSION errors[1]\nTRY\n{verb} {tail}\nCATCH TO failure\n=AERROR(errors)\nENDTRY");
            Assert.Equal(number, f.Num("failure.ErrorNo"));
            Assert.Equal(message, f.Interpreter.EvalExpression("failure.Message").AsString);
            Assert.Equal(message, f.Interpreter.EvalExpression("MESSAGE()").AsString);
            Assert.Equal(number, f.Num("errors[1,1]"));
            Assert.Equal(message, f.Interpreter.EvalExpression("errors[1,2]").AsString);
            Assert.Equal("0000", f.Flags());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PredicateError_LeavesEarlierChangesAndPartialTally(bool recall)
    {
        using var f = new Fixture();
        if (recall) f.Run("DELETE ALL");
        f.Run("GO 1\n_TALLY=77");
        var error = Assert.Throws<MicroVfpRuntimeException>(() => f.Run($"""
            {(recall ? "RECALL" : "DELETE")} ALL FOR checkrow(id)
            FUNCTION checkrow
            LPARAMETERS n
            IF n=4
                RETURN missing_variable
            ENDIF
            RETURN .T.
            ENDFUNC
            """));
        Assert.Equal(12, error.VfpErrorNumber);
        f.AssertState(3, 2, recall ? "0011" : "1100");
    }

    [Theory]
    [InlineData(3, "1100", 0)]
    [InlineData(5, "0000", 2)]
    public void BufferedScopes_CommitOrRevertPerVfpMode(int buffering, string disk, int reverted)
    {
        using var f = new Fixture();
        f.Run($"SET MULTILOCKS ON\n=CURSORSETPROP('Buffering',{buffering})\nGO 1\nDELETE NEXT 2");
        Assert.Equal(disk, f.Flags());
        Assert.Equal(reverted, f.Num("TABLEREVERT(.T.)"));
        f.Run("GO 1\nDELETE NEXT 2\n=TABLEUPDATE(.T.)");
        Assert.Equal("1100", f.Flags());
        f.Run("RECALL ALL");
        Assert.True(f.Interpreter.EvalExpression("GETFLDSTATE(0)").IsNull);
        f.Run("=TABLEUPDATE(.T.)");
        Assert.Equal("0000", f.Flags());
    }

    [Fact]
    public void InvalidSyntax_DoesNotDirtyTableBuffer()
    {
        using var f = new Fixture();
        f.Run("=CURSORSETPROP('Buffering',5)");
        Assert.Throws<MicroVfpRuntimeException>(() => f.Run("DELETE ALL FOR id >"));
        Assert.Equal(0, f.Num("TABLEREVERT(.T.)"));
        Assert.Equal("0000", f.Flags());
    }

    [Theory]
    [InlineData(2, "")]
    [InlineData(3, "")]
    [InlineData(2, "NEXT 1")]
    [InlineData(3, "NEXT 1")]
    public void SingleRowDelete_DefersTriggerAndReports1539OnMove(int buffering, string scope)
    {
        using var f = new Fixture();
        f.Interpreter.EnforceReferentialIntegrity = true;
        f.Run($"SET MULTILOCKS ON\n=CURSORSETPROP('Buffering',{buffering})\nGO 1\nPUBLIC trace\ntrace=''\nFUNCTION __RI_DELETE_items\ntrace=trace+TRANSFORM(id)+TRANSFORM(DELETED())\nRETURN .F.\nENDFUNC");
        f.Run("DELETE " + scope);
        Assert.Equal("", f.Interpreter.EvalExpression("trace").AsString);
        Assert.Equal(2, f.Num("GETFLDSTATE(0)"));
        Assert.Equal("0000", f.Flags());
        f.Run("DIMENSION errors[1]\nTRY\nSKIP\nCATCH TO failure\n=AERROR(errors)\nENDTRY");
        Assert.Equal(1539, f.Num("failure.ErrorNo"));
        Assert.Equal("Trigger failed in Items.", f.Interpreter.EvalExpression("MESSAGE()").AsString);
        Assert.Equal("Trigger failed in Items.", f.Interpreter.EvalExpression("errors[1,2]").AsString);
        Assert.Equal("3.T.", f.Interpreter.EvalExpression("trace").AsString);
        Assert.Equal(1, f.Num("RECNO()"));
        Assert.Equal(1, f.Num("TABLEREVERT(.T.)"));
        Assert.Equal("0000", f.Flags());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void TableUpdate_DeleteRejectionReturnsFalseAndRetainsError(int buffering)
    {
        using var f = new Fixture();
        f.Interpreter.EnforceReferentialIntegrity = true;
        f.Run($"SET MULTILOCKS ON\n=CURSORSETPROP('Buffering',{buffering})\nGO 1\nFUNCTION __RI_DELETE_items\nRETURN .F.\nENDFUNC\nDELETE");
        f.Run("DIMENSION errors[1]\nsaved=TABLEUPDATE(.T.)\nerrorcount=AERROR(errors)");
        Assert.False(f.Interpreter.EvalExpression("saved").AsLogical);
        Assert.Equal(1, f.Num("errorcount"));
        Assert.Equal(1539, f.Num("errors[1,1]"));
        Assert.Equal("Trigger failed in Items.", f.Interpreter.EvalExpression("MESSAGE()").AsString);
        Assert.Equal("Trigger failed in Items.", f.Interpreter.EvalExpression("errors[1,2]").AsString);
        Assert.Equal("0000", f.Flags());
        Assert.Equal(1, f.Num("TABLEREVERT(.T.)"));
    }

    [Fact]
    public void OnError_ReceivesPredicateFailureAfterPartialMutation()
    {
        using var f = new Fixture();
        f.Run("handled=0\nON ERROR handled=ERROR()\nDELETE ALL FOR IIF(id=4,missing_variable,.T.)");
        Assert.Equal(12, f.Num("handled"));
        f.AssertState(3, 2, "1100");
    }

    [Fact]
    public void DeleteAll_OneWritePathForOneHundredThousandRows()
    {
        using var f = new Fixture(100_000);
        int writePaths = 0;
        // Exercise transient writer leases: a per-row lease would reopen the DBF 100,000 times.
        f.Session.TxBeginWritePath = path => { writePaths++; return path; };
        var watch = Stopwatch.StartNew();
        f.Run("DELETE ALL");
        watch.Stop();
        output.WriteLine($"DELETE ALL 100,000 rows: {watch.ElapsedMilliseconds} ms; write paths: {writePaths}");
        Assert.Equal(1, writePaths);
        Assert.Equal(100_000, f.Num("_TALLY"));
        Assert.Equal(100_001, f.Num("RECNO()"));
        Assert.All(f.Flags(), flag => Assert.Equal('1', flag));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void DeleteTrigger_RetainsEarlierRowsAndCountsRejectedAttempt(int buffering)
    {
        using var f = new Fixture();
        f.Interpreter.EnforceReferentialIntegrity = true;
        f.Run($"""
            SET MULTILOCKS ON
            =CURSORSETPROP('Buffering',{buffering})
            PUBLIC trace
            trace=''
            FUNCTION __RI_DELETE_items
            trace=trace+TRANSFORM(id)+TRANSFORM(DELETED())
            RETURN id<>4
            ENDFUNC
            """);
        if (buffering == 5)
        {
            f.Run("DELETE ALL");
            Assert.False(f.Interpreter.EvalExpression("TABLEUPDATE(.T.)").AsLogical);
            Assert.Equal(2, f.Num("TABLEREVERT(.T.)"));
        }
        else
        {
            var error = Assert.Throws<MicroVfpRuntimeException>(() => f.Run("DELETE ALL"));
            Assert.Equal(1539, error.VfpErrorNumber);
            Assert.Equal(3, f.Num("_TALLY"));
            Assert.Equal(3, f.Num("RECNO()"));
        }
        Assert.Equal("3.T.1.T.4.T.", f.Interpreter.EvalExpression("trace").AsString);
        Assert.Equal("1100", f.Flags());
    }

    [Fact]
    public void EmptyScopes_RetainBothEndFlags()
    {
        using var f = new Fixture(0);
        foreach (string command in new[] { "DELETE", "RECALL", "DELETE NEXT 2", "RECALL REST", "DELETE FOR .F.", "RECALL ALL" })
        {
            f.Run(command);
            Assert.Equal(1, f.Num("RECNO()"));
            Assert.True(f.Interpreter.EvalExpression("EOF() AND BOF()").AsLogical);
            Assert.Equal(0, f.Num("_TALLY"));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MicroVfpTestSupport.TempDir _dir = new("delete_recall");
        private readonly string _dbf;
        public VfpSession Session { get; }
        public VfpInterpreter Interpreter { get; }

        public Fixture(int count = 4)
        {
            _dbf = _dir.File("items.dbf");
            using (var writer = DbfWriter.Create(_dbf, [new DbfColumnDef("ID", 'I', 4)]))
            {
                for (int i = 0; i < count; i++) writer.AppendRecord(count == 4 ? new[] { 3, 1, 4, 2 }[i] : i + 1);
            }
            Session = new VfpSession();
            Session.OpenDirectory(_dir.Path);
            Interpreter = new VfpInterpreter(Session);
            Run("SET DELETED OFF\nUSE items EXCLUSIVE");
        }

        public void Run(string source) => Interpreter.Execute(source);
        public int Num(string expression) => (int)Interpreter.EvalExpression(expression).AsNumber;
        public byte[] Bytes()
        {
            using var stream = new FileStream(_dbf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        public string Flags()
        {
            byte[] bytes = Bytes();
            int header = BitConverter.ToUInt16(bytes, 8), length = BitConverter.ToUInt16(bytes, 10);
            return new string(Enumerable.Range(0, BitConverter.ToInt32(bytes, 4))
                .Select(i => bytes[header + i * length] == '*' ? '1' : '0').ToArray());
        }
        public void AssertState(int recno, int tally, string flags)
        {
            Assert.Equal(recno, Num("RECNO()"));
            Assert.Equal(recno == 5, Interpreter.EvalExpression("EOF()").AsLogical);
            Assert.Equal(tally, Num("_TALLY"));
            Assert.Equal(flags, Flags());
        }
        public void Dispose()
        {
            Interpreter.Dispose();
            Session.Dispose();
            _dir.Dispose();
        }
    }
}
