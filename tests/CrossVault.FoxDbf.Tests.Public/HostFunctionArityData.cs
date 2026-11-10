using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CrossVault.FoxDbf.Tests;

// Generated from VFP9 NORMALIZE/EVALUATE probes; the internal oracle rechecks every count.
// Includes dispatch aliases and the COCREATEGUID extension (a 0..27-argument UDF in VFP9).
internal static class HostFunctionArityData
{
    private const string Table = """
        ACOPY 2 5 1229 1230
        ADATABASES 1 1 1229 1230
        ADEL 2 3 1229 1230
        ADIR 1 4 1229 1230
        AELEMENT 2 3 1229 1230
        AERROR 1 1 1229 1230
        AFIELDS 1 2 1229 1230
        AFONT 1 4 1229 1230
        AINS 2 3 1229 1230
        ALEN 1 2 1229 1230
        ALIAS 0 1 1229 1230
        ALINES 2 26 1229 1230
        ALLT 1 26 1229 1230
        ASCAN 2 6 1229 1230
        ASESSIONS 1 1 1229 1230
        ASORT 1 5 1229 1230
        ASUBSCRIPT 3 3 1229 1230
        ATAGINFO 1 3 1229 1230
        ATC 2 3 1229 1230
        ATCLINE 2 2 1229 1230
        ATLINE 2 2 1229 1230
        AUSED 1 3 1229 1230
        BOF 0 1 1229 1230
        CANDIDATE 0 3 1229 1230
        CDX 1 2 1229 1230
        COCREATEGUID 0 27 1229 1230
        CPCONVERT 3 3 1229 1230
        CPCURRENT 0 1 1229 1230
        CPDBF 0 1 1229 1230
        CURSORGETPROP 1 2 1229 1230
        CURSORSETPROP 1 3 1229 1230
        CURVAL 1 2 1229 1230
        DBC 0 0 1229 1230
        DBF 0 1 1229 1230
        DBUSED 1 1 1229 1230
        DELETED 0 1 1229 1230
        DESCENDING 0 3 1229 1230
        DIFFERENCE 2 2 1229 1230
        DTOT 1 1 1229 1230
        EOF 0 1 1229 1230
        ERROR 0 0 1229 1230
        EVAL 1 1 1229 1230
        EVALUATE 1 1 1229 1230
        FCHSIZE 2 2 1229 1230
        FCLOSE 1 1 1229 1230
        FCREATE 1 2 1229 1230
        FEOF 1 1 1229 1230
        FERROR 0 0 1229 1230
        FFLUSH 1 2 1229 1230
        FGETS 1 3 1229 1230
        FILE 1 2 1229 1230
        FILETOSTR 1 1 1229 1230
        FILTER 0 1 1229 1230
        FLDLIST 0 1 1229 1230
        FLOCK 0 1 1229 1230
        FOPEN 1 2 1229 1230
        FOR 0 3 1229 1230
        FOUND 0 1 1229 1230
        FPUTS 2 4 1229 1230
        FREAD 2 2 1229 1230
        FSEEK 2 3 1229 1230
        FWRITE 2 3 1229 1230
        GETFLDSTATE 1 2 1229 1230
        GETNEXTMODIFIED 1 3 1229 1230
        GETWORDCOUNT 1 2 1229 1230
        GETWORDNUM 2 3 1229 1230
        HEADER 0 1 1229 1230
        IDXCOLLATE 1 3 1229 1230
        INDBC 2 2 1229 1230
        INDEXSEEK 1 4 1229 1230
        ISALPHA 1 1 1229 1230
        ISBLANK 1 1 1229 1230
        ISDIGIT 1 1 1229 1230
        ISEXCLUSIVE 0 2 1229 1230
        ISFLOCKED 0 1 1229 1230
        ISLOWER 1 1 1229 1230
        ISREADONLY 0 1 1229 1230
        ISRLOCKED 0 2 1229 1230
        ISUPPER 1 1 1229 1230
        KEY 0 3 1229 1230
        KEYMATCH 1 4 1229 1230
        LINENO 0 1 1229 1230
        LOCK 0 2 1229 1230
        LOOKUP 3 4 1229 1230
        LUPDATE 0 1 1229 1230
        MDX 1 2 1229 1230
        MEMLINES 1 1 1229 1230
        MESSAGE 0 1 1229 1230
        MESSAGEBOX 1 4 1229 1230
        MLINE 2 3 1229 1230
        NDX 1 2 1229 1230
        OCCURS 2 2 1229 1230
        OLDVAL 1 2 1229 1230
        ON 1 2 1229 1230
        ORDER 0 2 1229 1230
        PARAMETERS 0 0 1229 1230
        PCOUNT 0 0 1229 1230
        PRIMARY 0 3 1229 1230
        PROGRAM 0 1 1229 1230
        PROPER 1 1 1229 1230
        RATLINE 2 2 1229 1230
        RECCOUNT 0 1 1229 1230
        RECNO 0 1 1229 1230
        RELATION 1 2 1229 1230
        RLOCK 0 2 1229 1230
        SECONDS 0 0 1229 1230
        SEEK 1 3 1229 1230
        SELECT 0 1 1229 1230
        SET 1 2 1229 1230
        SETFLDSTATE 2 3 1229 1230
        SOUNDEX 1 1 1229 1230
        STRCONV 2 4 1229 1230
        STREXTRACT 2 5 1229 1230
        STRTOFILE 2 3 1229 1230
        STRTRAN 2 6 1229 1230
        SYS 1 26 1229 1230
        TABLEREVERT 0 2 1229 1230
        TABLEUPDATE 0 4 1229 1230
        TAG 0 3 1229 1230
        TAGCOUNT 0 2 1229 1230
        TAGNO 0 3 1229 1230
        TARGET 1 2 1229 1230
        TEXTMERGE 1 4 1229 1230
        TRANSFORM 1 2 1229 1230
        TXNLEVEL 0 0 1229 1230
        TYPE 1 2 1229 1230
        UNIQUE 0 3 1229 1230
        USED 0 1 1229 1230
        VARTYPE 1 2 1229 1230
        """;

    internal static IEnumerable<(string Name, int Min, int Max, int Few, int Many)> Entries =>
        Table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var cells = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (cells[0], int.Parse(cells[1], CultureInfo.InvariantCulture), int.Parse(cells[2], CultureInfo.InvariantCulture),
                int.Parse(cells[3], CultureInfo.InvariantCulture), int.Parse(cells[4], CultureInfo.InvariantCulture));
        });
}
