using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P3 batch 4 (the FINAL backlog batch) — the remaining array / variable / DB-lifecycle / table
/// items: ADIR() / COPY TO ARRAY / AFONT (§C.1), SAVE TO / RESTORE FROM / WAIT / LIST|DISPLAY MEMORY (§C.4),
/// APPEND|COPY PROCEDURES / PACK DATABASE / VALIDATE DATABASE (§C.7), EXPORT|IMPORT / DISPLAY STRUCTURE|TABLES
/// (§C.14), SET FIELDS / SYS(2029) (§C.15), ZAP (§C.16).
///
/// Written TESTS-FIRST — RED until the commands/functions are wired. Public-safe: synthetic free tables in a
/// throwaway temp dir, or a FRESH TEMP COPY of the committed TasTrade sample DB (never a committed fixture,
/// never the VFP9 runtime — the byte-exact oracle golden lives in the Internal project).
/// </summary>
public sealed class MicroVfpP3MiscTests
{
    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_p3misc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public string PathOf(string name) => Path.Combine(Dir, name.EndsWith(".dbf") ? name : name + ".dbf");
        public string FileIn(string name) => Path.Combine(Dir, name);
        public void Run(string prg) => Interp.Execute(prg);

        /// <summary>A 3-row sales table (id/grp/amt) plus a memo column, ordered A,B,C.</summary>
        public void MakeSales(string name = "sales")
        {
            var cols = new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("grp", 'C', 1),
                new DbfColumnDef("amt", 'N', 6, 2),
                new DbfColumnDef("note", 'M'),
            };
            using var w = DbfWriter.Create(PathOf(name), cols);
            w.AppendRecord(1, "A", 10.00m, "hi");
            w.AppendRecord(2, "B", 20.00m, "yo");
            w.AppendRecord(3, "C", 30.00m, "zz");
            w.Flush();
        }

        public int DiskCount(string name)
        {
            using var t = DbfTable.Open(PathOf(name), new DbfOptions { LockMode = LockMode.Shared });
            return t.RecordCount;
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── §C.15 SYS(2029) ───────────────────────────

    [Fact]
    public void Sys2029_ReportsTheDbfVersionByteAsDecimalString()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales");
        // A VFP table's first header byte is 0x30 = 48 (oracle-pinned in the Internal project).
        Assert.Equal("48", b.Interp.EvalExpression("SYS(2029)").AsString);
        // No table in a bogus area ⇒ "0".
        Assert.Equal("0", b.Interp.EvalExpression("SYS(2029, 99)").AsString);
    }

    // ─────────────────────────── §C.15 SET FIELDS ───────────────────────────

    [Fact]
    public void SetFields_TracksListAndOnOff_ThroughFldlistAndSet()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nSET FIELDS TO id, amt");

        // FLDLIST() = the SET FIELDS list, alias-qualified + uppercase, comma-no-space (oracle-pinned).
        Assert.Equal("SALES.ID,SALES.AMT", b.Interp.EvalExpression("FLDLIST()").AsString);
        // SET("FIELDS") reports ON once a list is set; SET("FIELDS",1) is the bare list, ", "-separated.
        Assert.Equal("ON", b.Interp.EvalExpression("SET('FIELDS')").AsString);
        Assert.Equal("ID, AMT", b.Interp.EvalExpression("SET('FIELDS',1)").AsString);

        b.Run("SET FIELDS OFF");
        Assert.Equal("OFF", b.Interp.EvalExpression("SET('FIELDS')").AsString);

        b.Run("SET FIELDS TO");   // no list ⇒ clears
        Assert.Equal("", b.Interp.EvalExpression("FLDLIST()").AsString);
    }

    // ─────────────────────────── §C.16 ZAP ───────────────────────────

    [Fact]
    public void Zap_RemovesAllRecords_StructureKept()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales EXCLUSIVE\nZAP");

        Assert.Equal(0, b.DiskCount("sales"));   // all rows gone
        // Structure (columns) is kept: an independent read handle still sees the full field list.
        using var t = DbfTable.Open(b.PathOf("sales"), new DbfOptions { LockMode = LockMode.Shared });
        Assert.Equal(new[] { "ID", "GRP", "AMT", "NOTE" },
            t.Columns.Where(c => !c.IsSystem).Select(c => c.Name.ToUpperInvariant()).ToArray());
    }

    [Fact]
    public void Zap_In_TargetsTheNamedArea_NotTheCurrentOne()
    {
        using var b = new Bench();
        b.MakeSales("aa");
        b.MakeSales("bb");
        // Two areas open; the current one is bb. ZAP IN aa must hit aa, leaving bb untouched.
        b.Run("USE aa EXCLUSIVE IN 0\nUSE bb EXCLUSIVE IN 0\nSELECT bb\nZAP IN aa");

        Assert.Equal(0, b.DiskCount("aa"));
        Assert.Equal(3, b.DiskCount("bb"));
    }

    // ─────────────────────────── §C.1 COPY TO ARRAY ───────────────────────────

    [Fact]
    public void CopyToArray_UndefinedArray_AutoDimensionsToRecordsByFields()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nCOPY TO ARRAY aData");

        var arr = b.Interp.Memory.FindArray("aData");
        Assert.NotNull(arr);
        Assert.Equal(3, arr!.ALen(1));   // one row per record
        Assert.Equal(4, arr.ALen(2));    // one column per field (id/grp/amt/note)
        Assert.Equal(1m, arr.Get(1, 1).AsNumber);
        Assert.Equal("A", arr.Get(1, 2).AsString);
        Assert.Equal(30m, arr.Get(3, 3).AsNumber);
        // Memo column → .F. placeholder (never the memo string), like COPY TO ARRAY in VFP (hackfox s4g386).
        Assert.Equal(VfpType.Logical, arr.Get(1, 4).Type);
        Assert.False(arr.Get(1, 4).AsLogical);
    }

    [Fact]
    public void CopyToArray_Existing2DArray_FillsWithoutRedimensioning()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nDIMENSION a6[5,5]\nCOPY TO ARRAY a6 FIELDS id, amt");

        var arr = b.Interp.Memory.FindArray("a6");
        Assert.NotNull(arr);
        Assert.Equal(5, arr!.ALen(1));   // NOT redimensioned (existing array kept)
        Assert.Equal(5, arr.ALen(2));
        Assert.Equal(1m, arr.Get(1, 1).AsNumber);
        Assert.Equal(10m, arr.Get(1, 2).AsNumber);
        Assert.Equal(3m, arr.Get(3, 1).AsNumber);
        Assert.Equal(30m, arr.Get(3, 2).AsNumber);
    }

    [Fact]
    public void CopyToArray_Existing1DArray_CopiesFirstScopedRecordFields()
    {
        using var b = new Bench();
        b.MakeSales();
        // 1-D array, GO 2 first: COPY TO ARRAY copies the FIRST scoped record (record 1), not the current one.
        b.Run("USE sales\nDIMENSION a1[4]\nGO 2\nCOPY TO ARRAY a1");

        var arr = b.Interp.Memory.FindArray("a1");
        Assert.NotNull(arr);
        Assert.Equal(0, arr!.ALen(2));           // stayed 1-D
        Assert.Equal(1m, arr.Get(1, null).AsNumber);
        Assert.Equal("A", arr.Get(2, null).AsString);
        Assert.Equal(10m, arr.Get(3, null).AsNumber);
    }

    // ─────────────────────────── §C.1 ADIR() ───────────────────────────

    [Fact]
    public void Adir_FillsFiveColumnArray_AndReturnsMatchCount()
    {
        using var b = new Bench();
        File.WriteAllText(b.FileIn("aa.txt"), "12345");   // 5 bytes
        File.WriteAllText(b.FileIn("bb.txt"), "6789");    // 4 bytes
        File.WriteAllText(b.FileIn("skip.dat"), "x");     // not matched by *.txt

        b.Run("gnN = ADIR(gaFiles, '*.txt')");
        Assert.Equal(2, (int)b.Interp.EvalExpression("gnN").AsNumber);

        var arr = b.Interp.Memory.FindArray("gaFiles");
        Assert.NotNull(arr);
        Assert.Equal(2, arr!.ALen(1));
        Assert.Equal(5, arr.ALen(2));   // Name / Size / Date / Time / Attributes (oracle-pinned column count)

        // Column value types match VFP's ADIR layout: C, N, D, C, C.
        Assert.Equal(VfpType.Character, arr.Get(1, 1).Type);
        Assert.Equal(VfpType.Numeric, arr.Get(1, 2).Type);
        Assert.Equal(VfpType.Date, arr.Get(1, 3).Type);
        Assert.Equal(VfpType.Character, arr.Get(1, 4).Type);
        Assert.Equal(VfpType.Character, arr.Get(1, 5).Type);

        // Names (uppercase) + sizes are deterministic; order-independent set comparison.
        var byName = Enumerable.Range(1, 2)
            .ToDictionary(r => arr.Get(r, 1).AsString.Trim(), r => (int)arr.Get(r, 2).AsNumber);
        Assert.Equal(5, byName["AA.TXT"]);
        Assert.Equal(4, byName["BB.TXT"]);
    }

    // ─────────────────────────── §C.4 WAIT (headless, never blocks) ───────────────────────────

    [Fact]
    public void Wait_IsNonBlocking_AndFillsToVarWithEmptyString()
    {
        using var b = new Bench();
        // None of these may block; WAIT ... TO reads "" (no keypress in a headless run).
        b.Run("cKey = 'X'\nWAIT WINDOW 'building…' NOWAIT\nWAIT 'press a key' TO cKey TIMEOUT 1\nWAIT CLEAR");
        Assert.Equal("", b.Interp.Memory.Get("cKey").AsString);
    }

    // ─────────────────────────── §C.4 SAVE TO / RESTORE FROM ───────────────────────────

    [Fact]
    public void SaveTo_RestoreFrom_RoundTripsScalarsAndArrays()
    {
        using var b = new Bench();
        b.Run(@"
gcName = 'Ada'
gnAge = 36
glOk = .T.
DIMENSION gaNums[3]
gaNums[1] = 10
gaNums[2] = 20
gaNums[3] = 30
SAVE TO vars.mem");

        Assert.True(File.Exists(b.FileIn("vars.mem")));

        // Wipe memory, then RESTORE without ADDITIVE (implicit CLEAR MEMORY, then load).
        b.Run("CLEAR MEMORY\nRESTORE FROM vars.mem");
        Assert.Equal("Ada", b.Interp.Memory.Get("gcName").AsString);
        Assert.Equal(36m, b.Interp.Memory.Get("gnAge").AsNumber);
        Assert.True(b.Interp.Memory.Get("glOk").AsLogical);
        var arr = b.Interp.Memory.FindArray("gaNums");
        Assert.NotNull(arr);
        Assert.Equal(3, arr!.ALen(1));
        Assert.Equal(20m, arr.Get(2, null).AsNumber);
    }

    [Fact]
    public void RestoreFrom_Additive_KeepsExistingVars()
    {
        using var b = new Bench();
        b.Run("gcName = 'Ada'\nSAVE TO one.mem ALL LIKE gc*");
        b.Run("CLEAR MEMORY\ngcOther = 'kept'\nRESTORE FROM one.mem ADDITIVE");

        Assert.Equal("Ada", b.Interp.Memory.Get("gcName").AsString);    // loaded
        Assert.Equal("kept", b.Interp.Memory.Get("gcOther").AsString);  // preserved (ADDITIVE)
    }

    [Fact]
    public void RestoreFrom_Additive_RestoresAsPrivate_EvenOverExistingPublic()
    {
        using var b = new Bench();
        b.Run("gcName = 'saved'\nSAVE TO p.mem ALL LIKE gc*");
        // Re-create gcName as a PUBLIC, then RESTORE ADDITIVE: the value comes from the .mem AND the binding
        // is re-scoped PRIVATE (hackfox s4g222 — "all variables are restored with private scope"), NOT left
        // PUBLIC. (Regression: RESTORE used to reuse the existing cell's kind and leave it PUBLIC.)
        b.Run("CLEAR MEMORY\nPUBLIC gcName\ngcName = 'pub'\nRESTORE FROM p.mem ADDITIVE");

        Assert.Equal("saved", b.Interp.Memory.Get("gcName").AsString);
        var binding = b.Interp.Memory.EnumerateVisible()
            .First(x => x.Name.Equals("gcName", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(VarKind.Private, binding.Kind);
    }

    [Fact]
    public void SaveTo_AllLikeExcept_FiltersWhichVarsPersist()
    {
        using var b = new Bench();
        b.Run("gcKeep = 'k'\ngnDrop = 9\nSAVE TO f.mem ALL EXCEPT gn*");
        b.Run("CLEAR MEMORY\nRESTORE FROM f.mem");

        Assert.Equal("k", b.Interp.Memory.Get("gcKeep").AsString);      // gc* survived EXCEPT gn*
        Assert.False(b.Interp.Memory.IsDefined("gnDrop"));              // gn* excluded
    }

    // ─────────────────────────── §C.4 LIST / DISPLAY MEMORY TO FILE ───────────────────────────

    [Fact]
    public void ListMemory_ToFile_DumpsVisibleVars()
    {
        using var b = new Bench();
        b.Run("gcCity = 'Wien'\ngnCount = 7\nLIST MEMORY LIKE gc* TO FILE mem.txt");

        string dump = File.ReadAllText(b.FileIn("mem.txt"));
        Assert.Contains("GCCITY", dump, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wien", dump);
        // LIKE gc* filters out the numeric gnCount.
        Assert.DoesNotContain("GNCOUNT", dump, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── §C.14 DISPLAY STRUCTURE / TABLES TO FILE ───────────────────────────

    [Fact]
    public void DisplayStructure_ToFile_ListsFields()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nDISPLAY STRUCTURE TO FILE struc.txt");

        string txt = File.ReadAllText(b.FileIn("struc.txt"));
        Assert.Contains("ID", txt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GRP", txt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AMT", txt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOTE", txt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayTables_ToFile_ListsDbcMemberTables()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3misc_tables");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        string outFile = dir.File("tables.txt");
        interp.Execute($"DISPLAY TABLES TO FILE \"{outFile}\"");

        string txt = File.ReadAllText(outFile);
        Assert.Contains("customer", txt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", txt, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── §C.7 PACK DATABASE ───────────────────────────

    [Fact]
    public void PackDatabase_PreservesDeletedMemberRows_AndRejectsOpenMembers()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3misc_packdb");
        DbfDatabaseBuilder.Create(dir.File("sample.dbc"), new[]
        {
            new DbcTableSpec("member", "member.dbf", new[] { new DbfColumnDef("id", 'I') }),
        });
        using (var writer = DbfWriter.Open(dir.File("member.dbf")))
        {
            writer.AppendRecord(1);
            writer.AppendRecord(2);
            writer.Delete(0);
        }
        using var session = new VfpSession();
        session.OpenDatabase(dir.File("sample.dbc"));
        var interp = new VfpInterpreter(session);
        interp.Execute("SET DELETED OFF\nUSE member EXCLUSIVE");
        // The VFP9 oracle pins 1566 while a member is open, before any files are rewritten.
        var error = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute("PACK DATABASE"));
        Assert.Equal(1566, error.VfpErrorNumber);
        interp.Execute("USE\nPACK DATABASE\nUSE member EXCLUSIVE\nGO 1");
        Assert.Equal(2, interp.EvalExpression("RECCOUNT()").AsNumber);
        Assert.True(interp.EvalExpression("DELETED()").AsLogical);
    }

    // ─────────────────────────── §C.7 VALIDATE DATABASE ───────────────────────────

    [Fact]
    public void ValidateDatabase_IsAReadOnlyNoOp_OverAValidContainer()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3misc_validate");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        int before = (int)interp.EvalExpression("RECCOUNT('customer')").AsNumber;
        interp.Execute("VALIDATE DATABASE");   // read-only diagnostic — must not throw, must not mutate.
        int after = (int)interp.EvalExpression("RECCOUNT('customer')").AsNumber;
        Assert.Equal(before, after);
    }

    // ─────────────────────────── §C.7 COPY / APPEND PROCEDURES ───────────────────────────

    [Fact]
    public void CopyProcedures_WritesSpSourceToFile()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3misc_copyproc");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        string want = session.Database!.StoredProcedureSource ?? string.Empty;
        Assert.NotEqual(string.Empty, want);   // TasTrade ships a stored-procedure source

        string outFile = dir.File("procs.txt");
        interp.Execute($"COPY PROCEDURES TO \"{outFile}\"");
        Assert.Equal(want, File.ReadAllText(outFile));
    }

    [Fact]
    public void AppendProcedures_AddsToDbc_AndMakesProcCallable()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3misc_appendproc");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        string src = dir.File("newproc.prg");
        File.WriteAllText(src, "PROCEDURE mvfp_greet\r\nRETURN 4242\r\nENDPROC\r\n");
        interp.Execute($"APPEND PROCEDURES FROM \"{src}\"");

        // Callable after append.
        Assert.Equal(4242m, interp.EvalExpression("mvfp_greet()").AsNumber);
        // Persisted into the container's SP source.
        Assert.Contains("MVFP_GREET", (session.Database!.StoredProcedureSource ?? string.Empty).ToUpperInvariant());
    }

    // ─────────────────────────── §C.1 AFONT (GUI-bound — FLAGGED stub) ───────────────────────────

    [Fact]
    public void Afont_IsAHeadlessStub_ReturningZeroAndAnEmptyArray()
    {
        using var b = new Bench();
        // Headless: no GDI/font subsystem. AFONT returns 0 and leaves the array unpopulated (FLAG).
        b.Run("DIMENSION aF[1]\ngnFonts = AFONT(aF)");
        Assert.Equal(0, (int)b.Interp.EvalExpression("gnFonts").AsNumber);
    }
}
