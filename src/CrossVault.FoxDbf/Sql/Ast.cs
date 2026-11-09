using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Sql;

// ---------------------------------------------------------------------------
//  VFP-SQL Abstract Syntax Tree (v1 grammar).
//
//  All nodes are public + immutable. Every scalar / predicate fragment (a
//  select-item expression, WHERE, JOIN ON, HAVING, GROUP BY item, ORDER BY
//  item, UPDATE SET value, INSERT VALUES item) is a fully-parsed
//  CrossVault.FoxDbf.Expressions.VfpExpression — the SQL parser only owns the
//  SQL skeleton and locates each fragment's text.
// ---------------------------------------------------------------------------

/// <summary>Base type for every parsed VFP-SQL / work-area command.</summary>
public abstract record SqlStatement;

// ===========================================================================
//  SELECT
// ===========================================================================

/// <summary>How a JOIN combines its two inputs.</summary>
public enum JoinType { Inner, Left, Right, Full }

/// <summary>Destination kind for an INTO clause.</summary>
public enum IntoKind { Cursor, Table, Array }

/// <summary>
/// One entry in a SELECT column list. Exactly one shape applies:
/// <list type="bullet">
///   <item><c>*</c> — <see cref="IsStar"/> true, <see cref="StarAlias"/> null.</item>
///   <item><c>alias.*</c> — <see cref="IsStar"/> true, <see cref="StarAlias"/> = alias.</item>
///   <item><c>COUNT(*)</c> style — <see cref="AggregateStarFunction"/> = "COUNT"
///         (the expression engine cannot represent a bare <c>*</c> argument, so this is
///         captured at the SQL level rather than as a <see cref="VfpExpression"/>).</item>
///   <item>any other scalar — <see cref="Expression"/> set, optional <see cref="Alias"/>.</item>
/// </list>
/// </summary>
public sealed record SelectItem(
    bool IsStar,
    string? StarAlias,
    VfpExpression? Expression,
    string? Alias,
    string? AggregateStarFunction = null);

/// <summary>A table reference in a FROM list or JOIN: <c>[db'!']table [[AS] alias]</c>.</summary>
public sealed record FromSource(
    string? Database,
    string Table,
    string? Alias);

/// <summary>A single JOIN: <c>(INNER|LEFT|RIGHT|FULL) [OUTER] JOIN source ON expr</c>.</summary>
public sealed record JoinClause(
    JoinType JoinType,
    FromSource Source,
    VfpExpression On);

/// <summary>
/// One ORDER BY entry: either an <see cref="Expression"/> or a 1-based column
/// <see cref="Ordinal"/>, optionally <see cref="Descending"/>.
/// </summary>
public sealed record OrderItem(
    VfpExpression? Expression,
    int? Ordinal,
    bool Descending);

/// <summary>INTO destination: <c>CURSOR name [READWRITE][NOFILTER] | TABLE|DBF name | ARRAY name</c>.</summary>
public sealed record IntoClause(
    IntoKind Kind,
    string Name,
    bool ReadWrite,
    bool NoFilter);

/// <summary>A trailing <c>UNION [ALL] SELECT ...</c> attached to a query.</summary>
public sealed record UnionClause(
    bool All,
    SelectStatement Query);

/// <summary>A SQL SELECT query (the data-returning form, not the work-area switch).</summary>
/// <remarks>
/// A WHERE clause is carried in EXACTLY ONE of two shapes. When the clause contains NO sub-SELECT
/// it stays a single <see cref="Where"/> <see cref="VfpExpression"/> (the historical fast path —
/// the executor drives <see cref="CrossVault.FoxDbf.Query.QueryOptimizer"/> with its <c>.Text</c>).
/// When a sub-SELECT appears anywhere in the clause the SQL parser owns the boolean combination and
/// emits a <see cref="WherePredicate"/> tree whose leaves are plain expressions or subquery atoms;
/// in that case <see cref="Where"/> is <see langword="null"/>. HAVING never carries a subquery (VFP
/// rejects it), so it stays a plain <see cref="Having"/> expression.
/// </remarks>
public sealed record SelectStatement(
    bool Distinct,
    int? Top,
    bool TopPercent,
    IReadOnlyList<SelectItem> Items,
    IReadOnlyList<FromSource> From,
    IReadOnlyList<JoinClause> Joins,
    VfpExpression? Where,
    IReadOnlyList<VfpExpression> GroupBy,
    VfpExpression? Having,
    IReadOnlyList<OrderItem> OrderBy,
    IntoClause? Into,
    UnionClause? Union,
    SqlPredicate? WherePredicate = null) : SqlStatement;

// ===========================================================================
//  WHERE predicate tree (only built when a sub-SELECT appears in the clause)
// ===========================================================================

/// <summary>
/// A node in a SQL-level WHERE boolean tree. Built ONLY when the WHERE clause contains a sub-SELECT;
/// a plain (no-subquery) WHERE stays a single <see cref="VfpExpression"/> on
/// <see cref="SelectStatement.Where"/>. Leaves are either a plain predicate
/// (<see cref="ExprPredicate"/>, handed to the expression engine unchanged) or a subquery atom
/// (<see cref="InSubqueryPredicate"/> / <see cref="ExistsSubqueryPredicate"/> /
/// <see cref="ScalarSubqueryPredicate"/>); interior nodes are <see cref="AndPredicate"/> /
/// <see cref="OrPredicate"/> / <see cref="NotPredicate"/>.
/// </summary>
public abstract record SqlPredicate;

/// <summary>Logical conjunction (<c>left AND right</c>), three-valued.</summary>
public sealed record AndPredicate(SqlPredicate Left, SqlPredicate Right) : SqlPredicate;

/// <summary>Logical disjunction (<c>left OR right</c>), three-valued.</summary>
public sealed record OrPredicate(SqlPredicate Left, SqlPredicate Right) : SqlPredicate;

/// <summary>Logical negation (<c>NOT operand</c>), three-valued.</summary>
public sealed record NotPredicate(SqlPredicate Operand) : SqlPredicate;

/// <summary>A plain predicate leaf: a fully-parsed <see cref="VfpExpression"/> evaluated by the
/// expression engine (the unchanged path for any fragment without a sub-SELECT).</summary>
public sealed record ExprPredicate(VfpExpression Expression) : SqlPredicate;

/// <summary><c>expr [NOT] IN (SELECT …)</c>: membership of <see cref="Left"/>'s value in the single
/// column the <see cref="Subquery"/> returns.</summary>
public sealed record InSubqueryPredicate(
    VfpExpression Left, bool Negated, SelectStatement Subquery) : SqlPredicate;

/// <summary><c>EXISTS (SELECT …)</c>: true when the <see cref="Subquery"/> returns at least one row.
/// (<c>NOT EXISTS</c> is the same node wrapped in a <see cref="NotPredicate"/>.)</summary>
public sealed record ExistsSubqueryPredicate(SelectStatement Subquery) : SqlPredicate;

/// <summary><c>expr &lt;op&gt; (SELECT …)</c>: compares <see cref="Left"/> against the single value
/// the <see cref="Subquery"/> returns (<see cref="Op"/> is one of <c>= == &lt;&gt; != # &lt; &lt;= &gt; &gt;=</c>).
/// A subquery returning more than one row is a runtime error; returning no row yields NULL.</summary>
public sealed record ScalarSubqueryPredicate(
    VfpExpression Left, string Op, SelectStatement Subquery) : SqlPredicate;

// ===========================================================================
//  INSERT / UPDATE / DELETE
// ===========================================================================

/// <summary>The source of the rows an <c>INSERT</c> appends.</summary>
public enum InsertSourceKind
{
    /// <summary><c>VALUES (expr,...)</c> — a single explicit row.</summary>
    Values,
    /// <summary><c>FROM ARRAY name</c> — one row per array row (2-D) or a single row (1-D), mapped to the
    /// table's fields by POSITION (excess elements ignored, missing fields left blank).</summary>
    Array,
    /// <summary><c>FROM MEMVAR</c> — a single row whose fields map by NAME from the same-named
    /// <c>m.&lt;field&gt;</c> memory variables (a field with no matching memvar is left blank).</summary>
    Memvar,
    /// <summary><c>SELECT ...</c> — materialized query rows mapped positionally.</summary>
    Select,
}

/// <summary>
/// <c>INSERT INTO [db'!']table [(col,...)] VALUES (expr,...)</c> — or, when <see cref="SourceKind"/> is
/// <see cref="InsertSourceKind.Array"/> / <see cref="InsertSourceKind.Memvar"/>, the memory-source forms
/// <c>INSERT INTO table FROM ARRAY name</c> / <c>INSERT INTO table FROM MEMVAR</c>. For the memory-source
/// forms <see cref="Values"/> is empty and <see cref="Columns"/> is <see langword="null"/>;
/// <see cref="SourceName"/> is the array name for the ARRAY form (and <see langword="null"/> for MEMVAR).
/// With <see cref="InsertSourceKind.Select"/>, <see cref="Query"/> supplies the rows and VALUES is empty.
/// </summary>
public sealed record InsertStatement(
    string? Database,
    string Table,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<VfpExpression> Values,
    InsertSourceKind SourceKind = InsertSourceKind.Values,
    string? SourceName = null,
    SelectStatement? Query = null) : SqlStatement;

/// <summary>One <c>col = expr</c> assignment in an UPDATE.</summary>
public sealed record SetClause(
    string Column,
    VfpExpression Value);

/// <summary><c>UPDATE [db'!']table SET col = expr [, ...] [WHERE expr]</c>.</summary>
public sealed record UpdateStatement(
    string? Database,
    string Table,
    IReadOnlyList<SetClause> Assignments,
    VfpExpression? Where) : SqlStatement;

/// <summary><c>DELETE FROM [db'!']table [WHERE expr]</c>.</summary>
public sealed record DeleteStatement(
    string? Database,
    string Table,
    VfpExpression? Where) : SqlStatement;

// ===========================================================================
//  DDL — CREATE TABLE / ALTER TABLE / DROP TABLE
// ===========================================================================

/// <summary>
/// One column declaration in a DDL <c>CREATE TABLE</c> / <c>ALTER TABLE</c> statement:
/// the column <see cref="Name"/>, the VFP single-character <see cref="Type"/> code
/// (<c>C N F I B Y D T L M V W G Q P</c>), the optional declared <see cref="Length"/> and
/// <see cref="Decimals"/> (null when the type's width is fixed / unspecified in the syntax),
/// and the optional <see cref="Nullable"/> flag (<see langword="null"/> = unspecified, so the
/// executor applies the VFP default; <see langword="true"/> = <c>NULL</c>; <see langword="false"/> = <c>NOT NULL</c>).
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    char Type,
    int? Length,
    int? Decimals,
    bool? Nullable)
{
    /// <summary>Suppress code-page translation for character and memo storage.</summary>
    public bool NoCpTrans { get; init; }
    /// <summary>Integer autoincrement seed and step.</summary>
    public Write.DbfAutoIncrement? AutoIncrement { get; init; }
    /// <summary>Stored field default expression.</summary>
    public string? Default { get; init; }
    /// <summary>Stored field validation expression.</summary>
    public string? Check { get; init; }
    /// <summary>Stored validation error expression.</summary>
    public string? Error { get; init; }
}

/// <summary>A CREATE-time index or persistent relationship.</summary>
public sealed record CreateTableKey(string Kind, string Expression, string Tag,
    string? ReferencedTable = null, string? ReferencedTag = null);

/// <summary><c>CREATE TABLE | DBF name [NAME longname | FREE] [CODEPAGE = expr] (...)</c>.</summary>
public sealed record CreateTableStatement(
    string? Database,
    string Table,
    IReadOnlyList<ColumnDefinition> Columns) : SqlStatement
{
    /// <summary>Create a free table even when a database is current.</summary>
    public bool Free { get; init; }
    /// <summary>The requested logical name in the current database.</summary>
    public string? LongName { get; init; }
    /// <summary>Numeric VFP code page, not the DBF language-driver byte.</summary>
    public VfpExpression? CodePage { get; init; }
    /// <summary>AFIELDS-style array supplying the structure.</summary>
    public string? FromArray { get; init; }
    /// <summary>Keys in declaration order.</summary>
    public IReadOnlyList<CreateTableKey> Keys { get; init; } = Array.Empty<CreateTableKey>();
    /// <summary>Table validation expression.</summary>
    public string? Check { get; init; }
    /// <summary>Table validation error expression.</summary>
    public string? Error { get; init; }
    /// <summary>A constraint requiring a database-contained table in VFP9.</summary>
    public bool RequiresDatabase { get; init; }
    /// <summary>Table trigger expressions supplied by AFIELDS-style arrays.</summary>
    public string? InsertTrigger { get; init; }
    /// <summary>Table update trigger supplied by an AFIELDS-style array.</summary>
    public string? UpdateTrigger { get; init; }
    /// <summary>Table delete trigger supplied by an AFIELDS-style array.</summary>
    public string? DeleteTrigger { get; init; }
    /// <summary>Table comment supplied by an AFIELDS-style array.</summary>
    public string? Comment { get; init; }
}

/// <summary>The kind of structural change one <c>ALTER TABLE</c> performs.</summary>
public enum AlterTableActionKind { AddColumn, AlterColumn, DropColumn, RenameColumn }

/// <summary>
/// A single <c>ALTER TABLE</c> action. Exactly one shape applies per <see cref="Kind"/>:
/// <list type="bullet">
///   <item><see cref="AlterTableActionKind.AddColumn"/> / <see cref="AlterTableActionKind.AlterColumn"/> — <see cref="Column"/> set.</item>
///   <item><see cref="AlterTableActionKind.DropColumn"/> — <see cref="DropName"/> set.</item>
///   <item><see cref="AlterTableActionKind.RenameColumn"/> — <see cref="RenameFrom"/> + <see cref="RenameTo"/> set.</item>
/// </list>
/// </summary>
public sealed record AlterTableAction(
    AlterTableActionKind Kind,
    ColumnDefinition? Column = null,
    string? DropName = null,
    string? RenameFrom = null,
    string? RenameTo = null);

/// <summary><c>ALTER TABLE [db'!']name ADD|ALTER [COLUMN] ... | DROP [COLUMN] col | RENAME COLUMN old TO new</c>.</summary>
public sealed record AlterTableStatement(
    string? Database,
    string Table,
    AlterTableAction Action) : SqlStatement;

/// <summary><c>DROP TABLE [IF EXISTS] [db'!']name</c>.</summary>
public sealed record DropTableStatement(
    string? Database,
    string Table,
    bool IfExists) : SqlStatement;

// ===========================================================================
//  Work-area commands
// ===========================================================================

/// <summary>Open mode requested by a USE command.</summary>
public enum UseMode { Default, Exclusive, Shared }

/// <summary>
/// <c>USE [[db'!']table | ?] [IN (n|cAlias)] [AGAIN] [ALIAS name] [(EXCLUSIVE|SHARED)] [NOUPDATE]</c>.
/// A bare <c>USE</c> (close current work area) has <see cref="Table"/> null and
/// <see cref="Prompt"/> false.
/// </summary>
public sealed record UseCommand(
    string? Database,
    string? Table,
    bool Prompt,
    int? InArea,
    string? InAlias,
    bool Again,
    string? Alias,
    UseMode Mode,
    bool NoUpdate) : SqlStatement;

/// <summary>
/// The work-area switch form of SELECT: <c>SELECT &lt;nArea&gt;</c> or <c>SELECT &lt;cAlias&gt;</c>.
/// Exactly one of <see cref="Area"/> / <see cref="Alias"/> is set.
/// </summary>
public sealed record SelectAreaCommand(
    int? Area,
    string? Alias) : SqlStatement;
