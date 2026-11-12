# 1. Reading tables

**What you'll do:** open a `.dbf` (or a `.dbc` database container), iterate records, read typed
field values, and run a filtered/optimized query.

## Open a single table

```csharp
using CrossVault.FoxDbf;

using var table = DbfTable.Open("customers.dbf");

foreach (var rec in table.Records)
{
    string? name = rec.GetString("name");
    decimal? balance = rec.GetDecimal("balance");
    DateTime? created = rec.GetDateTime("created");
    bool isDeleted = rec.IsDeleted;
}
```

**Expected output:** one iteration per non-deleted record (deleted records are skipped by default —
see [`SET DELETED`](#deleted-records) below), reading straight from the file with no upfront full
load.

## Open from streams

When the table is not a file on disk (a blob, an archive entry, an upload), open it from a stream
and pass its memo file (`.fpt`, `.dbt`, or `.dct` for a `.dbc`) as a second stream. The values are
the same as opening the table from its path, memos included:

```csharp
using var dbf = OpenDbfBlob();    // any readable, seekable Stream
using var memo = OpenFptBlob();   // or null when the table has no memo file

using var table = DbfTable.Open(dbf, memo, options: null, leaveOpen: false);
string? notes = table.GetRecord(0)?.GetString("notes");
```

- **Start position:** each file spans from its stream's current `Position` to the end of the
  stream, and every later read is relative to that start. To open a table embedded with trailing
  data, pass a stream bounded to the table's length (for a byte buffer:
  `new MemoryStream(buffer, offset, length)`); trailing bytes would otherwise count as part of the
  file. The older `DbfTable.Open(Stream, …)` overloads keep reading from position 0.
- **No memo:** write `memo: null`. A bare `DbfTable.Open(stream, null)` does not compile (it is
  ambiguous with `DbfTable.Open(Stream, DbfOptions)`, which still works for a stream without memo).
- **Seekable streams only:** headers, random record access and memo blocks all seek, so a
  non-seekable stream (a network or decompression stream) throws `NotSupportedException` at open.
  Copy it into a `MemoryStream` first.
- **No memo stream:** with `memo: null` the table reads memo fields as `null`, exactly like a path
  open whose `.fpt`/`.dbt` is missing (unless `DbfOptions.MemoPath` names a memo file). A given memo
  stream wins over `DbfOptions.MemoPath`, and is ignored for a version without memos.
- **Ownership:** `leaveOpen` covers both streams. With `false` (the default) they are disposed with
  the table, or right away when the open fails.

## Open a `.dbc` database container

A `.dbc` gives you **long field names** (VFP truncates physical `.dbf` column names to 10
characters; the container's `PROPERTY`/`.DCT` metadata restores the real names) and lets you
enumerate member tables:

```csharp
using var db = DbfDatabase.OpenFoxpro("shop.dbc");

foreach (string tableName in db.TableNames)
{
    using var member = db.OpenTable(tableName);
    Console.WriteLine($"{tableName}: {member.RecordCount} records");
}
```

## Deleted records

Visual FoxPro doesn't remove a record on `DELETE` — it flags it (byte `0x2A`) and leaves it in
place until `PACK`. By default, `table.Records` **skips** flagged records, matching VFP's
`SET DELETED ON`. To see everything (matching `SET DELETED OFF`), use `EnumerateAll`:

```csharp
foreach (var rec in table.EnumerateAll(includeDeleted: true))
    if (rec.IsDeleted) { /* ... */ }
```

## Filtered / optimized queries

`table.Query(filter)` parses `filter` as a VFP expression and runs it through the **Rushmore-style
optimizer** — if an open `.cdx` index tag matches part of the expression, it seeks instead of
scanning; the residual (non-index-covered) part of the expression is still evaluated per candidate
row, so the result is always identical to a full scan, just faster when an index helps.

```csharp
CrossVault.FoxDbf.Query.QueryResult result = table.Query("AMOUNT > 1000 AND UPPER(NAME) = \"ACME\"");
foreach (var rec in result.GetRecords(table))
    Console.WriteLine(rec.GetString("name"));

// See exactly what the optimizer decided to do (like VFP's SYS(3054) ShowPlan):
CrossVault.FoxDbf.Query.QueryPlan plan = table.ExplainQuery("AMOUNT > 1000 AND UPPER(NAME) = \"ACME\"");
Console.WriteLine(plan);   // e.g. "Index AMOUNT_IDX (range) + residual UPPER(NAME) = \"ACME\""
```

## Indexes directly

To seek an index tag yourself (bypassing the query optimizer), open the `.cdx` and use `Seek`:

```csharp
using var cdx = CrossVault.FoxDbf.Index.CdxFile.Open("customers.cdx");
var tag = cdx.Tag("NAME_IDX");
uint? recordNumber = tag.Seek("ACME CORP");
```

## Memos, code pages, NULLs

- `.fpt` (FoxPro, big-endian) and `.dbt` (dBase III/IV, little-endian) memo files are followed
  automatically — `rec.GetString("notes")` on a Memo (`M`) field just works.
- Code pages are read from the header and applied automatically; `DbfOptions.Encoding` /
  `DbfOptions.DefaultEncoding` let you override it if a file's header byte is wrong or missing.
- VFP `.NULL.` is a real, distinct value from empty/blank, tracked via the hidden `_NullFlags`
  column. With `DbfOptions.ApplyNullFlags` at its default (`true`), a field whose null bit is set
  decodes as `null` through the same `rec.GetString`/`GetDecimal`/etc. accessors you already use —
  no separate NULL-check API needed.

## Next

[2. Writing and indexes →](02-writing-and-indexes.md)
