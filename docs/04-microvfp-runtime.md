# 4. The microVFP runtime

**What you'll do:** parse and run real VFP9 `.prg` source — a self-contained snippet, a file of
`PROCEDURE`/`FUNCTION` definitions, or the stored procedures embedded in a `.dbc`.

`CrossVault.FoxDbf.MicroVfp` is a **tree-walking interpreter**: it parses `.prg` text into an AST once,
then executes it against a live [`VfpSession`](03-sql-and-ado-net.md) — so table access inside your
`.prg` code (`USE`, `SEEK`, `SCAN`, `REPLACE`, …) runs on the exact same engine as everything else
in this project.

## Run a self-contained snippet

No table, no `.dbc` — just VFP language constructs. Define a `FUNCTION`, then read its result back in
C# with `Call(...)` (there is no `?`/`??` console output — see [Deviations](#deviations-from-vfp)):

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue lives here

var interp = new VfpInterpreter(new VfpSession());
interp.Execute(@"
    FUNCTION SumTo(n)
        LOCAL i, running
        running = 0
        FOR i = 1 TO n
            running = running + i
        ENDFOR
        RETURN running
    ENDFUNC
");

VfpValue result = interp.Call("SumTo", VfpValue.Integer(5));
Console.WriteLine(result.AsNumber);   // 15
```

## Load and call a procedure/function

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue lives here

using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");

var interp = new VfpInterpreter(session);
interp.LoadFile(@"C:\data\business_logic.prg");   // parses PROCEDURE/FUNCTION definitions

VfpValue result = interp.Call("CalcOrderTotal", VfpValue.Integer(1138));
Console.WriteLine(result.AsNumber);
```

`Call(name, args)` is a **by-value** call (matches `=Func(args)`/`(...)` call syntax in VFP — see
by-value vs. by-reference below). Parameters are `VfpValue` — VFP's tagged value type
(`VfpValue.Integer(...)`, `VfpValue.Character(...)`, `VfpValue.Logical(...)`, `VfpValue.Null`, …).

## Load stored procedures straight from a `.dbc`

Many real VFP9 applications keep their business logic as the database container's stored
procedures rather than a loose `.prg` file:

```csharp
var interp = new VfpInterpreter(session);   // session already has a database open
interp.LoadStoredProceduresFromDatabase();
VfpValue result = interp.Call("NewID", VfpValue.Character("orders"));
```

## Parse without executing

If you only need the AST (tooling, static analysis, validating that a `.prg` corpus parses cleanly
before running it):

```csharp
using CrossVault.FoxDbf.MicroVfp;

PrgProgram program = PrgParser.Parse(prgSourceText);
// or: PrgParser.ParseFile(@"C:\data\business_logic.prg");
```

## Search a table with `LOCATE` / `CONTINUE`

`LOCATE` positions the record pointer on the first record satisfying its `FOR` clause — in the work
area's **current order** (the master index if one is set, otherwise physical) and honouring
`SET DELETED` — and `CONTINUE` resumes that same search from the next record. There's
no `?`/`??` console, so open the table source, run the search as top-level statements and read the outcome back with
`EvalExpression`:

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

using var session = new VfpSession();
session.OpenDirectory(@"C:\data");

var interp = new VfpInterpreter(session);
interp.Execute(@"
    USE people
    LOCATE FOR AGE > 30
");

bool hit = interp.EvalExpression("FOUND()").AsLogical;      // .T. if a match exists
double rec = (double)interp.EvalExpression("RECNO()").AsNumber;   // the matched record number

interp.Execute("CONTINUE");                                 // next match, same FOR + scope window
bool more = interp.EvalExpression("FOUND()").AsLogical;
```

## Isolate work in a second data session

`CreateDataSession()` opens a fresh, isolated session (its own work areas, record pointers, locks and
session-scoped `SET`s); `SET DATASESSION TO n` switches between them, exactly like moving between two
forms with different `DataSession` properties:

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

using var session = new VfpSession();
session.OpenDirectory(@"C:\data");

var interp = new VfpInterpreter(session);

int s2 = interp.CreateDataSession();          // a second, isolated data session
interp.Execute($"SET DATASESSION TO {s2}");
interp.Execute("USE orders");                 // opens in session 2 — invisible to session 1's areas

interp.Execute("SET DATASESSION TO 1");        // back to the default session
interp.ReleaseDataSession(s2);                 // ASESSIONS()/AUSED() enumerate live sessions/areas
```

## What's implemented

- **Control flow**: `IF/ENDIF`, `DO CASE/ENDCASE`, `DO WHILE/ENDDO`, `FOR/ENDFOR`,
  `FOR EACH … IN <array>/ENDFOR`, `SCAN/ENDSCAN`, `EXIT`/`LOOP`, `RETURN`.
- **Scoping**: `LOCAL`/`PRIVATE`/`PUBLIC` with VFP's dynamic-scoping rules — a `PRIVATE` hides a
  same-named variable from an outer caller for the rest of the call chain (not lexical scoping).
  `DIMENSION`/`REDIMENSION` memory arrays (preserve existing elements on resize; new elements
  `.F.`-fill), `RELEASE`, `CLEAR MEMORY`/`CLEAR ALL`.
- **Preprocessing / macros**: `#DEFINE`/`#UNDEF`, `#IF`/`#IFDEF`/`#IFNDEF`, and generalised
  `&var` / `&var.` **macro substitution executed at runtime** (a `&`-line is expanded to the
  variable's current text and run).
- **Parameters**: `PARAMETERS`/`LPARAMETERS`, by-value (`=Func(args)`/`(...)`) vs. by-reference
  (`DO proc WITH args`) passing.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`GO`/`SKIP`,
  `LOCATE`/`CONTINUE` (full `FOR`/`WHILE` + scope, evaluated in the work area's current index order,
  with per-area `CONTINUE` state), `SET FILTER TO` (a per-work-area record-visibility predicate honoured
  by every navigation and scan — `GO TOP`/`BOTTOM`, `SKIP`, `LOCATE`/`CONTINUE`, `SCAN`, `SUM` — composed
  with `SET DELETED`/`SET KEY`; a direct `GOTO`/`RECNO()`/`RECCOUNT()` bypasses it, matching VFP; read back
  with `FILTER()`/`SET("FILTER")`), `REPLACE`/`DELETE`/`RECALL`/`INSERT`, `GATHER`/`SCATTER`,
  `APPEND FROM`/`COPY TO` (`.dbf`), `COPY MEMO … TO … [ADDITIVE]`, `PACK`, `SUM`/`TOTAL`,
  `COUNT [scope] [FOR] [WHILE] TO var` (honouring `SET DELETED`/`SET FILTER`/`SET KEY`),
  `USE … ORDER [TAG] name [OF cdx] [ASCENDING|DESCENDING]`, `CLOSE TABLES`/`DATABASES`/`ALL`,
  `BEGIN`/`END TRANSACTION`/`ROLLBACK`
  (copy-on-write), and an embedded VFP-SQL `SELECT` (routed through the same
  [SQL engine](03-sql-and-ado-net.md) as everything else).
- **Data sessions**: a **real** multi-datasession model. The host API
  `VfpInterpreter.CreateDataSession()` / `ReleaseDataSession(n)` opens and closes an isolated data
  session — the headless equivalent of a form with `DataSession = 2` (a `.prg` itself cannot create
  one, matching VFP) — and `SET DATASESSION TO n` switches among the live sessions. Each session has
  its own work areas, record pointers, orders, relations, buffering, byte-range locks and
  session-scoped `SET`s (`DELETED`/`EXACT`/…); `ASESSIONS()`/`AUSED()` enumerate them.
- **Record locking**: `RLOCK()`/`LOCK()`, `FLOCK()`, `UNLOCK`, `ISRLOCKED()`/`ISFLOCKED()` take
  **real** VFP-byte-compatible byte-range locks on the live `.dbf` (honouring `SET REPROCESS` /
  `SET MULTILOCKS`, released on close) — a stored proc that coordinates via `RLOCK` is mutually
  exclusive with a concurrent VFP client. (Only the `SET REPROCESS TO … SECONDS` wait *timing* is
  approximated — see [Deviations](#deviations-from-vfp).)
- **Low-level file I/O**: the `FOPEN`/`FCREATE`/`FREAD`/`FGETS`/`FPUTS`/`FWRITE`/`FSEEK`/`FEOF`/
  `FCLOSE`/`FFLUSH`/`FCHSIZE`/`FERROR` handle family, plus whole-file `FILETOSTR()`/`STRTOFILE()`
  and `ADIR()` listing of the session data directory.
- **Parent-child navigation**: `SET RELATION TO <key> INTO <alias>` and `SET SKIP TO` — moving the
  parent's record pointer repositions each related child; `RELATION()`/`TARGET()` read the links back.
- **Table / row buffering**: `CURSORSETPROP('Buffering', 1-5)` / `CURSORGETPROP`, `TABLEUPDATE()` /
  `TABLEREVERT()`, and `OLDVAL()`/`CURVAL()`/`GETFLDSTATE()` — a real optimistic-buffering model, not
  constants.
- **Indexing**: `INDEX ON … TAG` (structural `.cdx`), `INDEX ON … TAG … OF <cdx>` (non-structural
  `.cdx`), `INDEX ON … TO <idx>` (standalone `.idx`), `SET ORDER TO`, `SET INDEX TO` / `USE … INDEX`
  (multiple indexes per work area), `REINDEX`, `DELETE TAG`, plus introspection
  (`TAG()`/`TAGCOUNT()`/`TAGNO()`/`KEY()`/`ORDER()`/`CANDIDATE()`/`PRIMARY()`/`DESCENDING()`/`FOR()`/
  `CDX()`/`SYS(14)`/`ATAGINFO()`).
- **Error handling**: `TRY … CATCH [TO oErr] [WHEN lExpr] … FINALLY … ENDTRY` and `THROW [eExpr]` with
  VFP9 semantics (verified against the runtime): a TRY wins over `ON ERROR`, also for errors raised in
  procedures it calls; no matching `CATCH` runs `FINALLY` and passes the error outward; `oErr` exposes
  `ErrorNo`, `Message`, `LineNo`, `LineContents`, `Details`, `Procedure`, `StackLevel`, `UserValue`
  (`THROW x` raises 2071 with `UserValue = x`, a bare `THROW` in `CATCH` re-raises); inside `CATCH`,
  `ERROR()` is 0 while `MESSAGE()`/`AERROR()` describe the caught error; `EXIT` leaves the TRY structure,
  `LOOP` raises 1939 and `RETURN` 2060, as in VFP9. Plus `ON ERROR`, `AERROR()` (the full 7-column
  result-array contract), `MESSAGE()`/`ERROR()`/`LINENO()`.
- **Referential integrity**: the DBC's stored `RULE`s and the generated insert/update/delete RI
  triggers auto-fire on `INSERT`/`REPLACE`/`DELETE` through the interpreter, cascading exactly as VFP9.

## Function reference

Every function below runs as real VFP semantics, not a stub — see
[Deviations from VFP](#deviations-from-vfp) for the handful that are intentionally simplified.

**String**

| Function | Notes |
|---|---|
| `UPPER()`, `LOWER()` | |
| `TRIM()`, `LTRIM()`, `RTRIM()`, `ALLTRIM()` | |
| `LEFT()`, `RIGHT()`, `SUBSTR()` | |
| `LEN()` | |
| `AT()`, `ATC()` (case-insensitive `AT()`) | |
| `RAT()` | |
| `OCCURS()` | count of occurrences |
| `STUFF()`, `STRTRAN()` | |
| `PADL()`, `PADR()`, `PADC()` | |
| `SPACE()`, `REPLICATE()` | |
| `CHR()`, `ASC()` | |
| `CHRTRAN()` | |
| `STR()`, `STRZERO()`, `VAL()` | |
| `ISDIGIT()`, `ISALPHA()`, `ISUPPER()`, `ISLOWER()` | first-character checks, as in VFP |
| `PROPER()` | title-case each word |
| `TRANSFORM()` | format a value with a VFP `@`/picture format string |
| `STREXTRACT()` | text between two delimiters |
| `GETWORDCOUNT()`, `GETWORDNUM()` | word count / N-th word |
| `SOUNDEX()`, `DIFFERENCE()` | phonetic key / similarity |
| `STRCONV()`, `CPCONVERT()` | string / code-page conversions |
| `TEXTMERGE()` | expand `<< >>` expression bits in a template |
| `ALINES()` | split text into a memory array of lines |
| `CHRTRAN()`, `CHRTRANC()` | translate characters (the `…C` surface — see the note below) |
| `ICASE()` | first matching condition → value (an inline `CASE`) |
| `LIKE()`, `LIKEC()` | wildcard (`*`/`?`) match (the `…C` surface — see the note below) |
| `NORMALIZE()`, `ISLEADBYTE()` | string normalisation / lead-byte test (`ISLEADBYTE()` returns `.F.` — see the note below) |
| `MEMLINES()`, `MLINE()` | line count / N-th line of a memo (honours `SET MEMOWIDTH`) |
| `ATLINE()`, `ATCLINE()`, `RATLINE()` | line number of a substring (first / case-insensitive / last) |
| `STRTOFILE()`, `FILETOSTR()` | whole-string ↔ whole-file |
| `LEFTC()`, `RIGHTC()`, `SUBSTRC()`, `STUFFC()`, `AT_C()`, `ATCC()`, `RATC()`, `LENC()` | the `…C` function surface — see the note below |

> **The `…C` functions carry single-byte-code-page semantics.** `CHRTRANC()`, `LIKEC()`,
> `NORMALIZE()`, `ISLEADBYTE()` and the `…C` variants (`LEFTC`/`RIGHTC`/`SUBSTRC`/`STUFFC`/`AT_C`/
> `ATCC`/`RATC`/`LENC`) route to single-byte code paths (byte == character; `ISLEADBYTE()` always
> returns `.F.`). This matches VFP exactly on a single-byte code page; true double-byte handling on
> DBCS data (cp932/936/949/950) is **not** implemented.

**Date / time**

| Function | Notes |
|---|---|
| `DATE()`, `DATETIME()` | |
| `CTOD()`, `CTOT()`, `DTOC()`, `DTOS()`, `TTOC()` | |
| `YEAR()`, `MONTH()`, `DAY()`, `DOW()`, `CDOW()`, `CMONTH()` | |
| `GOMONTH()` | |

**Math / logic**

| Function | Notes |
|---|---|
| `ABS()`, `INT()`, `ROUND()`, `MOD()`, `MAX()`, `MIN()` | |
| `IIF()`, `BETWEEN()`, `INLIST()` | |
| `EMPTY()`, `ISNULL()`, `ISBLANK()` | emptiness / `.NULL.` / blank-field tests |
| `BLANK()` | the blank value for a field's type |
| `TYPE()` | returns a VFP type code (`C`, `N`, `L`, `D`, …) from an expression *string* |
| `VARTYPE()` | returns the type code of a *value* (no re-evaluation) |
| `EVALUATE()` / `EVAL()` | evaluates a text expression at runtime |

**Arrays**

| Function | Notes |
|---|---|
| `ALEN()` | total elements (no dimension arg), rows (dim `1`), or columns (dim `2`) |
| `AELEMENT()`, `ASUBSCRIPT()` | element number ↔ (row, col) subscript conversion |
| `ACOPY()`, `ADEL()`, `AINS()` | copy / delete / insert array elements |
| `ASORT()` | sort a memory array |
| `AFIELDS()` | fill an array with the current table's field structure |
| `ADATABASES()`, `AUSED()`, `ASESSIONS()` | open databases / work areas / data sessions into an array |

**Work area / record state** — these are the *session-aware* versions (they read the live cursor,
not a context-free default), and take precedence over any same-named generic function above:

| Function | Notes |
|---|---|
| `SELECT()`, `USED()`, `ALIAS()`, `DBUSED()` | current/ named work area, alias, whether a `.dbc` is open |
| `DBF()`, `DBC()` | current table's / current database's source path |
| `INDBC()`, `ISEXCLUSIVE()`, `ISREADONLY()` | membership / open-mode of a table or database |
| `SETFLDSTATE()`, `GETNEXTMODIFIED()` | set a field/row edit state / walk the modified rows in a buffer |
| `HEADER()`, `RECNO()`, `RECCOUNT()`, `LUPDATE()` | header size / record pointer / count / last-update date |
| `EOF()`, `BOF()`, `FOUND()`, `DELETED()` | |
| `FILTER()` | the current (or `FILTER(nArea)`) work area's `SET FILTER` expression text, VFP-normalised (`"CAT=\"A\""`) — `""` when none |
| `SEEK()`, `LOOKUP()` | function-form seek / seek-and-return-a-field |
| `RELATION()`, `TARGET()` | read back a work area's `SET RELATION` links |
| `CURSORGETPROP()`, `CURSORSETPROP()`, `GETFLDSTATE()` | the buffering model (`Buffering` 1-5, field/row state) |
| `OLDVAL()`, `CURVAL()`, `TABLEUPDATE()`, `TABLEREVERT()` | buffer snapshots / commit / discard |
| `PCOUNT()` / `PARAMETERS()` | count of arguments actually passed to the current call |
| `PROGRAM()` | current procedure name, or `n` levels up the call stack |

**Index introspection** — read the open indexes of the current work area:

| Function | Notes |
|---|---|
| `TAG()`, `TAGCOUNT()`, `TAGNO()` | tag name by position / count / number of a named tag |
| `KEY()`, `FOR()`, `ORDER()` | a tag's key / `FOR` filter / the controlling order |
| `CANDIDATE()`, `PRIMARY()`, `UNIQUE()`, `DESCENDING()` | a tag's flags |
| `CDX()`, `MDX()`, `NDX()`, `IDXCOLLATE()`, `ATAGINFO()` | `.cdx`/`.idx` path by position / collation / all tag info into an array |
| `KEYMATCH()`, `FLDLIST()` | probe whether a key exists in a tag / the current `SET FIELDS` list |

**Error handling**

| Function | Notes |
|---|---|
| `AERROR()` | fills the array with the full 7-column contract from the retained last error |
| `ERROR()`, `MESSAGE()`, `LINENO()` | |
| `ON("ERROR")` | reads back the installed `ON ERROR` handler text |

**System**

| Function | Notes |
|---|---|
| `SYS(0)` | `"<machine> # <user>"` — verified byte-for-byte against the VFP9 runtime's own format |
| `SYS(1)` | today's Julian day number |
| `SYS(2007, cExpr)` | CRC-16/CCITT checksum |
| `SYS(2015)` | a unique procedure/object name (`_` + 9 hex chars) |
| `SYS(14, n)`, `SYS(2021, n)` | the KEY / `FOR` filter of the N-th open index (all-caps) |
| `SYS(21)`, `SYS(22)` | controlling-index number / name (legacy `TAGNO()`/`ORDER()`) |
| `SYS(2029)` | the DBF header's version/type byte as a decimal string (a VFP table is `"48"`) |
| other `SYS(n)` codes | return `""` — see deviations |
| `SECONDS()` | |
| `COCREATEGUID()` | |
| `TXNLEVEL()` | transaction nesting depth |
| `CPCURRENT()`, `CPDBF()`, `CPCONVERT()`, `STRCONV()` | current / table code page, code-page conversion |
| `RLOCK()`/`LOCK()`, `FLOCK()`, `UNLOCK`, `ISRLOCKED()`/`ISFLOCKED()` | **real** VFP-byte-compatible byte-range locks on the live `.dbf` (`SET REPROCESS`/`SET MULTILOCKS`), released on close — only the `REPROCESS` wait *timing* is approximated (see deviations) |
| `MESSAGEBOX()`, `AFONT()` | GUI-bound stubs — see deviations |

**Low-level file I/O** — byte/line handles onto arbitrary files, plus directory listing:

| Function | Notes |
|---|---|
| `FOPEN()`, `FCREATE()`, `FCLOSE()` | open / create / close a file handle |
| `FREAD()`, `FGETS()`, `FWRITE()`, `FPUTS()` | read/write bytes or a line |
| `FSEEK()`, `FEOF()`, `FCHSIZE()`, `FFLUSH()`, `FERROR()` | position / EOF / truncate / flush / last error |
| `FILETOSTR()`, `STRTOFILE()` | whole-file read / write in one call |
| `ADIR()` | fill an array with the **session data directory**'s file entries (name skeleton only — see deviations) |

## Deviations from VFP

microVFP runs real VFP9 `.prg` source, but it is a **headless interpreter for
business-logic stored procedures** — not a drop-in replacement for the full VFP9 IDE/runtime. The
differences below are deliberate, verified simplifications, not bugs to be worked around:

- **`?`/`??` display output is not implemented.** There is no console to print to, so a `? expr` /
  `?? expr` line parses but produces nothing (it is on the headless allow-list below — no output, no
  exception). To observe a value, return it from a `FUNCTION`/`PROCEDURE` and read the
  `Call(...)` result in C# (as the snippets above do), or evaluate it with
  `interp.EvalExpression("expr")`.
- **Only the lock *wait timing* is approximated.** `RLOCK()`/`LOCK()`/`FLOCK()`/`UNLOCK`/
  `ISRLOCKED()`/`ISFLOCKED()` now take **real** VFP-byte-compatible byte-range locks on the live
  `.dbf` (see [What's implemented](#whats-implemented) and
  [2. Writing and indexes](02-writing-and-indexes.md#vfp-compatible-locking)) — a stored proc that
  coordinates via `RLOCK` is genuinely mutually exclusive with a concurrent VFP9 client, verified
  against one. The single remaining simplification is the `SET REPROCESS TO … SECONDS` **retry
  timing**: a headless library will not block a thread indefinitely the way an interactive VFP
  session parks the UI, so the wait/retry cadence on a *contended* lock is approximate — the grant /
  deny outcome and the byte ranges themselves are real.
- **A statement microVFP cannot execute raises an error when it runs** (since 0.11; it used to be
  skipped silently). Such a line still *parses*, so a whole `.prg` or DBC container loads even if an
  unused procedure contains an exotic command; executing it raises a catchable VFP error that `ON ERROR`
  and `TRY…CATCH` see like any other:

  | Situation | Error |
  |---|---|
  | unknown command verb (incl. 4-letter abbreviations such as `REPL`, and VFP verbs microVFP does not model, e.g. `UPDATE`-SQL, `QUIT`, `DEFINE CLASS`) | 16 "Unrecognized command verb." |
  | a modelled verb with a clause list microVFP cannot parse (`COPY garbage`, `CLOSE nonsense`) | 36 "Command contains unrecognized phrase/keyword." |
  | `DO name` of a procedure that is not loaded | 1 "File 'name.prg' does not exist." |
  | an expression the expression engine cannot parse (used to evaluate to `.NULL.`) | 10 "Syntax error." |
  | an embedded `SELECT`/`INSERT`/`CREATE` the SQL parser cannot parse | the parser's error (10 when it carries no number) |

  `MicroVfpRuntimeException.Message` stays VFP's exact text (what `MESSAGE()` returns);
  `MicroVfpRuntimeException.Statement` and `.LineNo` carry the offending source for the host.
  The **headless allow-list** — commands that only drive the interactive UI or console and are
  deliberately ignored — is: `?`/`??`/`???`, `@ … SAY/GET/…`, `ACTIVATE`/`DEACTIVATE`/`HIDE`/`SHOW`/
  `MOVE`/`ZOOM`, `DEFINE WINDOW/MENU/POPUP/BAR/PAD/BOX`, `PUSH`/`POP`, `KEYBOARD`, `MOUSE`, `READ`,
  `DOEVENTS`, `BROWSE`/`EDIT`/`CHANGE`/`MODIFY`, `ASSERT`, `DEBUG`/`DEBUGOUT`/`SUSPEND`, `EXTERNAL`,
  the event traps `ON KEY`/`ON ESCAPE`/`ON SHUTDOWN`/… (`ON ERROR` is modelled), `DECLARE … IN <dll>`
  (DLL imports; `CoCreateGuid` is a native built-in), `DISPLAY`/`LIST` forms that only paint the
  screen (a `TO FILE` form must be modelled or it raises), `CLOSE ALTERNATE/DEBUGGER/FORMAT/MEMO/
  PRINTER/PROCEDURE`, and unrecognised `SET` settings (`SET TALK`, `SET COMPATIBLE`, …). The real
  stored-procedure corpus the project is tested against uses none of the raising forms.
  Unknown *functions* are not covered yet: they still evaluate to `.NULL.`.
- **`CLOSE TABLES`/`CLOSE DATABASES`/`CLOSE ALL` close every table of the current data session** but
  leave the session's database container attached (the host owns it).
- **The genuinely GUI-bound functions are stubs**, because a headless interpreter has no window,
  screen or font metrics to answer them: `MESSAGEBOX()` always returns `6` (`IDYES`), `AFONT()`
  returns an empty font list, and `TXTWIDTH()`/font-extent style queries have no pixel geometry.
  Unimplemented `SYS(n)` codes still return `""` rather than their real per-code VFP9 behavior.
  (`LOCATE`/`CONTINUE`, real locking, `DBC()`, `CURSORGETPROP()`/`GETFLDSTATE()`, the low-level file
  I/O family all used to be on the deviations list — they are now fully implemented; see
  [What's implemented](#whats-implemented).)
- **`ADIR()` lists the session data directory only.** It enumerates the directory the session was
  opened on, filtered by the name skeleton; a path-qualified skeleton (e.g.
  `ADIR(a, 'C:\other\*.dbf')`) is not supported.

What is still out: the **form/class/visual object model**, **`.mem`
variable-file interop** (`SAVE TO`/`RESTORE FROM` work in-process, but not the on-disk `.mem`
format), **non-DBF import/export formats** (Excel/other office file types), the **GUI-bound
functions** above, **view buffering**, and **multi-user optimistic conflict detection** (a buffered
`TABLEUPDATE()` is not diffed against a concurrent writer, so cross-writer optimistic conflicts are
not detected). Most of what the data side of a real `.prg` business-logic stored procedure touches
— tables, indexes, table/row buffering, transactions, RI, locking, sessions, low-level file I/O — is
implemented; the goal is correctly running real-world stored procedures, not 100% coverage of the
IDE-facing language surface.

## Performance and limits

### `INSERT` fast path and deferred visibility

A plain autocommit `INSERT … VALUES` into an open, unbuffered table takes a **direct-append fast
path**: the row is appended straight through microVFP's cached writer (which keeps the structural
`.cdx` current) instead of re-opening the table for every row. A tight `INSERT` loop therefore runs in
roughly O(*rows*) rather than paying a per-row open + reopen.

To keep the loop cheap, the **read-view refresh is deferred**. The appended rows are flushed and the
work area's read view is reopened **lazily, at the first following statement that is not another
`INSERT`** — the moment something actually reads the table. In practice you never see the seam: by the
time a `RECCOUNT()`, `RECNO()`, `EOF()`, a field read, a `GO`/`SKIP`/`SEEK`, or a `SCAN` runs, the new
rows are already visible and already seekable through the maintained `.cdx`. Read-your-writes holds
within the session — the deferral is an internal optimization, not an observable delay.

The fast path is taken **only** for that plain case. It transparently **falls back to the classic
per-row path** — with byte-identical table state, index state and error numbers — whenever any of the
following applies:

| Condition | Why it falls back |
|---|---|
| the target work area is **buffered** (`CURSORSETPROP('Buffering', 2–5)`) | the row must land in the row/table buffer, not straight on disk |
| the table carries a **`CANDIDATE`** tag | a post-append duplicate re-check is required |
| **referential-integrity enforcement** is on for the target | the bound insert trigger has to fire on the new row |
| an open **`BEGIN TRANSACTION`** copy-on-write redirect is active | the write must land on the transaction's private copy, never straddle the commit swap |
| **`INSERT … FROM ARRAY` / `FROM MEMVAR`** (not a `VALUES` list) | not a direct-values source |
| a structural anomaly — unknown column, or value/column count mismatch | the error must surface exactly as the classic path raises it |

Because every one of these reproduces the pre-fast-path behaviour exactly, the fast path is a pure
throughput win for the common `INSERT … VALUES` loop and changes nothing you can observe otherwise.

### Recursion / call-nesting depth

microVFP caps interpreter call nesting — `PROCEDURE`/`FUNCTION` calls and RI-cascade chains — at **48
levels**, and raises a **catchable error 1809** ("Maximum call nesting depth exceeded — probable
unbounded recursion") when the cap is passed. An accidental infinite recursion in a stored procedure
therefore surfaces as an ordinary trappable VFP error that your `ON ERROR` handler or a `TRY…CATCH`
can catch — not a process crash.

This is deliberately **more robust than VFP9**, which permits deeper `DO` nesting (up to 128 levels)
but faults **uncatchably** on runaway recursion: a native stack overflow that takes the whole runtime
down before any trappable error can fire. A tree-walking interpreter spends many native stack frames
per logical call and has no safe way to ride that stack cliff, so it stops short and throws a normal
error instead. Real-world RI cascades and stored-procedure call chains are only single-digit deep, so
the 48-level ceiling never interferes with legitimate code — it exists solely to turn a would-be crash
into a catchable error.

## Next

[5. The Highlike accelerator →](05-highlike-accelerator.md)
