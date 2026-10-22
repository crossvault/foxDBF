USE fw EXCLUSIVE
* --- the default SET EXACT (VFP: OFF) and a comparison it decides
=FPUTS(lnH, 'D.exact=' + SET('EXACT'))
LOCATE FOR name = 'ab'
=FPUTS(lnH, 'D.locab=' + TRANSFORM(RECNO()))
FOR lnE = 1 TO 2
  IF lnE = 1
    SET EXACT OFF
  ELSE
    SET EXACT ON
  ENDIF
  lcE = IIF(lnE = 1, 'OFF', 'ON')
  SCAN
    lcP = 'E' + lcE + '.R' + TRANSFORM(RECNO()) + '.'
    =FPUTS(lnH, lcP + 'len=' + TRANSFORM(LEN(name)))
    =FPUTS(lnH, lcP + 'val=[' + name + '][' + code + ']')
    =FPUTS(lnH, lcP + 'eqlit=' + TRANSFORM(name = 'abc'))
    =FPUTS(lnH, lcP + 'eqeqlit=' + TRANSFORM(name == 'abc'))
    =FPUTS(lnH, lcP + 'liteq=' + TRANSFORM('abc' = name))
    =FPUTS(lnH, lcP + 'eqpad=' + TRANSFORM(name = 'abc       '))
    =FPUTS(lnH, lcP + 'eqeqpad=' + TRANSFORM(name == PADR('abc', 10)))
    =FPUTS(lnH, lcP + 'eqlb=' + TRANSFORM(name = '  abc'))
    =FPUTS(lnH, lcP + 'namecode=' + TRANSFORM(name = code))
    =FPUTS(lnH, lcP + 'codename=' + TRANSFORM(code = name))
    =FPUTS(lnH, lcP + 'nameeqeqcode=' + TRANSFORM(name == code))
    =FPUTS(lnH, lcP + 'namenm=' + TRANSFORM(name = nm))
    =FPUTS(lnH, lcP + 'nmname=' + TRANSFORM(nm = name))
    =FPUTS(lnH, lcP + 'eqempty=' + TRANSFORM(name = ''))
    =FPUTS(lnH, lcP + 'emptyeq=' + TRANSFORM('' = name))
    =FPUTS(lnH, lcP + 'empty=' + TRANSFORM(EMPTY(name)))
    =FPUTS(lnH, lcP + 'lt=' + TRANSFORM(name < 'abc'))
    =FPUTS(lnH, lcP + 'gt=' + TRANSFORM(name > 'abc'))
    =FPUTS(lnH, lcP + 'inlist=' + TRANSFORM(INLIST(name, 'abc', 'zz')))
    =FPUTS(lnH, lcP + 'between=' + TRANSFORM(BETWEEN(name, 'abc', 'abd')))
    =FPUTS(lnH, lcP + 'dollar=' + TRANSFORM(name $ 'xx abc       yy'))
    =FPUTS(lnH, lcP + 'cat=[' + name + '|' + code + ']')
    =FPUTS(lnH, lcP + 'ltrimlen=' + TRANSFORM(LEN(LTRIM(name))))
    =FPUTS(lnH, lcP + 'right3=[' + RIGHT(name, 3) + ']')
    lcV = name
    =FPUTS(lnH, lcP + 'memlen=' + TRANSFORM(LEN(lcV)))
    =FPUTS(lnH, lcP + 'memeqlit=' + TRANSFORM(lcV = 'abc'))
  ENDSCAN
  lnC = 0
  SCAN FOR name = 'ab'
    lnC = lnC + 1
  ENDSCAN
  =FPUTS(lnH, 'E' + lcE + '.countab=' + TRANSFORM(lnC))
  lnC = 0
  SCAN FOR name == 'abc'
    lnC = lnC + 1
  ENDSCAN
  =FPUTS(lnH, 'E' + lcE + '.counteqeq=' + TRANSFORM(lnC))
  lnC = 0
  SCAN FOR name == PADR('abc', 10)
    lnC = lnC + 1
  ENDSCAN
  =FPUTS(lnH, 'E' + lcE + '.counteqeqpad=' + TRANSFORM(lnC))
  lnC = 0
  SCAN FOR name = code
    lnC = lnC + 1
  ENDSCAN
  =FPUTS(lnH, 'E' + lcE + '.countnamecode=' + TRANSFORM(lnC))
  LOCATE FOR name = 'ab'
  =FPUTS(lnH, 'E' + lcE + '.locab=' + TRANSFORM(RECNO()))
  LOCATE FOR name = 'abc'
  =FPUTS(lnH, 'E' + lcE + '.locabc=' + TRANSFORM(RECNO()))
ENDFOR
* --- SEEK / KEYMATCH
INDEX ON name TAG tn
INDEX ON code + name TAG tc
FOR lnE = 1 TO 2
  IF lnE = 1
    SET EXACT OFF
  ELSE
    SET EXACT ON
  ENDIF
  lcE = IIF(lnE = 1, 'OFF', 'ON')
  SET ORDER TO tn
  DIMENSION laN[6]
  laN[1] = 'abc'
  laN[2] = '  abc'
  laN[3] = 'ab'
  laN[4] = 'abc       '
  laN[5] = 'abcdefghijkl'
  laN[6] = 'abcdef'
  FOR lnI = 1 TO 6
    SEEK laN[lnI]
    =FPUTS(lnH, 'S' + lcE + '.n' + TRANSFORM(lnI) + '=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()))
    =FPUTS(lnH, 'S' + lcE + '.km' + TRANSFORM(lnI) + '=' + TRANSFORM(KEYMATCH(laN[lnI])))
  ENDFOR
  GO 1
  lcK = name
  SEEK lcK
  =FPUTS(lnH, 'S' + lcE + '.fieldneedle=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()))
  GO 5
  lcK = name
  SEEK lcK
  =FPUTS(lnH, 'S' + lcE + '.fieldneedle5=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()))
  SET ORDER TO tc
  SEEK 'abc' + 'abc'
  =FPUTS(lnH, 'S' + lcE + '.comp1=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()))
  SEEK PADR('abc', 6) + 'abc'
  =FPUTS(lnH, 'S' + lcE + '.comp2=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()))
  GO 3
  lcK = code + name
  SEEK lcK
  =FPUTS(lnH, 'S' + lcE + '.comp3=' + TRANSFORM(FOUND()) + '/' + TRANSFORM(RECNO()) + '/' + TRANSFORM(LEN(lcK)))
ENDFOR
SET ORDER TO
* --- SET RELATION from the C(10) field into a C(6) tag (fwc: 'abcdef', 'abc', 'ab')
SELECT 0
USE fwc EXCLUSIVE
INDEX ON k TAG k
SELECT fw
SET RELATION TO name INTO fwc
FOR lnE = 1 TO 2
  IF lnE = 1
    SET EXACT OFF
  ELSE
    SET EXACT ON
  ENDIF
  lcE = IIF(lnE = 1, 'OFF', 'ON')
  FOR lnI = 1 TO 6
    GO lnI
    =FPUTS(lnH, 'L' + lcE + '.R' + TRANSFORM(lnI) + '=' + TRANSFORM(RECNO('fwc')) + '/' + TRANSFORM(EOF('fwc')))
  ENDFOR
ENDFOR
SET RELATION TO
SET RELATION TO LEFT(name, 6) INTO fwc
FOR lnE = 1 TO 2
  IF lnE = 1
    SET EXACT OFF
  ELSE
    SET EXACT ON
  ENDIF
  lcE = IIF(lnE = 1, 'OFF', 'ON')
  FOR lnI = 1 TO 6
    GO lnI
    =FPUTS(lnH, 'M' + lcE + '.R' + TRANSFORM(lnI) + '=' + TRANSFORM(RECNO('fwc')) + '/' + TRANSFORM(EOF('fwc')))
  ENDFOR
ENDFOR
SET RELATION TO
SELECT fwc
USE
SELECT fw
SET EXACT OFF
* --- TOTAL
TOTAL ON name TO fwtot FIELDS n
SELECT 0
USE fwtot
=FPUTS(lnH, 'T.count=' + TRANSFORM(RECCOUNT()))
SCAN
  =FPUTS(lnH, 'T.R' + TRANSFORM(RECNO()) + '=[' + name + ']' + TRANSFORM(n) + '/' + TRANSFORM(LEN(name)))
ENDSCAN
USE
SELECT fw
* --- SCATTER
GO 2
SCATTER MEMVAR
=FPUTS(lnH, 'SC.mlen=' + TRANSFORM(LEN(m.name)) + '/' + TRANSFORM(LEN(m.code)))
SCATTER TO laS
=FPUTS(lnH, 'SC.alen=' + TRANSFORM(LEN(laS[1])))
SCATTER MEMVAR BLANK
=FPUTS(lnH, 'SC.blank=[' + m.name + ']' + TRANSFORM(LEN(m.name)) + '/' + TRANSFORM(LEN(m.code)))
SCATTER TO laB BLANK
=FPUTS(lnH, 'SC.ablank=' + TRANSFORM(LEN(laB[1])))
* --- REPLACE
GO 1
REPLACE name WITH 'abcdefghijklmno'
=FPUTS(lnH, 'R.long=[' + name + ']' + TRANSFORM(LEN(name)))
REPLACE name WITH 'zz'
=FPUTS(lnH, 'R.short=[' + name + ']' + TRANSFORM(LEN(name)) + '/' + TRANSFORM(name == 'zz'))
REPLACE name WITH name + 'q'
=FPUTS(lnH, 'R.selfcat=[' + name + ']')
REPLACE name WITH '  x'
=FPUTS(lnH, 'R.lead=[' + name + ']')
SCAN
  REPLACE nm WITH ALLTRIM(nm) + '!'
ENDSCAN
SCAN
  =FPUTS(lnH, 'R.all' + TRANSFORM(RECNO()) + '=[' + nm + ']')
ENDSCAN
SCAN
  REPLACE code WITH code + 'Z'
ENDSCAN
GO 5
=FPUTS(lnH, 'R.code5=[' + code + ']')
GO 1
=FPUTS(lnH, 'R.code1=[' + code + ']')
* --- buffering
SET MULTILOCKS ON
=CURSORSETPROP('Buffering', 5, 'fw')
GO 2
REPLACE name WITH 'qq'
=FPUTS(lnH, 'B.edit=[' + name + ']' + TRANSFORM(LEN(name)))
=FPUTS(lnH, 'B.old=[' + OLDVAL('name') + ']' + TRANSFORM(LEN(OLDVAL('name'))))
=FPUTS(lnH, 'B.cur=[' + CURVAL('name') + ']' + TRANSFORM(LEN(CURVAL('name'))))
GO 3
=FPUTS(lnH, 'B.unedited=[' + name + ']' + TRANSFORM(LEN(name)))
INSERT INTO fw (n) VALUES (7)
GO BOTTOM
=FPUTS(lnH, 'B.app=[' + name + ']' + TRANSFORM(LEN(name)) + '/' + TRANSFORM(name == SPACE(10)) + '/' + TRANSFORM(EMPTY(name)) + '/' + TRANSFORM(n))
REPLACE code WITH 'k'
=FPUTS(lnH, 'B.appcode=[' + code + ']' + TRANSFORM(LEN(code)) + '/[' + name + ']')
=TABLEREVERT(.T., 'fw')
=CURSORSETPROP('Buffering', 1, 'fw')
* --- INSERT unbuffered
INSERT INTO fw (n) VALUES (8)
GO BOTTOM
=FPUTS(lnH, 'A.blank=[' + name + ']' + TRANSFORM(LEN(name)) + '/' + TRANSFORM(name == SPACE(10)) + '/' + TRANSFORM(EMPTY(name)) + '/' + TRANSFORM(n))
