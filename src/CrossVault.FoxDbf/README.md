# CrossVault.FoxDbf

An idiomatic .NET library for **reading, writing, and querying (SQL)** Visual FoxPro and dBase
database files — `.dbf` tables, `.fpt`/`.dbt` memos, `.cdx`/`.idx` indexes, and `.dbc` database
containers. The primary target is Visual FoxPro, with dBase III/IV/V supported alongside.
Written output is **byte-compatible with the real Visual FoxPro 9 runtime** (verified against
it: created tables, indexes, and databases open in VFP9; SQL results match a brute-force oracle
and the real VFP9 runtime).

Need SQL from Dapper/ADO.NET/reporting tools instead of the raw API below? Use
**[CrossVault.FoxDbf.Data](https://www.nuget.org/packages/CrossVault.FoxDbf.Data)** (the ADO.NET
provider built on this package). Need to run VFP9 stored-procedure `.prg` business logic? Use
**[CrossVault.FoxDbf.MicroVfp](https://www.nuget.org/packages/CrossVault.FoxDbf.MicroVfp)**.

## Features

### Reading
- Header / version / column parsing for all xBase layouts (incl. FoxBase v0x02).
- Streaming record iteration and random access; deleted-record handling.
- Field decoding for `C N F I Y B D T L + G` (Currency/Numeric-with-decimals as `decimal`).
- Visual FoxPro **NULL** semantics via the hidden `_NullFlags` column, plus Varchar/Varbinary.
- Full code-page map with encoding selection (UTF-8 short-circuit, NOCPTRANS binary bypass).
- Memo files: FoxPro `.fpt` (big-endian) and dBase III/IV `.dbt` (little-endian).
- Open from streams: `DbfTable.Open(dbfStream, memoStream, options, leaveOpen)` reads the same
  values as a path open, memos included. Each file spans from its stream's current position to
  the end of the stream (seekable streams only; bound the stream when the table is followed by
  other data).
- `.dbc` containers: resolves member tables via the `PROPERTY`/`.DCT` path and applies
  **long field names**.
- Header **recovery** for corrupted/zeroed headers, plus a force-version open.
- **Index reading** (`.cdx` compound / `.idx` single): tag enumeration in key order,
  typed key decode (numeric/date/datetime/integer/character), and `Seek`.
- Export helpers: CSV (RFC-4180, BOM) and JSON / Markdown / SQL-DDL schema.

### Writing
- Append / update / delete / recall records; field + record encoders (the exact inverse of
  the decoders), header maintenance, autoincrement, and `.fpt` memo append.
- `PACK` (physical delete + memo rebuild) and `ZAP`; **VFP-compatible byte-range locking**.
- **Create** a `.dbf` from scratch (≥ VFP6, version selected by feature) and `ALTER`
  (add/drop/modify columns) via an atomic whole-file rewrite.
- **Create** a `.dbc` database container (`.dbc` + `.DCT` + `.DCX` + member backlinks).
- **CDX writing / REINDEX**: bulk-build a compact B-tree index for a tag (key + FOR
  expressions, MACHINE/GENERAL collation).
- **Incremental index maintenance**: once a structural `.cdx` tag exists, every append/update
  edits it in place (`O(log n)`), so a `REPLACE` followed by a `SEEK` finds the row without a
  `REINDEX` — exactly as Visual FoxPro does. (`Delete`/`Recall` leave the key in the index, matching
  VFP soft-delete semantics — it clears at `PACK`.)
- **Page-level tag DDL**: adding a further tag (`CreateTag`/`CreateTagIn`) appends it as its own
  `.cdx` pages with a single tag-directory splice — the sibling tags are neither re-read nor
  rewritten; `DeleteTagsIn` (`DELETE TAG`) unlinks a single tag, deleting the file only when its
  last tag is removed.

### Expressions & query engine
- A VFP **expression engine** (`CrossVault.FoxDbf.Expressions`): hand-written lexer + Pratt
  parser compiled via `System.Linq.Expressions`, a broad function library, and byte-exact
  **MACHINE / GENERAL** collation.
- A **Rushmore-style** query optimizer: index-driven record bitmaps with a residual scan, honoring
  `SET EXACT` / `SET DELETED` / `SET OPTIMIZE`; index-optimizes Character/Date/DateTime as well as
  numeric keys; **late materialization** (reads only the surviving records); an adaptive sparse/dense
  bitmap; and a **SYS(3054)-style ShowPlan** (`DbfTable.ExplainQuery`). The result is always identical
  to a full scan.
- An optional **memory-mapped read backend** (`DbfOptions.ReadBackend`) for local files (falls back
  to `FileStream` on network shares).

### SQL (`CrossVault.FoxDbf.Sql` namespace, bundled in this package)
A Visual FoxPro SQL parser + AST, a VFP **work-area data session**, and an executor over the
Rushmore optimizer and writer above:
- **`SqlParser.Parse(sql)`** → an AST. Every scalar/predicate fragment is parsed by the shared
  `VfpExpression` engine — one expression grammar, used by indexes, filters, and SQL.
- **`VfpSession`** — the per-connection VFP work-area model: `USE` / `SELECT` work areas, open a
  `.dbc` (long field names) or a directory of free `.dbf` or a single `.dbf`, `alias.field`
  resolution, auto-open of tables named in a query.
- **Executor** — `SELECT` (projection, `WHERE` pushed to Rushmore, `GROUP BY`/`HAVING`, aggregates,
  `ORDER BY`, `DISTINCT`, `TOP`, `INNER`/`LEFT`/`RIGHT`/`FULL JOIN`, `UNION [ALL]`, correlated
  subqueries, `INTO CURSOR`/`TABLE`/`ARRAY`) and DML (`INSERT … VALUES`, `INSERT … FROM ARRAY`/
  `FROM MEMVAR`, `UPDATE … SET`, `DELETE` = VFP soft-delete) plus DDL (`CREATE`/`ALTER`/`DROP TABLE`).
  Returns a `SqlResult` (column schema + streamed rows, or affected count). SQL `=` follows
  **`SET ANSI`** (not `SET EXACT`); `UPDATE`/`DELETE` without a `WHERE` affect all rows.

Most users want **CrossVault.FoxDbf.Data** (ADO.NET) rather than `SqlParser`/`VfpSession` directly.

### Highlike accelerator (`CrossVault.FoxDbf.Highlike` namespace, bundled in this package)
The **opt-in high-performance accelerator**. It speeds up Rushmore-style queries **without ever
changing the result** — the answer is always identical to a full table scan. Statistics and caches
are **hints only**: stale, missing, or even wrong stats can only change the *plan* (speed), never
the result set (the residual always confirms every candidate).

> Named after the **Hochgern** (Hoch→High, gern→like). Referencing it never changes behaviour
> unless you opt in via `UseHighlike()`.

- **`.stx` statistics sidecar** — per-tag NDV, min/max, null/deleted counts, **equi-depth histograms**
  and **most-common-values**, harvested in one sorted index walk. `Analyze(table)` (an explicit
  UPDATE STATISTICS) or lazy on first use. Staleness via reccount + last-update stamp.
- **Cost-based planner** — `EstimateRows` drives AND-ordering (selective-first), an index-vs-scan
  threshold, and cost-driven lifting of the low-selectivity guards.
- **Cross-query index cache** — decode each CDX tag once, reuse across queries; change-token +
  `FileSystemWatcher` (local) / token-poll (network) invalidation; thread-safe, bounded, disposable.

## Quick start

```csharp
using CrossVault.FoxDbf;

// A single table
using var table = DbfTable.Open("customers.dbf");
foreach (var rec in table.Records)
{
    string? name = rec.GetString("name");
    decimal? bal = rec.GetDecimal("balance");
}

// A Visual FoxPro database container (long field names)
using var db = DbfDatabase.OpenFoxpro("app.dbc");
foreach (var t in db.TableNames)
    using (var member = db.OpenTable(t)) { /* ... */ }

// Export
table.ExportCsv("customers.csv");
```

```csharp
using CrossVault.FoxDbf.Write;

// Create a table, append a row, build an index
using (var w = DbfWriter.Create("people.dbf", new[]
{
    new DbfColumnDef("NAME", 'C', 30),
    new DbfColumnDef("AGE",  'I', 4),
}, new DbfCreateOptions { Overwrite = true }))
{
    w.AppendRecord(new Dictionary<string, object?> { ["NAME"] = "Ada", ["AGE"] = 36 });
    w.CreateTag(new CdxTagDefinition("NAMEIDX", "NAME"));   // structural .cdx
}

// Create a Visual FoxPro database container with member tables
DbfDatabaseBuilder.Create("shop.dbc", new[]
{
    new DbcTableSpec("customers", "customers.dbf", new[]
    {
        new DbfColumnDef("CUST_ID", 'I', 4),
        new DbfColumnDef("COMPANY", 'C', 30),
    }),
}, new DbcCreateOptions { Overwrite = true });
```

```csharp
using CrossVault.FoxDbf.Sql;

// SQL directly against a .dbc (long field names) or a directory of free tables (10-char names)
using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");   // or session.OpenDirectory(@"C:\data")
var result = session.Execute(
    "SELECT company_name, country FROM customer WHERE country = 'Germany' ORDER BY company_name");
foreach (var row in result!.Rows)
    Console.WriteLine(row[0]);
```

```csharp
using CrossVault.FoxDbf.Highlike;

// Same API, same results as Core — just faster on repeated / large queries:
using var accelerated = DbfTable.Open("orders.dbf").UseHighlike(new HighlikeOptions { EnableStatistics = true });
foreach (var rec in accelerated.Query("AMOUNT > 1000 AND UPPER(NAME) = \"ACME\"").GetRecords(accelerated))
{
    /* ... */
}
```

Targets `net10.0`.
