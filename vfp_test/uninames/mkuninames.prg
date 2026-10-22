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