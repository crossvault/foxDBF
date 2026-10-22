* Regenerates the field-descriptor fixtures (flags, upper-cased names) compared by DbfWriterFieldFlagsVfp9Tests.
* Run headless: vfp9.exe -T <absolute path to this file>. Save this file as CP1252.
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
LOCAL lcDir
lcDir = ADDBS(JUSTPATH(SYS(16)))
TRY
   CREATE TABLE (lcDir + "alltypes.dbf") FREE (fc C(5), fn N(5,2), ff F(5,2), fi I, fb B(2), fy Y, fd D, ft T, fl L, fm M, fg G, fq Q(5), fv V(5), fw W)
   USE
   CREATE TABLE (lcDir + "nocp.dbf") FREE (cc C(5) NOCPTRANS, mm M NOCPTRANS, vv V(5) NOCPTRANS)
   USE
   CREATE TABLE (lcDir + "nulls.dbf") FREE (nc C(5) NULL, ni I NULL, nt T NULL, ny Y NULL)
   USE
   CREATE TABLE (lcDir + "autoinc.dbf") FREE (ai I AUTOINC, plain I)
   USE
   CREATE TABLE (lcDir + "names.dbf") FREE (lowcase C(5), MixedCase C(5), razónsoc C(5), œuvre C(5), ÿes C(5))
   USE
   STRTOFILE("DONE", lcDir + "mkfieldflags_done.txt", 0)
CATCH
   STRTOFILE("ERR", lcDir + "mkfieldflags_done.txt", 0)
ENDTRY
QUIT