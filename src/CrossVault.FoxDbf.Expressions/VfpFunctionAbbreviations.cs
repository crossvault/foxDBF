using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// VFP9's abbreviated built-in function names. VFP resolves them when it compiles an expression, so
/// <c>SUBS(x, 2)</c> is <c>SUBSTR(x, 2)</c> and <c>UPPE(x)</c> is <c>UPPER(x)</c>; <c>KEY()</c> and
/// <c>FILTER()</c> then report the full name. The rule was read from the VFP9 compiler by compiling every
/// 3+-letter prefix of every built-in function name and comparing the function token it emits:
/// <list type="bullet">
/// <item>An abbreviation has at least 4 letters. Three letters (<c>SUB(</c>) and a name longer than the
/// function (<c>SUBSTRX(</c>) call a user function.</item>
/// <item>When a prefix fits several functions the older one wins: <c>SUBS</c> is SUBSTR (not SUBSTRC),
/// <c>MESS</c> is MESSAGE (not MESSAGEBOX), <c>CURS</c> is CURSORGETPROP, <c>STRT</c> is STRTRAN (not
/// STRTOFILE). The other function needs a longer prefix (<c>MESSAGEB</c>, <c>STRTO</c>).</item>
/// <item>Some later additions need 10 letters (<c>GETWORDCOU</c> is GETWORDCOUNT, <c>GETWORDC</c> is not a
/// function), and some cannot be abbreviated at all (JUSTSTEM, FORCEEXT, GETWORDNUM, SUBSTRC, LEFTC, ...).</item>
/// <item>A few other spellings name the same function: FIELDS is FIELD, FILES is FILE, MESSAGES is MESSAGE,
/// FLDCOUNT is FCOUNT, TYPEAHEAD is TYPE.</item>
/// </list>
/// The table lists every VFP9 function that has an abbreviation, whether the engine implements it or not: a
/// function the engine lacks still decides which function a shared prefix means. An entry is <c>NAME</c> (its
/// prefixes from 4 letters on), <c>NAME:n</c> (from n letters on) or <c>SPELLING[:n]&gt;NAME</c> (that spelling
/// and its prefixes from 4 or n letters on name NAME). An internal test compiles the whole table with vfp9.exe
/// and compares the tokens.
/// </summary>
internal static class VfpFunctionAbbreviations
{
    private const string Table = """
        ACLASS ACOPY ADATABASES ADBOBJECTS ADDBS ADDPROPERTY ADLLS ADOCKSTATE AELEMENT AERROR AEVENTS AFIELDS
        AFONT AGETCLASS AGETFILEVERSION:5 AINSTANCE:5 ALANGUAGE ALIAS ALINES ALLTRIM AMEMBERS AMOUSEOBJ
        ANETRESOURCES ANSITOOEM APRINTERS APROCINFO ASCAN ASELOBJ ASESSIONS ASORT ASQLHANDLES ASTACKINFO
        ASUBSCRIPT ATAGINFO ATCLINE ATLINE AUSED AVCXCLASSES BARCOUNT>CNTBAR BARPROMPT BETWEEN BINDEVENT BINTOC
        BITAND BITCLEAR BITLSHIFT BITNOT BITOR BITRSHIFT BITSET BITTEST BITXOR CANDIDATE CAPSLOCK CDX:3>MDX
        CEILING CHANGE CHRSAW CHRTRAN CLEARRESULTSET CMONTH CNTBAR CNTPAD COMARRAY COMCLASSINFO COMPLETED:5
        COMPOBJ COMRETURNERROR CPCONVERT CPCURRENT CPDBF CPNOTRANS CREATEBINARY:7 CREATEOBJECT CREATEOBJECTEX:13
        CREATEOFFLINE:8 CTOBIN CURDIR CURSORGETPROP CURSORSETPROP:7 CURSORTOXML:10 CURVAL DATETIME:5 DBALIAS
        DBGETPROP DBSETPROP DBUSED DDEABORTTRANS:10 DDEINITIATE:10 DDELASTERROR:10 DDESETOPTION:10
        DDESETSERVICE:10 DDESETTOPIC:10 DDETERMINATE:10 DEFAULTEXT DELETED DESCENDING DIFFERENCE DIRECTORY
        DISKSPACE DISPLAYPATH:10 DODEFAULT DRIVETYPE DROPOFFLINE EDITSOURCE EMPTY ERROR EVALUATE EVENTHANDLER
        EXECSCRIPT FCHSIZE FCLOSE FCOUNT FCREATE FDATE FERROR FFLUSH FGETS FIELD FIELDS:6>FIELD FILES:5>FILE
        FILETOSTR:5 FILTER FIXED FKLABEL FKMAX FLDCOUNT>FCOUNT FLDLIST FLOAT FLOCK FLOOR FONTMETRIC FOPEN FOUND
        FPUTS FREAD FSEEK FSIZE FTIME FULLPATH FWRITE GETAUTOINCVALUE GETBAR GETCOLOR:5 GETCURSORADAPTER GETDIR
        GETENV GETFILE GETFLDSTATE:5 GETFONT:5 GETHOST GETINTERFACE:10 GETNEXTMODIFIED GETOBJECT GETPAD:5
        GETPEM:5 GETPICT GETPRINTER:5 GETRESULTSET GETWORDCOUNT:10 GOMONTH HEADER ICASE IDXCOLLATE IMESTATUS
        INDBC INDEXSEEK INKEY INLIST INPUTBOX INSMODE ISALPHA ISBLANK ISCOLOR ISDIGIT ISEXCLUSIVE ISFLOCKED
        ISHOSTED ISLEADBYTE ISLOWER ISMEMOFETCHED ISMOUSE ISNULL ISPEN ISREADONLY ISRLOCKED ISTRANSACTABLE
        ISUPPER KEYMATCH LASTKEY LOADPICTURE LOCFILE LOG10 LOOKUP LOWER LTRIM LUPDATE MAKETRANSACTABLE MDOWN
        MEMLINES MEMORY MESSAGE MESSAGEBOX:8 MESSAGES:8>MESSAGE MINUTE MLINE MONTH MRKBAR MRKPAD MWINDOW NETWORK
        NEWOBJECT NORMALIZE NUMLOCK OBJNUM OBJTOCLIENT OBJVAR OCCURS OEMTOANSI OLDVAL ORDER PADPROMPT PARAMETERS
        PAYMENT PCOUNT PEMSTATUS POPUPS PRIMARY PRINTSTATUS PRMBAR PRMPAD PROGRAM PROMPT PROPER PRTINFO PUTFILE
        QUARTER RAISEEVENT RATLINE RDLEVEL READKEY RECCOUNT RECNO RECSIZE REFRESH RELATION REMOVEPROPERTY
        REPLICATE REQUERY RGBSCHEME RIGHT RLOCK ROLLBACK ROUND RTRIM SAVEPICTURE SCHEME SCOLS SECONDS SELECT
        SETFLDSTATE SETRESULTSET SKPBAR SKPPAD SOUNDEX SPACE SQLCANCEL:5 SQLCOLUMNS:6 SQLCOMMIT:6 SQLCONNECT
        SQLDISCONNECT SQLEXEC SQLGETPROP SQLIDLEDISCONNECT SQLMORERESULTS SQLPREPARE SQLROLLBACK SQLSETPROP
        SQLSTRINGCONNECT:5 SQLTABLES SROWS STRCONV STREXTRACT STRTOFILE:5 STRTRAN STUFF SUBSTR SYSMETRIC
        TABLEREVERT:6 TABLEUPDATE TAGCOUNT TAGNO TARGET TEXTMERGE TRANSFORM TXNLEVEL TXTWIDTH TYPEAHEAD:5>TYPE
        UNBINDEVENTS UNIQUE UNPACK UPDATED UPPER VARREAD VARTYPE VERSION WBORDER WCHILD WCOLS WDOCKABLE WEXIST
        WFONT WINDOW>WOUTPUT WLAST WLCOL WLROW WMAXIMUM WMINIMUM WONTOP WOUTPUT WPARENT WREAD WROWS WTITLE
        WVISIBLE XMLTOCURSOR:10 XMLUPDATEGRAM:10
        """;

    // Built on first use; dispatch resolves abbreviations before consulting the host.
    private static readonly FrozenDictionary<string, string> Map = Build();

    /// <summary>The function that the abbreviated or alternative <paramref name="upperName"/> (upper-cased)
    /// names, e.g. SUBS is SUBSTR. False for a full function name and for anything VFP does not resolve.</summary>
    internal static bool TryExpand(string upperName, out string fullName)
    {
        if (upperName.Length >= 3 && Map.TryGetValue(upperName, out var full))
        {
            fullName = full;
            return true;
        }
        fullName = upperName;
        return false;
    }

    /// <summary><paramref name="upperName"/> itself, or the function it abbreviates.</summary>
    internal static string Canonical(string upperName) => TryExpand(upperName, out var full) ? full : upperName;

    /// <summary>Every abbreviation or alternative spelling and the function it names.</summary>
    internal static IReadOnlyDictionary<string, string> All => Map;

    private static FrozenDictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Table.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int gt = entry.IndexOf('>');
            string spelling = gt >= 0 ? entry[..gt] : entry;
            int min = 4;
            int colon = spelling.IndexOf(':');
            if (colon >= 0)
            {
                min = int.Parse(spelling[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                spelling = spelling[..colon];
            }
            string target = gt >= 0 ? entry[(gt + 1)..] : spelling;
            // A function's own prefixes stop short of its full name; another spelling includes itself.
            int last = spelling == target ? spelling.Length - 1 : spelling.Length;
            for (int len = min; len <= last; len++)
                map.Add(spelling[..len], target);
        }
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
