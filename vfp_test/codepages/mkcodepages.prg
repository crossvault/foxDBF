SET TALK OFF
SET SAFETY OFF
SET CPDIALOG OFF
SET RESOURCE OFF
TRY
  lcList = '437,850,1252,10000,852,895,620,866,865,861,737,857,932,936,949,950,874,1250,1251,1253,1254,1255,1256,1257,10007,10029,10006'
  lcOut = ''
  FOR lnIndex = 1 TO GETWORDCOUNT(lcList, ',')
    lnCP = VAL(GETWORDNUM(lcList, lnIndex, ','))
    lcName = 'cp' + TRANSFORM(lnCP)
    TRY
      lcCpText = TRANSFORM(lnCP)
      CREATE TABLE (lcName) FREE CODEPAGE=&lcCpText (txt C(2))
      FOR lnB = 0 TO 255
        APPEND BLANK
      ENDFOR
      lcOut = lcOut + TRANSFORM(lnCP) + '|' + TRANSFORM(CPDBF()) + CHR(13)+CHR(10)
      USE
      lnH = FOPEN(lcName + '.dbf', 12)
      =FSEEK(lnH, 8)
      lcLen = FREAD(lnH, 2)
      lnOffset = ASC(LEFT(lcLen,1)) + 256*ASC(RIGHT(lcLen,1))
      =FSEEK(lnH, lnOffset)
      FOR lnB = 0 TO 255
        =FWRITE(lnH, ' ' + CHR(lnB) + 'X')
      ENDFOR
      =FCLOSE(lnH)
      lcBytes = ''
      FOR lnB = 0 TO 255
        lcBytes = lcBytes + CHR(lnB)
      ENDFOR
      =STRTOFILE(CPCONVERT(lnCP,1250,lcBytes),lcName+'.1250')
      =STRTOFILE(CPCONVERT(1250,lnCP,CPCONVERT(lnCP,1250,lcBytes)),lcName+'.back1250')
      =STRTOFILE(CPCONVERT(lnCP,437,lcBytes),lcName+'.437')
      =STRTOFILE(CPCONVERT(437,lnCP,CPCONVERT(lnCP,437,lcBytes)),lcName+'.back437')
      =STRTOFILE(CPCONVERT(lnCP,852,lcBytes),lcName+'.852')
      =STRTOFILE(CPCONVERT(852,lnCP,CPCONVERT(lnCP,852,lcBytes)),lcName+'.back')
      TRY
        =STRTOFILE(STRCONV(lcBytes,5,lnCP,1),lcName+'.unicode')
      CATCH
        DIMENSION laUnicodeError[1]
        =AERROR(laUnicodeError)
        lcOut = lcOut + 'UNICODE|' + TRANSFORM(lnCP) + '|' + TRANSFORM(laUnicodeError[1]) + CHR(13)+CHR(10)
      ENDTRY
    CATCH
      DIMENSION laError[1]
      =AERROR(laError)
      lcOut = lcOut + 'ERR|' + TRANSFORM(lnCP) + '|' + TRANSFORM(laError[1]) + CHR(13)+CHR(10)
      CLOSE TABLES ALL
    ENDTRY
  ENDFOR
  


  FOR lnDbcs = 1 TO 4
    lnCP = VAL(GETWORDNUM('932,936,949,950',lnDbcs,','))
    lcName = 'cp'+TRANSFORM(lnCP)
    lcPair = STRCONV(CHR(0)+CHR(78),6,lnCP,1)
    =STRTOFILE(lcPair,lcName+'.sample')
    lnH = FOPEN(lcName+'.dbf',12)
    =FSEEK(lnH,lnOffset+1)
    =FWRITE(lnH,lcPair)
    =FCLOSE(lnH)
  ENDFOR
  FOR lnExtra = 1 TO 2
    lnCP = IIF(lnExtra=1,1255,1256)
    lcName = 'cp'+TRANSFORM(lnCP)
    COPY FILE cp1252.dbf TO (lcName+'.dbf')
    lnH = FOPEN(lcName+'.dbf',12)
    =FSEEK(lnH,29)
    =FWRITE(lnH,CHR(IIF(lnExtra=1,125,126)))
    =FCLOSE(lnH)
    TRY
      =STRTOFILE(STRCONV(lcBytes,5,lnCP,1),lcName+'.unicode')
      lcOut = lcOut + 'STRCONV|' + TRANSFORM(lnCP) + '|OK' + CHR(13)+CHR(10)
    CATCH
      DIMENSION laExtra[1]
      =AERROR(laExtra)
      lcOut = lcOut + 'STRCONV|' + TRANSFORM(lnCP) + '|' + TRANSFORM(laExtra[1]) + CHR(13)+CHR(10)
    ENDTRY
  ENDFOR
  FOR lnIdIndex = 1 TO 6
    lnId = VAL(GETWORDNUM('0,175,125,126,3,174', lnIdIndex, ','))
    lcFile = 'id' + TRANSFORM(lnId) + '.dbf'
    COPY FILE cp1252.dbf TO (lcFile)
    lnH = FOPEN(lcFile,12)
    =FSEEK(lnH,29)
    =FWRITE(lnH,CHR(lnId))
    =FCLOSE(lnH)
    USE (lcFile) EXCLUSIVE
    GO 229
    lcOut = lcOut + 'ID|' + TRANSFORM(lnId) + '|' + TRANSFORM(CPDBF()) + '|' + TRANSFORM(ASC(txt)) + CHR(13)+CHR(10)
    REPLACE txt WITH CHR(195)+CHR(164)
    lcOut = lcOut + 'UTF8|' + TRANSFORM(lnId) + '|' + TRANSFORM(LEN(txt)) + '|' + TRANSFORM(ASC(txt)) + '|' + TRANSFORM(ASC(RIGHT(txt,1))) + CHR(13)+CHR(10)
    USE
  ENDFOR
  =STRTOFILE(lcOut,'results.txt')
  =STRTOFILE('OK','done.txt')
CATCH
  DIMENSION laFatal[1]
  =AERROR(laFatal)
  =STRTOFILE('ERR|'+TRANSFORM(laFatal[1]),'done.txt')
ENDTRY
CLOSE TABLES ALL
QUIT
