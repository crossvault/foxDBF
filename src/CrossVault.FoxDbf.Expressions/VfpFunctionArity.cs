using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>VFP9's engine and microVFP host-function limits, measured by the arity oracle tests.
/// Consulted only while parsing: valid calls incur no per-record arity lookup.</summary>
internal static class VfpFunctionArity
{
    private readonly record struct Arity(int Minimum, int Maximum, int TooFew = 1229, int TooMany = 1230);

    private static readonly FrozenDictionary<string, Arity> Table = Build();

    private static FrozenDictionary<string, Arity> Build()
    {
        var table = new Dictionary<string, Arity>(StringComparer.Ordinal);
        void Add(Arity arity, string names)
        {
            foreach (string name in names.Split(' ')) table.Add(name, arity);
        }

        Add(new(1, 1),
            "ABS ACOS ADDBS ANSITOOEM ASC ASIN ATAN CDOW CEILING CHR CMONTH COS CREATEBINARY CTOD CTOT DAY " +
            "DMY DTOR DTOS DTOT EMPTY EXP FIXED FLOAT FLOOR HOUR INT ISLEADBYTE ISNULL JUSTDRIVE JUSTEXT " +
            "JUSTFNAME JUSTPATH JUSTSTEM LEN LENC LOG LOG10 LOWER MDY MINUTE MONTH MTON NORMALIZE NTOM " +
            "OEMTOANSI RTOD SEC SIGN SIN SPACE SQRT TAN TTOD UNPACK UPPER VAL YEAR");
        Add(new(1, 26), "ALLTRIM LTRIM RTRIM TRIM");
        Add(new(2, 3), "AT ATC ATCC AT_C PADC PADL PADR RAT RATC SUBSTR SUBSTRC");
        Add(new(2, 2),
            "ATN2 BITLSHIFT BITRSHIFT BITTEST DEFAULTEXT EVL FORCEEXT FORCEPATH GOMONTH LEFT LEFTC LIKE LIKEC " +
            "MOD NVL REPLICATE RIGHT RIGHTC ROUND");
        Add(new(3, 3), "BETWEEN CHRTRAN CHRTRANC FV PAYMENT PV RGB");
        Add(new(1, 2), "BINTOC CTOBIN DOW DTOC QUARTER TTOC");
        Add(new(2, 26), "BITAND BITOR BITXOR INLIST MAX MIN");
        Add(new(1, 3), "BITCLEAR BITNOT BITSET STR WEEK");
        Add(new(0, 3), "DATE");
        Add(new(0, 6), "DATETIME");
        Add(new(0, 1), "DELETED RAND RECCOUNT RECNO TIME");
        // ICASE reaches VFP9's expression-complexity ceiling at the 101st condition (202 args).
        Add(new(2, 201, 1306, 1252), "ICASE");
        Add(new(3, 3, 1306, 1300), "IIF");
        Add(new(0, 0), "PI");
        Add(new(2, 6), "STRTRAN");
        // STRZERO is a library extension: VFP9 parses it as a UDF (0..27), then reports missing PRG.
        Add(new(0, 27), "STRZERO");
        Add(new(4, 4), "STUFF STUFFC");
        Add(new(1, 4), "TXTWIDTH");

        // microVFP host functions, measured by MicroVfpHostFunctionArityVfp9OracleTests.
        Add(new(0, 0), "DBC ERROR FERROR PARAMETERS PCOUNT SECONDS TXNLEVEL");
        Add(new(0, 1),
            "ALIAS BOF CPCURRENT CPDBF DBF EOF FILTER FLDLIST FLOCK FOUND HEADER ISFLOCKED ISREADONLY LINENO LUPDATE " +
            "MESSAGE PROGRAM SELECT USED");
        Add(new(0, 2), "ISEXCLUSIVE ISRLOCKED LOCK ORDER RLOCK TABLEREVERT TAGCOUNT");
        Add(new(0, 3), "CANDIDATE DESCENDING FOR KEY PRIMARY TAG TAGNO UNIQUE");
        Add(new(0, 4), "TABLEUPDATE");
        // COCREATEGUID is a host extension, parsed as a UDF (0..27) by VFP9.
        Add(new(0, 27), "COCREATEGUID");
        Add(new(1, 1),
            "ADATABASES AERROR ASESSIONS DBUSED EVALUATE FCLOSE FEOF FILETOSTR ISALPHA ISBLANK ISDIGIT ISLOWER " +
            "ISUPPER MEMLINES PROPER SOUNDEX");
        Add(new(1, 2),
            "AFIELDS ALEN CURSORGETPROP CURVAL FCREATE FFLUSH FILE FOPEN GETFLDSTATE GETWORDCOUNT MDX NDX OLDVAL ON " +
            "RELATION SET TARGET TRANSFORM TYPE VARTYPE");
        Add(new(1, 3), "ATAGINFO AUSED CURSORSETPROP FGETS GETNEXTMODIFIED IDXCOLLATE SEEK");
        Add(new(1, 4), "ADIR AFONT INDEXSEEK KEYMATCH MESSAGEBOX TEXTMERGE");
        Add(new(1, 5), "ASORT");
        Add(new(1, 26), "SYS");
        Add(new(2, 2), "ATCLINE ATLINE DIFFERENCE FCHSIZE FREAD INDBC OCCURS RATLINE");
        Add(new(2, 3), "ADEL AELEMENT AINS FSEEK FWRITE GETWORDNUM MLINE SETFLDSTATE STRTOFILE");
        Add(new(2, 4), "FPUTS STRCONV");
        Add(new(2, 5), "ACOPY STREXTRACT");
        Add(new(2, 6), "ASCAN");
        Add(new(2, 26), "ALINES");
        Add(new(3, 3), "ASUBSCRIPT CPCONVERT");
        Add(new(3, 4), "LOOKUP");
        return table.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal static AstNode Bind(string name, AstNode[] args, int position, bool sql = false)
    {
        string canonical = VfpFunctionAbbreviations.Canonical(name.ToUpperInvariant());
        if (!Table.TryGetValue(canonical, out var arity)) return new FunctionNode(name, args);
        int count = args.Length;
        int error = count < arity.Minimum
            ? count == 0 && canonical is "IIF" or "ICASE" ? 152 : arity.TooFew
            : count > arity.Maximum ? arity.TooMany : 0;
        if (error != 0) throw Error(error, position);

        if (sql && canonical == "ISBLANK") return new SqlIsBlankNode(args[0]);

        // These counts compile in VFP9. They fail only if evaluated, after argument evaluation;
        // the BIT functions even propagate NULL before reporting their runtime count error.
        int runtimeError = canonical switch
        {
            "DATE" or "DATETIME" when count is 1 or 2 => 1229,
            "BITSET" or "BITCLEAR" when count == 1 => 1229,
            "BITSET" or "BITCLEAR" when count == 3 => 1230,
            "BITNOT" when count is 2 or 3 => 1230,
            _ => 0,
        };
        // ISBLANK needs the field's raw bytes, not its decoded value. Lower its private field marker
        // only AFTER validating the source call's single argument; user-written second args stay illegal.
        if (canonical == "ISBLANK" && args[0] is FieldNode { IsValueExpression: false } field)
            args = new AstNode[] { new FieldReferenceNameNode(field.Name), new LiteralNode(VfpValue.Logical(true)) };
        var function = new FunctionNode(name, args);
        return runtimeError == 0 ? function : new FunctionArityErrorNode(function, args, runtimeError,
            canonical is "BITSET" or "BITCLEAR" or "BITNOT", position);
    }

    internal static ExpressionException Error(int number, int position = -1) => new(number switch
    {
        1229 => "Too few arguments.",
        1230 => "Too many arguments.",
        152 => "Expression is invalid. A valid expression is required.",
        1306 => "Missing comma (,).",
        1300 => "Function name is missing ( ).",
        1252 => "Nesting level or expression complexity exceeded.",
        _ => "Function argument count is invalid.",
    }, position) { VfpErrorNumber = number };
}
