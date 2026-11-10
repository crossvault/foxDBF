using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CrossVault.FoxDbf.Expressions;

internal static partial class VfpRuntime
{
    // VFP9 oracle pins these scalar functions independently of the interpreter host, so they
    // also work in native index maintenance, SQL and compiled filter delegates.
    private static VfpValue CallScalarExtras(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
    {
        double x = Arg(args, 0).AsDouble;
        double y = Arg(args, 1).AsDouble;
        switch (name)
        {
            case "RAND": return VfpValue.Number(ctx.Random.Next(args.Length == 0 ? null : x)).WithDisplayDecimals(ctx.NumericDecimals);
            case "FLOAT": case "FIXED": return VfpValue.Number(x).WithDisplayDecimals(Arg(args, 0).DisplayDecimals);
            case "TIME": return VfpValue.Character(DateTime.Now.ToString(args.Length > 0 && x == 1 ? "HH:mm:ss.ff" : "HH:mm:ss", Inv));
            case "CEILING": return VfpValue.Number(Math.Ceiling(x)).WithDisplayDecimals(0).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "FLOOR": return VfpValue.Number(Math.Floor(x)).WithDisplayDecimals(0).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "ACOS":
                if (x < -1 || x > 1) throw new ExpressionException("Expression used with ACOS() is out of range.") { VfpErrorNumber = 293 };
                return VfpValue.Number(Math.Acos(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "ASIN":
                if (x < -1 || x > 1) throw new ExpressionException("Expression used with ASIN() is out of range.") { VfpErrorNumber = 291 };
                return VfpValue.Number(Math.Asin(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "ATAN": return VfpValue.Number(Math.Atan(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "ATN2": return VfpValue.Number(Math.Atan2(x, y)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "COS": return VfpValue.Number(Math.Cos(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "SIN": return VfpValue.Number(Math.Sin(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "TAN": return VfpValue.Number(Math.Tan(x)).WithDisplayDecimals(ctx.NumericDecimals).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "DTOR": return VfpValue.Number(x * (Math.PI / 180)).WithDisplayDecimals(ctx.NumericDecimals);
            case "RTOD": return VfpValue.Number(x * (180 / Math.PI)).WithDisplayDecimals(ctx.NumericDecimals);
            case "PI": return VfpValue.Number(Math.PI).WithDisplayDecimals(ctx.NumericDecimals);
            case "EXP": return VfpValue.Number(Math.Exp(x)).WithDisplayDecimals(Math.Max(ctx.NumericDecimals, Arg(args, 0).DisplayDecimals))
                .WithOverflowDisplayWidth(double.IsNaN(x) ? Arg(args, 0).OverflowDisplayWidth : NumericOverflowWidth(ctx.NumericDecimals));
            case "LOG":
                if (x <= 0) throw new ExpressionException("LOG(): Zero or negative used as argument.") { VfpErrorNumber = 58 };
                return VfpValue.Number(Math.Log(x)).WithDisplayDecimals(Math.Max(ctx.NumericDecimals, Arg(args, 0).DisplayDecimals)).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "LOG10":
                if (x <= 0) throw new ExpressionException("Cannot use 0 or negative as the argument for LOG10().") { VfpErrorNumber = 292 };
                return VfpValue.Number(Math.Log10(x)).WithDisplayDecimals(Math.Max(ctx.NumericDecimals, Arg(args, 0).DisplayDecimals)).WithOverflowDisplayWidth(Math.Max(20, NumericOverflowWidth(ctx.NumericDecimals)));
            case "SQRT":
                if (x < 0) throw new ExpressionException("SQRT() argument cannot be negative.") { VfpErrorNumber = 61 };
                return VfpValue.Number(Math.Sqrt(x)).WithDisplayDecimals(Math.Max(ctx.NumericDecimals, Arg(args, 0).DisplayDecimals))
                    .WithOverflowDisplayWidth(Arg(args, 0).OverflowDisplayWidth);
            // Round5 oracle: SIGN of an indeterminate overflow is -1, not a CLR ArithmeticException.
            case "SIGN": return VfpValue.Number(double.IsNaN(x) ? -1d : Math.Sign(x)).WithDisplayDecimals(0);
            case "FV": case "PV": case "PAYMENT":
            {
                // VFP9 uses the rounded absolute period count and powers the reciprocal for PV/PAYMENT.
                double periods = Math.Round(Math.Abs(Arg(args, 2).AsDouble), MidpointRounding.AwayFromZero);
                double result = periods == 0 ? 0 : name switch
                {
                    "FV" => y == 0 ? 0 : x * (Math.Pow(1 + y, periods) - 1) / y,
                    "PV" => y == 0 ? x * periods : x * (1 - Math.Pow(1 / (1 + y), periods)) / y,
                    _ => y == 0 ? x / periods : x * y / (1 - Math.Pow(1 / (1 + y), periods)),
                };
                return VfpValue.Number(result, ctx.NumericDecimals);
            }
            case "MTON": return Arg(args, 0).Type == VfpType.Currency
                ? VfpValue.Number(Arg(args, 0).AsNumber).WithDisplayDecimals(4) : Arg(args, 0);
            case "NTOM": return VfpValue.Currency(Arg(args, 0).ToCurrencyNumber());
            case "BITAND": case "BITOR": case "BITXOR":
            {
                int bits = unchecked((int)(long)x);
                foreach (var a in args.AsSpan(1))
                {
                    int next = unchecked((int)(long)a.AsDouble);
                    bits = name == "BITAND" ? bits & next : name == "BITOR" ? bits | next : bits ^ next;
                }
                return VfpValue.Number((double)bits).WithDisplayDecimals(0);
            }
            case "BITNOT": return VfpValue.Number((double)~unchecked((int)(long)x)).WithDisplayDecimals(0);
            case "BITSET": return VfpValue.Number((double)(unchecked((int)(long)x) | (1 << (int)y))).WithDisplayDecimals(0);
            case "BITCLEAR": return VfpValue.Number((double)(unchecked((int)(long)x) & ~(1 << (int)y))).WithDisplayDecimals(0);
            case "BITTEST": return VfpValue.Logical((unchecked((int)(long)x) & (1 << (int)y)) != 0);
            case "BITLSHIFT": return VfpValue.Number((double)(unchecked((int)(long)x) << (int)y)).WithDisplayDecimals(0);
            case "BITRSHIFT": return VfpValue.Number((double)((uint)unchecked((int)(long)x) >> (int)y)).WithDisplayDecimals(0);
            case "RGB": return VfpValue.Number(x + y * 256 + Arg(args, 2).AsDouble * 65536).WithDisplayDecimals(0);
            case "HOUR": return VfpValue.Number((double)Arg(args, 0).AsDateTime.Hour).WithDisplayDecimals(0);
            case "MINUTE": return VfpValue.Number((double)Arg(args, 0).AsDateTime.Minute).WithDisplayDecimals(0);
            case "SEC": return VfpValue.Number((double)Arg(args, 0).AsDateTime.Second).WithDisplayDecimals(0);
            case "TTOD": return VfpValue.Date(Arg(args, 0).AsDate, Arg(args, 0).IsEmptyDate);
            case "QUARTER": return VfpValue.Number(Arg(args, 0).IsEmptyDate ? 0d : ((Arg(args, 0).AsDate.Month - (args.Length > 1 ? (int)y : 1) + 12) % 12) / 3 + 1d).WithDisplayDecimals(0);
            case "WEEK":
            {
                var rule = args.Length > 1 ? (int)y : 1;
                var day = args.Length > 2 ? IntArg(args, 2) : 1;
                if (rule is < 0 or > 3 || day is < 0 or > 7) throw InvalidFunctionArgument();
                return VfpValue.Number(Arg(args, 0).IsEmptyDate ? 0d : Inv.Calendar.GetWeekOfYear(Arg(args, 0).AsDateTime,
                    rule == 2 ? CalendarWeekRule.FirstFourDayWeek : rule == 3 ? CalendarWeekRule.FirstFullWeek : CalendarWeekRule.FirstDay,
                    (DayOfWeek)(Math.Max(1, day) - 1))).WithDisplayDecimals(0);
            }
            case "DMY": case "MDY": return VfpValue.Character(Arg(args, 0).IsEmptyDate ? "" : Arg(args, 0).AsDateTime.ToString(name == "DMY" ? "dd MMMM yyyy" : "MMMM dd, yyyy", Inv));
            case "CREATEBINARY": return VfpValue.Varbinary(EncodeByteString(Arg(args, 0), ctx));
            case "BINTOC": return BinaryToCharacter(args, ctx);
            case "CTOBIN": return CharacterToBinary(args, ctx);
            case "ANSITOOEM": case "OEMTOANSI":
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var ansi = ctx.Encoding ?? Encoding.GetEncoding(1252);
                var oem = Encoding.GetEncoding(437);
                string text = Arg(args, 0).AsString;
                return VfpValue.Character(name == "ANSITOOEM" ? ansi.GetString(oem.GetBytes(text)) : oem.GetString(ansi.GetBytes(text)));
            }
            case "ADDBS": case "DEFAULTEXT": case "FORCEEXT": case "FORCEPATH":
            case "JUSTDRIVE": case "JUSTEXT": case "JUSTFNAME": case "JUSTPATH": case "JUSTSTEM":
                return VfpValue.Character(PathScalar(name, args));
            default: return UnknownFunction(name, args, ctx, row);
        }
    }

    private static string PathScalar(string name, VfpValue[] args)
    {
        string path = Arg(args, 0).AsString.TrimEnd();
        int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        int colon = path.IndexOf(':');
        string file = path[(Math.Max(slash, colon) + 1)..];
        int dot = file.LastIndexOf('.');
        string extension = dot < 0 ? "" : file[(dot + 1)..];
        string stem = dot < 0 ? file : file[..dot];
        string AddSlash(string value) => value.Length == 0 || value.EndsWith('\\') || value.EndsWith('/') || value.EndsWith(':') ? value : value + "\\";
        string newExtension = Arg(args, 1).AsString.Trim().TrimStart('.');
        return name switch
        {
            "JUSTDRIVE" => colon < 0 ? "" : path[..(colon + 1)],
            "JUSTPATH" => slash < 0 ? colon < 0 ? "" : path[..(colon + 1)] : path[..slash],
            "JUSTFNAME" => file,
            "JUSTSTEM" => stem,
            "JUSTEXT" => extension,
            "ADDBS" => AddSlash(path),
            "FORCEPATH" => AddSlash(Arg(args, 1).AsString.TrimEnd()) + file,
            "DEFAULTEXT" when dot >= 0 => path,
            _ => path[..(Math.Max(slash, colon) + 1)] + stem + (newExtension.Length == 0 ? "" : "." + newExtension)
        };
    }

    private static string DecodeByteString(byte[] bytes, EvaluationContext ctx)
        => (ctx.Encoding ?? CodePagesEncodingProvider.Instance.GetEncoding(1252)!).GetString(bytes);

    private static byte[] EncodeByteString(VfpValue value, EvaluationContext ctx)
        => (value.IsBinary ? Encoding.Latin1 : ctx.Encoding ?? CodePagesEncodingProvider.Instance.GetEncoding(1252)!).GetBytes(value.AsString);

    private static string BinaryFlags(VfpValue[] args, string fallback)
        => args.Length < 2 ? fallback : args[1].Type == VfpType.Character
            ? args[1].AsString.ToUpperInvariant() : args[1].AsInteger.ToString(Inv);

    private static VfpValue BinaryToCharacter(VfpValue[] args, EvaluationContext ctx)
    {
        string flags = BinaryFlags(args, "4");
        var value = Arg(args, 0);
        byte[] bytes;
        if (flags.Contains('F') || flags.Contains('B'))
        {
            bytes = flags.Contains('F') ? BitConverter.GetBytes((float)value.AsDouble) : BitConverter.GetBytes(value.AsDouble);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        }
        else
        {
            int width = flags.Contains('8') ? 8 : flags.Contains('2') ? 2 : flags.Contains('1') ? 1 : 4;
            bytes = new byte[width];
            long number = (long)value.AsDouble;
            if (width == 8)
            {
                if (value.Type == VfpType.Currency)
                    BinaryPrimitives.WriteInt64BigEndian(bytes, (long)(value.AsNumber * 10000));
                else
                    BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value.AsDouble));
                // Sortable doubles invert every bit for negative values; currency uses scaled integers.
                if (value.Type != VfpType.Currency && (bytes[0] & 0x80) != 0)
                    for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0xff;
                else bytes[0] ^= 0x80;
            }
            else
            {
                if (width == 4) BinaryPrimitives.WriteInt32BigEndian(bytes, unchecked((int)number));
                else if (width == 2) BinaryPrimitives.WriteInt16BigEndian(bytes, unchecked((short)number));
                else bytes[0] = unchecked((byte)number);
                if (!flags.Contains('S')) bytes[0] ^= 0x80;
            }
        }
        if (flags.Contains('R')) Array.Reverse(bytes);
        return VfpValue.Character(DecodeByteString(bytes, ctx));
    }

    private static VfpValue CharacterToBinary(VfpValue[] args, EvaluationContext ctx)
    {
        byte[] bytes = EncodeByteString(Arg(args, 0), ctx);
        string flags = BinaryFlags(args, bytes.Length == 8 ? "B" : bytes.Length.ToString(Inv));
        if (bytes.Length is not (1 or 2 or 4 or 8)
            || (flags.Contains('N') || flags == "8") && bytes.Length is not (4 or 8)
            || flags.Contains('Y') && bytes.Length != 8) throw InvalidFunctionArgument();
        if (flags.Contains('R')) Array.Reverse(bytes);
        if (flags.Contains('N') || flags == "8")
        {
            if (!flags.Contains('S')) bytes[0] ^= 0x80;
            return VfpValue.Number(bytes.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(bytes)
                : BinaryPrimitives.ReadDoubleLittleEndian(bytes)).WithDisplayDecimals(18);
        }
        if (flags.Contains('Y'))
        {
            bytes[0] ^= 0x80;
            return VfpValue.Currency(BinaryPrimitives.ReadInt64BigEndian(bytes) / 10000m);
        }
        if (bytes.Length == 8)
        {
            if ((bytes[0] & 0x80) == 0)
                for (int i = 0; i < bytes.Length; i++) bytes[i] ^= 0xff;
            else bytes[0] ^= 0x80;
            return VfpValue.Number(BinaryPrimitives.ReadDoubleBigEndian(bytes)).WithDisplayDecimals(18);
        }
        if (!flags.Contains('S')) bytes[0] ^= 0x80;
        return VfpValue.Number(bytes.Length == 4 ? (double)BinaryPrimitives.ReadInt32BigEndian(bytes)
            : bytes.Length == 2 ? BinaryPrimitives.ReadInt16BigEndian(bytes) : unchecked((sbyte)bytes[0])).WithDisplayDecimals(0);
    }
}
