* Golden master for microVFP character-field reads at full declared width (run by hand in VFP9, then commit
* fwseed.dbf + mvfpwidth.txt): fwseed.dbf holds a C(10)/C(6)/C(10) table with leading/trailing-blank, longer, shorter
* and empty values; body.prg, run on a copy (fw.dbf), logs under SET EXACT OFF and ON LEN(), field == / = literal,
* field = field of equal and different widths, EMPTY/INLIST/BETWEEN/$, memvar copies, COUNT/LOCATE FOR, SEEK/KEYMATCH
* needles (literal, padded, longer than the key, a field value, a compound key), TOTAL ON, SCATTER, REPLACE (longer /
* shorter / self-concatenating values), buffered edits with OLDVAL/CURVAL and buffered / unbuffered APPEND BLANK.
* MicroVfpFullWidthGoldenTests runs the same body.prg through the interpreter and compares line by line.
SET TALK OFF
SET SAFETY OFF
SET EXCLUSIVE ON
SET DELETED ON
SET ANSI OFF
SET DEFAULT TO (JUSTPATH(SYS(16)))
CREATE TABLE fwseed FREE (name C(10), code C(6), nm C(10), n N(3))
INSERT INTO fwseed VALUES ('abc', 'abc', 'abc', 1)
INSERT INTO fwseed VALUES ('  abc', 'abc', 'abc', 2)
INSERT INTO fwseed VALUES ('abcdef', 'abc', 'abcdef', 3)
INSERT INTO fwseed VALUES ('', '', '', 4)
INSERT INTO fwseed VALUES ('ab', 'abcdef', 'ab', 5)
INSERT INTO fwseed VALUES ('abc', 'abc', 'abc', 6)
USE
COPY FILE fwseed.dbf TO fw.dbf
lnH = FCREATE('mvfpwidth.txt')
ON ERROR =FPUTS(lnH, 'ERR=' + MESSAGE() + ' L' + TRANSFORM(LINENO()))
DO body.prg
ON ERROR
=FCLOSE(lnH)
CLOSE TABLES ALL
ERASE fw.dbf
ERASE fw.cdx
ERASE fwtot.dbf
ERASE body.fxp
QUIT
