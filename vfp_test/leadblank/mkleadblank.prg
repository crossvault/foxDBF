* Golden master for leading / trailing blanks in a C(10) field (run by hand in VFP9, then commit lb.dbf + lb.txt):
* 12 values with and without leading blanks, needles A, " A", x, " x", "  x", CHR(193), " "+CHR(193), " ", Ab and a padded
* "A"; per line COLLATE (tag + SET COLLATE), EXACT, [needle], operator, then the matching recnos of a plain scan
* (SET OPTIMIZE OFF), of Rushmore over the tag, of SQL WHERE under SET ANSI OFF and under SET ANSI ON.
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
SET DEFAULT TO (JUSTPATH(SYS(16)))
LOCAL lcOut, i, j, k, lcN, lcOp, lcF, lcR, lcColl, lcEx, lcOpt, lnCnt
lcOut = ''
TRY
CREATE TABLE lb FREE CODEPAGE=1252 (name C(10))
LOCAL laV[12]
laV[1] = '  x'
laV[2] = ' x'
laV[3] = 'x'
laV[4] = ' ' + CHR(193)
laV[5] = 'A'
laV[6] = CHR(193)
laV[7] = ' A'
laV[8] = 'Ab'
laV[9] = ' '
laV[10] = ' Ab'
laV[11] = 'a'
laV[12] = ' a'
FOR i = 1 TO 12
  INSERT INTO lb VALUES (laV[i])
ENDFOR
LOCAL laN[10]
laN[1] = 'A'
laN[2] = ' A'
laN[3] = 'x'
laN[4] = ' x'
laN[5] = '  x'
laN[6] = CHR(193)
laN[7] = ' ' + CHR(193)
laN[8] = ' '
laN[9] = 'Ab'
laN[10] = 'A         '
LOCAL laOp[6]
laOp[1] = '='
laOp[2] = '=='
laOp[3] = '<'
laOp[4] = '<='
laOp[5] = '>'
laOp[6] = '>='
* field value as seen by expressions
GO 4
lcOut = lcOut + 'LEN=' + TRANSFORM(LEN(name)) + ' val=[' + name + '] lt=[' + LTRIM(name) + ']' + CHR(13)+CHR(10)
FOR k = 1 TO 2
  lcColl = IIF(k = 1, 'MACHINE', 'GENERAL')
  SET COLLATE TO "&lcColl"
  DELETE TAG ALL
  INDEX ON name TAG k
  FOR j = 1 TO 2
    lcEx = IIF(j = 1, 'OFF', 'ON')
    SET EXACT &lcEx
    FOR i = 1 TO 10
      lcN = laN[i]
      FOR m = 1 TO 6
        lcOp = laOp[m]
        lcF = 'name ' + lcOp + ' lcN'
        * plain scan, no optimization
        SET ORDER TO 0
        SET OPTIMIZE OFF
        lcR = ''
        SCAN FOR &lcF
          lcR = lcR + TRANSFORM(RECNO()) + ' '
        ENDSCAN
        lcS = lcR
        * Rushmore through the tag
        SET OPTIMIZE ON
        lcR = ''
        SCAN FOR &lcF
          lcR = lcR + TRANSFORM(RECNO()) + ' '
        ENDSCAN
        lcI = lcR
        * SQL SELECT (Rushmore on), ANSI OFF / ON
        SET ANSI OFF
        lcR = ''
        SELECT RECNO() AS r FROM lb WHERE &lcF INTO ARRAY laR
        IF _TALLY > 0
          FOR q = 1 TO ALEN(laR, 1)
            lcR = lcR + TRANSFORM(laR[q]) + ' '
          ENDFOR
        ENDIF
        lcQ0 = lcR
        SET ANSI ON
        lcR = ''
        SELECT RECNO() AS r FROM lb WHERE &lcF INTO ARRAY laR
        IF _TALLY > 0
          FOR q = 1 TO ALEN(laR, 1)
            lcR = lcR + TRANSFORM(laR[q]) + ' '
          ENDFOR
        ENDIF
        lcQ1 = lcR
        SET ANSI OFF
        lcOut = lcOut + lcColl + ' EXACT ' + lcEx + ' [' + lcN + '] ' + lcOp + ' | scan: ' + lcS + '| rush: ' + lcI + '| sqlAnsiOff: ' + lcQ0 + '| sqlAnsiOn: ' + lcQ1 + CHR(13) + CHR(10)
      ENDFOR
    ENDFOR
  ENDFOR
ENDFOR
SET EXACT OFF
USE
USE lb EXCLUSIVE
DELETE TAG ALL
USE
STRTOFILE(lcOut, 'lb.txt')
STRTOFILE('OK', 'done.txt')
CATCH TO oErr
STRTOFILE('ERR ' + oErr.Message + ' line ' + TRANSFORM(oErr.LineNo), 'done.txt')
ENDTRY
QUIT
