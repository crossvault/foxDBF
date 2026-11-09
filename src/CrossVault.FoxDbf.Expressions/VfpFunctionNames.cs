using System;
using System.Collections.Generic;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// How VFP9 spells a function name when it decompiles an expression (index KEY/FOR text, NORMALIZE()):
/// built-ins upper-case and unabbreviated, anything else (a UDF) lower-case. The tables are MEASURED, not
/// derived from documentation: <c>NORMALIZE(name + "(x)")</c> (with the arity widened until VFP accepted the
/// call) was run for every name in vfp9.exe's keyword tables, every name this engine and microVFP implement,
/// and every 4+ character prefix of each — 4537 probes. A name of the form <c>NAME:k</c> is a built-in whose
/// prefixes of at least k characters expand to it (<c>RECN</c> → <c>RECNO</c>, <c>SUBS</c> → <c>SUBSTR</c>,
/// <c>FIEL</c>/<c>FIELD</c> → <c>FIELDS</c>); a name without <c>:k</c> is only recognised in full. The
/// <c>NAME:k=TARGET</c> entries are VFP's few synonyms (<c>FLDCOUNT</c> → <c>FCOUNT</c>). The same mapping
/// shows in the index header (FIELD(1) is stored as FIELDS(1), verified with INDEX ON ... FOR).
/// Short pre-VFP built-ins that take one argument and cannot be abbreviated (ASC, DATE, DBF, KEY, TAG, TIME,
/// LOCK, SEEK) were confirmed with INDEX ON ... FOR; STRZERO, SUBSTRING, RECNOX are UDFs to VFP.
/// </summary>
internal static class VfpFunctionNames
{
    private const string Builtins =
        "ABS ACLASS:4 ACOPY:4 ACOS ADATABASES:4 ADBOBJECTS:4 ADDBS:4 ADDPROPERTY:4 ADEL ADIR ADLLS:4 " +
        "ADOCKSTATE:4 AELEMENT:4 AERROR:4 AEVENTS:4 AFIELDS:4 AFONT:4 AGETCLASS:4 AGETFILEVERSION:5 AINS " +
        "AINSTANCE:5 ALANGUAGE:4 ALEN ALIAS:4 ALINES:4 ALLTRIM:4 AMEMBERS:4 AMOUSEOBJ:4 ANETRESOURCES:4 " +
        "ANSITOOEM:4 APRINTERS:4 APROCINFO:4 ASC ASCAN:4 ASELOBJ:4 ASESSIONS:4 ASIN ASORT:4 ASQLHANDLES:4 " +
        "ASTACKINFO:4 ASUBSCRIPT:4 AT ATAGINFO:4 ATAN ATC ATCC ATCLINE:4 ATLINE:4 ATN2 AT_C AUSED:4 " +
        "AVCXCLASSES:4 BAR BARCOUNT:4 BARPROMPT:4 BETWEEN:4 BINDEVENT:4 BINTOC:4 BITAND:4 BITCLEAR:4 " +
        "BITLSHIFT:4 BITNOT:4 BITOR:4 BITRSHIFT:4 BITSET:4 BITTEST:4 BITXOR:4 BOF CANDIDATE:4 CAPSLOCK:4 CDOW " +
        "CDX CEILING:4 CHANGE:4 CHR CHRSAW:4 CHRTRAN:4 CHRTRANC CLEARRESULTSET:4 CMONTH:4 CNTPAD:4 COL " +
        "COMARRAY:4 COMCLASSINFO:4 COMPLETED:5 COMPOBJ:4 COMPROP COMRETURNERROR:4 COS CPCONVERT:4 CPCURRENT:4 " +
        "CPDBF:4 CPNOTRANS:4 CREATEBINARY:7 CREATEOBJECT:4 CREATEOBJECTEX:13 CREATEOFFLINE:8 CTOBIN:4 CTOD " +
        "CTOT CURDIR:4 CURSORGETPROP:4 CURSORSETPROP:7 CURSORTOXML:10 CURVAL:4 DATE DATETIME:5 DAY DBALIAS:4 " +
        "DBC DBF DBGETPROP:4 DBSETPROP:4 DBUSED:4 DDEABORTTRANS:10 DDEADVISE DDEENABLED DDEEXECUTE " +
        "DDEINITIATE:10 DDELASTERROR:10 DDEPOKE DDEREQUEST DDESETOPTION:10 DDESETSERVICE:10 DDESETTOPIC:10 " +
        "DDETERMINATE:10 DEFAULTEXT:4 DELETED:4 DESCENDING:4 DIFFERENCE:4 DIRECTORY:4 DISKSPACE:4 " +
        "DISPLAYPATH:10 DMY DODEFAULT:4 DOW DRIVETYPE:4 DROPOFFLINE:4 DTOC DTOR DTOS DTOT EDITSOURCE:4 " +
        "EMPTY:4 EOF ERROR:4 EVALUATE:4 EVENTHANDLER:4 EVL EXECSCRIPT:4 EXP FCHSIZE:4 FCLOSE:4 FCOUNT:4 " +
        "FCREATE:4 FDATE:4 FEOF FERROR:4 FFLUSH:4 FGETS:4 FIELDS:4 FILES:4 FILETOSTR:5 FILTER:4 FIXED:4 " +
        "FKLABEL:4 FKMAX:4 FLDLIST:4 FLOAT:4 FLOCK:4 FLOOR:4 FONTMETRIC:4 FOPEN:4 FORCEEXT FORCEPATH FOUND:4 " +
        "FPUTS:4 FREAD:4 FSEEK:4 FSIZE:4 FTIME:4 FULLPATH:4 FV FWRITE:4 GETAUTOINCVALUE:4 GETBAR:4 GETCOLOR:5 " +
        "GETCP GETCURSORADAPTER:4 GETDIR:4 GETENV:4 GETFILE:4 GETFLDSTATE:5 GETFONT:5 GETHOST:4 " +
        "GETINTERFACE:10 GETNEXTMODIFIED:4 GETOBJECT:4 GETPAD:5 GETPEM:5 GETPICT:4 GETPRINTER:5 " +
        "GETRESULTSET:4 GETWORDCOUNT:10 GETWORDNUM GOMONTH:4 HEADER:4 HOME HOUR ICASE:4 ID IDXCOLLATE:4 IIF " +
        "IMESTATUS:4 INDBC:4 INDEXSEEK:4 INKEY:4 INLIST:4 INPUTBOX:4 INSMODE:4 INT ISALPHA:4 ISBLANK:4 " +
        "ISCOLOR:4 ISDIGIT:4 ISEXCLUSIVE:4 ISFLOCKED:4 ISHOSTED:4 ISLEADBYTE:4 ISLOWER:4 ISMEMOFETCHED:4 " +
        "ISMOUSE:4 ISNULL:4 ISPEN:4 ISREADONLY:4 ISRLOCKED:4 ISTRANSACTABLE:4 ISUPPER:4 JUSTDRIVE JUSTEXT " +
        "JUSTFNAME JUSTPATH JUSTSTEM KEY KEYMATCH:4 LASTKEY:4 LEFT LEFTC LEN LENC LIKE LIKEC LINENO:4 " +
        "LOADPICTURE:4 LOCFILE:4 LOCK LOG LOG10:4 LOOKUP:4 LOWER:4 LTRIM:4 LUPDATE:4 MAKETRANSACTABLE:4 MAX " +
        "MCOL MDOWN:4 MDY MEMLINES:4 MEMORY:4 MENU MESSAGEBOX:8 MESSAGES:4 MIN MINUTE:4 MLINE:4 MOD MONTH:4 " +
        "MRKBAR:4 MRKPAD:4 MROW MTON MWINDOW:4 NDX NETWORK:4 NEWOBJECT:4 NORMALIZE:4 NTOM NUMLOCK:4 NVL " +
        "OBJNUM:4 OBJTOCLIENT:4 OBJVAR:4 OCCURS:4 OEMTOANSI:4 OLDVAL:4 ORDER:4 OS PADC PADL PADPROMPT:4 PADR " +
        "PARAMETERS:4 PAYMENT:4 PCOL PCOUNT:4 PEMSTATUS:4 PI POPUPS:4 PRIMARY:4 PRINTSTATUS:4 PRMBAR:4 " +
        "PRMPAD:4 PROGRAM:4 PROMPT:4 PROPER:4 PROW PRTINFO:4 PUTFILE:4 PV QUARTER:4 RAISEEVENT:4 RAND RAT " +
        "RATC RATLINE:4 RDLEVEL:4 READKEY:4 RECCOUNT:4 RECNO:4 RECSIZE:4 REFRESH:4 RELATION:4 " +
        "REMOVEPROPERTY:4 REPLICATE:4 REQUERY:4 RGB RGBSCHEME:4 RIGHT:4 RIGHTC RLOCK:4 ROLLBACK:4 ROUND:4 ROW " +
        "RTOD RTRIM:4 SAVEPICTURE:4 SCHEME:4 SCOLS:4 SEC SECONDS:4 SEEK SELECT:4 SETFLDSTATE:4 SETRESULTSET:4 " +
        "SIGN SIN SKPBAR:4 SKPPAD:4 SOUNDEX:4 SPACE:4 SQLCANCEL:5 SQLCOLUMNS:6 SQLCOMMIT:6 SQLCONNECT:4 " +
        "SQLDISCONNECT:4 SQLEXEC:4 SQLGETPROP:4 SQLIDLEDISCONNECT:4 SQLMORERESULTS:4 SQLPREPARE:4 " +
        "SQLROLLBACK:4 SQLSETPROP:4 SQLSTRINGCONNECT:5 SQLTABLES:4 SQRT SROWS:4 STR STRCONV:4 STREXTRACT:4 " +
        "STRTOFILE:5 STRTRAN:4 STUFF:4 STUFFC SUBSTR:4 SUBSTRC SYS SYSMETRIC:4 TABLEREVERT:6 TABLEUPDATE:4 " +
        "TAG TAGCOUNT:4 TAGNO:4 TAN TARGET:4 TEXTMERGE:4 TIME TRANSFORM:4 TRIM TTOC TTOD TXNLEVEL:4 " +
        "TXTWIDTH:4 TYPE UNBINDEVENTS:4 UNIQUE:4 UNPACK:4 UPDATED:4 UPPER:4 USED VAL VARREAD:4 VARTYPE:4 " +
        "VERSION:4 WBORDER:4 WCHILD:4 WCOLS:4 WDOCKABLE:4 WEEK WEXIST:4 WFONT:4 WINDOW:4 WLAST:4 WLCOL:4 " +
        "WLROW:4 WMAXIMUM:4 WMINIMUM:4 WONTOP:4 WPARENT:4 WREAD:4 WROWS:4 WTITLE:4 WVISIBLE:4 XMLTOCURSOR:10 " +
        "XMLUPDATEGRAM:10 YEAR ";

    private const string Synonyms =
        "CNTBAR:4=BARCOUNT MDX:3=CDX FLDCOUNT:4=FCOUNT TYPEAHEAD:5=TYPE WOUTPUT:4=WINDOW ";

    private static readonly Dictionary<string, string> Map = Build();

    // SET/FOR have special argument grammar and are not in the decompiler probe's name(x) table.
    internal static bool IsBuiltin(string upperName)
        => upperName is "SET" or "FOR" || Map.ContainsKey(upperName);

    /// <summary>The function an upper-case <paramref name="upperName"/> calls. VFP's decompiler prints FIELD(),
    /// FILE() and MESSAGE() as FIELDS(), FILES(), MESSAGES() (one internal function each), so an index text
    /// VFP — or we — stored with the plural means the singular function.</summary>
    public static string EvaluationName(string upperName) => upperName switch
    {
        "FIELDS" => "FIELD",
        "FILES" => "FILE",
        "MESSAGES" => "MESSAGE",
        _ => upperName,
    };

    /// <summary>The spelling VFP9 stores for a call to <paramref name="name"/>.</summary>
    public static string Canonical(string name)
        => Map.TryGetValue(name.ToUpperInvariant(), out var canonical) ? canonical : name.ToLowerInvariant();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Builtins.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            string name = colon < 0 ? entry : entry[..colon];
            map[name] = name;
            if (colon >= 0) AddPrefixes(map, name, int.Parse(entry.AsSpan(colon + 1)), name);
        }
        foreach (var entry in Synonyms.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            int eq = entry.IndexOf('=');
            string word = entry[..colon];
            AddPrefixes(map, word, int.Parse(entry.AsSpan(colon + 1, eq - colon - 1)), entry[(eq + 1)..]);
            map.TryAdd(word, entry[(eq + 1)..]);
        }
        return map;
    }

    private static void AddPrefixes(Dictionary<string, string> map, string word, int min, string target)
    {
        for (int n = min; n < word.Length; n++)
            map.TryAdd(word[..n], target);   // a full built-in name always wins over another's prefix
    }
}
