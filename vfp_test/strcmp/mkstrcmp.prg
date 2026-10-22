* Golden master for VFP9 string comparison (run by hand in VFP9, then commit the output):
*   strcmp.txt - every pair of pairs.txt (CP1252, "left<TAB>right") under MACHINE / GENERAL / SPANISH, SET EXACT
*                OFF / ON and the operators = == < <= > >= <>; per line COLLATE, EXACT, left, right, operator and three
*                results T/F: the Xbase expression, SQL WHERE under SET ANSI OFF, SQL WHERE under SET ANSI ON.
*   fn.txt     - INLIST / BETWEEN / EMPTY / ASCAN / $ / MAX / ISBLANK / LEN on a C(10) field ('A', 'Ab', blank).
SET TALK OFF
SET SAFETY OFF
SET DEFAULT TO (JUSTPATH(SYS(16)))
LOCAL lcOut, lcFn, i, j, k, m, lcOp, lnN, lcX, lcA0, lcA1, lcColl, lcEx
PRIVATE pL, pR
lcOut = ''
lcFn = ''
TRY
CREATE CURSOR one (x I)
INSERT INTO one VALUES (1)
lnN = ALINES(laP, FILETOSTR('pairs.txt'), 0, CHR(13) + CHR(10))
LOCAL laOp[7]
laOp[1] = '='
laOp[2] = '=='
laOp[3] = '<'
laOp[4] = '<='
laOp[5] = '>'
laOp[6] = '>='
laOp[7] = '<>'
FOR k = 1 TO 3
  lcColl = ICASE(k = 1, 'MACHINE', k = 2, 'GENERAL', 'SPANISH')
  SET COLLATE TO "&lcColl"
  FOR j = 1 TO 2
    lcEx = IIF(j = 1, 'OFF', 'ON')
    SET EXACT &lcEx
    FOR i = 1 TO lnN
      IF AT(CHR(9), laP[i]) = 0
        LOOP
      ENDIF
      pL = LEFT(laP[i], AT(CHR(9), laP[i]) - 1)
      pR = SUBSTR(laP[i], AT(CHR(9), laP[i]) + 1)
      FOR m = 1 TO 7
        lcOp = laOp[m]
        lcX = IIF(EVALUATE('pL ' + lcOp + ' pR'), 'T', 'F')
        SET ANSI OFF
        SELECT x FROM one WHERE pL &lcOp pR INTO ARRAY laR
        lcA0 = IIF(_TALLY > 0, 'T', 'F')
        SET ANSI ON
        SELECT x FROM one WHERE pL &lcOp pR INTO ARRAY laR
        lcA1 = IIF(_TALLY > 0, 'T', 'F')
        SET ANSI OFF
        lcOut = lcOut + lcColl + CHR(9) + lcEx + CHR(9) + pL + CHR(9) + pR + CHR(9) + lcOp + CHR(9) + lcX + lcA0 + lcA1 + CHR(13) + CHR(10)
      ENDFOR
    ENDFOR
  ENDFOR
ENDFOR
SET EXACT OFF
SET COLLATE TO "MACHINE"
CREATE CURSOR fw (name C(10))
INSERT INTO fw VALUES ('A')
INSERT INTO fw VALUES ('Ab')
INSERT INTO fw VALUES ('')
LOCAL laA[2]
laA[1] = 'Ab'
laA[2] = 'X'
FOR j = 1 TO 2
  lcEx = IIF(j = 1, 'OFF', 'ON')
  SET EXACT &lcEx
  SCAN
    lcFn = lcFn + lcEx + CHR(9) + TRANSFORM(RECNO()) + CHR(9) + ;
      IIF(INLIST(name, 'A'), 'T', 'F') + IIF(INLIST(name, 'A         '), 'T', 'F') + IIF(INLIST('A', name), 'T', 'F') + ;
      IIF(BETWEEN(name, 'A', 'A'), 'T', 'F') + IIF(BETWEEN(name, 'Ab', 'Ab'), 'T', 'F') + IIF(EMPTY(name), 'T', 'F') + ;
      IIF(ASCAN(laA, 'A') > 0, 'T', 'F') + IIF(ASCAN(laA, name) > 0, 'T', 'F') + IIF(name $ 'Ab        ', 'T', 'F') + ;
      IIF('A' $ name, 'T', 'F') + IIF(MAX(name, 'Ab') == name, 'T', 'F') + IIF(ISBLANK(name), 'T', 'F') + ;
      IIF(name = '', 'T', 'F') + CHR(9) + TRANSFORM(LEN(name + 'x')) + CHR(13) + CHR(10)
  ENDSCAN
ENDFOR
SET EXACT OFF
USE IN fw
USE IN one
STRTOFILE(lcOut, 'strcmp.txt')
STRTOFILE(lcFn, 'fn.txt')
STRTOFILE('OK', 'done.txt')
CATCH TO oErr
STRTOFILE('ERR ' + oErr.Message + ' line ' + TRANSFORM(oErr.LineNo), 'done.txt')
ENDTRY
QUIT
