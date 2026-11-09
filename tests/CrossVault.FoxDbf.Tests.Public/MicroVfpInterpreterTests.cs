using System;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P1b — UNIT tests for the RISKIEST semantics in isolation (task 2 + the "8 riskiest points"
/// in MICROVFP_SEMANTICS.md): by-ref vs by-value parameter passing, PRIVATE hide+restore, the
/// implicit-private dynamic scope through a call, PCOUNT()/missing-arg=.F., DO CASE first-true-only,
/// FOR bounds burned-in once, SCAN current-area + implicit-skip ending at EOF, the SELECT() FUNCTION
/// (0=current / 1=highest-unused) vs the SELECT 0 COMMAND (lowest-free), REPLACE default = current
/// record only, and SET REPROCESS TO 0 branching on whether an ON ERROR handler is installed.
///
/// These are written TESTS-FIRST: they pin the contract and are RED until the interpreter is built
/// (the scaffold's Execute/Call throw). SAFETY: data-touching cases run on TEMP COPIES only.
/// </summary>
public sealed class MicroVfpInterpreterTests
{
    // ───────────────────────── parameter passing: by-ref vs by-value ─────────────────────────

    [Fact]
    public void DoWith_BareVariable_PassesByReference_CalleeMutatesCaller()
    {
        // DO callee WITH x  — bare var ⇒ BY REFERENCE: the callee's assignment writes back to x.
        const string prg = @"
PROCEDURE br_caller
  PRIVATE x
  x = 1
  DO br_callee WITH x
  RETURN x
PROCEDURE br_callee
  PARAMETERS p
  p = 99
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(99m, interp.Call("br_caller").AsNumber);
    }

    [Fact]
    public void DoWith_ExtraParens_PassesByValue_CallerUnchanged()
    {
        // DO callee WITH (x)  — extra parens ⇒ BY VALUE: x must be unchanged.
        const string prg = @"
PROCEDURE bv_caller
  PRIVATE x
  x = 1
  DO bv_callee WITH (x)
  RETURN x
PROCEDURE bv_callee
  PARAMETERS p
  p = 99
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(1m, interp.Call("bv_caller").AsNumber);
    }

    // ───────────────────────── PRIVATE scoping ─────────────────────────

    [Fact]
    public void Private_HidesOuterVar_AndRestoresOnRoutineExit()
    {
        // pr_mid declares gv PRIVATE → its assignment must NOT leak; on return the outer gv is restored.
        const string prg = @"
PROCEDURE pr_outer
  PUBLIC gv
  gv = 'outer'
  DO pr_mid
  RETURN gv
PROCEDURE pr_mid
  PRIVATE gv
  gv = 'inner'
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal("outer", interp.Call("pr_outer").AsString);
        // cleanup of the PUBLIC happens with the store; not asserted here.
    }

    [Fact]
    public void ImplicitPrivate_IsVisibleToCallee()
    {
        // imp is undeclared-but-assigned in the outer routine ⇒ implicitly PRIVATE ⇒ the callee sees it.
        const string prg = @"
PROCEDURE ip_outer
  PRIVATE got
  imp = 'seen'
  DO ip_callee
  RETURN got
PROCEDURE ip_callee
  got = imp
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal("seen", interp.Call("ip_outer").AsString);
    }

    // ───────────────────────── PCOUNT() / PARAMETERS() / missing arg ─────────────────────────

    [Fact]
    public void Pcount_CountsActuallyPassedArguments()
    {
        const string prg = @"
PROCEDURE pc_count
  LPARAMETERS a, b, c
  RETURN PCOUNT()
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(2m, interp.Call("pc_count", VfpValue.Integer(10), VfpValue.Integer(20)).AsNumber);
    }

    [Fact]
    public void MissingArgument_BindsToFalse()
    {
        const string prg = @"
PROCEDURE pc_missing
  LPARAMETERS a, b, c
  RETURN c
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
        {
            VfpValue r = interp.Call("pc_missing", VfpValue.Integer(10), VfpValue.Integer(20));
            Assert.Equal(VfpType.Logical, r.Type);
            Assert.False(r.AsLogical);
        }
    }

    // ───────────────────────── DO CASE: only the first true case runs ─────────────────────────

    [Fact]
    public void DoCase_RunsOnlyFirstTrueCase_NoFallThrough()
    {
        // n=5: CASE n>0 is true AND CASE n>-100 is also true — only the FIRST must run.
        const string prg = @"
PROCEDURE docase_t
  LPARAMETERS n
  LOCAL r
  r = ''
  DO CASE
    CASE n > 0
      r = 'pos'
    CASE n > -100
      r = 'big'
    OTHERWISE
      r = 'other'
  ENDCASE
  RETURN r
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal("pos", interp.Call("docase_t", VfpValue.Integer(5)).AsString);
    }

    // ───────────────────────── FOR: bounds evaluated ONCE (burned in) ─────────────────────────

    [Fact]
    public void For_BoundsAreBurnedInOnce_MutatingLimitDoesNotExtendLoop()
    {
        // The limit n is read once at entry; bumping n inside the body must NOT lengthen the loop.
        const string prg = @"
PROCEDURE for_burn
  LOCAL n, cnt
  n = 3
  cnt = 0
  FOR i = 1 TO n
    cnt = cnt + 1
    n = 99
  ENDFOR
  RETURN cnt
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(3m, interp.Call("for_burn").AsNumber);
    }

    // ───────────────────────── SELECT() function vs SELECT 0 command ─────────────────────────

    [Fact]
    public void SelectFunction0_IsCurrentArea_While_Select0Command_IsLowestFreeArea()
    {
        // setup→area1, orders→area2, SELECT setup ⇒ SELECT(0)=current=1; then the SELECT 0 COMMAND
        // moves to the lowest FREE area = 3 ⇒ SELECT(0)=3. Encoded as cur*100 + low = 103.
        using var dir = new MicroVfpTestSupport.TempDir("selfn");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            interp.Execute(@"
USE tastrade!setup IN 0
USE tastrade!orders IN 0
SELECT setup
LOCAL cur, low
cur = SELECT(0)
SELECT 0
low = SELECT(0)");
            Assert.Equal(103m, interp.Memory.Get("cur").AsNumber * 100 + interp.Memory.Get("low").AsNumber);
        }
    }

    // ───────────────────────── SCAN: current area, implicit SKIP, ends at EOF ─────────────────────────

    [Fact]
    public void Scan_WalksCurrentArea_ImplicitSkip_EndsAtEof()
    {
        // SCAN visits every (non-deleted) record of the CURRENT area once → count == RECCOUNT(setup) (7 keys).
        using var dir = new MicroVfpTestSupport.TempDir("scan");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            const string prg = @"
PROCEDURE scan_count
  LOCAL cnt
  USE tastrade!setup IN 0
  SELECT setup
  cnt = 0
  SCAN
    cnt = cnt + 1
  ENDSCAN
  RETURN cnt
";
            interp.Load(PrgParser.Parse(prg));
            Assert.Equal(7m, interp.Call("scan_count").AsNumber);
        }
    }

    // ───────────────────────── REPLACE default scope = current record ONLY ─────────────────────────

    [Fact]
    public void Replace_DefaultScope_TouchesOnlyCurrentRecord()
    {
        // GO TOP then a bare REPLACE must change ONLY record 1 — never the whole table (risk #4).
        using var dir = new MicroVfpTestSupport.TempDir("repl");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            const string prg = @"
PROCEDURE repl_one
  USE tastrade!setup IN 0
  SELECT setup
  GO TOP
  REPLACE value WITH 'XX'
  LOCAL v1, v2
  v1 = value
  GO 2
  v2 = value
  RETURN v1 + '|' + ALLTRIM(v2)
";
            interp.Load(PrgParser.Parse(prg));
            string r = interp.Call("repl_one").AsString;
            // record 1 now 'XX' (setup.value is a C(6), read at its full width as in VFP); record 2 is whatever it was
            // — must NOT be 'XX'.
            Assert.StartsWith("XX    |", r);
            Assert.NotEqual("XX    |XX", r);
        }
    }

    // ───────────────────────── SET REPROCESS TO 0 branches on ON ERROR ─────────────────────────

    [Theory]
    [InlineData("REPLACE ALL value WITH 'XX'")]
    [InlineData("REPLACE REST value WITH 'XX'")]
    [InlineData("REPLACE NEXT 2 value WITH 'XX'")]
    [InlineData("REPLACE RECORD 2 value WITH 'XX'")]
    [InlineData("REPLACE value WITH 'XX' FOR .T.")]
    [InlineData("REPLACE value WITH 'XX' WHILE .T.")]
    [InlineData("BLANK ALL")]
    public void Replace_RecordScopeOrFilter_UpdatesTable(string command)
    {
        using var dir = new MicroVfpTestSupport.TempDir("repl_scope");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            interp.Execute("USE tastrade!setup IN 0\nSELECT setup");
            interp.Load(PrgParser.Parse(@"
PROCEDURE snapshot_setup_values
  LOCAL result
  result = ''
  SELECT setup
  GO TOP
  SCAN
    result = result + ALLTRIM(value) + '|'
  ENDSCAN
  RETURN result
"));
            string before = interp.Call("snapshot_setup_values").AsString;
            interp.Execute("SELECT setup\nGO TOP");

            if (command == "BLANK ALL")
            {
                Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute(command));
                Assert.Equal(before, interp.Call("snapshot_setup_values").AsString);
            }
            else
            {
                // All scopes and their final pointers are pinned by MicroVfpDataCommandsVfp9OracleTests.
                interp.Execute(command);
                Assert.NotEqual(before, interp.Call("snapshot_setup_values").AsString);
                Assert.Contains("XX|", interp.Call("snapshot_setup_values").AsString);
            }
        }
    }

    [Fact]
    public void SetReprocessTo0_WithOnErrorHandler_IsFailFast()
    {
        const string prg = @"
PROCEDURE rep_with
  ON ERROR lnDummy = 1
  SET REPROCESS TO 0
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
        {
            interp.Call("rep_with");
            Assert.True(interp.Runtime.LockFailFast);   // handler installed ⇒ locks fail fast (.F.)
        }
    }

    [Fact]
    public void SetReprocessTo0_WithoutOnErrorHandler_IsNotFailFast()
    {
        const string prg = @"
PROCEDURE rep_without
  ON ERROR
  SET REPROCESS TO 0
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
        {
            interp.Call("rep_without");
            Assert.False(interp.Runtime.LockFailFast); // no handler ⇒ would retry forever (not fail-fast)
        }
    }

    // ───────────────────────── assignment-shape vs command-verb collision ─────────────────────────
    // A local named after a command verb (total/sum/pack/count/…) must be an ASSIGNMENT, not a mis-parsed
    // command silently dropped as UnknownCommand. Regression for the review finding that `total = 0`
    // routed to BuildTotal (TOTAL ON…TO…), failed the grammar, and left the variable unchanged.

    [Theory]
    [InlineData("total")]
    [InlineData("sum")]
    [InlineData("pack")]
    [InlineData("count")]
    [InlineData("copy")]
    [InlineData("delete")]
    public void Assignment_ToCommandVerbNamedLocal_IsStored_NotDroppedAsCommand(string name)
    {
        // <name> = 5; <name> = <name> + 37  ⇒ 42, proving both the first store AND a re-store parse.
        string prg = $@"
PROCEDURE verb_local
  LOCAL {name}
  {name} = 5
  {name} = {name} + 37
  RETURN {name}
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(42m, interp.Call("verb_local").AsNumber);
    }

    [Fact]
    public void Assignment_ArrayElementAndMemberTargets_AreStored()
    {
        // Subscripted (arr(2)) and m.-qualified (m.total) lvalues also route to assignment, not a command.
        const string prg = @"
PROCEDURE lv_targets
  LOCAL total
  DIMENSION arr(2)
  arr(2) = 40
  m.total = 2
  RETURN arr(2) + m.total
";
        var interp = MicroVfpTestSupport.NewFromSource(prg, out var s);
        using (s)
            Assert.Equal(42m, interp.Call("lv_targets").AsNumber);
    }

    [Fact]
    public void CommandContainingTopLevelEquals_IsNotMisreadAsAssignment()
    {
        // A real command whose text contains a top-level '=' further along (a FOR/filter condition) must
        // still parse as the COMMAND — the assignment fast-path only intercepts a single-lvalue prefix, so
        // "DELETE FOR id = 1" (prefix "DELETE FOR id" has a space) and "SET FILTER TO amount = 10" stay
        // commands, not Assignments.
        Assert.IsType<DeleteStmt>(PrgParser.Parse("DELETE FOR id = 1").Main[0]);
        Assert.IsType<SetStmt>(PrgParser.Parse("SET FILTER TO amount = 10").Main[0]);
        // …while the bare store still becomes an Assignment even though STORE-less "delete"/"set" are verbs.
        Assert.IsType<Assignment>(PrgParser.Parse("delete = id = 1").Main[0]);
    }

    [Fact]
    public void UnknownCommandContainingTopLevelEquals_IsNotMisreadAsAssignment()
    {
        Assert.IsType<UnknownCommand>(PrgParser.Parse("BROWSE FOR total = 1").Main[0]);
        Assert.IsType<Assignment>(PrgParser.Parse("total = 1").Main[0]);
        Assert.IsType<DeleteStmt>(PrgParser.Parse("DELETE FOR total = 1").Main[0]);
    }
}
