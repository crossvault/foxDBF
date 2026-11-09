using System;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>Binary operator tag used by the AST / runtime.</summary>
internal enum BinOp
{
    Add, Sub, Mul, Div, Mod, Pow,
    Eq, ExactEq, Ne, Lt, Le, Gt, Ge, Dollar,
    And, Or,
}

/// <summary>How two strings of different length are lined up before they are compared.</summary>
internal enum StringCompareMode
{
    /// <summary>Xbase SET EXACT OFF: the left operand is cut to the right one's length.</summary>
    TruncateLeft,
    /// <summary>SET EXACT ON / SET ANSI ON / SQL <c>==</c>: the shorter operand is padded with blanks.</summary>
    Pad,
    /// <summary>SQL SET ANSI OFF: the longer operand is cut to the shorter one's length.</summary>
    Shorter,
}

/// <summary>Unary operator tag used by the AST / runtime.</summary>
internal enum UnOp { Neg, Not }

/// <summary>
/// Single source of truth for VFP operator and function semantics. Both the
/// tree-walking interpreter and the compiled (System.Linq.Expressions) delegate
/// call into these methods, guaranteeing identical results. Nothing here throws
/// on bad/null data — it coerces or yields <c>.NULL.</c> per VFP rules.
/// </summary>
internal static class VfpRuntime
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static bool IsNumeric(in VfpValue v) =>
        v.Type is VfpType.Numeric or VfpType.Currency or VfpType.Integer;

    // ===================================================================
    //  FIELD ACCESS
    // ===================================================================

    public static VfpValue GetField(IRowContext row, string name)
        => VfpValue.FromClr(row.GetField(name));

    // ===================================================================
    //  UNARY
    // ===================================================================

    public static VfpValue Unary(UnOp op, VfpValue v, EvaluationContext ctx)
    {
        switch (op)
        {
            case UnOp.Neg:
                if (v.IsNull) return VfpValue.Null;
                return VfpValue.Number(-v.AsDouble);
            case UnOp.Not:
                if (v.IsNull) return VfpValue.Null;
                return VfpValue.Logical(!v.AsLogical);
            default:
                return VfpValue.Null;
        }
    }

    // ===================================================================
    //  BINARY
    // ===================================================================

    public static VfpValue Binary(BinOp op, VfpValue l, VfpValue r, EvaluationContext ctx)
    {
        // Logical operators implement three-valued logic and must NOT short to .NULL.
        if (op == BinOp.And) return And(l, r);
        if (op == BinOp.Or) return Or(l, r);

        // Everything else: .NULL. propagates.
        if (l.IsNull || r.IsNull) return VfpValue.Null;

        switch (op)
        {
            case BinOp.Add: return Add(l, r);
            case BinOp.Sub: return Sub(l, r);
            case BinOp.Mul: return VfpValue.Number(l.AsDouble * r.AsDouble);
            case BinOp.Div:
                return r.AsDouble == 0d ? VfpValue.Null
                                        : VfpValue.Number(l.AsDouble / r.AsDouble);
            case BinOp.Mod: return VfpValue.Number(Modulo(l.AsDouble, r.AsDouble));
            case BinOp.Pow: return VfpValue.Number(Math.Pow(l.AsDouble, r.AsDouble));

            case BinOp.Eq: return VfpValue.Logical(ValueEquals(l, r, ctx, exact: false));
            case BinOp.ExactEq: return VfpValue.Logical(ValueEquals(l, r, ctx, exact: true));
            case BinOp.Ne: return VfpValue.Logical(!ValueEquals(l, r, ctx, exact: false));
            case BinOp.Lt: return VfpValue.Logical(CompareOperands(l, r, ctx) < 0);
            case BinOp.Le: return VfpValue.Logical(CompareOperands(l, r, ctx) <= 0);
            case BinOp.Gt: return VfpValue.Logical(CompareOperands(l, r, ctx) > 0);
            case BinOp.Ge: return VfpValue.Logical(CompareOperands(l, r, ctx) >= 0);
            case BinOp.Dollar:
                return VfpValue.Logical(Contains(l.AsString, r.AsString, ctx));
            default:
                return VfpValue.Null;
        }
    }

    /// <summary>True when the LEFT operand alone decides an AND (a logical .F.) or an OR (a logical .T.), so the
    /// right operand must not be evaluated. Only a genuine Logical decides — .NULL. or a non-logical does not.</summary>
    internal static bool Decides(BinOp op, VfpValue l)
        => l.Type == VfpType.Logical && l.AsLogical == (op == BinOp.Or);

    private static VfpValue And(VfpValue l, VfpValue r)
    {
        bool? a = l.IsNull ? null : l.AsLogical;
        bool? b = r.IsNull ? null : r.AsLogical;
        if (a == false || b == false) return VfpValue.Logical(false);
        if (a is null || b is null) return VfpValue.Null;
        return VfpValue.Logical(true);
    }

    private static VfpValue Or(VfpValue l, VfpValue r)
    {
        bool? a = l.IsNull ? null : l.AsLogical;
        bool? b = r.IsNull ? null : r.AsLogical;
        if (a == true || b == true) return VfpValue.Logical(true);
        if (a is null || b is null) return VfpValue.Null;
        return VfpValue.Logical(false);
    }

    private static VfpValue Add(VfpValue l, VfpValue r)
    {
        if (l.Type == VfpType.Character || r.Type == VfpType.Character)
            return VfpValue.Character(l.AsString + r.AsString);
        if (l.Type == VfpType.Date && IsNumeric(r))
        {
            if (l.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(l.AsDate.AddDays((int)r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (IsNumeric(l) && r.Type == VfpType.Date)
        {
            if (r.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(r.AsDate.AddDays((int)l.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (l.Type == VfpType.DateTime && IsNumeric(r))
        {
            if (l.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(l.AsDateTime.AddSeconds(r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        if (IsNumeric(l) && r.Type == VfpType.DateTime)
        {
            if (r.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(r.AsDateTime.AddSeconds(l.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        return VfpValue.Number(l.AsDouble + r.AsDouble);
    }

    private static VfpValue Sub(VfpValue l, VfpValue r)
    {
        if (l.Type == VfpType.Character || r.Type == VfpType.Character)
            return VfpValue.Character(MinusConcat(l.AsString, r.AsString));
        if (l.Type == VfpType.Date && r.Type == VfpType.Date)
            return VfpValue.Number((double)(l.AsDate.DayNumber - r.AsDate.DayNumber));
        if (l.Type == VfpType.DateTime && r.Type == VfpType.DateTime)
            return VfpValue.Number((l.AsDateTime - r.AsDateTime).TotalSeconds);
        if (l.Type == VfpType.Date && IsNumeric(r))
        {
            if (l.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(l.AsDate.AddDays(-(int)r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (l.Type == VfpType.DateTime && IsNumeric(r))
        {
            if (l.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(l.AsDateTime.AddSeconds(-r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        return VfpValue.Number(l.AsDouble - r.AsDouble);
    }

    /// <summary>VFP string "-": move the left operand's trailing blanks to the end.</summary>
    private static string MinusConcat(string l, string r)
    {
        int trimmed = l.Length;
        while (trimmed > 0 && l[trimmed - 1] == ' ') trimmed--;
        int trailing = l.Length - trimmed;
        return string.Concat(l.AsSpan(0, trimmed), r, new string(' ', trailing));
    }

    /// <summary>VFP MOD/% : the result takes the sign of the divisor.</summary>
    internal static double Modulo(double a, double b)
    {
        if (b == 0d) return 0d;
        return a - b * Math.Floor(a / b);
    }

    // ===================================================================
    //  EQUALITY + ORDERING
    // ===================================================================

    private static bool ValueEquals(VfpValue l, VfpValue r, EvaluationContext ctx, bool exact)
    {
        if (l.Type == VfpType.Character && r.Type == VfpType.Character)
        {
            if (exact) return StrEqExact(l.AsString, r.AsString, ctx);
            return CompareStrings(l.AsString, r.AsString, OperatorMode(ctx), ctx) == 0;
        }
        if (IsNumeric(l) && IsNumeric(r)) return l.AsDouble == r.AsDouble;
        if (l.Type == VfpType.Logical && r.Type == VfpType.Logical)
            return l.AsLogical == r.AsLogical;
        if ((l.Type is VfpType.Date or VfpType.DateTime) &&
            (r.Type is VfpType.Date or VfpType.DateTime))
            return l.AsDateTime == r.AsDateTime;
        return false;
    }

    /// <summary>True when the active collation is plain ordinal (MACHINE) — the fast path.</summary>
    private static bool IsMachine(EvaluationContext ctx)
        => string.Equals(ctx.Collation.Name, "MACHINE", StringComparison.Ordinal);

    /// <summary>
    /// The length rule of <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>, INLIST and
    /// BETWEEN for two strings (VFP9-verified, see <c>StringComparisonGoldenTests</c>): Xbase SET EXACT OFF cuts the
    /// LEFT operand to the right one's length, SET EXACT ON pads the shorter with blanks; SQL (SET ANSI) cuts the
    /// LONGER to the shorter's length (ANSI OFF) or pads (ANSI ON).
    /// </summary>
    private static StringCompareMode OperatorMode(EvaluationContext ctx) => ctx.SqlSemantics
        ? (ctx.Ansi ? StringCompareMode.Pad : StringCompareMode.Shorter)
        : (ctx.Exact ? StringCompareMode.Pad : StringCompareMode.TruncateLeft);

    /// <summary>
    /// Three-way string comparison under <paramref name="mode"/> and the active collation. Trailing blanks are
    /// significant in the cutting modes (<c>'Ab' = 'A '</c> is false) and insignificant under
    /// <see cref="StringCompareMode.Pad"/>. MACHINE cuts by characters; a weighted collation cuts by head (primary)
    /// weights, so <c>'AEtest' = 'Æ'</c> and <c>'Straße' = 'Stras'</c> hold under GENERAL while <c>'Á' = 'A'</c> does
    /// not (the diacritic tails still count).
    /// </summary>
    internal static int CompareStrings(string l, string r, StringCompareMode mode, EvaluationContext ctx)
    {
        if (IsMachine(ctx)) return MachineCompare(l, r, mode);
        if (ctx.Collation is GeneralCollation general) return general.Compare(l.AsSpan(), r.AsSpan(), mode);
        // A host-supplied collation: cut by characters, then its own three-way compare.
        ReadOnlySpan<char> a = l, b = r;
        if (mode == StringCompareMode.TruncateLeft && a.Length > b.Length) a = a[..b.Length];
        else if (mode == StringCompareMode.Shorter)
        {
            int n = Math.Min(a.Length, b.Length);
            a = a[..n]; b = b[..n];
        }
        return ctx.Collation.Compare(a, b);
    }

    /// <summary>MACHINE: CP1252 byte order, cut by characters (TruncateLeft / Shorter) or blank-padded (Pad).</summary>
    private static int MachineCompare(string l, string r, StringCompareMode mode)
    {
        int nl = l.Length, nr = r.Length;
        if (mode == StringCompareMode.TruncateLeft) nl = Math.Min(nl, nr);
        else if (mode == StringCompareMode.Shorter) nl = nr = Math.Min(nl, nr);
        int n = mode == StringCompareMode.Pad ? Math.Max(nl, nr) : Math.Min(nl, nr);
        for (int i = 0; i < n; i++)
        {
            // Below 0x80 a char is its own CP1252 byte; only the rest needs the (not inlined) lookup.
            int x = ' ', y = ' ';
            if (i < nl) { x = l[i]; if (x >= 0x80) x = Cp1252.ToByte((char)x); }
            if (i < nr) { y = r[i]; if (y >= 0x80) y = Cp1252.ToByte((char)y); }
            if (x != y) return x < y ? -1 : 1;
        }
        return mode == StringCompareMode.Pad ? 0 : nl.CompareTo(nr);
    }

    // The "==" operator. Xbase: character for character and of equal length; trailing blanks count and SET COLLATE
    // does not apply ('a' == 'A' is false even under GENERAL). SQL "==" (SqlSemantics): the collation-aware
    // blank-padded comparison, independent of SET ANSI.
    private static bool StrEqExact(string l, string r, EvaluationContext ctx)
    {
        if (ctx.SqlSemantics) return CompareStrings(l, r, StringCompareMode.Pad, ctx) == 0;
        return string.Equals(l, r, StringComparison.Ordinal);
    }

    /// <summary>The Xbase <c>=</c> of two strings under the context's SET EXACT.</summary>
    internal static bool StrEqInexact(string l, string r, EvaluationContext ctx)
        => CompareStrings(l, r, ctx.Exact ? StringCompareMode.Pad : StringCompareMode.TruncateLeft, ctx) == 0;

    /// <summary>
    /// The SQL <c>=</c> operator's STRING comparison, governed by SET ANSI (used when
    /// <see cref="EvaluationContext.SqlSemantics"/> is on — the SQL executor's predicate path).
    /// ANSI OFF: compare up to the SHORTER operand's length, ORDER-INDEPENDENT
    /// (<c>"Smith" = "Sm"</c> and <c>"Sm" = "Smith"</c> are BOTH true). ANSI ON: pad the shorter
    /// operand with blanks for a full-length compare. Collation-aware in both modes.
    /// </summary>
    internal static bool StrEqSql(string l, string r, EvaluationContext ctx)
        => CompareStrings(l, r, ctx.Ansi ? StringCompareMode.Pad : StringCompareMode.Shorter, ctx) == 0;

    /// <summary>The "$" operator: is <paramref name="l"/> contained in <paramref name="r"/>,
    /// honouring the active collation (case/accent-insensitive under GENERAL).</summary>
    internal static bool Contains(string l, string r, EvaluationContext ctx)
    {
        if (IsMachine(ctx))
            return r.Contains(l, StringComparison.Ordinal);
        if (l.Length == 0) return true;
        // Collate the needle ONCE (not re-collated per window position), then look for its
        // weight run inside the haystack's collated key. Sliding by weight-units — not
        // char-units — is required so expansion chars match: "AE" $ "Ætest" is TRUE because
        // the weights of "AE" appear contiguously in the weights of "Ætest".
        byte[] kl = ctx.Collation.GetCollatedKey(l.AsSpan());
        if (kl.Length == 0) return true;
        byte[] kr = ctx.Collation.GetCollatedKey(r.AsSpan());
        if (kl.Length > kr.Length) return false;
        var needle = kl.AsSpan();
        for (int i = 0; i + kl.Length <= kr.Length; i++)
            if (kr.AsSpan(i, kl.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    /// <summary>The relational operators and BETWEEN: strings follow the length rule of <c>=</c>
    /// (<see cref="OperatorMode"/>), every other type <see cref="Compare"/>.</summary>
    internal static int CompareOperands(VfpValue l, VfpValue r, EvaluationContext ctx)
    {
        if (l.Type == VfpType.Character && r.Type == VfpType.Character)
            return CompareStrings(l.AsString, r.AsString, OperatorMode(ctx), ctx);
        return Compare(l, r, ctx);
    }

    /// <summary>Ordering (ORDER BY, MAX/MIN): strings by the active collation over their full length.</summary>
    internal static int Compare(VfpValue l, VfpValue r, EvaluationContext ctx)
    {
        if (l.Type == VfpType.Character && r.Type == VfpType.Character)
            return ctx.Collation.Compare(l.AsString.AsSpan(), r.AsString.AsSpan());
        if (IsNumeric(l) && IsNumeric(r)) return l.AsDouble.CompareTo(r.AsDouble);
        if ((l.Type is VfpType.Date or VfpType.DateTime) &&
            (r.Type is VfpType.Date or VfpType.DateTime))
            return l.AsDateTime.CompareTo(r.AsDateTime);
        if (l.Type == VfpType.Logical && r.Type == VfpType.Logical)
            return l.AsLogical.CompareTo(r.AsLogical);
        // Fallback: numeric coercion.
        return l.AsDouble.CompareTo(r.AsDouble);
    }

    // ===================================================================
    //  FUNCTION DISPATCH
    // ===================================================================

    /// <summary>
    /// Dispatches a VFP function. <paramref name="name"/> MUST already be uppercase
    /// (the AST uppercases once at build time), so the per-record hot path performs
    /// no string casing work here.
    /// </summary>
    public static VfpValue CallFunction(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
    {
        // VFP resolves built-in abbreviations before looking for a user procedure of that spelling.
        name = VfpFunctionAbbreviations.Canonical(name);
        // HOST HOOK (microVFP): a live interpreter may own the runtime STATE functions
        // (SEEK/ALIAS/SELECT/USED/RECNO(alias)/PCOUNT/SYS/EVALUATE/TYPE …) and user-defined
        // procedure/function calls. Consulted FIRST; false falls through to the built-ins below.
        if (row is IVfpFunctionHost host && host.TryInvoke(name, args, ctx, out var hostResult))
            return hostResult;

        if (name == "STRTRAN" && args.Length > 3)
            throw new NotSupportedException("STRTRAN supports only two or three arguments; extended arities are not supported.");

        // Functions that must SEE a .NULL. argument rather than propagate it.
        switch (name)
        {
            case "DATETIME": return MakeDateTime(args);
            case "ISNULL": return VfpValue.Logical(Arg(args, 0).IsNull);
            case "EMPTY": return Empty(Arg(args, 0), ctx);
            case "IIF":
            {
                var c = Arg(args, 0);
                bool t = !c.IsNull && c.AsLogical;
                return t ? Arg(args, 1) : Arg(args, 2);
            }
        }

        // Default .NULL. propagation for everything else — but only for a name the engine knows: an unknown
        // name with a .NULL. argument still reaches the host's unknown-function hook (see NullArgument).
        for (int i = 0; i < args.Length; i++)
            if (args[i].IsNull) return NullArgument(name, args, ctx, row);

        switch (name)
        {
            // ---- string ----
            case "UPPER": return VfpValue.Character(VfpLanguageCase.Convert(Arg(args, 0).AsString, ctx.Encoding, upper: true));
            case "LOWER": return VfpValue.Character(VfpLanguageCase.Convert(Arg(args, 0).AsString, ctx.Encoding, upper: false));
            case "ALLTRIM": return VfpValue.Character(Arg(args, 0).AsString.Trim(' '));
            case "TRIM":
            case "RTRIM": return VfpValue.Character(Arg(args, 0).AsString.TrimEnd(' '));
            case "LTRIM": return VfpValue.Character(Arg(args, 0).AsString.TrimStart(' '));
            case "LEFT": return Left(args);
            case "RIGHT": return Right(args);
            case "SUBSTR": return Substr(args);
            case "STR": return Str(args);
            case "STRZERO": return Strzero(args);
            case "PADL": return Pad(args, 'L');
            case "PADR": return Pad(args, 'R');
            case "PADC": return Pad(args, 'C');
            case "AT": return VfpValue.Integer(At(args, fromEnd: false));
            case "RAT": return VfpValue.Integer(At(args, fromEnd: true));
            case "STUFF": return Stuff(args);
            case "CHRTRAN": return Chrtran(args);
            case "STRTRAN": return Strtran(args);
            case "REPLICATE": return Replicate(args);
            case "SPACE": return VfpValue.Character(new string(' ', Math.Max(0, IntArg(args, 0))));
            case "LEN": return VfpValue.Integer(Arg(args, 0).AsString.Length);
            case "CHR":
            {
                int code = IntArg(args, 0) & 0xFF;
                char ch = ctx.Encoding is { } e1 ? e1.GetChars(new[] { (byte)code })[0]
                                                 : Cp1252.ToChar((byte)code);
                return VfpValue.Character(ch.ToString());
            }
            case "ASC":
            {
                var s = Arg(args, 0).AsString;
                if (s.Length == 0) return VfpValue.Integer(0);
                byte b = ctx.Encoding is { } e2 ? e2.GetBytes(new[] { s[0] })[0]
                                                : Cp1252.ToByte(s[0]);
                return VfpValue.Integer(b);
            }

            // ---- convert ----
            case "VAL": return VfpValue.Number(Val(Arg(args, 0).AsString));
            case "CTOD": return Ctod(Arg(args, 0).AsString);
            case "CTOT": return Ctot(Arg(args, 0).AsString);
            // VFP9: a Date becomes midnight, including the empty Date; NULL propagates above.
            case "DTOT": return Arg(args, 0).Type == VfpType.Date
                ? VfpValue.DateTime(Arg(args, 0).AsDateTime) : VfpValue.Null;
            case "DTOC":
            {
                var d = Arg(args, 0).AsDate;
                return VfpValue.Character(d == default ? new string(' ', 10)
                                                       : d.ToString("MM/dd/yyyy", Inv));
            }
            case "DTOS":
            {
                var d = Arg(args, 0).AsDate;
                // VFP: an empty/blank date yields 8 spaces, NOT "00010101".
                return VfpValue.Character(d == default ? new string(' ', 8)
                                                       : d.ToString("yyyyMMdd", Inv));
            }
            case "TTOC": return VfpValue.Character(Arg(args, 0).AsDateTime.ToString("MM/dd/yyyy hh:mm:ss tt", Inv));

            // ---- date ----
            case "DATE": return VfpValue.Date(DateOnly.FromDateTime(DateTime.Today));
            case "DAY": return VfpValue.Integer(Arg(args, 0).AsDate.Day);
            case "MONTH": return VfpValue.Integer(Arg(args, 0).AsDate.Month);
            case "YEAR": return VfpValue.Integer(Arg(args, 0).AsDate.Year);
            case "DOW": return VfpValue.Integer((int)Arg(args, 0).AsDate.DayOfWeek + 1);
            case "CDOW": return VfpValue.Character(Inv.DateTimeFormat.GetDayName(Arg(args, 0).AsDate.DayOfWeek));
            case "CMONTH": return VfpValue.Character(Inv.DateTimeFormat.GetMonthName(Arg(args, 0).AsDate.Month));
            case "GOMONTH": return Gomonth(args);

            // ---- numeric ----
            case "INT": return VfpValue.Number(Math.Truncate(Arg(args, 0).AsDouble));
            case "ROUND": return VfpValue.Number(Round(Arg(args, 0).AsDouble, IntArg(args, 1)));
            case "ABS": return VfpValue.Number(Math.Abs(Arg(args, 0).AsDouble));
            case "MOD": return VfpValue.Number(Modulo(Arg(args, 0).AsDouble, Arg(args, 1).AsDouble));
            case "MAX": return MaxMin(args, ctx, max: true);
            case "MIN": return MaxMin(args, ctx, max: false);

            // ---- system / logical ----
            case "RECNO": return VfpValue.Integer(row.RecNo);
            case "RECCOUNT": return VfpValue.Integer(row.RecCount);
            case "DELETED": return VfpValue.Logical(row.Deleted);
            case "BETWEEN": return Between(args, ctx);
            case "INLIST": return Inlist(args, ctx);

            default:
                // The §C.17/§C.18 string/DBCS-variant extras are dispatched in a SEPARATE method (below),
                // NOT inline in this giant switch: keeping CallFunction's body — and hence its native stack
                // frame, which the JIT allocates in full on entry even though a user-function call returns
                // early at the host hook above — at its prior size is what preserves the MaxCallDepth
                // recursion headroom (CallFunction sits on EVERY nested UDF call). Same rationale as the
                // interpreter's TryInvokeFileIo split. Genuinely unknown names end in UnknownFunction.
                return CallStringExtras(name, args, ctx, row);
        }
    }

    /// <summary>The §C.17/§C.18 string / DBCS-variant additions (LEFTC/RIGHTC/SUBSTRC/STUFFC/CHRTRANC,
    /// AT_C/RATC/ATCC, ISLEADBYTE, LIKE/LIKEC, NORMALIZE, TXTWIDTH), split out of <see cref="CallFunction"/>'s
    /// hot switch on purpose (see its <c>default</c> arm). On a single-byte code page (CP1252, our target)
    /// one char == one byte, so each C-variant is byte-identical to its base function (verified against the
    /// VFP9 runtime); they exist purely for VFP9 name compatibility (FLAGGED: real double-byte behaviour is
    /// untested). A genuinely unknown name goes to <see cref="UnknownFunction"/>.</summary>
    private static VfpValue CallStringExtras(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
    {
        switch (name)
        {
            case "LEFTC": return Left(args);
            case "RIGHTC": return Right(args);
            case "SUBSTRC": return Substr(args);
            case "LENC": return VfpValue.Integer(Arg(args, 0).AsString.Length);
            case "STUFFC": return Stuff(args);
            case "CHRTRANC": return Chrtran(args);
            case "AT_C": return VfpValue.Integer(At(args, fromEnd: false));
            case "RATC": return VfpValue.Integer(At(args, fromEnd: true));
            case "ATC": return VfpValue.Integer(At(args, false, StringComparison.OrdinalIgnoreCase));
            case "ATCC": return VfpValue.Integer(At(args, fromEnd: false, StringComparison.OrdinalIgnoreCase));
            // ISLEADBYTE(cExpr): the first byte is a DBCS lead byte. Always .F. on a single-byte code page
            // (VFP9-verified). Takes ONE argument only (a 2nd arg raises an error in VFP9); we ignore extras.
            case "ISLEADBYTE": return VfpValue.Logical(false);
            // LIKE(cPattern, cString) / LIKEC (its DBCS twin, identical on CP1252): anchored wildcard match,
            // '*' = any run (incl. empty), '?' = exactly one char, everything else literal, case-SENSITIVE,
            // trailing blanks significant on both sides (FOXPLUS default). No bracket classes.
            case "LIKE":
            case "LIKEC": return VfpValue.Logical(Like(Arg(args, 0).AsString, Arg(args, 1).AsString));
            case "NORMALIZE": return VfpValue.Character(VfpExpressionText.Normalize(Arg(args, 0).AsString));
            // TXTWIDTH(cString [, cFont, nSize, cStyle]): headless — no GDI/font engine. Returns the CHARACTER
            // COUNT, which is EXACT for a fixed-pitch font (VFP: TXTWIDTH(s,'Courier New',10) == LEN(s)) and a
            // FLAGGED approximation for the proportional desktop font (that value is GUI-/machine-bound).
            case "TXTWIDTH": return VfpValue.Integer(Arg(args, 0).AsString.Length);
            default: return UnknownFunction(name, args, ctx, row);
        }
    }

    /// <summary>
    /// Every name <see cref="CallFunction"/> and <see cref="CallStringExtras"/> dispatch on. Consulted ONLY when an
    /// argument is <c>.NULL.</c> (the default propagation short-circuits before the switch), so an unknown name
    /// with a <c>.NULL.</c> argument still reaches the host. Keep it in step with both switches — a test scans
    /// their <c>case</c> labels against this set.
    /// </summary>
    internal static readonly System.Collections.Frozen.FrozenSet<string> EngineFunctionNames =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(new[]
        {
            "ISNULL", "EMPTY", "IIF", "ICASE",
            "UPPER", "LOWER", "ALLTRIM", "TRIM", "RTRIM", "LTRIM", "LEFT", "RIGHT", "SUBSTR", "STR", "STRZERO",
            "PADL", "PADR", "PADC", "AT", "RAT", "STUFF", "CHRTRAN", "STRTRAN", "REPLICATE", "SPACE", "LEN",
            "CHR", "ASC",
            "VAL", "CTOD", "CTOT", "DTOT", "DTOC", "DTOS", "TTOC",
            "DATE", "DATETIME", "DAY", "MONTH", "YEAR", "DOW", "CDOW", "CMONTH", "GOMONTH",
            "INT", "ROUND", "ABS", "MOD", "MAX", "MIN",
            "RECNO", "RECCOUNT", "DELETED", "BETWEEN", "INLIST",
            "LEFTC", "RIGHTC", "SUBSTRC", "LENC", "STUFFC", "CHRTRANC", "AT_C", "RATC", "ATC", "ATCC", "ISLEADBYTE",
            "LIKE", "LIKEC", "NORMALIZE", "TXTWIDTH",
        }, StringComparer.Ordinal);

    /// <summary>The first function the index engine cannot evaluate, with VFP abbreviations expanded.</summary>
    internal static string? UnsupportedIndexFunction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? unsupported = null;
        try
        {
            VfpExpression.Parse(text).ContainsFunction((name, _) =>
            {
                name = VfpFunctionAbbreviations.Canonical(name);
                if (EngineFunctionNames.Contains(name)) return false;
                unsupported = name;
                return true;
            });
        }
        catch (ExpressionException) { } // The caller also rejects unparseable definitions.
        return unsupported;
    }

    // Out of CallFunction's frame on purpose (MaxCallDepth headroom, see its default arm): the .NULL.-argument
    // tail. A known name propagates .NULL.; an unknown one is reported like any other unknown call.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static VfpValue NullArgument(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
        => EngineFunctionNames.Contains(name) ? VfpValue.Null : UnknownFunction(name, args, ctx, row);

    /// <summary>A name neither the host nor the engine implements, already expanded by CallFunction.
    /// The host decides the failure; without one (SQL / ADO.NET / index evaluation), it yields NULL.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static VfpValue UnknownFunction(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
    {
        return row is IVfpFunctionHost host ? host.UnknownFunction(name, args) : VfpValue.Null;
    }

    private static VfpValue Arg(VfpValue[] a, int i) => i < a.Length ? a[i] : VfpValue.Null;
    private static VfpValue MakeDateTime(VfpValue[] args)
    {
        if (args.Length == 0) return VfpValue.DateTime(DateTime.Now);
        if (args.Length < 3)
            throw new ExpressionException("Too few arguments.") { VfpErrorNumber = 1229 };
        if (args.Length > 6)
            throw new ExpressionException("Too many arguments.") { VfpErrorNumber = 1230 };

        // VFP substitutes today's date components for NULL, but zero for omitted/NULL time parts.
        var today = DateTime.Today;
        int[] parts = { today.Year, today.Month, today.Day, 0, 0, 0 };
        int[] minimum = { 100, 1, 1, 0, 0, 0 };
        int[] maximum = { 9999, 12, 31, 23, 59, 59 };
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull) continue;
            double value = Math.Truncate(args[i].AsDouble);
            if (!IsNumeric(args[i]) || !double.IsFinite(value) || value < minimum[i] || value > maximum[i])
                throw new ExpressionException("Function argument value, type, or count is invalid.") { VfpErrorNumber = 11 };
            parts[i] = (int)value;
        }
        // An impossible day within the component ranges (e.g. February 29, 2023) is the empty T value.
        if (parts[2] > DateTime.DaysInMonth(parts[0], parts[1])) return VfpValue.DateTime(default);
        return VfpValue.DateTime(new DateTime(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]));
    }

    private static int IntArg(VfpValue[] a, int i) => i < a.Length ? (int)a[i].AsDouble : 0;

    /// <summary>Truthiness of a condition value the SAME way IIF/ICASE treat it: a <c>.NULL.</c> is false,
    /// otherwise the logical value. Used by the compiled + tree-walk ICASE special form.</summary>
    internal static bool AsCondition(VfpValue v) => !v.IsNull && v.AsLogical;

    private static VfpValue Left(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int n = Math.Clamp(IntArg(a, 1), 0, s.Length);
        return VfpValue.Character(s.Substring(0, n));
    }

    private static VfpValue Right(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int n = Math.Clamp(IntArg(a, 1), 0, s.Length);
        return VfpValue.Character(s.Substring(s.Length - n));
    }

    private static VfpValue Substr(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int start = IntArg(a, 1);
        if (start < 1) start = 1;
        int i = start - 1;
        if (i >= s.Length) return VfpValue.Character(string.Empty);
        int len = a.Length > 2 ? IntArg(a, 2) : s.Length - i;
        if (len < 0) len = 0;
        if (i + len > s.Length) len = s.Length - i;
        return VfpValue.Character(s.Substring(i, len));
    }

    private static VfpValue Str(VfpValue[] a)
    {
        double n = Arg(a, 0).AsDouble;
        int len = a.Length > 1 ? IntArg(a, 1) : 10;
        int dec = a.Length > 2 ? IntArg(a, 2) : 0;
        return VfpValue.Character(StrCore(n, len, dec));
    }

    /// <summary>
    /// VFP STR(): right-justified, leading-space padded. On overflow VFP only returns
    /// asterisks when the value HAS a decimal point and the width cannot even hold the
    /// integer digits; otherwise it REDUCES the decimal count to fit. So
    /// STR(1234.5,6,2) = "1234.5", not "******".
    /// </summary>
    private static string StrCore(double n, int len, int dec)
    {
        if (len < 0) len = 0;
        if (dec < 0) dec = 0;
        if (!double.IsFinite(n)) return new string('*', len);

        // Integer-digit count of the rounded-to-zero magnitude, including a sign slot.
        double absInt = Math.Floor(Math.Abs(n));
        int intDigits = absInt < 1 ? 1 : (int)Math.Floor(Math.Log10(absInt)) + 1;
        if (n < 0) intDigits++; // room for '-'

        if (dec > 0 && len <= intDigits)
            return new string('*', len);

        // Shrink decimals to whatever fits: width - intDigits - 1 (the '.').
        int effectiveDec = dec > 0 ? Math.Clamp(len - intDigits - 1, 0, dec) : 0;
        double rounded = Round(n, effectiveDec);
        string s = rounded.ToString("F" + effectiveDec, Inv);
        if (s.Length > len) return new string('*', len);
        return s.PadLeft(len);
    }

    private static VfpValue Strzero(VfpValue[] a)
    {
        double n = Arg(a, 0).AsDouble;
        int len = a.Length > 1 ? IntArg(a, 1) : 10;
        int dec = a.Length > 2 ? IntArg(a, 2) : 0;
        // VFP STRZERO == STR() then replace each LEADING space with '0' (keeping the
        // sign in place): STRZERO(-1234,8) = "000-1234"; STRZERO(-1234,4) = "****".
        string s = StrCore(n, len, dec);
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length && chars[i] == ' '; i++)
            chars[i] = '0';
        return VfpValue.Character(new string(chars));
    }

    private static VfpValue Pad(VfpValue[] a, char mode)
    {
        string s = Arg(a, 0).AsString;
        int len = IntArg(a, 1);
        if (len < 0) len = 0;
        char pad = ' ';
        if (a.Length > 2)
        {
            var p = a[2].AsString;
            if (p.Length > 0) pad = p[0];
        }
        if (s.Length >= len)
            return VfpValue.Character(s.Substring(0, len));
        int total = len - s.Length;
        switch (mode)
        {
            case 'L': return VfpValue.Character(new string(pad, total) + s);
            case 'R': return VfpValue.Character(s + new string(pad, total));
            default:
                int left = total / 2;
                int right = total - left;
                return VfpValue.Character(new string(pad, left) + s + new string(pad, right));
        }
    }

    private static int At(VfpValue[] a, bool fromEnd, StringComparison cmp = StringComparison.Ordinal)
    {
        string f = Arg(a, 0).AsString;
        string w = Arg(a, 1).AsString;
        int occ = a.Length > 2 ? IntArg(a, 2) : 1;
        if (occ < 1) occ = 1;
        if (f.Length == 0) return 0;
        int idx = -1;
        if (!fromEnd)
        {
            int from = 0;
            for (int k = 0; k < occ; k++)
            {
                idx = w.IndexOf(f, from, cmp);
                if (idx < 0) break;
                from = idx + 1;
            }
        }
        else
        {
            int start = w.Length - 1;
            for (int k = 0; k < occ; k++)
            {
                if (start < 0) { idx = -1; break; }
                idx = w.LastIndexOf(f, start, cmp);
                if (idx < 0) break;
                start = idx - 1;
            }
        }
        return idx < 0 ? 0 : idx + 1;
    }

    private static VfpValue Stuff(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        int start = IntArg(a, 1);
        int len = IntArg(a, 2);
        string repl = Arg(a, 3).AsString;
        int i = start - 1;
        if (i < 0) i = 0;
        if (i > s.Length) i = s.Length;
        if (len < 0) len = 0;
        if (i + len > s.Length) len = s.Length - i;
        return VfpValue.Character(string.Concat(s.AsSpan(0, i), repl, s.AsSpan(i + len)));
    }

    private static VfpValue Chrtran(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        string from = Arg(a, 1).AsString;
        string to = Arg(a, 2).AsString;
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            int idx = from.IndexOf(ch);
            if (idx < 0) sb.Append(ch);
            else if (idx < to.Length) sb.Append(to[idx]);
            // else: char is deleted (replacement shorter than search).
        }
        return VfpValue.Character(sb.ToString());
    }

    private static VfpValue Strtran(VfpValue[] a)
    {
        if (a.Length > 3)
            throw new NotSupportedException("STRTRAN supports only two or three arguments; extended arities are not supported.");

        string source = Arg(a, 0).AsString;
        string sought = Arg(a, 1).AsString;
        string replacement = a.Length >= 3 ? Arg(a, 2).AsString : string.Empty;
        return VfpValue.Character(sought.Length == 0
            ? source
            : source.Replace(sought, replacement, StringComparison.Ordinal));
    }

    /// <summary>VFP <c>LIKE()</c> wildcard match: <c>*</c> matches any run (including empty), <c>?</c>
    /// matches exactly one character, every other character is literal. Case-SENSITIVE and fully anchored
    /// (the whole string must match), so trailing blanks are significant on both sides. Iterative
    /// backtracking matcher (no recursion, linear-ish).</summary>
    private static bool Like(string pat, string s)
    {
        int p = 0, si = 0, star = -1, ss = 0;
        while (si < s.Length)
        {
            if (p < pat.Length && (pat[p] == '?' || pat[p] == s[si])) { p++; si++; }
            else if (p < pat.Length && pat[p] == '*') { star = p; ss = si; p++; }
            else if (star != -1) { p = star + 1; ss++; si = ss; }
            else return false;
        }
        while (p < pat.Length && pat[p] == '*') p++;
        return p == pat.Length;
    }

    private static VfpValue Replicate(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        int n = Math.Max(0, IntArg(a, 1));
        if (n == 0 || s.Length == 0) return VfpValue.Character(string.Empty);
        var sb = new StringBuilder(s.Length * n);
        for (int k = 0; k < n; k++) sb.Append(s);
        return VfpValue.Character(sb.ToString());
    }

    private static double Val(string s)
    {
        int i = 0, n = s.Length;
        while (i < n && s[i] == ' ') i++;
        int start = i;
        bool any = false;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; any = true; }
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; any = true; }
        }
        if (!any) return 0d;
        return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, Inv, out var d) ? d : 0d;
    }

    private static VfpValue Ctod(string s)
    {
        s = s.Trim();
        string[] formats = { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(s, formats, Inv, DateTimeStyles.None, out var dt))
            return VfpValue.Date(DateOnly.FromDateTime(dt));
        return VfpValue.Date(default);
    }

    private static VfpValue Ctot(string s)
    {
        s = s.Trim();
        if (DateTime.TryParse(s, Inv, DateTimeStyles.None, out var dt))
            return VfpValue.DateTime(dt);
        return VfpValue.DateTime(default);
    }

    private static VfpValue Gomonth(VfpValue[] a)
    {
        var d = Arg(a, 0);
        int m = IntArg(a, 1);
        if (d.Type == VfpType.DateTime)
        {
            // VFP9 returns an empty Date (not DateTime) for GOMONTH(CTOT(''), n).
            if (d.AsDateTime == default) return VfpValue.Date(default);
            try { return VfpValue.DateTime(d.AsDateTime.AddMonths(m)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        if (d.Type == VfpType.Date)
        {
            if (d.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(d.AsDate.AddMonths(m)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        return VfpValue.Date(d.AsDate.AddMonths(m));
    }

    internal static double Round(double n, int dec)
    {
        if (dec >= 0)
            return Math.Round(n, Math.Min(dec, 15), MidpointRounding.AwayFromZero);
        double factor = Math.Pow(10, -dec);
        return Math.Round(n / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }

    private static VfpValue MaxMin(VfpValue[] a, EvaluationContext ctx, bool max)
    {
        if (a.Length == 0) return VfpValue.Null;
        var best = a[0];
        for (int i = 1; i < a.Length; i++)
        {
            int cmp = Compare(a[i], best, ctx);
            if (max ? cmp > 0 : cmp < 0) best = a[i];
        }
        return best;
    }

    private static VfpValue Empty(VfpValue v, EvaluationContext ctx)
    {
        switch (v.Type)
        {
            // VFP9: EMPTY(.NULL.) is .F. — .NULL. is not an empty value (BlankFieldNullBitVfp9OracleTests).
            // Only where a row read separates blank from .NULL. (VfpBlankFields, the microVFP runtime): on the
            // public record/ADO.NET read a blank N/F/D/T/M field IS null, and EMPTY() must keep matching it.
            case VfpType.Null: return VfpValue.Logical(!ctx.VfpBlankFields);
            case VfpType.Character: return VfpValue.Logical(v.AsString.Trim().Length == 0);
            case VfpType.Numeric:
            case VfpType.Currency:
            case VfpType.Integer: return VfpValue.Logical(v.AsDouble == 0d);
            case VfpType.Logical: return VfpValue.Logical(!v.AsLogical);
            case VfpType.Date: return VfpValue.Logical(v.AsDate == default);
            case VfpType.DateTime: return VfpValue.Logical(v.AsDateTime == default);
            default: return VfpValue.Logical(true);
        }
    }

    private static VfpValue Between(VfpValue[] a, EvaluationContext ctx)
    {
        var v = Arg(a, 0); var lo = Arg(a, 1); var hi = Arg(a, 2);
        return VfpValue.Logical(CompareOperands(v, lo, ctx) >= 0 && CompareOperands(v, hi, ctx) <= 0);
    }

    private static VfpValue Inlist(VfpValue[] a, EvaluationContext ctx)
    {
        if (a.Length == 0) return VfpValue.Logical(false);
        var v = a[0];
        for (int i = 1; i < a.Length; i++)
            if (ValueEquals(v, a[i], ctx, exact: false)) return VfpValue.Logical(true);
        return VfpValue.Logical(false);
    }
}
