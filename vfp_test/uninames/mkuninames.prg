* Regenerates the non-ASCII field-name fixtures (CP1252) read by UnicodeFieldNameFixtureTests.
* Run headless: vfp9.exe -T <absolute path to this file>. Save this file as CP1252.
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
LOCAL lcDir
lcDir = ADDBS(JUSTPATH(SYS(16)))
TRY
   CREATE TABLE (lcDir + "unifree.dbf") FREE (razónSoc C(10), id I)
   INSERT INTO unifree VALUES ("ACME", 1)
   INSERT INTO unifree VALUES ("Peñarol", 2)
   USE
   * Indexed copy (kept separate so unifree stays index-free): tag names and KEY/FOR text are stored in
   * the TABLE code page. ŠUP (0x8A) sorts before ÀID (0xC0) in the byte-ordered tag directory.
   CREATE TABLE (lcDir + "uniidx.dbf") FREE (razónSoc C(10), id I)
   INSERT INTO uniidx VALUES ("Peñarol", 2)
   INSERT INTO uniidx VALUES ("ACME", 1)
   INDEX ON razónSoc TAG razón
   INDEX ON UPPER(razónSoc) TAG ŠUP FOR razónSoc <> "X"
   INDEX ON id TAG ÀID
   USE
   * The same in a CP850 table: 'Ó' is 0xE0 and 'ó' is 0xA2 there (CP1252: 0xD3 / 0xF3).
   CREATE TABLE (lcDir + "uni850.dbf") FREE CODEPAGE=850 (razónSoc C(10), id I)
   INSERT INTO uni850 VALUES ("Peñarol", 2)
   INDEX ON razónSoc TAG razón
   USE
   CREATE DATABASE (lcDir + "uninames")
   CREATE TABLE (lcDir + "clientes.dbf") (razónSocial C(20), información C(10), id I)
   INSERT INTO clientes VALUES ("ACME S.A.", "uno", 1)
   INSERT INTO clientes VALUES ("Peñarol S.A.", "dos", 2)
   USE
   CLOSE DATABASES ALL
   STRTOFILE("DONE", lcDir + "mkuninames_done.txt", 0)
CATCH
   STRTOFILE("ERR", lcDir + "mkuninames_done.txt", 0)
ENDTRY
QUIT