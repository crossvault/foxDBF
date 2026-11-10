# 3. SQL and the ADO.NET provider

**What you'll do:** run SQL against FoxPro/dBase tables two ways — directly through the VFP work-area
session (`CrossVault.FoxDbf.Sql`), or through the ADO.NET provider (`CrossVault.FoxDbf.Data`) from
Dapper/raw `DbCommand`/reporting tools. Most applications want the ADO.NET route.

## Direct: `VfpSession` + `SqlParser`

```csharp
using CrossVault.FoxDbf.Sql;

using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");  // a .dbc gives the long field names below; use
                                            // session.OpenDirectory(@"C:\data") for a folder of free
                                            // .dbf tables (whose columns keep VFP's 10-char names).

var result = session.Execute(
    "SELECT company_name, country FROM customer WHERE country = 'Germany' ORDER BY company_name");

foreach (var row in result!.Rows)
    Console.WriteLine(row[0]);
```

`VfpSession` models VFP's **work-area** concept: `USE table IN 0`, `SELECT alias`, and plain table
names in a query auto-open the matching `.dbf`/`.dbc` member the first time they're referenced —
the same mental model as a VFP9 program, minus the language runtime around it.

## Via ADO.NET: `CrossVault.FoxDbf.Data`

The more common path — plug FoxPro/dBase data into anything that speaks `System.Data.Common`:

```csharp
using CrossVault.FoxDbf.Data;

using var cn = new FoxDbfConnection(@"Data Source=C:\data\shop.dbc;Deleted=on");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT company_name, country FROM customer WHERE country = @c ORDER BY company_name";
var p = cmd.CreateParameter();
p.ParameterName = "@c";
p.Value = "Germany";
cmd.Parameters.Add(p);

using var reader = cmd.ExecuteReader();
while (reader.Read())
    Console.WriteLine(reader.GetString(reader.GetOrdinal("company_name")));
```

### With Dapper

```csharp
var customers = cn.Query<Customer>(
    "SELECT * FROM customer WHERE country = @c", new { c = "Germany" });

int updated = cn.Execute(
    "UPDATE customer SET region = @r WHERE customer_id = @id", new { r = "DE", id = 42 });
```

### Connection string keywords

| Keyword | Values | Meaning |
|---|---|---|
| `Data Source` | a `.dbc`, a directory of free tables, or a `.dbf` | what to open |
| `Collate` | `machine` (default) / `general` | collation for compares + `ORDER BY` |
| `Exclusive` | `true` / `false` | open mode |
| `Deleted` | `on` / `off` | exclude soft-deleted rows |
| `Ansi` | `on` / `off` | SQL `=` comparison (`SET ANSI`) |
| `ReadOnly` | `true` / `false` | |
| `Accelerator` | `Highlike` / `None` | opt-in cost-based query acceleration ([5. Highlike](05-highlike-accelerator.md)) |
| `EnforceRules` (alias `EnforceRI`) | `on` / `off` (default `off`) | opt in to the VFP write model on `INSERT`/`UPDATE`/`DELETE` — see [Enforcing DBC rules](#enforcing-dbc-rules-on-writes) |

### What SQL is supported

`SELECT` with projection, `WHERE`, `INNER`/`LEFT`/`RIGHT`/`FULL JOIN`, `UNION [ALL]`, correlated
subqueries, `GROUP BY`/`HAVING`, aggregates, `ORDER BY`, `DISTINCT`, `TOP [n [PERCENT]]`,
`INTO CURSOR`/`TABLE`/`ARRAY`; `INSERT … VALUES`, `INSERT … FROM ARRAY`/`FROM MEMVAR`,
`UPDATE`, `DELETE` (VFP soft-delete — see [1. Reading tables](01-reading-tables.md#deleted-records));
`CREATE`/`ALTER`/`DROP TABLE`. Positional `?` and named `@`/`:` parameters. SQL `=` follows
**`SET ANSI`** (the `Ansi` connection-string keyword above), not `SET EXACT`.

Conditions in `WHERE`, `JOIN ON`, `HAVING`, and `UPDATE`/`DELETE WHERE` support
`x [NOT] IN (list)`, `x [NOT] BETWEEN a AND b`, and `x [NOT] LIKE pattern [ESCAPE 'c']`,
including prefix `NOT` and parentheses. IN and BETWEEN follow `SET ANSI` and SQL NULL logic;
LIKE uses case-sensitive `%`/`_` wildcards and accepts excess trailing blanks in the value.
As in VFP9, these infix predicates are not SELECT-list expressions (error 36).

Character fields are read at their full declared width, exactly as in a VFP cursor: `SELECT name`
from a `C(10)` returns `'Smith     '`, and leading blanks are part of the value (`' Smith'` is not
`'Smith'`). Under `SET ANSI OFF` `name = 'Smith'` compares over the shorter operand, so it matches;
`name == 'Smith'` and `SET ANSI ON` compare blank-padded. Trim in the query (`ALLTRIM(name)`) or in
your code when you want the bare text; `DbfRecord`'s indexer returns it trimmed.

`SELECT … INTO ARRAY name` lands the result in the connection's active-session memvar array store
(a 2-D `rows × cols` array — even a single-column result is an `N×1` array, matching VFP9, *not* a
true 1-D array; zero matching rows leave the array unchanged), and sets `_TALLY` to the row count,
which `ExecuteNonQuery` returns. The array is then readable from a following microVFP expression
(`?ALEN(name)`) or stored procedure on the **same** connection. `INSERT … FROM ARRAY arr` appends one
row per array row (a 1-D array is a single row), mapping array columns to the table's fields by
position (excess ignored, missing left blank); `INSERT … FROM MEMVAR` appends one row mapped by field
name from the `m.<field>` memvars. Both flow through the normal DML path, so index maintenance, the
copy-on-write transaction, buffering and `EnforceRules` all apply.

### Stored procedures and UDFs

When the data source is a `.dbc`, the connection can run the container's stored procedures / UDFs —
the same VFP9 `.prg` business logic the [microVFP runtime](04-microvfp-runtime.md) executes, sharing
the connection's work-area session. Set `CommandType.StoredProcedure` and put the procedure name in
`CommandText`; parameters are passed positionally, by value, in the order you add them, and
`ExecuteScalar` returns the procedure's `RETURN` value:

```csharp
using var cmd = cn.CreateCommand();
cmd.CommandType = CommandType.StoredProcedure;
cmd.CommandText = "NewID";                         // a stored procedure in the .dbc
var p = cmd.CreateParameter();
p.ParameterName = "alias";
p.Value = "orders";
cmd.Parameters.Add(p);

object? nextId = cmd.ExecuteScalar();              // the SP's RETURN value; side effects persist
```

For a quick one-off, an ad-hoc microVFP expression — `CommandText` starting with `?` or `=` — is
evaluated directly (no stored procedure required):

```csharp
using var e = cn.CreateCommand();
e.CommandText = "?UPPER('abc')";                   // or "=1 + 2"
object? value = e.ExecuteScalar();                 // "ABC"
```

### Enforcing DBC rules on writes

By default the provider's `INSERT`/`UPDATE`/`DELETE` take the raw DML path — fast, and byte-for-byte
what you'd get writing the `.dbf` yourself. Opt in with `EnforceRules=on` (alias `EnforceRI`) **and a
`.dbc` open**, and each write instead runs the full VFP write model through microVFP, atomically, in
the same order VFP9 does: field `DEFAULT`s → field `RULE`s → `NOT NULL` / type / width → the record
`RULE` → unique / candidate / primary-key → the table's bound insert/update/delete
**referential-integrity trigger**. (`UPDATE` has no `DEFAULT` phase.)

```csharp
using var cn = new FoxDbfConnection(@"Data Source=C:\data\shop.dbc;EnforceRules=on");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "INSERT INTO orders (customer_id) VALUES ('ACME')";
cmd.ExecuteNonQuery();     // per-field DEFAULTs auto-fill; RULEs + the RI trigger run; a violation
                           // throws a FoxDbfException and rolls the row back — exactly like VFP9
```

Enforcement is a `.dbc` concept: a free-table / single-`.dbf` connection has no `DEFAULT`/`RULE`/
`TRIGGER` metadata, so its writes stay on the raw path even with `EnforceRules=on`. Default is **off**
— existing code is unaffected unless you ask for it.

### A `DbDataSource` for dependency injection

`FoxDbfDataSource` is the modern [`DbDataSource`](https://learn.microsoft.com/dotnet/api/system.data.common.dbdatasource)
factory (.NET 7+) — register one as a singleton from a single connection string and resolve
connections/commands from it, instead of threading the string through your app:

```csharp
using var source = new FoxDbfDataSource(@"Data Source=C:\data\shop.dbc;Deleted=on");

using DbConnection cn = source.OpenConnection();          // a fresh, already-open connection
using DbCommand cmd = cn.CreateCommand();
cmd.CommandText = "SELECT company_name FROM customer WHERE country = 'Germany'";
object? first = cmd.ExecuteScalar();
```

### Batching several commands

`FoxDbfBatch` implements [`DbBatch`](https://learn.microsoft.com/dotnet/api/system.data.common.dbbatch)
(.NET 6+). The engine is a local file engine, so there's no network round-trip to save — the batch is
an API-completeness convenience: `ExecuteReader` exposes the first command's rows and `NextResult()`
walks to each subsequent one, `ExecuteNonQuery` sums the affected counts, and the batch honors the
connection's active transaction and its `EnforceRules` setting.

```csharp
using var batch = cn.CreateBatch();
batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT company_name FROM customer WHERE country = 'Germany'"));
batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT company_name FROM customer WHERE country = 'USA'"));

using var r = batch.ExecuteReader();
while (r.Read()) { /* first command's rows */ }
if (r.NextResult())
    while (r.Read()) { /* second command's rows */ }
```

### Register as a named provider

```csharp
DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
```

Useful for tools that look up providers by name (config-driven data layers, some reporting tools)
rather than `new`-ing `FoxDbfConnection` directly. The registered `FoxDbfProviderFactory` also creates
data sources (`CreateDataSource`) and batches (`CreateBatch`).

### Transactions and isolation

`BeginTransaction()` returns a real, **copy-on-write** `FoxDbfTransaction`. DBF/VFP has no rollback log,
so isolation is provided by **private per-table working copies**: the first time the transaction *writes*
a table, that table's `.dbf` + its `.fpt`/`.cdx` sidecars are copied aside and this connection's reads and
writes to it are redirected to the copy for the rest of the transaction. `Commit()` verifies each touched
table's change-token still matches the live file (else it throws `FoxDbfTransactionConflictException`, so no
foreign write is lost) and atomically swaps each copy in; `Rollback()` (and dispose-without-commit) simply
discards the copies — the live files, including the `.cdx`, are left byte-for-byte untouched.

Within one transaction, DML followed by DDL on the same table is rejected with `FoxDbfException`;
DDL followed by DML remains supported.

```csharp
using var cn = new FoxDbfConnection(@"Data Source=C:\data");
cn.Open();
using var tx = cn.BeginTransaction();          // IsolationLevel.ReadCommitted
using (var c = cn.CreateCommand()) { c.CommandText = "INSERT INTO emp (id,name) VALUES (6,'Frank')"; c.ExecuteNonQuery(); }
tx.Rollback();                                  // the row — and its index entry — never touched the live file
```

**Isolation level.** The reported `IsolationLevel` is **always** `ReadCommitted` — the level this model
actually delivers. Whatever you pass to `BeginTransaction(IsolationLevel)` is **clamped** to it: a request for
a *stronger* level (`RepeatableRead` / `Snapshot` / `Serializable`) is not honoured and is **not** echoed back
(the property never claims a level the transaction cannot provide), and a *weaker* request
(`ReadUncommitted` / `Chaos`) is likewise served as `ReadCommitted` (uncommitted writes stay isolated
regardless). This is deliberate and honest — the transaction reports exactly what it does.

**Isolation boundary — read this.** The transaction is honest
[`ReadCommitted`](https://learn.microsoft.com/dotnet/api/system.data.isolationlevel) (its reported
`IsolationLevel`). Concretely:

- **Read-your-own-writes** — the transacting connection sees its own pending changes.
- **Isolation of uncommitted writes** — another connection never sees them until `Commit()`.
- **Stable reads for a *written* table** — once the transaction has written a table (a private copy exists),
  its reads of that table come from the copy and are unaffected by a foreign writer touching the live file.
- **Non-repeatable reads for a table the transaction has only *read*** — because such a table has **no**
  private copy, its reads go straight to the live file, so a *foreign* process that commits a change to it
  mid-transaction **becomes visible** to a later read in the same transaction. This is exactly what
  `ReadCommitted` permits, and it is a **deliberate** boundary: upgrading to repeatable-read/snapshot
  isolation would require snapshotting *every* table on first read (a large, always-on cost even for
  read-only tables) and does not fit the optimistic, write-tracked concurrency model. If you need a stable
  view of a table across a transaction, read it inside an explicit write, or serialize the foreign writer.

## Next

[4. The microVFP runtime →](04-microvfp-runtime.md)
