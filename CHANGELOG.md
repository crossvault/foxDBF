# Changelog

All notable changes to the public packages are documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [Unreleased]

### Fixed

- Expressions and SQL accept Unicode letters in identifiers, so VFP field names such as `razónSoc` or the DBC
  long name `razónSocial` no longer fail with "Unexpected character" (#2, thanks @rubenrovira). Field names
  in `.dbf` headers are decoded in the table's code page instead of ASCII, which had turned them into
  `RAZ?NSOC` and made such a column read as `null`.
- A qualified field reference in the WHERE of a single-table `SELECT`, `UPDATE` or `DELETE`
  (`SELECT … FROM person p WHERE p.id = 3`, `WHERE person.id = 3`) silently matched no rows, because the
  qualifier reached the row evaluator as part of the column name; it now resolves to the query's own source
  (subqueries and UNION branches included). Qualifiers of other aliases are left alone, so correlated
  subqueries resolve as before.
- Non-ASCII names (VFP9 accepts field names such as `razónSoc`) now work beyond the SQL read path. Field
  names in created/altered `.dbf` headers, CDX tag names and CDX/IDX KEY/FOR expressions are written and
  read in the TABLE's code page, as VFP9 does (`RAZÓNSOC` carries 'Ó' as `0xD3` in CP1252 and `0xE0` in
  CP850), instead of ASCII, which turned every such letter into `?`. The CDX tag directory sorts on those
  bytes, matching VFP's order.
- microVFP scans identifiers (fields, variables, arrays, procedures, macros, `#DEFINE`s) with the same
  Unicode-letter rule as the expression lexer, so `REPLACE razónSoc …`, `DIMENSION arró[2]` and
  `FUNCTION sumaÑ` work; `INDEX ON … TAG razón` stores `RAZÓN` (upper-cased per code page, so `ƒ` stays `ƒ`
  as in VFP).
- ADO.NET: `FoxDbfCommandBuilder` recognises non-ASCII column names, and the advertised
  `DataSourceInformation.IdentifierPattern` accepts Unicode letters.
- CHARACTER CDX key data under MACHINE collation is stored in the TABLE's code page, as VFP9 does
  (`'Peñarol'` in a CP850 table is `50 65 A4 …`, not the CP1252 `50 65 F1 …`); a bare NOCPTRANS field keeps
  its raw bytes, and `CHR()` inside a KEY/FOR maps bytes in the table code page. The bulk build, incremental
  row maintenance, `CdxTag.Seek`/`DecodeKey`, Rushmore character bounds and microVFP `SEEK`/`SET KEY` use the
  same encoding, so VFP9 finds rows in our CDX and we find rows in VFP's (an indexed `WHERE name = 'Peñarol'`
  on a VFP-built CP850 CDX used to return no rows). CDX files for non-CP1252 tables that an earlier version of
  this library wrote hold CP1252 key bytes: REINDEX them.

### Changed

- JOIN queries apply WHERE conjuncts that read only the driving table to that table before the join runs, so
  a selective WHERE no longer pays one inner-side lookup per driving row (≈283 s → ≈7 s on a 593k-row LEFT
  JOIN in the contributor's benchmark; #1, thanks @rubenrovira). The full WHERE is still evaluated after the
  join, RIGHT/FULL chains are never pushed, and only deterministic conjuncts that read a driving-table field
  are pushed.
- BREAKING: A field name, tag name, KEY/FOR expression or backlink path that the table's code page cannot represent
  now throws (`DbfSchemaException` / `DbfWriteException`) instead of being written with `?`. Name length
  limits count encoded bytes, and a CDX tag name longer than 10 bytes now throws instead of being clipped
  silently (VFP9 raises error 1888, "Tag name is too long").
- New overloads `CdxHeader.Parse(span, Encoding)`, `IdxHeader.Parse(span, Encoding)` and
  `IdxFile.Open(path, Encoding)` decode index expressions in a given table code page; the existing
  overloads use the library default code page (Windows-1252) instead of ASCII.

## [0.9.0] - 2026-07-05

### Changed

- BREAKING: `PACK` and `ZAP` now require writers opened or created with `LockMode.Exclusive`.
  Calling either command through the default/shared writer now fails before touching the file with
  typed VFP error `110` ("File must be opened exclusively.").
- BREAKING: microVFP's maximum procedure/function call depth was tightened from `64` to `48`. Code
  that relied on deeper recursion now hits the existing maximum-call-depth error earlier.
- BREAKING: `DbfWriter.Create` (and the VFP-SQL / microVFP `CREATE TABLE` paths that route through it)
  now opens the freshly created table with `LockMode.Exclusive` by default (was `LockMode.Shared`),
  matching Visual FoxPro — `CREATE TABLE` opens the new table `EXCLUSIVE`, so a just-created table is
  immediately `PACK`/`ZAP`/`INDEX`-able (this is what makes the create → `Pack()` quickstart work with
  defaults again). Pass `DbfCreateOptions.LockMode = LockMode.Shared` explicitly when a second handle
  must coexist with the writer while it is still open (that stays fully supported); after `Dispose` the
  file is a normal on-disk table either way.
