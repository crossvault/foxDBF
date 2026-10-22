# Changelog

All notable changes to the public packages are documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [Unreleased]

### Added

- Compact standalone `.idx` files: `DbfWriter.CreateCompactIdx` writes what VFP9's `INDEX ON … TO x.idx COMPACT`
  writes. The file is one compact tree in the CDX tag format, with the collation (MACHINE, GENERAL or SPANISH) and
  VFP's key statistics in the header, and the nodes on the pages VFP9 puts them on. microVFP keeps the `COMPACT`
  clause instead of dropping it. As VFP9 does, it also writes a compact `.idx` for a plain `INDEX ON … TO` while
  `SET COLLATE` is not MACHINE; before, it wrote an uncompressed MACHINE `.idx` for both. `IdxFile` reads compact
  files: `IdxHeader` takes the KEY/FOR text from the expression pool and has a new `Collation` property;
  `EnumerateEntries`, the microVFP orders and `SEEK` read compact leaves and honour the collation;
  `IdxFile.Open(path, table)` resolves the key type from the table.

### Fixed

- microVFP `APPEND FROM` preserves blank numeric source fields as blank bytes instead of writing zero;
  explicit zero values and `.NULL.` fields remain distinct, under `SET NULL OFF` and `ON`.
- Expressions reject chained or mixed arrow qualifiers such as `person->name->x` and `m.person->name`,
  including forms with spaces, as VFP9 does. Valid `alias->field` references still work.

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
- microVFP `INDEX ON … TO x.idx` under a `SET COLLATE` other than MACHINE writes a compact `.idx` under that
  collation (see Added); under a sort sequence we cannot weight (GERMAN, …) it now fails as `INDEX ON … TAG` does,
  instead of writing an uncompressed MACHINE `.idx`.
- BREAKING: The new `IdxFile.Open(string, DbfTable)` overload makes a call with a literal `null` second argument ambiguous;
  write `IdxFile.Open(path, (Encoding?)null)` or `IdxFile.Open(path)`.
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
### Changed

- **BREAKING — microVFP fails loudly on statements it cannot execute.** A command it does not model used
  to be skipped silently, so a stored procedure could run to the end with a wrong result. Executing such a
  line now raises a catchable VFP error that `ON ERROR` and `TRY…CATCH` see: 16 "Unrecognized command
  verb." for an unknown verb (this includes 4-letter abbreviations such as `REPL` and VFP verbs microVFP
  does not model, e.g. SQL `UPDATE`, `QUIT`, `DEFINE CLASS`), 36 "Command contains unrecognized
  phrase/keyword." for a modelled verb whose clauses it cannot parse, 1 for `DO` of a procedure that is not
  loaded, 10 for an expression the expression engine cannot parse (it used to evaluate to `.NULL.`), and the
  SQL parser's error for an embedded `SELECT`/`INSERT`/`CREATE` it cannot parse (an unparseable `INSERT`
  used to write nothing). Source still parses, so a `.prg` or DBC container with an exotic command in an
  unused procedure keeps loading. A documented allow-list of UI/console-only commands (`?`, `@ … SAY`,
  `ACTIVATE WINDOW`, `KEYBOARD`, `ON KEY`, `DECLARE … IN <dll>`, `DISPLAY`/`LIST` to the screen, …) stays a
  headless no-op; see "Deviations from VFP" in `docs/04-microvfp-runtime.md`. `MicroVfpRuntimeException`
  gains `Statement` and `LineNo` with the offending source; its `Message` stays VFP's exact text.
- **BREAKING — microVFP raises an error for a function it does not implement.** A call of a name that is
  neither a procedure, an array, a microVFP function nor an expression-engine built-in used to evaluate to
  `.NULL.` (always when an argument was `.NULL.`), so for example `IF FILE(x)` never fired. It now raises
  VFP9's catchable error 1 "File 'name.prg' does not exist." (`Details` = `name.prg`), also through
  `EVALUATE()` and a macro; `EVALUATE()` now passes on any VFP runtime error raised while evaluating (text it
  cannot parse still yields `.NULL.`). A method call on the `CATCH TO` object raises 1734 "Property M is not
  found." (a property called with a subscript, `oErr.Message(1)`, is the property, as in VFP9), on a defined
  non-object name 1924, on an unknown name 1923. Not modelled and therefore raising now: `CREATEOBJECT()`
  (also abbreviated, `CREA()`). `TYPE()` of such a call is
  `"U"`; `TYPE()` of a built-in call now reports its result type (`TYPE("FILE('x')")` is `"L"`; it was
  `"U"`). Stored procedures and RI triggers that microVFP runs for the ADO.NET enforced write model raise
  the error too; a DBC `RULE` calling an unknown function was already reported as a violation and still is,
  a field `DEFAULT` still evaluates to `.NULL.`. Expressions evaluated without an attached interpreter are
  unchanged — an unknown function there is still `.NULL.`: the plain SQL / ADO.NET path and the index-key
  maintenance of the table writer. `IVfpFunctionHost` gains `UnknownFunction(name, args)` with a default
  implementation that returns `.NULL.`, so existing hosts keep their behaviour.
- **BREAKING — microVFP evaluates embedded SQL, `INDEX ON` and `SET FILTER TO` with the interpreter.** These
  expressions used to be evaluated without it, so an unknown function there read `.NULL.` and a UDF or
  microVFP function (`TRANSFORM()`, `VARTYPE()`, …) in an embedded `SELECT` read `.NULL.` too. Now, as in
  VFP9: an embedded `SELECT` (field list, `WHERE`, `JOIN … ON`, `HAVING`, subqueries) calls UDFs and microVFP
  functions per row and raises error 1 for an unknown function; a result column fed by such a call takes the
  type of its first non-`.NULL.` value (it was an unsized character column). `RECNO()`, `RECCOUNT()`,
  `DELETED()` and the other engine functions still answer from the scanned row. `INDEX ON` and `SET FILTER TO`
  evaluate their expression once when the command runs (at EOF against a blank record), so an unknown function
  raises error 1 then, also on an empty table, and a filter UDF is called once at SET time. An error raised
  while a filter is evaluated during navigation propagates out of `GO`/`SKIP`/`LOCATE`/… (it hid the record).
  `INDEX ON` refuses a UDF, an array or a microVFP-only function in the key or `FOR` expression with an error,
  because the index engine evaluates keys without the interpreter and would key every record `.NULL.` (VFP9
  builds such an index). Deviations: VFP9 also raises for an unknown function in a `SELECT` over an empty
  table (microVFP evaluates per row), and a function the interpreter answers inside a `SELECT` sees microVFP's
  work areas, not the scanned row. A memory variable inside an uncorrelated subquery of an embedded `SELECT`
  now resolves (it read as `.NULL.`, so `… IN (SELECT … WHERE id = lnX)` matched nothing). The plain SQL /
  ADO.NET path is unchanged.
- `AND` / `OR` short-circuit like VFP9 in every expression path (microVFP, SQL, filters, compiled
  delegates): a logical `.F.` left of `AND` or `.T.` left of `OR` decides the result and the right operand is
  not evaluated, so `.F. AND NoSuchFn()` is `.F.` and a UDF on the right is not called. Results are unchanged;
  a `.NULL.` left operand still evaluates the right one. In referential-integrity code written as
  `llRetVal = llRetVal .AND. riupdate(...)` the cascade no longer runs after an earlier one failed.
- microVFP `CLOSE TABLES`, `CLOSE DATABASES` and `CLOSE ALL` close every table of the current data session
  (they did nothing); the session's database container stays attached.
- **BREAKING — microVFP `SCATTER NAME` and `GATHER NAME` raise error 36** "Command contains unrecognized
  phrase/keyword." (catchable). microVFP has no record objects, and both commands used to do nothing, so the
  object stayed undefined and `GATHER NAME` wrote nothing. `SCATTER TO`/`MEMVAR` and `GATHER FROM`/`MEMVAR`
  are unchanged.

### Added

- Abbreviated function names, as VFP9 accepts them: `SUBS()` is `SUBSTR()`, `RECC()` is `RECCOUNT()`,
  `UPPE()`, `TRAN()`, `ALLT()`, `ICAS()` and so on, in microVFP, SQL, filters, index expressions and the
  compiled delegates. The rule was read from the VFP9 compiler and an internal test checks the whole table
  against it: at least 4 letters; when a prefix fits several functions the older one wins (`SUBS` is
  `SUBSTR`, not `SUBSTRC`; `MESS` is `MESSAGE`, not `MESSAGEBOX`; `STRT` is `STRTRAN`, not `STRTOFILE`);
  some later functions need 10 letters (`GETWORDCOU`) or cannot be abbreviated at all (`JUSTSTEM`,
  `FORCEEXT`, `SUBSTRC`, `LEFTC`). Three letters (`SUB()`) and a name longer than the function (`SUBSTRX()`)
  are calls of a user function, as before. An abbreviation of a function the engine does not implement is
  reported under the full name (`CREA()` raises error 1 "File 'createobject.prg' does not exist."; without a
  microVFP host it is `.NULL.`). The array-by-reference functions (`AFIE()`, `ALIN()`, `AERR()`, …),
  `ISBL()` and `LOOK()` get their array or field by name like the full spelling.
- microVFP `TRY … CATCH [TO oErr] [WHEN lExpr] … FINALLY … ENDTRY` and `THROW [eExpr]` with VFP9 semantics,
  verified against the VFP9 runtime. Before, TRY was not modelled and both the TRY and the CATCH block ran.
  A TRY wins over `ON ERROR`, also for errors raised in called procedures; without a matching CATCH the
  FINALLY block runs and the error propagates. The `CATCH TO` object exposes `ErrorNo`, `Message`, `LineNo`,
  `LineContents`, `Details`, `Procedure`, `StackLevel` and `UserValue` (`VARTYPE()` = `"O"`); `THROW x`
  raises 2071 with `UserValue = x`, a bare `THROW` inside CATCH re-raises the caught error. Inside CATCH
  `ERROR()` is 0 while `MESSAGE()` and `AERROR()` describe the caught error. `EXIT` inside TRY leaves the
  TRY structure, `LOOP` raises 1939 and `RETURN` raises 2060, as in VFP9. An error that leaves a procedure
  now also unwinds its call frame, so the caller no longer resumes with the callee's variables in scope.
- microVFP `COUNT [scope] [FOR lExpr] [WHILE lExpr] TO var` (it was not implemented and left the variable
  untouched), honouring `SET DELETED`, `SET FILTER` and `SET KEY` and leaving the record pointer where VFP9
  does.
- microVFP `COPY MEMO … TO file ADDITIVE` appends to the file (a missing file raises error 1, as in VFP9).
- Currency literals such as `$0` and `$12.50` in expressions. The TasTrade sample's credit-limit check
  (`IF lyRemainingCredit < $0`) used to evaluate to `.NULL.` and never fire.
- microVFP `DECLARE name[n]` as the `DIMENSION` synonym.
- microVFP `FILE(cFileName [, nFlags])` with VFP9 semantics: blanks are trimmed, a relative name resolves
  against the session data directory, case does not matter (also on Linux); `""`, a directory, a name without
  its extension and a wildcard skeleton are `.F.`; `.NULL.` or a non-character argument raises 11, no argument
  1229. It does not search `SET PATH`, which microVFP does not model.
- `LENC()` (the DBCS twin of `LEN()`, identical on single-byte code pages). The microVFP guide listed it
  as implemented, but it evaluated to `.NULL.`; the guide also listed a `BLANK()` function, which VFP9 does
  not have (it raises error 1 there, as it now does in microVFP).
- `MicroVfpRuntimeException.Details` — VFP's error detail (`Exception.Details`, `AERROR()` column 3).

### Fixed

- microVFP `USE … ORDER TAG name` failed with "tag not found" even for an existing tag; `ORDER TAG … OF cdx`
  and the `ASCENDING`/`DESCENDING` override now work too, and `DESCENDING()` reports the overridden
  direction. An unknown tag raises error 12 and leaves the table open, as in VFP9.
### Added

- The xBase qualifier `alias->field` (also `m->var`) is accepted wherever `alias.field` is: in expressions,
  in SQL projection, WHERE, ORDER BY, GROUP BY, HAVING, JOIN ON, UPDATE and DELETE, with DBC long table and
  field names, and in microVFP commands (`LOCATE FOR`, `SCAN FOR`, `REPLACE alias->field WITH`,
  `STORE … TO m->x`, `m->x = …`). As in VFP9, blanks around the arrow are allowed and `a - b > c` stays
  arithmetic; `a- >b`, `a->b->c` and `5->3` are rejected.

### Changed

- Qualifiers are checked when a SQL statement is bound, before any row is read, as VFP9 does (even over an
  empty table or behind `.F. AND`). An alias that is not a source of the query raises error 13
  ("Alias 'ZZ' is not found."), a column the alias does not have, an undefined `m.` variable and a bad
  qualifier in ORDER BY / GROUP BY raise 1806. Previously `WHERE zz.id = 3` silently matched no rows and
  `SELECT zz.id FROM person` silently returned `person.id`. `UPDATE` / `DELETE` also reject an unknown
  unqualified name (1806); it used to read `.NULL.` and match nothing or write `.NULL.`. A bare `NULL`
  (`SET field = NULL`) stays the null literal.
- `UPDATE`, `DELETE` and `INSERT` on a DBC table no longer accept the truncated 10-character physical name of
  a longer field (`customer_n` for `customer_name`): it is error 1806 in `UPDATE` / `DELETE` and error 12 in the
  `INSERT` column list, as in VFP9. It used to be the only name these statements knew (see Fixed); use the long
  name.
- A qualifier naming another open work area, or the table name under a local alias
  (`SELECT … FROM person p WHERE person.id = 3`), reads that work area's current record in microVFP, a
  constant for the whole query, as in VFP9. The pure SQL `VfpSession` keeps no record pointer and now raises
  an error for it instead of returning no rows.
- microVFP: `EVALUATE()` raises errors (a malformed expression, an unknown alias) instead of returning
  `.NULL.`; `alias.field` naming no open work area raises error 13 and an unknown field of an open alias
  error 12; a field read at EOF returns the blank value (0, empty string, .F.) instead of `.NULL.`.

### Fixed

- SQL `UPDATE`, `DELETE` and `INSERT` on a DBC table use its long field names, as VFP9 does: in SET, WHERE,
  `table.long_name`, the INSERT column list and through a work-area alias. Previously a field name longer than
  10 characters was unknown (`UPDATE customer_accounts SET customer_name = …` failed with "does not exist", a
  long name in WHERE matched no rows). The structural `.cdx` of such a table is now maintained by these
  statements: VFP9 stores the tag KEY with the long names (`CUSTOMER_NAME`), which the write path could not
  resolve, so an `UPDATE` or `INSERT` deleted the `.cdx` and VFP had to rebuild it.
- microVFP `REPLACE` (also `GATHER`), `INSERT … VALUES` and `APPEND FROM` on a DBC table write through its
  long field names. The structural `.cdx` is no longer deleted by these writes (its tag KEY names the long
  fields), an `INSERT` naming the long fields no longer loses the row silently, and `APPEND FROM` matches the
  source fields by their full names as VFP9 does (a free table's truncated `customer_n` no longer fills
  `customer_name`).
- microVFP `INDEX ON` (also `TO` an `.idx` and with `FOR`), `REINDEX`, `DELETE TAG`, `COPY TAG` and
  `COPY INDEXES` on a DBC table use its long field names. `INDEX ON customer_name TAG x` built a tag that no
  `SEEK` found, and the existing tags whose KEY names the long fields (`CUSTOMER_NAME`, as VFP9 stores it)
  were no longer seekable after it or after `REINDEX`.
- `GROUP BY` with a column ordinal (`SELECT p.id % 2 AS k, COUNT(*) FROM person p GROUP BY 1`) grouped by the
  constant `1` and returned a single group; it now groups by that select item, as does `GROUP BY k` on a
  select alias that is no source field. An ordinal outside the select list raises VFP error 1807, one that
  names an aggregate column 1846, and an `ORDER BY` ordinal outside the select list 1808.
- `ORDER BY` on a select alias (`SELECT id AS k FROM person ORDER BY k DESC`) left the rows unsorted outside
  aggregate queries; it now sorts by that column. A source field of the same name still wins over the alias,
  as in VFP9 (`SELECT name AS id, id AS name … ORDER BY id` sorts by the field `id`).
- `HAVING` could not reference a select alias (`SELECT id AS k, COUNT(*) … GROUP BY k HAVING k > 3` returned
  no rows; so did `COUNT(*) AS n … HAVING n > 1`). HAVING now sees the select aliases, aggregate aliases
  included; a source field of the same name wins. A compound HAVING (`… HAVING COUNT(*) > 1 AND k > 0`, `OR`)
  is evaluated instead of throwing `NotSupportedException`, and aggregates inside a function
  (`HAVING BETWEEN(SUM(amount), 60, 80)`) are computed per group. A HAVING in a query without GROUP BY or
  aggregates (`SELECT id AS k FROM person HAVING k > 4`) was ignored; it now filters the rows, as in VFP9,
  and an aggregate in such a HAVING (`SELECT id FROM person HAVING COUNT(*) > 1`) raises VFP error 1807.

### Changed

- `GROUP BY`, `DISTINCT` and `UNION` results without `ORDER BY` come back in VFP9's order instead of the order
  the rows were first seen in (checked against VFP9). GROUP BY returns the groups sorted by the GROUP BY key
  in GROUP BY list order, also when the key is not selected (`SELECT id % 2 AS k, COUNT(*) … GROUP BY 1` gives
  `0, 1`, was `1, 0`). DISTINCT and every `UNION` step without ALL sort by the whole row in column order; a
  `UNION ALL` step appends its rows unsorted. Sorting uses the session collation (`SET COLLATE`); .NULL. comes
  first and .F. before .T. An `ORDER BY` re-sorts that order stably, so rows with equal ORDER BY keys stay in
  group-key or row order, as in VFP9. DISTINCT now runs before ORDER BY.

- BREAKING: SQL result columns are named as VFP9 names them (checked against VFP9). Names are upper case (also
  user aliases and DBC long field names: `customer_name` → `CUSTOMER_NAME`). An unaliased expression is `EXP_n`
  with n its column position (`SELECT a, b, b * 2` → `EXP_3`, was `EXP_1`). An unaliased aggregate is `CNT`
  for `COUNT(*)`, `CNT_FIELD` / `SUM_FIELD` / `AVG_FIELD` / `MIN_FIELD` / `MAX_FIELD` over a field and
  `SUM_EXP_n` etc. over an expression (was `COUNT`, `SUM`, …). A name that occurs more than once, user aliases
  included, becomes `NAME_A`, `NAME_B`, `NAME_C`, … in column order: `SELECT ca.account_number,
  ao.account_number` gives `ACCOUNT_NUMBER_A` / `ACCOUNT_NUMBER_B` (was `ca_account_number` /
  `ao_account_number`), and a single-table `SELECT id, id` no longer returns two columns named `id`. ADO.NET
  lookups by name (`GetOrdinal`, `DataTable` columns) are case-insensitive and keep working for unchanged names.
  `INTO TABLE` / `INTO CURSOR` still cut field names to 10 characters, so long result names there now become
  e.g. `ACCOUNT_NU` / `ACCOUNT__2` (was `ca_account` / `ao_account`).
- SQL `INSERT INTO t (a, b) VALUES (…)` wrote `.NULL.` into every NULL-able column the column list left out, so
  VFP9 read e.g. an omitted nullable memo as `.NULL.` instead of `''`. Omitted columns now follow `SET NULL` as
  in VFP9: under `SET NULL OFF` (the default) they are written blank with the null bit clear; under
  `SET NULL ON` a NULL-able one is `.NULL.` and a NOT NULL one rejects the row with error 1581 "does not accept
  null values" (except an AUTOINC column, and a DBC field with a DEFAULT: microVFP fills it from the DEFAULT,
  see below; the raw SQL / ADO.NET path writes it blank). `INSERT … FROM MEMVAR` treats a field
  without a memvar the same way, and the fields a short `INSERT … FROM ARRAY` row does not reach are always
  blank. microVFP's `SET NULL` applies. A `.NULL.` Varchar/Varbinary, and one an INSERT leaves blank, is now
  stored as VFP9 stores it (0x20 padding, length byte 0, varlen bit set) instead of zero bytes.
- A microVFP `INSERT` into a table of the open database left the columns it omits blank instead of filling
  them from their field DEFAULT (`DBGETPROP(…, 'Field', 'DefaultValue')`). It now evaluates the DEFAULTs as
  VFP9 does, under `SET NULL OFF` and `ON` alike: once per row, after the VALUES, with the target table as the
  current work area (so a stored-procedure DEFAULT such as TasTrade's `newid()` reads the right `ALIAS()`),
  for `INSERT … VALUES`, `INSERT … FROM MEMVAR` and a buffered `INSERT` (whose DEFAULTs show in the buffer
  before `TABLEUPDATE()`). A short `INSERT … FROM ARRAY` row still leaves the fields it does not reach blank, as
  in VFP9. Under `SET NULL ON` the fields are taken in order and the first omitted NOT NULL field without a
  DEFAULT rejects the row with error 1581, so DEFAULTs after it never run (a counter UDF such as `newid()`
  still advances for the ones before it, as in VFP9). This includes stored procedures called through the
  ADO.NET provider. A plain ADO.NET command without `EnforceRules=on` still takes the raw path and does not
  evaluate DEFAULTs. Inside a DEFAULT, `RECCOUNT()` does not yet count the new row (VFP9's does). A DEFAULT
  that microVFP cannot evaluate (for instance a UDF whose stored procedures were not loaded with
  `LoadStoredProceduresFromDatabase()`) now rejects the row; because a microVFP `INSERT` swallows its errors,
  no row lands, where before it landed with that column blank.
- microVFP `APPEND FROM` wrote `.NULL.` into every NULL-able target field the source table does not have, and
  turned blank source fields into `.NULL.` (N, D, M) or `.F.` (L). It now writes as VFP9 does: a field the
  source lacks is blank under `SET NULL OFF` and `.NULL.` under `SET NULL ON`; a blank source field stays
  blank and a `.NULL.` one stays `.NULL.`. A `.NULL.` that would land in a NOT NULL field (or, under
  `SET NULL ON`, a NOT NULL field the source lacks) raises error 1581 and stops the APPEND; the rows before it
  stay appended.
- A buffered microVFP `INSERT` committed by `TABLEUPDATE` wrote its omitted columns as typed values (`0` in
  N as `"     0"`, `.F.` in L, an empty memo block in M). They are now written like the unbuffered INSERT's —
  blank bytes, or `.NULL.` / error 1581 when `SET NULL` was ON at INSERT time.
- An empty memo or blob (`''`, `0h`) no longer allocates a memo block; it is stored as a 0 pointer, as VFP9
  stores it on INSERT and REPLACE, and microVFP reads such a memo (null bit clear) as `''`, not `.NULL.`. A
  Varbinary (`Q`) value shorter than its field is padded with 0x20, as in VFP9, instead of zero bytes.
- microVFP read a blank N, F, D, T or M field (null bit clear, or a column that is not NULL-able) as `.NULL.`:
  `ISNULL()` returned `.T.`, `n + 1` stayed `.NULL.`, and the same happened in the `SELECT`s microVFP runs
  (`WHERE ISNULL(n)`, `COUNT(n)`, `INTO ARRAY`, `INTO CURSOR`). As in VFP9, such a field now reads as its
  empty value (0, `{}`, `""`) with `ISNULL()` `.F.` and `EMPTY()` `.T.`; only a set null bit is `.NULL.`.
  `SELECT … INTO CURSOR`/`TABLE` from microVFP makes a column NULL-able when a `.NULL.` lands in it, so the
  value stays `.NULL.` there. In microVFP `EMPTY(.NULL.)` now returns `.F.`, as in VFP9. `DbfRecord`,
  `VfpSession.Execute` and ADO.NET are unchanged: a blank N/F/D/T/M field still reads as `null` (`IsDBNull`
  true), and there `EMPTY()` still matches it.

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
