using System.Globalization;
using System.Text.RegularExpressions;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Sql;

// Conversion for APPEND's external data and INSERT SELECT. VALUES deliberately retains its
// stricter assignment semantics; these conversions are pinned by DataImportVfp9OracleTests.
internal static class VfpImport
{
    internal sealed record StoredText(byte[] Bytes);

    internal static object? ConvertValue(object? value, DbfColumn column, VfpSession session,
        bool array = false, bool sdf = false)
    {
        if (value is null || value is DBNull) return null;
        string text = value as string ?? string.Empty;
        switch (column.Type)
        {
            case 'C': case 'V': case 'M':
                if (value is StoredText) return value;
                if (value is string) return text;
                if (value is DateOnly date) return FormatDate(date, session);
                if (value is DateTime time) return FormatDate(DateOnly.FromDateTime(time), session);
                if (value is bool logical) return logical ? "T" : "F";
                string number = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                if (!array) return number.PadLeft(column.Length);
                int dot = number.IndexOf('.');
                return number.PadLeft(10 + (dot < 0 ? 0 : number.Length - dot));
            case 'I': case '+': case 'N': case 'F': case 'Y': case 'B':
                if (value is bool or DateOnly or DateTime) return 0m;
                if (value is not string) return value;
                var match = Regex.Match(text.TrimStart(), @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?");
                if (decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
                return double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var large) ? large : 0m;
            case 'L':
                return value is bool b ? b : value is string && text.TrimStart() is { Length: > 0 } s && s[0] is 'T' or 'Y';
            case 'D':
                if (value is DateOnly) return value;
                if (value is DateTime dt) return DateOnly.FromDateTime(dt);
                return ParseDate(text, session, sdf);
            case 'T': case '@':
                if (value is DateTime) return value;
                if (value is DateOnly d) return d.ToDateTime(TimeOnly.MinValue);
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : default(DateTime);
            default: return value;
        }
    }

    internal static bool IsBlankConversion(object? value, DbfColumn column)
    {
        if (value is null) return false; // .NULL. is never a stored blank.
        return column.Type switch
        {
            'N' or 'F' or 'I' or 'B' or 'Y' => value is bool or DateOnly or DateTime,
            'L' => value is not bool && value is not string,
            'D' or 'T' => value is not DateOnly && value is not DateTime && value is not string,
            _ => false,
        };
    }

    private static string FormatDate(DateOnly date, VfpSession session)
    {
        string year = session.ImportCentury ? "yyyy" : "yy";
        string format = session.ImportDateFormat switch
        {
            "GERMAN" => "dd.MM." + year,
            "BRITISH" or "FRENCH" or "DMY" => "dd/MM/" + year,
            "ANSI" => year + ".MM.dd",
            "JAPAN" or "TAIWAN" or "YMD" => year + "/MM/dd",
            _ => "MM/dd/" + year,
        };
        return date.ToString(format, CultureInfo.InvariantCulture);
    }

    private static DateOnly ParseDate(string text, VfpSession session, bool sdf)
    {
        if (text.Length == 8 && text.All(char.IsAsciiDigit)
            && DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
            return compact;
        var parts = Regex.Matches(text, @"\d+").Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length != 3) return default;
        bool dmy = session.ImportDateFormat is "GERMAN" or "BRITISH" or "FRENCH" or "DMY" or "ITALIAN";
        bool ymd = session.ImportDateFormat is "ANSI" or "JAPAN" or "TAIWAN" or "YMD";
        int y = ymd ? parts[0] : parts[2];
        int m = ymd ? parts[1] : dmy ? parts[1] : parts[0];
        int d = ymd ? parts[2] : dmy ? parts[0] : parts[1];
        // SDF consumes eight bytes for D even with CENTURY ON; MDY then treats its
        // two-digit year literally. DMY applies the normal century rollover.
        if (y < 100 && !(sdf && !dmy && !ymd && session.ImportCentury)) y += 100 * (session.ImportCenturyBase + (y < session.ImportRollover ? 1 : 0));
        try { return new DateOnly(y, m, d); }
        catch (ArgumentOutOfRangeException) { return default; }
    }
}
