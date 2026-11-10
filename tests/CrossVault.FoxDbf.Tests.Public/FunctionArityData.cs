using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CrossVault.FoxDbf.Tests;

// Measured with FunctionArityVfp9OracleTests, not inferred from our dispatcher.
// Limits are syntactic. DATE/DATETIME and BITNOT/BITSET/BITCLEAR also have runtime-only errors.
internal static class FunctionArityData
{
    internal const string Table = """
        ABS 1 1 1229 1230
        ACOS 1 1 1229 1230
        ADDBS 1 1 1229 1230
        ALLTRIM 1 26 1229 1230
        ANSITOOEM 1 1 1229 1230
        ASC 1 1 1229 1230
        ASIN 1 1 1229 1230
        AT 2 3 1229 1230
        ATAN 1 1 1229 1230
        ATC 2 3 1229 1230
        ATCC 2 3 1229 1230
        ATN2 2 2 1229 1230
        AT_C 2 3 1229 1230
        BETWEEN 3 3 1229 1230
        BINTOC 1 2 1229 1230
        BITAND 2 26 1229 1230
        BITCLEAR 1 3 1229 1230
        BITLSHIFT 2 2 1229 1230
        BITNOT 1 3 1229 1230
        BITOR 2 26 1229 1230
        BITRSHIFT 2 2 1229 1230
        BITSET 1 3 1229 1230
        BITTEST 2 2 1229 1230
        BITXOR 2 26 1229 1230
        CDOW 1 1 1229 1230
        CEILING 1 1 1229 1230
        CHR 1 1 1229 1230
        CHRTRAN 3 3 1229 1230
        CHRTRANC 3 3 1229 1230
        CMONTH 1 1 1229 1230
        COS 1 1 1229 1230
        CREATEBINARY 1 1 1229 1230
        CTOBIN 1 2 1229 1230
        CTOD 1 1 1229 1230
        CTOT 1 1 1229 1230
        DATE 0 3 1229 1230
        DATETIME 0 6 1229 1230
        DAY 1 1 1229 1230
        DEFAULTEXT 2 2 1229 1230
        DELETED 0 1 1229 1230
        DMY 1 1 1229 1230
        DOW 1 2 1229 1230
        DTOC 1 2 1229 1230
        DTOR 1 1 1229 1230
        DTOS 1 1 1229 1230
        DTOT 1 1 1229 1230
        EMPTY 1 1 1229 1230
        EVL 2 2 1229 1230
        EXP 1 1 1229 1230
        FIXED 1 1 1229 1230
        FLOAT 1 1 1229 1230
        FLOOR 1 1 1229 1230
        FORCEEXT 2 2 1229 1230
        FORCEPATH 2 2 1229 1230
        FV 3 3 1229 1230
        GOMONTH 2 2 1229 1230
        HOUR 1 1 1229 1230
        ICASE 2 201 1306 1252
        IIF 3 3 1306 1300
        INLIST 2 26 1229 1230
        INT 1 1 1229 1230
        ISLEADBYTE 1 1 1229 1230
        ISNULL 1 1 1229 1230
        JUSTDRIVE 1 1 1229 1230
        JUSTEXT 1 1 1229 1230
        JUSTFNAME 1 1 1229 1230
        JUSTPATH 1 1 1229 1230
        JUSTSTEM 1 1 1229 1230
        LEFT 2 2 1229 1230
        LEFTC 2 2 1229 1230
        LEN 1 1 1229 1230
        LENC 1 1 1229 1230
        LIKE 2 2 1229 1230
        LIKEC 2 2 1229 1230
        LOG 1 1 1229 1230
        LOG10 1 1 1229 1230
        LOWER 1 1 1229 1230
        LTRIM 1 26 1229 1230
        MAX 2 26 1229 1230
        MDY 1 1 1229 1230
        MIN 2 26 1229 1230
        MINUTE 1 1 1229 1230
        MOD 2 2 1229 1230
        MONTH 1 1 1229 1230
        MTON 1 1 1229 1230
        NORMALIZE 1 1 1229 1230
        NTOM 1 1 1229 1230
        NVL 2 2 1229 1230
        OEMTOANSI 1 1 1229 1230
        PADC 2 3 1229 1230
        PADL 2 3 1229 1230
        PADR 2 3 1229 1230
        PAYMENT 3 3 1229 1230
        PI 0 0 1229 1230
        PV 3 3 1229 1230
        QUARTER 1 2 1229 1230
        RAND 0 1 1229 1230
        RAT 2 3 1229 1230
        RATC 2 3 1229 1230
        RECCOUNT 0 1 1229 1230
        RECNO 0 1 1229 1230
        REPLICATE 2 2 1229 1230
        RGB 3 3 1229 1230
        RIGHT 2 2 1229 1230
        RIGHTC 2 2 1229 1230
        ROUND 2 2 1229 1230
        RTOD 1 1 1229 1230
        RTRIM 1 26 1229 1230
        SEC 1 1 1229 1230
        SIGN 1 1 1229 1230
        SIN 1 1 1229 1230
        SPACE 1 1 1229 1230
        SQRT 1 1 1229 1230
        STR 1 3 1229 1230
        STRTRAN 2 6 1229 1230
        STRZERO 0 27 1229 1230
        STUFF 4 4 1229 1230
        STUFFC 4 4 1229 1230
        SUBSTR 2 3 1229 1230
        SUBSTRC 2 3 1229 1230
        TAN 1 1 1229 1230
        TIME 0 1 1229 1230
        TRIM 1 26 1229 1230
        TTOC 1 2 1229 1230
        TTOD 1 1 1229 1230
        TXTWIDTH 1 4 1229 1230
        UNPACK 1 1 1229 1230
        UPPER 1 1 1229 1230
        VAL 1 1 1229 1230
        WEEK 1 3 1229 1230
        YEAR 1 1 1229 1230
        """;

    internal static IEnumerable<(string Name, int Min, int Max, int Few, int Many)> Entries =>
        Table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var cells = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (cells[0], int.Parse(cells[1], CultureInfo.InvariantCulture), int.Parse(cells[2], CultureInfo.InvariantCulture),
                int.Parse(cells[3], CultureInfo.InvariantCulture), int.Parse(cells[4], CultureInfo.InvariantCulture));
        });

    internal static int RuntimeError(string name, int count) => name switch
    {
        "DATE" or "DATETIME" when count is 1 or 2 => 1229,
        "BITSET" or "BITCLEAR" when count == 1 => 1229,
        "BITSET" or "BITCLEAR" when count == 3 => 1230,
        "BITNOT" when count is 2 or 3 => 1230,
        _ => 0,
    };
}
