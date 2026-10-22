* Regenerates the expression-tag CDX fixture read by CdxExpressionKeyTypeTests.
* Run headless: vfp9.exe -T <absolute path to this file>.
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
LOCAL lcDir
lcDir = ADDBS(JUSTPATH(SYS(16)))
TRY
   CREATE TABLE (lcDir + "exprtags.dbf") FREE (id I, cust C(6), no N(4), d D, amt N(8,2))
   INSERT INTO exprtags VALUES (1, "alfki", 7, {^2020-01-02}, 10.50)
   INSERT INTO exprtags VALUES (2, "Bonap", 12, {^2021-03-04}, -5.25)
   INSERT INTO exprtags VALUES (3, "chops", 3, {^2019-12-31}, 99.99)
   INSERT INTO exprtags VALUES (4, "ALFKI", 12, {^2020-01-02}, 0.00)
   INDEX ON id TAG tid
   INDEX ON id+1 TAG tidp1
   INDEX ON cust+STR(no,4) TAG tcs
   INDEX ON DTOS(d) TAG tds
   INDEX ON UPPER(cust) TAG tup
   INDEX ON no*2 TAG tno2
   INDEX ON YEAR(d) TAG tyr
   INDEX ON d TAG td
   INDEX ON amt*2 TAG tamt2
   INDEX ON d+1 TAG tdp1
   SET COLLATE TO "GENERAL"
   INDEX ON UPPER(cust) TAG tupg
   SET COLLATE TO "MACHINE"
   STRTOFILE("ID+1=" + TYPE("id+1") + CHR(13)+CHR(10) + "NO*2=" + TYPE("no*2") + CHR(13)+CHR(10) + ;
      "YEAR(D)=" + TYPE("YEAR(d)") + CHR(13)+CHR(10) + "AMT*2=" + TYPE("amt*2") + CHR(13)+CHR(10) + ;
      "D+1=" + TYPE("d+1") + CHR(13)+CHR(10), lcDir + "types.txt", 0)
   USE
   STRTOFILE("DONE", lcDir + "mkexprtags_done.txt", 0)
CATCH
   STRTOFILE("ERR", lcDir + "mkexprtags_done.txt", 0)
ENDTRY
QUIT