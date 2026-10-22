# CrossVault.FoxDbf.Expressions

The Visual FoxPro **expression engine** shared by every other layer in this repo — the query
optimizer, the SQL executor, and the microVFP interpreter all parse and evaluate expressions
through this one grammar. Not independently published on NuGet: it ships bundled inside the
[CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf) package (`CrossVault.FoxDbf.Expressions.dll`
sits alongside `CrossVault.FoxDbf.dll` in the same package).

## What it does

- **`VfpExpression.Parse(text)`** — a hand-written lexer + Pratt parser producing an AST, compiled
  via `System.Linq.Expressions` for fast repeated evaluation (parse once, evaluate per row).
- **`Evaluate(IRowContext row, EvaluationContext? context = null)`** — evaluates against a row
  (table record, SQL result row, or a synthetic context) honoring `SET EXACT` / `SET ANSI` /
  collation settings via `EvaluationContext`.
- A broad **VFP9 function library**: string (`SUBSTR`, `ALLTRIM`, `PADL/R/C`, `STRTRAN`, `CHRTRAN`,
  `LIKE`, `ICASE`, plus the `…C` function surface — `LEFTC`/`SUBSTRC`/`AT_C`/`NORMALIZE`/`ISLEADBYTE`,
  … — with single-byte-code-page semantics (byte == character; `ISLEADBYTE()` returns `.F.`), matching
  VFP on single-byte code pages; true double-byte handling is not implemented),
  date (`DTOC`, `DTOS`, `GOMONTH`, …), numeric (`ROUND`, `INT`, `MOD`, …), and logical/type functions
  (`IIF`, `EMPTY`, `ISNULL`, `ISBLANK`, `TYPE`, `BETWEEN`, `INLIST`, …), growing towards full VFP9
  coverage.
- **Host hook** — a row that also implements `IVfpFunctionHost` handles functions the engine does not
  (`TryInvoke`) and decides what a call of an unknown function means (`UnknownFunction`; the default, and
  the behaviour without a host, is `.NULL.` — microVFP raises VFP error 1). `AND` / `OR` short-circuit as
  in VFP9.
- **Byte-exact MACHINE / GENERAL collation** — the same two collating sequences Visual FoxPro uses
  for indexing and comparisons, reverse-engineered against real VFP9 CDX keys.

## Quick start

```csharp
using CrossVault.FoxDbf.Expressions;

VfpExpression expr = VfpExpression.Parse("UPPER(name) = \"ACME\" AND amount > 1000");
VfpValue result = expr.Evaluate(row);   // row implements IRowContext
bool matches = result.AsBoolean;
```

In practice you rarely call this directly — `DbfTable.Query(...)`, the SQL executor, and the
microVFP interpreter all parse expressions through this engine for you.

Targets `net10.0`.
