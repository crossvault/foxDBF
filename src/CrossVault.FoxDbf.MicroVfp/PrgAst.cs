using System.Collections.Generic;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP — PRG (stored-procedure) Abstract Syntax Tree.
//
//  A parsed program is a list of PROCEDURE/FUNCTION definitions plus any top-level
//  ("main") statements that sit outside a procedure. Every expression fragment is a
//  PrgExpr (→ a real CrossVault.FoxDbf.Expressions.VfpExpression). A NAME-expression
//  `(expr)` (USE (lc) / SELECT (ln) / REPLACE (lcField) WITH …) is a NameExpr — it is
//  evaluated-as-a-name, NOT re-tokenised as code. A macro `&var[.]` is a MacroSubst —
//  captured verbatim (deferred; never expanded at parse time).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Root: the procedures/functions of a .prg, plus any top-level statements. PUBLIC as an
/// OPAQUE handle only (produced by <see cref="PrgParser.Parse"/>, consumed by the interpreter) — the
/// parse tree itself is an implementation detail and stays <c>internal</c> (finding 5.11: locking ~74
/// node record types into the public API would make every parser change a breaking change).</summary>
public sealed class PrgProgram
{
    internal PrgProgram(IReadOnlyList<ProcDef> procedures, IReadOnlyList<PrgStatement> main)
    {
        Procedures = procedures;
        Main = main;
    }

    internal IReadOnlyList<ProcDef> Procedures { get; }
    internal IReadOnlyList<PrgStatement> Main { get; }
}

/// <summary>Base for every statement node. <see cref="Line"/> is the 1-based source line.</summary>
internal abstract record PrgStatement
{
    public int Line { get; init; }

    /// <summary>The logical source line the statement was parsed from (an Exception object's LineContents).</summary>
    public string? SourceText { get; init; }
}

// ── helpers ──────────────────────────────────────────────────────────────────

/// <summary>A name-position expression <c>(expr)</c> — evaluated to a string, then used
/// as a name (alias/table/field/tag). NOT re-parsed as code.</summary>
internal sealed record NameExpr(PrgExpr Expression);

/// <summary>A macro substitution <c>&amp;var</c> / <c>&amp;var.</c> — captured verbatim and
/// deferred (the parser never expands or re-compiles it).</summary>
internal sealed record MacroSubst(string Text);

/// <summary>A name where the grammar allows either a literal identifier/number or a
/// <c>(expr)</c> name-expression (the area/alias/table/order/field positions).</summary>
internal sealed record NameRef
{
    public string? Name { get; }
    public NameExpr? Expr { get; }
    public bool IsNameExpr => Expr is not null;

    private NameRef(string? name, NameExpr? expr) { Name = name; Expr = expr; }

    public static NameRef OfName(string name) => new(name, null);
    public static NameRef OfExpr(NameExpr expr) => new(null, expr);

    public override string ToString() => IsNameExpr ? $"({Expr!.Expression.Text})" : Name!;
}

// ── procedures / declarations ────────────────────────────────────────────────

internal enum ProcKind { Procedure, Function }

internal sealed record ProcDef(
    string Name,
    ProcKind Kind,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<PrgStatement> Body) : PrgStatement
{
    public bool IsFunction => Kind == ProcKind.Function;
}

internal enum DeclScope { Local, Private, Public }

/// <summary>LOCAL / PRIVATE / PUBLIC declaration, incl. <c>ALL [LIKE|EXCEPT skel]</c>.
/// <see cref="Dimensions"/> is parallel to <see cref="Names"/>: the captured bracket text of an
/// array declaration (e.g. <c>"(1,12)"</c> for <c>PUBLIC gaErrors(1,12)</c>), or <c>null</c> for a
/// scalar. It distinguishes <c>PUBLIC x</c> (scalar) from <c>PUBLIC x(5)</c> (array).</summary>
internal sealed record VarDecl(
    DeclScope Scope,
    IReadOnlyList<string> Names,
    bool All,
    string? Like,
    string? Except,
    IReadOnlyList<string?> Dimensions) : PrgStatement;

internal sealed record ReleaseStmt(
    IReadOnlyList<string> Names,
    bool All,
    string? Like,
    string? Except) : PrgStatement;

/// <summary><c>DIMENSION</c> / <c>REDIMENSION</c> <c>name(r[,c]) [, …]</c> — create or resize memory
/// arrays. <see cref="Dimensions"/> is parallel to <see cref="Names"/> (the captured bracket text, e.g.
/// <c>"(1,12)"</c>); both DIMENSION and REDIMENSION preserve existing elements and <c>.F.</c>-fill growth.</summary>
internal sealed record DimensionStmt(
    IReadOnlyList<string> Names,
    IReadOnlyList<string?> Dimensions) : PrgStatement;

/// <summary>PARAMETERS (PRIVATE binding) or LPARAMETERS (LOCAL binding).</summary>
internal sealed record ParametersStmt(bool IsLocal, IReadOnlyList<string> Names) : PrgStatement;

// ── assignment ───────────────────────────────────────────────────────────────

/// <summary><c>target = expr</c>. <see cref="Target"/> is the raw lvalue text
/// (<c>var</c>, <c>alias.field</c>, <c>var(i)</c>, <c>m.var</c>).</summary>
internal sealed record Assignment(string Target, PrgExpr Value) : PrgStatement;

/// <summary><c>STORE expr TO v1, v2, …</c>.</summary>
internal sealed record StoreStmt(PrgExpr Value, IReadOnlyList<string> Targets) : PrgStatement;

// ── control flow ─────────────────────────────────────────────────────────────

internal sealed record IfStmt(
    PrgExpr Condition,
    IReadOnlyList<PrgStatement> Then,
    IReadOnlyList<PrgStatement> Else) : PrgStatement;

internal sealed record CaseClause(PrgExpr Condition, IReadOnlyList<PrgStatement> Body);

internal sealed record DoCaseStmt(
    IReadOnlyList<CaseClause> Cases,
    IReadOnlyList<PrgStatement>? Otherwise) : PrgStatement;

internal sealed record DoWhileStmt(PrgExpr Condition, IReadOnlyList<PrgStatement> Body) : PrgStatement;

internal sealed record ForStmt(
    string Variable,
    PrgExpr From,
    PrgExpr To,
    PrgExpr? Step,
    IReadOnlyList<PrgStatement> Body) : PrgStatement;

internal sealed record ScanStmt(
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    IReadOnlyList<PrgStatement> Body) : PrgStatement;

/// <summary><c>FOR EACH uVar IN aArray … ENDFOR|NEXT</c> — iterate every element of a memory ARRAY (in
/// LINEAR row-major order), binding <see cref="Variable"/> to the element VALUE on each pass (a value COPY,
/// not an alias — assigning <see cref="Variable"/> does NOT write back into the array, per hackfox s4g688).
/// Supports <c>EXIT</c>/<c>LOOP</c> like the other loops. microVFP FINAL P2 — §C.3.</summary>
internal sealed record ForEachStmt(
    string Variable,
    PrgExpr Collection,
    IReadOnlyList<PrgStatement> Body) : PrgStatement;

internal sealed record ReturnStmt(PrgExpr? Value) : PrgStatement;
internal sealed record ExitStmt : PrgStatement;
internal sealed record LoopStmt : PrgStatement;

// ── calls ────────────────────────────────────────────────────────────────────

/// <summary><c>DO Name [WITH args] [IN file]</c> (a statement; the PRG parser owns it —
/// unlike <c>=Name(args)</c>, which is an expression handled by the engine).</summary>
internal sealed record DoCall(string Name, IReadOnlyList<PrgExpr> Args, NameRef? In) : PrgStatement;

/// <summary>A bare expression-statement: <c>=Func(args)</c>.</summary>
internal sealed record ExprStatement(PrgExpr Expression) : PrgStatement;

// ── data / work-area ─────────────────────────────────────────────────────────

internal enum UseMode { Default, Shared, Exclusive }

/// <summary>USE — open/close a table in a work area. <see cref="Index"/> is the optional
/// <c>INDEX &lt;list&gt;</c> clause (additional <c>.idx</c>/<c>.cdx</c> files opened alongside the
/// structural <c>.cdx</c>); empty when absent.</summary>
internal sealed record UseStmt(
    string? Database,
    NameRef? Table,
    bool Again,
    NameRef? Alias,
    NameRef? Order,
    NameRef? In,
    UseMode Mode,
    bool NoUpdate,
    bool IsClose,
    IReadOnlyList<NameRef>? Index = null) : PrgStatement
{
    /// <summary>The <c>ORDER … ASCENDING|DESCENDING</c> override: null = the tag's own direction,
    /// true = DESCENDING, false = ASCENDING (same contract as <see cref="SetOrderStmt.Direction"/>).</summary>
    public bool? OrderDescending { get; init; }
}

/// <summary>The work-area form <c>SELECT n | alias | 0 | (expr)</c> (NOT a SQL query).</summary>
internal sealed record SelectAreaStmt(NameRef Area) : PrgStatement;

/// <summary>An embedded VFP-SQL <c>SELECT … FROM …</c>; routed to the existing SQL parser
/// (<see cref="Parsed"/>), with the raw text retained for round-tripping.</summary>
internal sealed record SqlSelectStmt(string Sql, SqlStatement? Parsed) : PrgStatement;

internal sealed record SeekStmt(PrgExpr Key, NameRef? Order, NameRef? In) : PrgStatement;
internal sealed record LocateStmt(string? Scope, PrgExpr? For, PrgExpr? While) : PrgStatement;
internal sealed record ContinueStmt : PrgStatement;

/// <summary><c>GO|GOTO n|TOP|BOTTOM [IN area]</c>. <see cref="Keyword"/> is TOP/BOTTOM, else
/// <see cref="Record"/> carries the record-number expression.</summary>
internal sealed record GoStmt(string? Keyword, PrgExpr? Record, NameRef? In) : PrgStatement;

internal sealed record SkipStmt(PrgExpr? Count, NameRef? In) : PrgStatement;
/// <summary><c>SET ORDER TO [n | cTag [OF cCdx]] [IN area] [ASCENDING|DESCENDING]</c>. A per-call
/// <see cref="Direction"/> override (VFP: the explicit clause wins for this order): <see langword="null"/> =
/// no clause (traverse in the tag's own stored direction), <see langword="true"/> = DESCENDING,
/// <see langword="false"/> = ASCENDING. microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
internal sealed record SetOrderStmt(NameRef? Order, NameRef? In, bool? Direction) : PrgStatement;

/// <summary><c>INDEX ON eKey [TAG cTag [OF cCdx] | TO cIdx] [FOR lExpr] [ASCENDING|DESCENDING]
/// [UNIQUE|CANDIDATE] [ADDITIVE] [COMPACT]</c> — build/replace an index. <see cref="Tag"/> targets a
/// CDX tag (structural when <see cref="OfCdx"/> is null); <see cref="ToIdx"/> targets a standalone
/// <c>.idx</c>, compact when <see cref="Compact"/> is set (VFP9's default <c>.idx</c> is uncompressed).
/// <see cref="Candidate"/> is CANDIDATE (a duplicate-key is an error), <see cref="Unique"/> is UNIQUE (keep first
/// per key).
/// microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
internal sealed record IndexStmt(
    PrgExpr Key,
    NameRef? Tag,
    NameRef? OfCdx,
    NameRef? ToIdx,
    PrgExpr? For,
    bool Descending,
    bool Unique,
    bool Candidate,
    bool Additive,
    bool Compact = false) : PrgStatement;

/// <summary><c>REINDEX [IN area]</c> — rebuild every open tag of a work area from live data.
/// microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
internal sealed record ReindexStmt(NameRef? In) : PrgStatement;

/// <summary><c>DELETE TAG &lt;name&gt;[, …] | ALL [OF &lt;cdx&gt;]</c> — remove tag(s) from a compound
/// <c>.cdx</c> (structural, or the named <see cref="OfCdx"/>). <see cref="All"/> removes every tag.
/// microVFP INDEX/ORDER MODEL.</summary>
internal sealed record DeleteTagStmt(
    IReadOnlyList<string> Tags,
    bool All,
    NameRef? OfCdx,
    NameRef? In) : PrgStatement;

/// <summary><c>SET INDEX TO [&lt;idx/cdx list&gt;] [ORDER &lt;tag|n&gt; [ASCENDING|DESCENDING]]
/// [ADDITIVE]</c> — open additional (non-structural) index files in the current work area. An empty
/// <see cref="Files"/> list is the clear form (<c>SET INDEX TO</c>). microVFP INDEX/ORDER MODEL.</summary>
internal sealed record SetIndexStmt(
    IReadOnlyList<NameRef> Files,
    NameRef? Order,
    bool? Direction,
    bool Additive) : PrgStatement;

/// <summary>Generic <c>SET &lt;setting&gt; …</c>. <see cref="Arguments"/> is the raw remainder
/// (<c>ON</c>/<c>OFF</c>/<c>TO …</c>). SET ORDER is modelled separately as
/// <see cref="SetOrderStmt"/>.</summary>
internal sealed record SetStmt(string Setting, string Arguments) : PrgStatement;

// ── relations (microVFP P1 gap #2 — SET RELATION / SET SKIP) ──────────────────

/// <summary>One <c>eExpr INTO area</c> pair of a <c>SET RELATION</c> statement: the relation KEY
/// expression (evaluated in the PARENT area on each parent move) and the child work-area / alias.</summary>
internal sealed record RelationTarget(PrgExpr Key, NameRef Into);

/// <summary><c>SET RELATION TO [eExpr INTO area [, …]] [ADDITIVE]</c> — link the CURRENT (parent) work
/// area to child area(s); on every parent record move an automatic SEEK repositions each child on its
/// active order (a miss leaves the child at EOF). An EMPTY <see cref="Targets"/> list is the clear form
/// (<c>SET RELATION TO</c> with no arguments). <see cref="Additive"/> ADDS to the existing relations
/// instead of replacing them. microVFP P1 gap #2 — the MULTI-TABLE RELATION model.</summary>
internal sealed record SetRelationStmt(IReadOnlyList<RelationTarget> Targets, bool Additive) : PrgStatement;

/// <summary><c>SET RELATION OFF [INTO area]</c> — remove ONE child relation of the current area
/// (<see cref="Into"/>), or ALL relations of the current area when <see cref="Into"/> is
/// <see langword="null"/>. microVFP P1 gap #2.</summary>
internal sealed record SetRelationOffStmt(NameRef? Into) : PrgStatement;

/// <summary><c>SET SKIP TO [alias [, …]]</c> — mark already-related child area(s) of the current area as
/// one-to-many; an EMPTY <see cref="Aliases"/> list clears all one-to-many marks. microVFP P1 gap #2.</summary>
internal sealed record SetSkipStmt(IReadOnlyList<NameRef> Aliases) : PrgStatement;

internal enum OnErrorKind { Clear, Command, Macro }

/// <summary><c>ON ERROR [command]</c>: clear (bare), a command (parsed sub-statement, with its raw
/// <see cref="CommandText"/> retained so <c>ON("ERROR")</c> reads back the source and a later
/// <c>ON ERROR &amp;var</c> can re-install it verbatim), or a macro body (<c>&amp;var.</c>).</summary>
internal sealed record OnErrorStmt(
    OnErrorKind Kind,
    PrgStatement? Command,
    MacroSubst? Macro,
    string? CommandText = null) : PrgStatement;

// ── write ────────────────────────────────────────────────────────────────────

internal sealed record ReplaceClause(NameRef Field, PrgExpr Value, bool Additive);

internal sealed record ReplaceStmt(
    IReadOnlyList<ReplaceClause> Clauses,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    NameRef? In) : PrgStatement;

internal sealed record DeleteStmt(string? Scope, PrgExpr? For, PrgExpr? While, NameRef? In) : PrgStatement;
internal sealed record RecallStmt(string? Scope, PrgExpr? For, PrgExpr? While) : PrgStatement;

/// <summary><c>INSERT INTO …</c>; routed to the existing SQL parser when possible. <see cref="Parsed"/> is
/// the parse captured at PRG-parse time (non-null for a well-formed INSERT); <see cref="ParsedCache"/> is a
/// MUTABLE lazy backfill for the rare case <see cref="Parsed"/> is null — the fast path parses ONCE and
/// caches here so it never re-parses per row (the AST node is reused across executions of the same source).</summary>
internal sealed record InsertStmt(string Sql, SqlStatement? Parsed) : PrgStatement
{
    /// <summary>Lazily-parsed fallback for <see cref="Parsed"/> when the PRG parser could not parse the SQL
    /// eagerly. Backfilled once by the interpreter (never per row). Mutable by design.</summary>
    internal SqlStatement? ParsedCache;
}

internal sealed record SumStmt(
    IReadOnlyList<PrgExpr> Expressions,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    IReadOnlyList<string> To) : PrgStatement;

internal sealed record UnlockStmt(bool All, NameRef? Record, NameRef? In) : PrgStatement;

/// <summary>The scope of a <c>CLEAR</c> command. <see cref="Memory"/> = <c>CLEAR MEMORY</c> (release all
/// memvars + arrays); <see cref="All"/> = <c>CLEAR ALL</c> (memvars + arrays AND close every work area);
/// <see cref="Ui"/> = the screen/window/menu/GETS/… forms — FLAGGED (no UI model), executed as a no-op.</summary>
internal enum ClearKind { Memory, All, Ui }

/// <summary><c>CLEAR [MEMORY|ALL|…]</c>. The bare <c>CLEAR</c> and the WINDOWS/MENUS/GETS/READ/TYPEAHEAD/…
/// forms carry no memory-model effect in a headless interpreter (<see cref="ClearKind.Ui"/>, no-op).
/// microVFP FINAL P2 — §C.4.</summary>
internal sealed record ClearStmt(ClearKind Kind) : PrgStatement;

// ── transactions ─────────────────────────────────────────────────────────────

internal sealed record BeginTxnStmt : PrgStatement;
internal sealed record EndTxnStmt : PrgStatement;
internal sealed record RollbackStmt : PrgStatement;

// ── P2 table/record movers + whole-table I/O ─────────────────────────────────

/// <summary>The source/destination class of a GATHER / SCATTER.</summary>
internal enum ScatterKind { Array, Memvar, Object }

/// <summary><c>GATHER FROM aArray | MEMVAR [FIELDS cList] [MEMO]</c> — REPLACE the current record's fields
/// from the array (positional) or the same-named memvars. <see cref="Name"/> is the array name (null for
/// MEMVAR); <see cref="Memo"/> includes memo fields.</summary>
internal sealed record GatherStmt(
    ScatterKind Kind,
    string? Name,
    IReadOnlyList<string> Fields,
    bool Memo,
    string? FieldPattern = null,
    bool Except = false) : PrgStatement;

/// <summary><c>SCATTER TO aArray | MEMVAR [BLANK] [FIELDS cList] [MEMO]</c> — read the current record's
/// fields into a fresh 1-D array (one element per field) or the same-named memvars.
/// <see cref="Blank"/> produces type-appropriate EMPTY() values instead of the record's data.</summary>
internal sealed record ScatterStmt(
    ScatterKind Kind,
    string? Name,
    IReadOnlyList<string> Fields,
    bool Memo,
    bool Blank,
    bool Additive = false,
    string? FieldPattern = null,
    bool Except = false) : PrgStatement;

/// <summary><c>APPEND FROM cFile [FIELDS cList] [FOR lExpr] [TYPE cType]</c> — append the matching-named
/// fields of another DBF's rows to the CURRENT work area. Non-DBF <see cref="Type"/> formats are FLAGGED.</summary>
internal sealed record AppendFromStmt(
    NameRef Source,
    IReadOnlyList<string> Fields,
    PrgExpr? For,
    string? Type) : PrgStatement;

internal sealed record AppendBlankStmt(NameRef? In) : PrgStatement;

/// <summary><c>COPY STRUCTURE [EXTENDED] TO cFile [FIELDS cList]</c> — create an EMPTY same-structure table
/// (<see cref="Extended"/> = false) or a one-row-per-field structure-descriptor table (EXTENDED).</summary>
internal sealed record CopyStructureStmt(
    NameRef Target,
    bool Extended,
    IReadOnlyList<string> Fields) : PrgStatement;

/// <summary><c>COPY TO cFile [FIELDS cList] [FOR lExpr] [TYPE cType]</c> and the <c>COPY MEMO mField TO cFile</c>
/// variant. For the record-copy form <see cref="Memo"/> is false; non-DBF <see cref="Type"/> is FLAGGED. For the
/// memo-export form <see cref="Memo"/> is true and <see cref="MemoField"/> names the memo field.</summary>
internal sealed record CopyToStmt(
    NameRef Target,
    IReadOnlyList<string> Fields,
    PrgExpr? For,
    string? Type,
    bool Memo,
    NameRef? MemoField) : PrgStatement
{
    /// <summary><c>COPY MEMO … TO cFile ADDITIVE</c> — append to the existing file instead of overwriting it.</summary>
    public bool Additive { get; init; }
}

/// <summary><c>CREATE cTable FROM cStructureExtendedFile</c> — build a fresh EMPTY table from a
/// structure-descriptor table (the inverse of <c>COPY STRUCTURE EXTENDED</c>).</summary>
internal sealed record CreateFromStmt(NameRef Table, NameRef From) : PrgStatement;

/// <summary><c>BLANK [FIELDS cList] [scope] [FOR lExpr] [WHILE lExpr] [IN area]</c> — reset the current
/// (or scoped) record's fields to their type-specific BLANK bytes (all-spaces for C/N/D/L, all-zero for
/// binary), the write-side counterpart of <c>ISBLANK()</c>. An empty <see cref="Fields"/> list blanks
/// every field. microVFP P3 §C.8.</summary>
internal sealed record BlankStmt(
    IReadOnlyList<string> Fields,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    NameRef? In) : PrgStatement;

/// <summary><c>APPEND MEMO mField FROM cFile [OVERWRITE] [AS nCodePage]</c> — copy the file's content into
/// the current record's memo field; additive by default, replacing when <see cref="Overwrite"/>. The bytes
/// are copied 1:1 (binary-safe). <see cref="AsCodePage"/> carries the raw <c>AS</c> argument when present
/// (codepage translation is unsupported → rejected by the executor). microVFP P3 §C.12.</summary>
internal sealed record AppendMemoStmt(NameRef Field, NameRef Source, bool Overwrite, string? AsCodePage = null) : PrgStatement;

/// <summary><c>COPY INDEXES cIdxList | ALL [TO cCdx]</c> — compile the named open standalone <c>.idx</c>
/// files as new tags (named after each source file's stem) in the structural (or named <see cref="ToCdx"/>)
/// compound index. <see cref="All"/> converts every open standalone index. microVFP P3 §C.9.</summary>
internal sealed record CopyIndexesStmt(IReadOnlyList<NameRef> Sources, bool All, NameRef? ToCdx) : PrgStatement;

/// <summary><c>COPY TAG cTag [OF cCdx] TO cIdx</c> — extract one compound-index tag as a standalone legacy
/// <c>.idx</c> file. microVFP P3 §C.9.</summary>
internal sealed record CopyTagStmt(NameRef Tag, NameRef? OfCdx, NameRef ToIdx) : PrgStatement;

/// <summary><c>REPLACE FROM ARRAY aArray [FIELDS cList] [scope] [FOR lExpr] [WHILE lExpr] [IN area]</c> —
/// update the current (or scoped) record's fields from an array, element i → field i in physical field
/// order (or the FIELDS list). The SCATTER inverse. microVFP P3 §C.12.</summary>
internal sealed record ReplaceFromArrayStmt(
    string ArrayName,
    IReadOnlyList<string> Fields,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    NameRef? In) : PrgStatement;

/// <summary><c>TOTAL ON eKey TO cFile [FIELDS nList] [FOR lExpr]</c> — one output row per group of
/// consecutive equal <see cref="Key"/> values, with the numeric <see cref="Fields"/> summed across the
/// group (all numeric fields when the list is empty). Requires the source ordered on the key.</summary>
internal sealed record TotalStmt(
    NameRef Target,
    PrgExpr Key,
    IReadOnlyList<string> Fields,
    PrgExpr? For) : PrgStatement;

/// <summary><c>PACK [MEMO | DBF]</c> — physical delete-compaction of the current table (wires to
/// <c>DbfWriter.Pack</c>). <see cref="Kind"/> is MEMO / DBF / null (both).</summary>
internal sealed record PackStmt(string? Kind) : PrgStatement;

/// <summary><c>RENAME TABLE cOld TO cNew</c> — rename the DBC member long-name (the <c>NAME</c>/OBJECTNAME
/// catalog entry); the physical <c>.dbf</c> is untouched.</summary>
internal sealed record RenameTableStmt(NameRef From, NameRef To) : PrgStatement;

/// <summary><c>FLUSH [FORCE]</c> — persist any pending writes. microVFP writes through on each REPLACE/
/// DELETE, so this is a recognised no-op that succeeds. <see cref="Force"/> mirrors the FORCE keyword.</summary>
internal sealed record FlushStmt(bool Force) : PrgStatement;

/// <summary>A CREATE TABLE / CREATE CURSOR / CREATE DATABASE command routed verbatim to the SQL/DDL
/// executor (<see cref="Sql"/>). CREATE TABLE additionally opens the new table in a work area (VFP).</summary>
internal sealed record SqlPassthroughStmt(string Sql) : PrgStatement;

// ── P3 batch 4 (FINAL): remaining array / variable / DB-lifecycle / table items ──────────────

/// <summary><c>COPY TO ARRAY aName [FIELDS cList] [scope] [FOR lExpr] [WHILE lExpr]</c> — the read
/// counterpart of APPEND/REPLACE FROM ARRAY. An UNDEFINED array auto-dimensions to (records × fields);
/// an EXISTING 2-D array is filled without redimensioning; an EXISTING 1-D array takes the FIRST scoped
/// record's fields. Memo/general/blob/picture cells hold a <c>.F.</c> placeholder. microVFP P3 §C.1.</summary>
internal sealed record CopyToArrayStmt(
    string ArrayName,
    IReadOnlyList<string> Fields,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While) : PrgStatement;

/// <summary><c>SAVE TO cFile [ALL LIKE skel | ALL EXCEPT skel]</c> — persist the visible memvars/arrays to
/// a file (microVFP's OWN round-trip format; the proprietary <c>.mem</c> binary is not interop — FLAG).
/// <see cref="Like"/>/<see cref="Except"/> are the wildcard skeletons. microVFP P3 §C.4.</summary>
internal sealed record SaveToStmt(NameRef Target, string? Like, string? Except) : PrgStatement;

/// <summary><c>RESTORE FROM cFile [ADDITIVE]</c> — reload memvars/arrays saved by <see cref="SaveToStmt"/>;
/// without <see cref="Additive"/> the memory is cleared first (implicit CLEAR MEMORY). microVFP P3 §C.4.</summary>
internal sealed record RestoreFromStmt(NameRef Source, bool Additive) : PrgStatement;

/// <summary><c>WAIT [cMsg] [WINDOW …] [TIMEOUT n] [TO mVar] [NOWAIT] [CLEAR]</c> — headless: NEVER blocks;
/// a <see cref="ToVar"/> receives "" (no keypress). microVFP P3 §C.4.</summary>
internal sealed record WaitStmt(string? ToVar) : PrgStatement;

/// <summary><c>LIST | DISPLAY MEMORY [LIKE skel] [TO FILE cFile]</c> — a headless text dump of the visible
/// memvars to <see cref="ToFile"/> (no console). <see cref="Like"/> filters. microVFP P3 §C.4.</summary>
internal sealed record MemoryDumpStmt(string? Like, NameRef? ToFile) : PrgStatement;

/// <summary><c>APPEND PROCEDURES FROM cFile</c> (<see cref="Append"/>=true) / <c>COPY PROCEDURES TO cFile</c>
/// — extract/append the current DBC's stored-procedure SOURCE text. microVFP P3 §C.7.</summary>
internal sealed record ProceduresStmt(bool Append, NameRef File) : PrgStatement;

/// <summary><c>PACK DATABASE</c> — physical delete-compaction of the current DBC container.
/// microVFP P3 §C.7.</summary>
internal sealed record PackDatabaseStmt : PrgStatement;

/// <summary><c>VALIDATE DATABASE [NOCONSOLE] [RECOVER]</c> — a read-only diagnostic over the current DBC
/// (member-path existence). <see cref="Recover"/> is FLAGGED (no automatic repair). microVFP P3 §C.7.</summary>
internal sealed record ValidateDatabaseStmt(bool Recover) : PrgStatement;

/// <summary><c>DISPLAY | LIST STRUCTURE [IN area] [TO FILE cFile]</c> — a text dump of a table's field
/// structure (name/type/width/dec). microVFP P3 §C.14.</summary>
internal sealed record DisplayStructureStmt(NameRef? In, NameRef? ToFile) : PrgStatement;

/// <summary><c>DISPLAY | LIST TABLES [TO FILE cFile]</c> — list the current DBC's member tables + paths.
/// microVFP P3 §C.14.</summary>
internal sealed record DisplayTablesStmt(NameRef? ToFile) : PrgStatement;

/// <summary><c>ZAP [IN nArea | cAlias]</c> — remove ALL records of the (current or named) table, structure
/// + indexes kept. Requires exclusive open; the DBC delete-trigger is intentionally NOT fired (VFP
/// bug-compatible). microVFP P3 §C.16.</summary>
internal sealed record ZapStmt(NameRef? In) : PrgStatement;

// ── misc / fallback ──────────────────────────────────────────────────────────

/// <summary>A standalone macro line <c>&amp;var.</c> (deferred; never expanded).</summary>
internal sealed record MacroSubstStmt(MacroSubst Macro) : PrgStatement;

/// <summary>A preprocessor directive line: <c>#INCLUDE</c> / <c>#DEFINE</c> / <c>#IF…</c>.</summary>
internal sealed record DirectiveStmt(string Text) : PrgStatement;

/// <summary>A command microVFP cannot execute — captured (verb + raw args) so the WHOLE corpus still
/// PARSES; executing it raises a catchable VFP error (16 for an unknown verb, 36 when <see cref="KnownVerb"/>
/// marks a verb microVFP models whose clause list it could not parse) unless the verb is on the documented
/// headless no-op allow-list (see <c>VfpInterpreter.HeadlessNoOp</c>).</summary>
internal sealed record UnknownCommand(string Verb, string Arguments) : PrgStatement
{
    /// <summary>True when the verb itself is one microVFP models but this clause form was not recognised.</summary>
    public bool KnownVerb { get; init; }
}

// ── structured error handling ────────────────────────────────────────────────

/// <summary>One <c>CATCH [TO VarName] [WHEN lExpression]</c> clause of a <see cref="TryStmt"/>.</summary>
internal sealed record CatchClause(string? ToVar, PrgExpr? When, IReadOnlyList<PrgStatement> Body);

/// <summary><c>TRY … [CATCH [TO v] [WHEN l] …]… [FINALLY …] ENDTRY</c> (VFP9 structured error handling).</summary>
internal sealed record TryStmt(
    IReadOnlyList<PrgStatement> Body,
    IReadOnlyList<CatchClause> Catches,
    IReadOnlyList<PrgStatement>? Finally) : PrgStatement;

/// <summary><c>THROW [eUserExpression]</c>. With no expression inside a CATCH it re-raises the caught error.</summary>
internal sealed record ThrowStmt(PrgExpr? Value) : PrgStatement;

/// <summary><c>COUNT [Scope] [FOR lExpr] [WHILE lExpr] TO VarName</c>.</summary>
internal sealed record CountStmt(string? Scope, PrgExpr? For, PrgExpr? While, string? Target) : PrgStatement;
