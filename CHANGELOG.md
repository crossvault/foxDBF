# Changelog

All notable changes to the public packages are documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [Unreleased]

### Fixed

- ADO.NET: a Memo column loaded with `DataTable.Load` (or `DbDataAdapter.Fill` with key info) got
  `MaxLength = 4`, the width of the memo-block pointer in the record, so any memo longer than four characters
  failed with a `ConstraintException` (#3, thanks @asmith-zinck for the report). `GetSchemaTable` now reports
  what the Visual FoxPro OLE DB provider reports: `ColumnSize = 2147483647` and `IsLong = true` for M, G, P and
  W columns, the declared width and `IsLong = false` for V and Q (Varbinary was wrongly flagged as long).
  `GetSchema("Columns")` gives the same `CHARACTER_MAXIMUM_LENGTH` / `CHARACTER_OCTET_LENGTH`, and, as VFPOLEDB,
  none for numeric, date and logical columns (previously their field width).
- GENERAL and SPANISH (weighted) CDX keys are padded with 0x00 up to the key length, as VFP9 pads them; MACHINE
  keys keep the blank (0x20) pad. The reader, the bulk build, incremental row maintenance (leaves and interior
  keys), exact `SEEK`, microVFP `SET KEY` bounds and the Highlike key statistics use the tag's pad. Keys that are a
  prefix of one another now sit in VFP9's order (`Aa` = `60 60` before `áa` = `60 60 01`, `Muller` before
  `Müller`, the empty key first), and a trailing weight 0x20 (the head of `/`) is stored as data instead of being
  taken for pad. Our bulk-built leaves now hold the same entries as VFP9's `INDEX ON`, VFP9 SEEKs on our CDX as on
  its own, and our SEEK and Rushmore find VFP9's rows on its CDX. The "cut, then lose the trailing 0x00 tail
  bytes" rule listed for 0.10.0 is this pad. GENERAL/SPANISH CDX files written by an earlier version keep the
  old order and lose a trailing `/` on read: REINDEX them.
- microVFP `SET KEY TO` a single character value uses VFP9's bound: under GENERAL/SPANISH and `SET EXACT OFF` the head
  weights only, like SEEK (`SET KEY TO 'áa'` shows `Aa`, `aa`, `áa`, `Áa`, `Aab`); a MACHINE bound is cut to the key
  length; `SET KEY TO ''` clears the scope. The relation reposition into a DESCENDING child tag lands on the first
  match in descending order.
- Expressions see a character field as VFP9 does, at its full declared width. A value with leading blanks no
  longer matches as if they were not there (`name = 'A'` matched `' Á'` in a scan but not through an index), so
  a plain scan and Rushmore return the same rows. The index window for `<` / `<=` on a GENERAL/SPANISH tag now
  holds every key that starts with the literal's head weights (`'Ab' < 'Á'` under SET EXACT OFF).
- SQL expressions over character fields keep the field's blanks: on a C(5) holding `'N'`, `SELECT p.region +
  p.region` returns `'N    N    '` (it returned `'NN'`), also across a JOIN (`ca.region + ao.region`), `LEN(region)`
  is 5, `SUBSTR`/`RIGHT` see the blanks, and `WHERE region + '|' = 'N    |'` matches, under SET ANSI OFF and ON.
- A SQL `UPDATE` no longer strips the leading blanks of the character fields it does not assign (right-justified
  keys such as TasTrade's `'     1'` turned into `'1'`).

### Changed

- BREAKING: String comparison follows VFP9 (pinned by a 4116-case VFP9 golden master, `vfp_test/strcmp`). SET EXACT
  OFF cuts the left operand to the right one's length (MACHINE by characters, GENERAL/SPANISH by head weights),
  SET EXACT ON pads the shorter one with blanks; `<`, `<=`, `>`, `>=`, `<>`, `INLIST()` and `BETWEEN()` follow
  the same rule as `=` (`'Ab' <= 'A'` is true under SET EXACT OFF). SQL compares under SET ANSI: OFF cuts the
  longer operand, ON pads. Xbase `==` compares character for character with equal length whatever SET COLLATE says
  (`'a' == 'A'` is false under GENERAL); SQL `==` is the padded collation-aware comparison. Five SPANISH ligature
  pairs (ß, Æ, Œ) under the cutting rules keep a documented deviation.
- BREAKING: Expressions (`QueryOptimizer` filters, SQL `WHERE` / `JOIN ON` / `HAVING`, computed columns, `UPDATE …
  SET`) and SQL results (`SELECT *`, `SELECT name`, ADO.NET readers) carry a character field at its full width,
  as a VFP cursor does: `SELECT name` returns `'Smith     '` for a C(10), an Xbase filter (`QueryOptimizer`)
  `name == 'Smith'` is false on it (VFP: `ALLTRIM(name) == 'Smith'` or `name = 'Smith'`; SQL `==` pads and
  matches), and `UPDATE … SET name = name + 'x'` leaves the field unchanged. `DbfRecord`'s indexer still returns
  the trimmed value.
- BREAKING: microVFP reads a character field at its full declared width as well (pinned by a VFP9 golden master,
  `vfp_test/mvfpwidth`): `LEN(name)` is the field width, leading and trailing blanks stay, on disk as in a
  buffered edit or appended row, through `OLDVAL()`/`CURVAL()`, memory variable copies, `SCATTER` (`SCATTER …
  BLANK` gives blanks of the field width) and `TOTAL`. `name == 'abc'` is false for a C(10) (`name ==
  PADR('abc', 10)` or `ALLTRIM(name) == 'abc'`), `REPLACE name WITH name + 'x'` leaves the field unchanged, and a
  field value used as a `SEEK` needle or `SET RELATION` key carries its trailing blanks (a relation from a C(10)
  field into a C(6) tag finds nothing, as in VFP9; relate on `LEFT(name, 6)`). The interpreter now starts under VFP's default
  SET EXACT OFF instead of forcing SET EXACT ON (also on the `VfpSession` it runs on, and for a new data
  session): `name = 'ab'` matches `'abc'` and `'abcdef'` unless the program sets SET EXACT ON.
- BREAKING: A character `SEEK` on a GENERAL/SPANISH tag (`CdxTag.Seek(object)` and microVFP `SEEK`) uses VFP9's SET EXACT
  OFF needle: only the head weights of the value, so the diacritics of the needle are ignored and the first key
  whose heads start with them matches (`Seek("áa")` lands on `Aa`, `Seek("Müller")` on `Muller` when that sorts
  first), exactly as VFP9's `SEEK`. The raw-byte `CdxTag.Seek(bytes, exact: true)` compares the rest of the key
  against the tag's pad (0x00 for a weighted tag) instead of blanks.
- Index KEY/FOR text is stored as VFP9 stores it. VFP does not keep what was typed: `INDEX ON` writes the
  decompiled expression into the `.idx` header and the CDX tag (`MOD(RECNO(), 3) = 0` → `MOD(RECNO(),3)=0`,
  `UPPER( Name )` → `UPPER(name)`, `NOT lg AND x <> 'a'` → `.NOT.lg.AND.x#"a"`, `RECN()` → `RECNO()`,
  `n * (2 + 3)` → `n*5`, `>=` → `=>`). `CreateTag`, `CreateTagIn`, `CreateStandaloneIdx` and `Reindex` now do
  the same; tags that are only carried along (a sibling while another tag is added or deleted) keep their
  text, and a new database's `.dcx` keeps VFP's typed texts. Texts outside the reproduced subset (numbers
  beyond 15 significant digits, `{01/02/2000}` dates, …) are stored as given.
- Expressions written by VFP could not be read back: `n>3.AND.n<10` (the `3.` swallowed the dot of
  `.AND.`), `=>` / `=<`, currency literals (`$12.5000`), hexadecimal literals (`0x1F`), `alias->field` and
  single-digit strict dates (`{^2000-1-2}`) now parse.
- Rushmore and the Highlike statistics matched a condition against a tag's KEY by its exact spelling, so a
  VFP-built tag stored as `IIF(.NOT.EMPTY(name),…)` or with `"…"`, `#`, `=>` was not used for a condition
  typed `IIF(NOT EMPTY(NAME),…)`, `'…'`, `<>`, `>=`.
- BREAKING: microVFP `SEEK`, `SEEK()` and `KEYMATCH()` honour `SET EXACT` under every collation, as VFP9 does (they
  were always prefix seeks; only the relation reposition honoured it). Under `SET EXACT ON` — microVFP's default —
  `SEEK 'Stra'` no longer finds `Straße` and a short value no longer finds a longer key: set `SET EXACT OFF` for a
  prefix seek. EXACT ON matches the whole key (value plus pad); under GENERAL/SPANISH also a value exactly as wide
  as the key does under EXACT OFF (`SEEK 'Muller    '` does not find `Müller`). Trailing needle blanks under
  GENERAL/SPANISH EXACT OFF compare with the heads of the blank-padded value (`SEEK 'Ab '` finds `Ab` or `Ab  c`,
  `SEEK 'Stra  '` not `Strab`); a MACHINE needle longer than the key never matches (also not under `SET NEAR`).
  `CdxTag.Seek(object)` follows the same EXACT OFF rule.

## [0.10.0] - 2026-10-22

### Fixed

- On a case-sensitive file system (Linux, containers) the sidecar files VFP writes as `NAME.CDX` / `NAME.FPT`
  were only found for memos: the structural index was neither read nor maintained (it went stale for VFP),
  and transactions, RI snapshots, DROP TABLE and microVFP index commands overlooked it. Every sidecar lookup
  now resolves the file whatever the case of its name; a new sidecar still gets the lower-case default.
- microVFP file commands wrote and read UTF-8. `COPY MEMO … TO` now writes the memo in the table's code page
  (a binary memo byte for byte), `COPY PROCEDURES TO` / `APPEND PROCEDURES FROM` round-trip the stored-procedure
  source byte for byte (an APPEND used to store mangled non-ASCII text in the DBC), and `LIST/DISPLAY MEMORY`,
  `DISPLAY STRUCTURE` and `DISPLAY TABLES` `TO FILE` use the interpreter's byte model, the table's or the
  container's code page.
- Identifiers in `.prg` source may use the CP1252 letters 0x80–0x9F (`Š Œ š œ Ÿ ƒ`), which the byte-per-char
  (Latin-1) reader used to turn into control characters, so such a variable or a field like `ŠUMA` was not
  recognised. String literals and `TEXT … ENDTEXT` blocks keep their raw bytes.
- CDX tags whose KEY is an expression (`cust+STR(no,4)`, `DTOS(d)`, `UPPER(name)`, `no*2`, `YEAR(d)`, …) now get
  their key type from the expression's static type, as the index builder already did and as VFP9 stores them
  (`id+1` on an Integer field is an 8-byte Numeric key). Previously such a tag was `IndexKeyType.Unknown` and
  treated as a character key: `CdxTag.Seek(object)` returned not-found for every value, numeric and date
  expression keys were decoded as character bytes, Rushmore skipped the tag for range bounds, and microVFP
  `SET KEY` on a numeric expression tag threw.
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
- GENERAL CDX keys are weighted in the TABLE's code page, as VFP9 does. A CP850 table uses VFP's CP850 GENERAL
  table (ported from CodeBase `COLL4ARR.C`, with VFP9's `þ`/`Þ` → `TH`): umlauts expand to vowel+E (`'Äpfel'`
  → `A E P F E L`, not `A` + accent) and symbols, box drawing and currency signs collapse to one weight, so VFP9
  finds rows in our CP850 GENERAL CDX and we find rows in VFP's. CP1252 GENERAL is unchanged except that the
  five undefined CP1252 bytes (0x81, 0x8D, 0x8F, 0x90, 0x9D) now weigh like VFP9 (0x10) instead of as `?`. A
  GENERAL key whose heads and tails run past the key length is cut and then loses its trailing 0x00 tail bytes,
  as in VFP9. Rebuild GENERAL CDX files on CP850 tables written by an earlier version.
- SPANISH is supported for CP1252 and CP850 tables (CDX keys byte-identical to VFP9: `ñ`, `ch` and `ll` sort as
  letters of their own). Before, a SPANISH tag was built, sought and maintained as MACHINE.
- A tag under a collating sequence the library cannot reproduce (e.g. GERMAN) is no longer maintained with
  MACHINE keys: an insert/update invalidates the structural CDX (as for an un-derivable KEY), `CdxTag.Seek`
  returns not-found, microVFP `SEEK` raises an error, and Rushmore does not narrow on it. Rushmore also narrows a
  GENERAL/SPANISH tag only when the context collation is the tag's own (code-page) collation.
- GENERAL is also exact for CP437 and CP1250 tables. CP437 uses CodeBase's `cp437generalCollationArray`
  (umlauts stay accented vowels, `ß` → `SS`, symbols and box drawing weigh 0x10). CP1250 uses VFP9's own Central
  European order, read off VFP9-built keys because CodeBase has no GENERAL table for it: `a` weighs 0x6C, `ą ć ę
  ł ń ó ś ź ż` are letters of their own, `á č ď ě ň ř š ť ů …` are base letter + accent. Both tables reproduce the
  keys of a VFP9 probe over every byte, 600 random strings, all 65 025 byte pairs and 18 472 letter triples.
  Before, both code pages used the CP1252 table (wrong beyond ASCII, so Rushmore did not narrow on them). Rebuild
  GENERAL CDX files on CP437 and CP1250 tables written by an earlier version.

### Changed

- BREAKING: `DbfWriter.Create` (and every CREATE TABLE / ALTER path built on it) stores field names upper-cased in
  the table's code page, as VFP9 does (`razónsoc` → `RAZÓNSOC`, `œuvre` → `ŒUVRE`, `ƒ` stays), so
  `DbfColumn.Name` of a created table now reads back upper-case. Field descriptors are otherwise byte-identical
  to VFP9's: the binary flag 0x04 is set automatically on I, B, Y, T, Q and W fields (with NULL 0x06, AUTOINC
  0x0C), and a Currency field records its 4 decimals.
- JOIN queries apply WHERE conjuncts that read only the driving table to that table before the join runs, so
  a selective WHERE no longer pays one inner-side lookup per driving row (≈283 s → ≈7 s on a 593k-row LEFT
  JOIN in the contributor's benchmark; #1, thanks @rubenrovira). The full WHERE is still evaluated after the
  join, RIGHT/FULL chains are never pushed, and only deterministic conjuncts that read a driving-table field
  are pushed.
- BREAKING: `DbfWriter.CreateTag` / REINDEX with an unsupported collating sequence (anything but MACHINE, GENERAL and
  SPANISH), or a REINDEX of a CDX holding such a VFP-built tag, throws `DbfWriteException` and leaves the CDX
  untouched instead of silently rebuilding the tag as MACHINE. A bulk append invalidates such a CDX.
- New overloads `VfpCollations.ByName(name, codePage)` and `VfpCollations.FromSortSequence(name, codePage)`
  resolve the code-page-specific collation; `GeneralCollation.CodePage` reports it. The one-argument overloads
  stay CP1252 and now also resolve `SPANISH`. microVFP `SET COLLATE TO "SPANISH"` is accepted.
- BREAKING: A field name, tag name, KEY/FOR expression or backlink path that the table's code page cannot represent
  now throws (`DbfSchemaException` / `DbfWriteException`) instead of being written with `?`. Name length
  limits count encoded bytes, and a CDX tag name longer than 10 bytes now throws instead of being clipped
  silently (VFP9 raises error 1888, "Tag name is too long").
- New overloads `CdxHeader.Parse(span, Encoding)`, `IdxHeader.Parse(span, Encoding)` and
  `IdxFile.Open(path, Encoding)` decode index expressions in a given table code page; the existing
  overloads use the library default code page (Windows-1252) instead of ASCII.

### Known issues

- GENERAL/SPANISH CDX keys are padded with 0x20 where VFP9 pads them with 0x00. Each key matches VFP9, but keys
  that are a prefix of one another (`Aa` / `áa`, `Muller` / `Müller`, an empty key) can sit in a different order,
  so a SEEK on such a key may miss across the two engines. This predates 0.10.0 and affects every code page.

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
