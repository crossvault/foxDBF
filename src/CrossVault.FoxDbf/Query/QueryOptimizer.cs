using System.Globalization;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Query;

/// <summary>
/// A Rushmore-style query OPTIMIZER (plan §D8, faithful to CodeBase <c>m4map.c</c>):
/// given a <see cref="DbfTable"/>, its open <see cref="CdxFile"/> and a VFP boolean
/// FILTER expression, produce the matching record numbers efficiently by driving
/// index seeks instead of a full table scan — WITHOUT ever changing the result set.
/// </summary>
/// <remarks>
/// Algorithm (§D8):
/// <list type="number">
///   <item>Parse the filter into a boolean tree (AND / OR / NOT over leaf conditions).</item>
///   <item>Recognise OPTIMIZABLE leaves — <c>(field | tag-key-expr) (op) (constant)</c>
///   whose field/expression matches a CDX tag KEY — and build a per-leaf
///   <see cref="RecordBitmap"/> by scanning the tag (equality → exact range;
///   <c>&lt;</c>/<c>&lt;=</c>/<c>&gt;</c>/<c>&gt;=</c> → one-sided range; BETWEEN /
///   low+high on one tag → a single window; <c>&lt;&gt;</c> → set-all-minus-range).</item>
///   <item>COMBINE per the boolean tree: AND intersects, OR unions, NOT flips; an OR
///   with ANY non-optimizable operand collapses the whole OR to non-optimizable
///   (its operand's candidate is the all-set universe, so the union is the universe).</item>
///   <item>SCAN the resulting candidate set (or all records when nothing optimized) and
///   evaluate the full compiled filter to confirm each match (the residual).</item>
/// </list>
/// CORRECTNESS is paramount: the candidate set is always a conservative SUPERSET of the
/// true matches, so the residual confirmation returns EXACTLY the full-scan result.
/// The optimizer NEVER throws: any analysis failure falls back to a correct full scan.
/// </remarks>
public static class QueryOptimizer
{
    /// <summary>
    /// Find every record matching <paramref name="filter"/> over <paramref name="table"/>,
    /// using <paramref name="cdx"/>'s tags to optimize where possible (plan §D8). When
    /// <paramref name="cdx"/> is <see langword="null"/> or no leaf is optimizable the
    /// filter falls back to a full scan — still returning the exact same set. The optional
    /// <paramref name="context"/> supplies SET EXACT / the active collation used to evaluate
    /// the residual (defaults to VFP defaults: EXACT OFF, MACHINE collation).
    /// </summary>
    // <paramref name="entrySource"/> is the OPTIONAL Highlike accelerator seam (design §2/§5, Peak 2): a
    // dependency-free delegate that, given a CDX tag, returns the SAME decoded index entries
    // CdxTag.EnumerateEntries would yield — served from a warm cross-query cache instead of re-walking the
    // B-tree. When null (the default) the optimizer walks the tree itself, exactly as before. MEMOIZATION
    // only: the candidate-build / seek logic below is UNCHANGED and the supplied entries must be byte-for-
    // byte what the live walk produces, so the result set is provably identical.
    // <paramref name="onIndexEntryExamined"/> is the OPTIONAL diagnostic INSTRUMENTATION seam (perf §D8
    // Rushmore seek): a dependency-free probe invoked ONCE for every index entry the candidate BUILD
    // actually examines (a B-tree key it decodes / compares while assembling a leaf's bitmap). It is the
    // deterministic, timing-free proof that the build is O(log n + matches) for a seekable ordered-key
    // predicate rather than O(n): a full scan invokes it ~n times, a seek invokes it ~(log n + matches).
    // It NEVER influences the result set or which records are scanned — purely an observation hook.
    public static QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null,
        IOptimizerPolicy? policy = null, Func<CdxTag, IEnumerable<IndexEntry>>? entrySource = null,
        Action<CdxTag>? onIndexEntryExamined = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var ctx = context ?? EvaluationContext.Default;
        int recordCount = table.RecordCount;

        // (1)-(3) Build the candidate set via index analysis. Best-effort: anything we
        // cannot understand collapses to the all-records universe (a correct full scan).
        Analysis analysis;
        try
        {
            var tokens = Tokenize(filter ?? string.Empty);
            var parser = new BoolParser(tokens, cdx, recordCount, ctx.Collation?.Name, optimize: ctx.Optimize, policy: policy, entrySource: entrySource, onIndexEntryExamined: onIndexEntryExamined);
            analysis = parser.ParseTop();
        }
        catch
        {
            analysis = Analysis.NonOptimized(recordCount);
        }

        // The correctness anchor: the FULL compiled filter, evaluated on every candidate.
        Func<IRowContext, VfpValue> compiled;
        try
        {
            compiled = VfpExpression.Parse(filter ?? string.Empty).Compile(ctx);
        }
        catch
        {
            return new QueryResult(Array.Empty<int>(), false, false, 0);
        }

        bool optimized = analysis.UsedIndex;
        bool fullyOptimized = analysis.UsedIndex && analysis.Exact;

        // (4)-(5) Residual scan: confirm the full filter on each candidate record.
        var hits = new List<int>();
        int scanned = 0;

        // OPTIMIZED path is taken ONLY for SET DELETED ON: the CDX index does not include
        // deleted records, so its candidate bitmap would be incomplete under SET DELETED OFF.
        // For SET DELETED OFF (and any non-optimized / unoptimizable filter) we keep the full
        // EnumerateAll scan below. The candidate bitmap holds 0-based physical record indices.
        bool useCandidates = optimized && ctx.Deleted;

        if (useCandidates)
        {
            // PEAK 7 — true late materialization: read ONLY the survivors. Iterate the candidate
            // physical indices and random-access each via GetRecord, instead of materializing every
            // row buffer with EnumerateAll. The result is byte-for-byte identical to the full scan:
            //  - recno = index + 1 (0-based physical index → 1-based record number);
            //  - GetRecord returns null for a DELETED record, so SET DELETED ON still skips deleted
            //    candidates (the CDX indexes deleted rows, so a candidate may be deleted);
            //  - GetRecord reads the same buffer EnumerateAll would, so memo / _NullFlags fields
            //    resolve identically;
            //  - Enumerate yields ascending indices, and hits are sorted ascending below.
            foreach (int idx in analysis.Bitmap.Enumerate())
            {
                if ((uint)idx >= (uint)recordCount) continue;
                var rec = table.GetRecord(idx); // null ⇒ deleted (skip) — implicit AND NOT DELETED()
                if (rec is null) continue;

                int recno = idx + 1; // 1-based physical record number
                scanned++;
                var v = compiled(new RowAdapter(rec.Value, recno, recordCount));
                if (v.Type == VfpType.Logical && v.AsLogical)
                    hits.Add(recno);
            }
        }
        else
        {
            // NON-optimized / SET DELETED OFF: full scan over ALL records (including deleted) to
            // keep recno = physical record number. Behaviour is UNCHANGED from before Peak 7.
            int recno = 0;
            foreach (var rec in table.EnumerateAll(includeDeleted: true))
            {
                recno++; // 1-based physical record number

                // SET DELETED ON (ctx.Deleted = true): implicit AND NOT DELETED() excludes deleted rows.
                if (ctx.Deleted && rec.IsDeleted) continue;

                scanned++;
                var v = compiled(new RowAdapter(rec, recno, recordCount));
                if (v.Type == VfpType.Logical && v.AsLogical)
                    hits.Add(recno);
            }
        }

        hits.Sort();
        return new QueryResult(hits, optimized, fullyOptimized, scanned);
    }

    /// <summary>
    /// Count every record matching <paramref name="filter"/> over <paramref name="table"/> — the SAME
    /// number <see cref="FindRecords"/> would return as <c>RecordNumbers.Count</c>, computed more
    /// cheaply. It reuses the SAME candidate-build path as <see cref="FindRecords"/> (single source of
    /// truth), but when the candidate set is EXACT (fully optimized — no residual term remains) it
    /// returns the candidate COUNT WITHOUT building a recno list and WITHOUT reading full records (only
    /// a 1-byte deletion-flag check per candidate under SET DELETED ON); when a residual remains it
    /// iterates the candidates evaluating the residual and counts matches WITHOUT allocating a recno
    /// list. NEVER throws: any uncertainty falls back to <c>FindRecords(...).RecordNumbers.Count</c>.
    /// <para>
    /// HARD INVARIANT: <c>Count(filter) == FindRecords(filter).RecordNumbers.Count</c>, ALWAYS — for
    /// every shape, with or without an accelerator, under any SET EXACT / SET DELETED combination.
    /// </para>
    /// </summary>
    public static int Count(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null,
        IOptimizerPolicy? policy = null, Func<CdxTag, IEnumerable<IndexEntry>>? entrySource = null,
        Action<CdxTag>? onIndexEntryExamined = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        try
        {
            var ctx = context ?? EvaluationContext.Default;
            int recordCount = table.RecordCount;

            // (1)-(3) Build the candidate set via the SAME index analysis FindRecords drives — the
            // single source of truth. Anything we cannot understand collapses to the all-records
            // universe (a correct full scan), exactly as FindRecords does.
            Analysis analysis;
            try
            {
                var tokens = Tokenize(filter ?? string.Empty);
                var parser = new BoolParser(tokens, cdx, recordCount, ctx.Collation?.Name, optimize: ctx.Optimize, policy: policy, entrySource: entrySource, onIndexEntryExamined: onIndexEntryExamined);
                analysis = parser.ParseTop();
            }
            catch
            {
                analysis = Analysis.NonOptimized(recordCount);
            }

            // The correctness anchor: the FULL compiled filter (used only when a residual remains).
            Func<IRowContext, VfpValue> compiled;
            try
            {
                compiled = VfpExpression.Parse(filter ?? string.Empty).Compile(ctx);
            }
            catch
            {
                return 0; // mirror FindRecords: an uncompilable filter matches NOTHING (count 0).
            }

            bool optimized = analysis.UsedIndex;
            bool fullyOptimized = analysis.UsedIndex && analysis.Exact;

            // OPTIMIZED candidate path is taken ONLY for SET DELETED ON (mirrors FindRecords): under
            // SET DELETED OFF the recno = physical-record-number requirement means a full scan.
            bool useCandidates = optimized && ctx.Deleted;

            int count = 0;
            // The no-read fast path is sound ONLY for a POSITIVE exact candidate — one that is a SUBSET
            // of decodable rows that provably satisfy the compiled filter (point/range/window/INLIST and
            // AND/OR of positives). A COMPLEMENTED candidate (a NOT over an exact leaf) is exact in
            // set-algebra terms but NOT residual-free under VFP three-valued logic: RecordBitmap.Not()
            // includes recnos with a NULL/undecodable key, which the compiled filter evaluates to .NULL.
            // (not .T.), so FindRecords — which always runs the residual — excludes them. Counting them
            // would break Count==FindRecords (e.g. `NOT (ID = 3)` over a NULL ID, SET DELETED ON). So
            // require !analysis.Complemented for the fast path; otherwise fall through to the residual loop.
            bool noResidualFastPath = fullyOptimized && !analysis.Complemented;
            if (useCandidates)
            {
                if (noResidualFastPath)
                {
                    // EXACT positive (fully optimized, not complemented): the candidate set IS the answer
                    // — no residual term remains, so we count the candidates WITHOUT building a recno list
                    // and WITHOUT reading full records. Under SET DELETED ON the CDX can index deleted
                    // rows, which FindRecords drops via GetRecord==null; we mirror that by EXCLUDING any
                    // deleted candidate through a 1-byte deletion-flag probe (never a whole-record read).
                    foreach (int idx in analysis.Bitmap.Enumerate())
                    {
                        if ((uint)idx >= (uint)recordCount) continue;
                        if (table.IsRecordDeleted(idx)) continue; // implicit AND NOT DELETED()
                        count++;
                    }
                }
                else
                {
                    // RESIDUAL remains: iterate ONLY the candidates (not a full scan), confirm the full
                    // filter on each survivor and COUNT matches — WITHOUT allocating a recno list. This
                    // is byte-for-byte the FindRecords candidate confirmation, only counting instead of
                    // collecting (GetRecord returns null for a deleted candidate ⇒ skipped under DELETED ON).
                    foreach (int idx in analysis.Bitmap.Enumerate())
                    {
                        if ((uint)idx >= (uint)recordCount) continue;
                        var rec = table.GetRecord(idx);
                        if (rec is null) continue;

                        int recno = idx + 1; // 1-based physical record number
                        var v = compiled(new RowAdapter(rec.Value, recno, recordCount));
                        if (v.Type == VfpType.Logical && v.AsLogical) count++;
                    }
                }
            }
            else
            {
                // NON-optimized / SET DELETED OFF: full scan over ALL records (recno = physical record
                // number), confirming the full filter and COUNTING matches — still no recno list.
                int recno = 0;
                foreach (var rec in table.EnumerateAll(includeDeleted: true))
                {
                    recno++; // 1-based physical record number
                    if (ctx.Deleted && rec.IsDeleted) continue; // implicit AND NOT DELETED()
                    var v = compiled(new RowAdapter(rec, recno, recordCount));
                    if (v.Type == VfpType.Logical && v.AsLogical) count++;
                }
            }
            return count;
        }
        catch
        {
            // SAFETY: any uncertainty → the proven-correct full path (never throw).
            try { return FindRecords(table, cdx, filter, context, policy, entrySource, onIndexEntryExamined).RecordNumbers.Count; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Produce a structured <see cref="QueryPlan"/> (the VFP SYS(3054) equivalent) for
    /// <paramref name="filter"/> over <paramref name="table"/> using <paramref name="cdx"/>'s
    /// tags — WITHOUT running the residual scan. The plan reports, per leaf condition, whether
    /// it is index-optimizable (and the TAG that drives it) or the REASON it falls to the
    /// residual, plus an overall Full / Partial / None level. Plan-ONLY (fast) and never throws.
    /// <para>
    /// CRITICAL invariant: this MUST reuse the SAME leaf-classification + tag-selection path as
    /// <see cref="FindRecords"/>, so the plan can never disagree with what execution does.
    /// </para>
    /// </summary>
    public static QueryPlan Explain(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null,
        IOptimizerPolicy? policy = null, Func<CdxTag, IEnumerable<IndexEntry>>? entrySource = null)
    {
        try
        {
            var ctx = context ?? EvaluationContext.Default;
            int recordCount = table?.RecordCount ?? 0;

            // SAME path as FindRecords: tokenize → BoolParser → ParseTop. The ONLY difference is
            // planOnly:true, which records each leaf's classification (into parser.Leaves) and
            // skips the index-entry scan — the optimizability verdict is unaffected. (planOnly skips
            // the candidate scan, so entrySource is accepted for symmetry but never consulted here.)
            var tokens = Tokenize(filter ?? string.Empty);
            var parser = new BoolParser(tokens, cdx, recordCount, ctx.Collation?.Name, planOnly: true, optimize: ctx.Optimize, policy: policy, entrySource: entrySource);
            var analysis = parser.ParseTop();
            var leaves = parser.Leaves;

            // The distinct tags the optimizer would drive, in first-seen order.
            var used = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in leaves)
                if (c.Optimizable && c.TagName is not null && seen.Add(c.TagName))
                    used.Add(c.TagName);

            // Overall mirrors execution's Optimized flag at the None boundary (single source of
            // truth: analysis.UsedIndex IS what FindRecords reports), then splits the optimized
            // case into Full (EVERY leaf index-resolved) vs Partial (a residual leaf remains).
            OptimizationLevel overall;
            if (!analysis.UsedIndex)
                overall = OptimizationLevel.None;
            else
                overall = leaves.All(c => c.Optimizable) ? OptimizationLevel.Full : OptimizationLevel.Partial;

            return new QueryPlan(overall, leaves, used);
        }
        catch
        {
            // A diagnostic NEVER throws: degrade to a "nothing optimizable" plan.
            return new QueryPlan(OptimizationLevel.None, Array.Empty<ConditionPlan>(), Array.Empty<string>());
        }
    }

    // ============================================================ residual row adapter

    /// <summary>Adapts one <see cref="DbfRecord"/> to the engine's row contract.</summary>
    private sealed class RowAdapter : IRowContext
    {
        private readonly DbfRecord _rec;
        public RowAdapter(DbfRecord rec, int recNo, int recCount)
        {
            _rec = rec; RecNo = recNo; RecCount = recCount;
        }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _rec.IsDeleted;
        public object? GetField(string name) => _rec[name];
    }

    // ============================================================ analysis result

    /// <summary>
    /// The result of analysing a (sub)expression against the indexes: a candidate
    /// <see cref="Bitmap"/> that is ALWAYS a superset of the true matches, plus
    /// <see cref="Exact"/> (the bitmap equals the match set, so a NOT may safely flip it)
    /// and <see cref="UsedIndex"/> (an index narrowed the candidate vs. the full universe).
    /// </summary>
    /// <remarks>
    /// <see cref="Complemented"/> records whether a <see cref="RecordBitmap.Not()"/> ever
    /// contributed to this candidate (a NOT over an exact positive leaf, propagated through any
    /// AND/OR that combines such a subtree). Crucial for <see cref="Count"/>: a complemented
    /// candidate is exact in SET-ALGEBRA terms, but <see cref="RecordBitmap.Not()"/> flips EVERY
    /// bit in <c>[0, recordCount)</c>, so it INCLUDES records whose index key is NULL/undecodable
    /// (those recnos are absent from the positive set, hence present after Not()). Under VFP
    /// three-valued logic the compiled filter returns .NULL. (not .T.) for such a row, so
    /// <see cref="FindRecords"/> — which ALWAYS runs the residual — EXCLUDES it. Count's no-read
    /// fast path must therefore NOT trust a complemented candidate; it has to evaluate the residual
    /// just like FindRecords. (FindRecords itself is unaffected: it never takes a no-residual path.)
    /// </remarks>
    private readonly struct Analysis
    {
        public readonly RecordBitmap Bitmap;
        public readonly bool Exact;
        public readonly bool UsedIndex;
        public readonly bool Complemented;

        public Analysis(RecordBitmap bitmap, bool exact, bool usedIndex, bool complemented = false)
        {
            Bitmap = bitmap; Exact = exact; UsedIndex = usedIndex; Complemented = complemented;
        }

        /// <summary>A non-optimizable node: candidate = the whole record universe.</summary>
        public static Analysis NonOptimized(int recordCount)
        {
            var bm = new RecordBitmap(recordCount);
            bm.SetAll();
            return new Analysis(bm, exact: false, usedIndex: false);
        }
    }

    // ============================================================ comparison operators

    private enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

    private static CmpOp? MapOp(string t) => t switch
    {
        "=" or "==" => CmpOp.Eq,
        "<>" or "!=" or "#" => CmpOp.Ne,
        "<" => CmpOp.Lt,
        "<=" or "=<" => CmpOp.Le,
        ">" => CmpOp.Gt,
        ">=" or "=>" => CmpOp.Ge,
        _ => null,
    };

    private static CmpOp Flip(CmpOp op) => op switch
    {
        CmpOp.Lt => CmpOp.Gt,
        CmpOp.Le => CmpOp.Ge,
        CmpOp.Gt => CmpOp.Lt,
        CmpOp.Ge => CmpOp.Le,
        _ => op, // Eq / Ne are symmetric
    };

    // ============================================================ tokenizer

    private enum TokType { Ident, Number, Str, Cmp, Arith, LParen, RParen, Comma, And, Or, Not, Other }

    private readonly struct Token
    {
        public readonly TokType Type;
        public readonly string Text; // identifiers/keywords uppercased; operators verbatim
        public readonly double Num;  // Number tokens
        public readonly string Str;  // Str tokens (literal value)

        public Token(TokType type, string text, double num = 0, string str = "")
        {
            Type = type; Text = text; Num = num; Str = str;
        }
    }

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(') { list.Add(new Token(TokType.LParen, "(")); i++; continue; }
            if (c == ')') { list.Add(new Token(TokType.RParen, ")")); i++; continue; }
            if (c == ',') { list.Add(new Token(TokType.Comma, ",")); i++; continue; }

            if (c == '\'' || c == '"')
            {
                char q = c; i++; int st = i;
                while (i < n && s[i] != q) i++;
                string val = s.Substring(st, Math.Max(0, i - st));
                if (i < n) i++; // closing quote
                list.Add(new Token(TokType.Str, "'" + val + "'", str: val));
                continue;
            }

            // VFP date / datetime literal: {^YYYY-MM-DD [HH:MM:SS]} (also {YYYY/MM/DD}).
            // We collapse it to a single NUMBER token whose value is the SAME Julian-day
            // (+ fractional-day) scale the Date/DateTime index keys decode to, so the
            // ordered-key path can compare it directly. An unparseable brace blob becomes
            // an Other token → that leaf falls back to the (correct) residual scan.
            if (c == '{')
            {
                int st = i + 1;
                while (i < n && s[i] != '}') i++;
                string body = s.Substring(st, Math.Max(0, i - st));
                if (i < n) i++; // closing brace
                if (TryParseDateLiteral(body, out double jul))
                    list.Add(new Token(TokType.Number, "{" + body + "}", num: jul));
                else
                    list.Add(new Token(TokType.Other, "{" + body + "}"));
                continue;
            }

            if (c == '.')
            {
                if (i + 1 < n && char.IsDigit(s[i + 1])) { i = ReadNumber(s, i, list); continue; }
                int st = i + 1, j = st;
                while (j < n && char.IsLetter(s[j])) j++;
                string word = s.Substring(st, j - st).ToUpperInvariant();
                int end = (j < n && s[j] == '.') ? j + 1 : j;
                i = end;
                switch (word)
                {
                    case "AND": list.Add(new Token(TokType.And, "AND")); break;
                    case "OR": list.Add(new Token(TokType.Or, "OR")); break;
                    case "NOT": list.Add(new Token(TokType.Not, "NOT")); break;
                    default: list.Add(new Token(TokType.Other, "." + word)); break;
                }
                continue;
            }

            if (char.IsDigit(c)) { i = ReadNumber(s, i, list); continue; }

            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                string up = s.Substring(st, i - st).ToUpperInvariant();
                list.Add(up switch
                {
                    "AND" => new Token(TokType.And, "AND"),
                    "OR" => new Token(TokType.Or, "OR"),
                    "NOT" => new Token(TokType.Not, "NOT"),
                    _ => new Token(TokType.Ident, up),
                });
                continue;
            }

            if (c is '<' or '>' or '=' or '!' or '#')
            {
                int st = i;
                while (i < n && s[i] is '<' or '>' or '=' or '!' or '#') i++;
                list.Add(new Token(TokType.Cmp, s.Substring(st, i - st)));
                continue;
            }

            if (c is '+' or '-' or '*' or '/')
            {
                list.Add(new Token(TokType.Arith, c.ToString())); i++; continue;
            }

            list.Add(new Token(TokType.Other, c.ToString())); i++;
        }
        return list;
    }

    private static int ReadNumber(string s, int i, List<Token> list)
    {
        int st = i, n = s.Length;
        bool dot = false;
        while (i < n && (char.IsDigit(s[i]) || (s[i] == '.' && !dot)))
        {
            if (s[i] == '.') dot = true;
            i++;
        }
        string num = s.Substring(st, i - st);
        double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        list.Add(new Token(TokType.Number, num, num: v));
        return i;
    }

    /// <summary>
    /// Parses the BODY of a VFP date/datetime literal (the text between <c>{</c> and
    /// <c>}</c>) into the SAME Julian-day-number (+ fractional day) scale the Date/DateTime
    /// index keys decode to (<see cref="IndexKey.YmdToJulianDay"/> + time-of-day in days).
    /// Accepts the invariant date and optional time formats supported by the expression lexer.
    /// Returns false for empty / malformed / null-date blobs so the
    /// caller can fall back to a non-optimizable (residual) leaf. Never throws.
    /// </summary>
    private static bool TryParseDateLiteral(string body, out double julian)
    {
        julian = 0;
        try
        {
            string s = body.Trim();
            if (s.StartsWith('^')) s = s[1..].Trim();
            if (s.Length == 0) return false; // empty date {} → not a usable constant

            // Split the optional time part off the date part.
            int sp = s.IndexOf(' ');
            string datePart = sp < 0 ? s : s[..sp];
            string timePart = sp < 0 ? string.Empty : s[(sp + 1)..].Trim();

            string[] dateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "M/d/yyyy" };
            if (!DateTime.TryParseExact(datePart, dateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                return false;

            TimeSpan timeOfDay = TimeSpan.Zero;
            if (timePart.Length > 0)
            {
                string[] timeFormats = { "HH:mm:ss", "H:mm:ss", "HH:mm", "h:mm:ss tt", "hh:mm:ss tt" };
                if (!DateTime.TryParseExact(timePart, timeFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time))
                    return false;
                timeOfDay = time.TimeOfDay;
            }

            julian = IndexKey.YmdToJulianDay(date.Year, date.Month, date.Day) + timeOfDay.TotalDays;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ============================================================ boolean parser / analyzer

    /// <summary>
    /// A defensive recursive-descent analyzer over the token stream. It identifies the
    /// boolean structure (NOT &gt; AND &gt; OR precedence) and, at the leaves, the
    /// optimizable comparison / BETWEEN / INLIST shapes whose key matches a CDX tag —
    /// ordered (Integer/Numeric/Date/DateTime) keys via a transformed-value range, and
    /// CHARACTER keys via a collation-transformed superset. Anything else becomes a
    /// non-optimizable (all-records) leaf — always correct.
    /// </summary>
    private sealed class BoolParser
    {
        private readonly List<Token> _t;
        private readonly CdxFile? _cdx;
        private readonly int _rc;
        private readonly string _collation; // the ACTIVE (context) collation name, e.g. MACHINE / GENERAL
        private readonly bool _planOnly;    // EXPLAIN mode: classify leaves but SKIP the index-entry scan
        private readonly bool _optimize;    // SET OPTIMIZE: false → consult no index (plain full scan)
        private readonly IOptimizerPolicy? _policy; // opt-in COST seam (Highlike); null → default Core behaviour
        private readonly Func<CdxTag, IEnumerable<IndexEntry>>? _entrySource; // opt-in MEMOIZATION seam (Highlike warm cache); null → walk the tree
        private readonly Action<CdxTag>? _onExamine; // opt-in INSTRUMENTATION seam: pinged once per index entry the candidate build examines (O(log n) proof)
        private int _pos;

        /// <summary>
        /// The per-leaf classification accumulated while parsing, in source order — the data the
        /// <see cref="Explain"/> ShowPlan reports. Populated by the SAME code path that
        /// <see cref="FindRecords"/> drives, so the plan can never disagree with execution.
        /// </summary>
        public readonly List<ConditionPlan> Leaves = new();

        public BoolParser(List<Token> tokens, CdxFile? cdx, int recordCount, string? collation = null, bool planOnly = false, bool optimize = true, IOptimizerPolicy? policy = null, Func<CdxTag, IEnumerable<IndexEntry>>? entrySource = null, Action<CdxTag>? onIndexEntryExamined = null)
        {
            _t = tokens; _cdx = cdx; _rc = recordCount; _planOnly = planOnly; _optimize = optimize; _policy = policy; _entrySource = entrySource; _onExamine = onIndexEntryExamined;
            _collation = string.IsNullOrWhiteSpace(collation) ? "MACHINE" : collation.Trim();
        }

        /// <summary>
        /// The tag's decoded index entries: served from the Highlike warm cache (the
        /// <see cref="_entrySource"/> seam) when one is attached, otherwise walked live from the B-tree
        /// (<see cref="CdxTag.EnumerateEntries"/>). The cache MUST return exactly what the live walk would,
        /// so the candidate set — and therefore the result — is identical either way.
        /// </summary>
        private IEnumerable<IndexEntry> EntriesOf(CdxTag tag)
            => _entrySource is null ? tag.EnumerateEntries() : _entrySource(tag);

        public Analysis ParseTop() => _t.Count == 0 ? Analysis.NonOptimized(_rc) : ParseOr();

        private Token Peek() => _pos < _t.Count ? _t[_pos] : new Token(TokType.Other, "");
        private void Next() => _pos++;

        private Analysis ParseOr()
        {
            // Remember where this OR subtree's leaves begin so we can DEMOTE them as a group if the
            // OR collapses to a full scan (an OR with any all-set operand spreads the universe, so
            // even a sibling that DID seek a tag has its candidate swallowed — execution seeks
            // nothing, and the plan must agree).
            int leafStart = Leaves.Count;
            var left = ParseAnd();
            bool sawOr = false;
            while (Peek().Type == TokType.Or)
            {
                sawOr = true;
                Next();
                var right = ParseAnd();
                left = new Analysis(
                    left.Bitmap.Or(right.Bitmap),
                    left.Exact && right.Exact,
                    left.UsedIndex && right.UsedIndex, // OR collapses on any all-set operand
                    left.Complemented || right.Complemented); // a complement anywhere taints the union
            }
            // Collapsed OR: the combined subtree no longer drives an index, so any leaf recorded
            // optimized within it would be a phantom seek FindRecords never performs — demote them.
            if (sawOr && !left.UsedIndex) DemoteLeaves(leafStart);
            return left;
        }

        private Analysis ParseAnd()
        {
            // Collect every AND-conjunct first WITHOUT building ordered-comparison leaves yet:
            // an ordered seekable leaf returns a DEFERRED bound (no index walk). This lets us MERGE
            // several constraints on the SAME tag (e.g. ID >= a AND ID <= b) into ONE bounded window
            // seek — otherwise each one-sided leaf would walk a huge open-ended run before the AND
            // trims it. AND is commutative, so grouping bounds by tag regardless of position is safe.
            var conjuncts = new List<Conjunct> { ParseNot() };
            while (Peek().Type == TokType.And)
            {
                Next();
                conjuncts.Add(ParseNot());
            }

            // Intersect deferred bounds per tag; everything else (already-built analyses) passes through.
            // FUSING the same-tag bounds FIRST is what turns a low+high pair (e.g. AMOUNT >= lo AND
            // AMOUNT <= hi) into ONE window — the per-leaf cost verdict (PrefersScan) is carried, not
            // applied, until here, so a wide one-sided bound the cost planner would DROP is still merged
            // into the tight window and driven (sidestepping the threshold for the genuine window).
            var byTag = new Dictionary<string, OrderedAcc>(StringComparer.OrdinalIgnoreCase);
            var tagOrder = new List<string>();
            var charByTag = new Dictionary<string, CharAcc>(StringComparer.OrdinalIgnoreCase);
            var charOrder = new List<string>();
            var built = new List<Analysis>();
            foreach (var c in conjuncts)
            {
                if (c.Bound is OrderedBound b)
                {
                    string k = b.Tag.Name;
                    if (byTag.TryGetValue(k, out var cur)) byTag[k] = cur.Merge(b, c);
                    else { byTag[k] = OrderedAcc.From(b, c); tagOrder.Add(k); }
                }
                else if (c.CBound is CharBound cb)
                {
                    string k = cb.Tag.Name;
                    if (charByTag.TryGetValue(k, out var cur)) charByTag[k] = cur.Merge(cb, c);
                    else { charByTag[k] = CharAcc.From(cb, c); charOrder.Add(k); }
                }
                else
                {
                    built.Add(c.Built!.Value);
                }
            }
            foreach (var k in tagOrder)
            {
                var a = byTag[k];
                built.Add(ResolveOrdered(a.Bound, a.Exact, a.PrefersScan, a.HasLower && a.HasUpper, a.LeafIdx));
            }
            foreach (var k in charOrder)
            {
                var a = charByTag[k];
                built.Add(ResolveChar(a.Bound, a.PrefersScan, a.HasLower && a.HasUpper, a.LeafIdx));
            }

            var acc = built[0];
            for (int i = 1; i < built.Count; i++)
                acc = new Analysis(
                    acc.Bitmap.And(built[i].Bitmap),
                    acc.Exact && built[i].Exact,
                    acc.UsedIndex || built[i].UsedIndex,
                    acc.Complemented || built[i].Complemented); // a complement anywhere taints the intersection
            return acc;
        }

        private Conjunct ParseNot()
        {
            if (Peek().Type == TokType.Not)
            {
                Next();
                int leafStart = Leaves.Count;
                var operand = Force(ParseNot()); // a NOT must flip a concrete bitmap → force any deferral
                if (operand.Exact)
                    // COMPLEMENTED: Bitmap.Not() flips every bit in [0, recordCount), so the complement
                    // includes recnos whose index key is NULL/undecodable (absent from the positive set).
                    // The set is exact, but those rows evaluate to .NULL. (not .T.) under VFP three-valued
                    // logic, so Count must NOT trust this candidate without the residual (see Analysis docs).
                    return Conjunct.OfBuilt(new Analysis(operand.Bitmap.Not(), exact: true, operand.UsedIndex, complemented: true));
                // Inexact operand (e.g. a Character leaf or a DateTime '==' leaf): we cannot flip a
                // mere superset, so the NOT subtree falls to the residual. Any leaf recorded
                // optimized inside it is a phantom seek FindRecords never performs — demote them.
                DemoteLeaves(leafStart);
                return Conjunct.OfBuilt(Analysis.NonOptimized(_rc));
            }
            return ParsePrimary();
        }

        private Conjunct ParsePrimary()
        {
            // Collect the maximal leaf token span (to the next depth-0 AND/OR/RParen/end).
            int start = _pos, depth = 0;
            while (_pos < _t.Count)
            {
                var t = _t[_pos];
                if (depth == 0 && (t.Type == TokType.And || t.Type == TokType.Or || t.Type == TokType.RParen))
                    break;
                if (t.Type == TokType.LParen) depth++;
                else if (t.Type == TokType.RParen) depth--;
                _pos++;
            }
            var span = _t.GetRange(start, _pos - start);
            if (span.Count == 0) return Conjunct.OfBuilt(Analysis.NonOptimized(_rc));

            // A whole operand wrapped in one outer paren pair is a boolean sub-group.
            if (span[0].Type == TokType.LParen && MatchParen(span, 0) == span.Count - 1)
            {
                var inner = span.GetRange(1, span.Count - 2);
                if (inner.Count == 0) return Conjunct.OfBuilt(Analysis.NonOptimized(_rc));
                var sub = new BoolParser(inner, _cdx, _rc, _collation, _planOnly, _optimize, _policy, _entrySource, _onExamine);
                var subAnalysis = sub.ParseOr();
                Leaves.AddRange(sub.Leaves); // bubble nested leaves up in source order
                return Conjunct.OfBuilt(subAnalysis);
            }

            return AnalyzeLeaf(span);
        }

        private static int MatchParen(List<Token> t, int p)
        {
            int depth = 0;
            for (int k = p; k < t.Count; k++)
            {
                if (t[k].Type == TokType.LParen) depth++;
                else if (t[k].Type == TokType.RParen && --depth == 0) return k;
            }
            return -1;
        }

        private Conjunct AnalyzeLeaf(List<Token> span)
        {
            string text = NormText(span);

            // A depth-0 comparison operator → a comparison leaf.
            int depth = 0, cmpAt = -1;
            for (int k = 0; k < span.Count; k++)
            {
                var t = span[k];
                if (t.Type == TokType.LParen) depth++;
                else if (t.Type == TokType.RParen) depth--;
                else if (depth == 0 && t.Type == TokType.Cmp) { cmpAt = k; break; }
            }

            if (cmpAt >= 0)
            {
                var op = MapOp(span[cmpAt].Text);
                if (op is null) return RecordResidual(text, PlanReason.NotSimpleComparison);
                var left = span.GetRange(0, cmpAt);
                var right = span.GetRange(cmpAt + 1, span.Count - cmpAt - 1);
                return ClassifyComparison(text, left, op.Value, right);
            }

            return ClassifyFunction(text, span);
        }

        /// <summary>
        /// Classify (and, when not plan-only, build the candidate bitmap for) a single comparison
        /// leaf. This is the SINGLE source of truth: <see cref="FindRecords"/> consumes the returned
        /// <see cref="Analysis"/> while <see cref="Explain"/> consumes the <see cref="ConditionPlan"/>
        /// recorded into <see cref="Leaves"/> — both produced here from the same tag-selection path.
        /// </summary>
        private Conjunct ClassifyComparison(string text, List<Token> left, CmpOp op, List<Token> right)
        {
            double? rc = AsConst(right), lc = AsConst(left);
            string? rs = AsStringConst(right), ls = AsStringConst(left);

            // ordered key (Integer / Numeric / Date / DateTime) vs numeric/date const
            if (rc is not null && lc is null) return Ordered(text, left, op, rc.Value);
            if (lc is not null) return Ordered(text, right, Flip(op), lc.Value);

            // character key vs string const (collation-transformed SUPERSET)
            if (rs is not null) return Character(text, left, op, rs);
            if (ls is not null) return Character(text, right, Flip(op), ls);

            // Neither side is a recognizable constant (e.g. field-to-field) → residual.
            return RecordResidual(text, PlanReason.NotSimpleComparison);
        }

        private Conjunct Ordered(string text, List<Token> keySpan, CmpOp op, double v)
        {
            // Classify for CORRECTNESS only (applyCost:false): the per-leaf cost verdict is CARRIED on
            // the deferred bound and applied later (in ParseAnd / Force), AFTER a same-tag low+high pair
            // has had the chance to fuse into one window — so a wide one-sided bound the cost planner
            // would drop is still merged into the tight window and driven.
            var (reason, tag) = ClassifyTag(NormText(keySpan), ordered: true, conditionText: text, applyCost: false);
            if (reason == PlanReason.Optimized && tag is not null)
            {
                bool prefersScan = _policy?.PreferFullScan(tag, text) == true;
                // <> (not-equal) is a set-all-minus-range complement, not a contiguous run — it cannot
                // fuse into a window, so apply the cost verdict per-leaf (as before) and otherwise keep
                // it on the (correct) full index scan.
                if (op == CmpOp.Ne)
                {
                    if (prefersScan) return RecordResidual(text, PlanReason.CostPrefersScan);
                    RecordOptimized(text, tag);
                    return Conjunct.OfBuilt(BuildNotEqual(tag, v));
                }
                // = (equality) is a TIGHT point lookup, NOT a directional bound that fuses into a
                // window — so build it IMMEDIATELY instead of deferring. Deferring made
                // `FIELD = v AND FIELD >= lo` merge the point into the wide lower bound and inherit its
                // PrefersScan verdict, so the cost planner full-scanned a single-row lookup
                // (RecordsScanned regression). A point is always selective, so honour the per-leaf cost
                // verdict exactly as <> does, otherwise drive the (exact, contiguous) equality seek.
                if (op == CmpOp.Eq)
                {
                    if (prefersScan) return RecordResidual(text, PlanReason.CostPrefersScan);
                    RecordOptimized(text, tag);
                    var (elo, ehi, epred, eexact) = OrderedSpec(tag, CmpOp.Eq, v);
                    return Conjunct.OfBuilt(BuildOrderedFromBound(new OrderedBound(tag, elo, ehi, epred), eexact));
                }
                // Optimistically record the leaf as optimized; the fusion step demotes it to a residual
                // iff it is NOT part of a window AND the cost policy dislikes it.
                RecordOptimized(text, tag);
                int leafIdx = Leaves.Count - 1;
                var (lo, hi, pred, exact) = OrderedSpec(tag, op, v);
                bool hasLower = op is CmpOp.Gt or CmpOp.Ge;
                bool hasUpper = op is CmpOp.Lt or CmpOp.Le;
                return Conjunct.OfBound(new OrderedBound(tag, lo, hi, pred), exact, prefersScan, hasLower, hasUpper, leafIdx);
            }
            return RecordResidual(text, reason);
        }

        private Conjunct Character(string text, List<Token> keySpan, CmpOp op, string literal)
        {
            // Correctness-only classification; the cost verdict is carried on the deferred bound and
            // applied after fusion (see Ordered) so a same-tag low+high char pair fuses into one window.
            var (reason, tag) = ClassifyTag(NormText(keySpan), ordered: false, conditionText: text, applyCost: false);
            if (reason == PlanReason.Optimized && tag is not null)
            {
                // A seekable char comparison becomes a DEFERRED collated-byte window so a same-tag
                // AND (e.g. NAME >= 'A' AND NAME <= 'C') fuses low+high into ONE contiguous seek run
                // — otherwise each one-sided leaf would walk a huge open-ended slice before the AND
                // trims it. <> / empty literal / collation failure cannot be narrowed → residual.
                var cb = MakeCharBound(tag, op, literal);
                if (cb is not null)
                {
                    bool prefersScan = _policy?.PreferFullScan(tag, text) == true;
                    RecordOptimized(text, tag);
                    int leafIdx = Leaves.Count - 1;
                    bool hasLower = op is CmpOp.Gt or CmpOp.Ge;
                    bool hasUpper = op is CmpOp.Lt or CmpOp.Le;
                    return Conjunct.OfCharBound(cb.Value, prefersScan, hasLower, hasUpper, leafIdx);
                }
                // A usable tag matched, but this op/literal cannot be narrowed (e.g. <> or empty
                // literal) → execution falls to the residual, so the plan must say so too.
                return RecordResidual(text, PlanReason.Residual);
            }
            return RecordResidual(text, reason);
        }

        private Conjunct ClassifyFunction(string text, List<Token> span)
        {
            if (span.Count >= 3 && span[0].Type == TokType.Ident && span[1].Type == TokType.LParen
                && MatchParen(span, 1) == span.Count - 1)
            {
                string fname = span[0].Text;
                var args = SplitArgs(span.GetRange(2, span.Count - 3));

                if (fname == "BETWEEN" && args.Count == 3)
                {
                    double? lo = AsConst(args[1]), hi = AsConst(args[2]);
                    if (lo is not null && hi is not null)
                    {
                        var (reason, tag) = ClassifyTag(NormText(args[0]), ordered: true, conditionText: text);
                        if (reason == PlanReason.Optimized && tag is not null)
                        {
                            double l = lo.Value, h = hi.Value;
                            RecordOptimized(text, tag);
                            int leafIdx = Leaves.Count - 1;
                            // A two-sided window → a single contiguous SEEK run; defer like a comparison.
                            // It is ALREADY a window (low AND high), so HasLower/HasUpper are both set and
                            // the cost verdict never demotes it (ClassifyTag above already applied cost).
                            return Conjunct.OfBound(
                                new OrderedBound(tag, l, h, d => d >= l && d <= h),
                                exact: true, prefersScan: false, hasLower: true, hasUpper: true, leafIdx);
                        }
                        return RecordResidual(text, reason);
                    }
                }

                if (fname == "INLIST" && args.Count >= 2)
                {
                    var vals = new List<double>();
                    bool ok = true;
                    for (int k = 1; k < args.Count; k++)
                    {
                        var cv = AsConst(args[k]);
                        if (cv is null) { ok = false; break; }
                        vals.Add(cv.Value);
                    }
                    if (ok)
                    {
                        var (reason, tag) = ClassifyTag(NormText(args[0]), ordered: true, conditionText: text);
                        if (reason == PlanReason.Optimized && tag is not null)
                        {
                            // A set of discrete values is NOT one contiguous run — keep the full index
                            // scan (out of the contiguous-seek scope, still correct).
                            var a = BuildPredicate(tag, d =>
                            {
                                foreach (var x in vals) if (d == x) return true;
                                return false;
                            }, exact: true);
                            RecordOptimized(text, tag);
                            return Conjunct.OfBuilt(a);
                        }
                        return RecordResidual(text, reason);
                    }
                }
            }

            return RecordResidual(text, PlanReason.NotSimpleComparison);
        }

        // ---- per-leaf plan recording ----

        private void RecordOptimized(string text, CdxTag tag)
            => Leaves.Add(new ConditionPlan(text, optimizable: true, tag.Name, tag.KeyExpression,
                PlanReason.Optimized, ReasonText(PlanReason.Optimized)));

        private Conjunct RecordResidual(string text, PlanReason reason)
        {
            Leaves.Add(new ConditionPlan(text, optimizable: false, tagName: null,
                tagKeyExpression: null, reason, ReasonText(reason)));
            return Conjunct.OfBuilt(Analysis.NonOptimized(_rc));
        }

        /// <summary>
        /// Re-mark every leaf recorded at or after <paramref name="leafStart"/> as a residual
        /// (clear its tag), reflecting POST-combine reality: a boolean combine (collapsed OR /
        /// inexact NOT) discarded this subtree's index, so its leaves are NOT actually seeked by
        /// <see cref="FindRecords"/>. Keeps the plan's per-leaf verdict + <c>UsedTags</c> in lock
        /// step with execution. Already-residual leaves are left unchanged.
        /// </summary>
        private void DemoteLeaves(int leafStart)
        {
            for (int i = leafStart; i < Leaves.Count; i++)
            {
                var c = Leaves[i];
                if (!c.Optimizable) continue;
                Leaves[i] = new ConditionPlan(c.Condition, optimizable: false, tagName: null,
                    tagKeyExpression: null, PlanReason.Residual, ReasonText(PlanReason.Residual));
            }
        }

        private static string ReasonText(PlanReason reason) => reason switch
        {
            PlanReason.Optimized => "optimized via index tag",
            PlanReason.NoMatchingTag => "no matching index tag",
            PlanReason.ForFiltered => "tag has a FOR filter (subset) — unsafe",
            PlanReason.Unique => "tag is UNIQUE — unsafe for a positive leaf",
            PlanReason.UnsupportedKeyType => "tag key type / collation is not index-resolvable",
            PlanReason.KeyExpressionHasNot => "tag KEY expression contains NOT (low-selectivity boolean) — skipped without stats",
            PlanReason.NotSimpleComparison => "not a simple (key) (op) (constant) comparison",
            PlanReason.Residual => "handled only by the residual confirmation",
            PlanReason.CostPrefersScan => "cost planner prefers a full scan (estimated low selectivity)",
            PlanReason.OptimizationDisabled => "optimization disabled (SET OPTIMIZE OFF)",
            _ => reason.ToString(),
        };

        /// <summary>
        /// The shared tag-selection helper both <see cref="FindRecords"/> and <see cref="Explain"/>
        /// use. Finds the CDX tag whose KEY expression matches <paramref name="keyText"/> and reports
        /// whether it is usable (<see cref="PlanReason.Optimized"/>, with the tag) or, if a key-matching
        /// tag exists but is disqualified, the REASON (FOR-filtered / UNIQUE / unsupported key type /
        /// collation mismatch). Returns <see cref="PlanReason.NoMatchingTag"/> when no tag's key matches.
        /// </summary>
        private (PlanReason Reason, CdxTag? Tag) ClassifyTag(string keyText, bool ordered, string conditionText, bool applyCost = true)
        {
            // SET OPTIMIZE OFF: consult no index at all — every leaf is residual (a plain full scan).
            if (!_optimize) return (PlanReason.OptimizationDisabled, null);
            if (_cdx is null || keyText.Length == 0) return (PlanReason.NoMatchingTag, null);

            PlanReason firstDisq = PlanReason.NoMatchingTag;
            bool haveDisq = false;
            foreach (var name in _cdx.TagNames)
            {
                var tag = _cdx.Tag(name);
                if (tag is null) continue;
                if (NormKey(tag.KeyExpression) != keyText) continue;

                var r = GuardReason(tag, ordered, conditionText);
                if (r == PlanReason.Optimized)
                {
                    // INDEX-vs-SCAN (design §7, decision 2): the cost policy may prefer a full scan
                    // over driving this tag when it estimates the leaf is non-selective. PLAN-only —
                    // demoting to the residual keeps the candidate the all-records superset, so the
                    // result set is unchanged. (No policy → the default mode always drives the tag.)
                    if (applyCost && _policy?.PreferFullScan(tag, conditionText) == true)
                    {
                        if (!haveDisq) { firstDisq = PlanReason.CostPrefersScan; haveDisq = true; }
                        continue;
                    }
                    return (PlanReason.Optimized, tag);
                }
                if (!haveDisq) { firstDisq = r; haveDisq = true; }
            }
            return haveDisq ? (firstDisq, null) : (PlanReason.NoMatchingTag, null);
        }

        /// <summary>
        /// The usability guards applied to a key-matching tag, in the SAME order the optimizer's
        /// candidate seek requires. Returns <see cref="PlanReason.Optimized"/> when the tag is safe
        /// to drive, else the disqualifying reason. A tag is usable iff this returns Optimized — the
        /// exact condition under which <see cref="FindRecords"/> seeks it.
        /// </summary>
        private PlanReason GuardReason(CdxTag tag, bool ordered, string conditionText)
        {
            // A NOT-keyed tag is almost always a boolean / low-selectivity index (NOT DELETED(),
            // NOT EMPTY(x) → 2 values). WITHOUT statistics we cannot tell whether seeking it pays,
            // so the default mode conservatively skips it (driving it could scan ~half the table —
            // the same low-selectivity pessimisation the char-equality fix removed). This is a COST
            // choice, not a correctness one: superset+residual would handle it correctly. The
            // Highlike cost-planner LIFTS this guard once NDV proves the tag selective enough.
            // (Historically VFP ignored such tags outright — for us it is cost-deferred, not banned.)
            // Lifting is PLAN-only: driving the tag still yields a SUPERSET the residual confirms.
            if (KeyHasNot(tag.KeyExpression) && _policy?.LiftGuard(tag, conditionText) != true)
                return PlanReason.KeyExpressionHasNot;

            if (ordered)
            {
                // Integer/Numeric/Date/DateTime keys: one order-preserving transform, no collation.
                if (tag.KeyType is not (IndexKeyType.Integer or IndexKeyType.Numeric
                                        or IndexKeyType.Date or IndexKeyType.DateTime))
                    return PlanReason.UnsupportedKeyType;
            }
            else
            {
                // Character keys (incl. function keys like UPPER(NAME)).
                if (!tag.IsCharacterKey) return PlanReason.UnsupportedKeyType;
                // The tag's stored collation must equal the active collation, or a range over its
                // collation-ordered keys would not be a superset of the context-collation match set.
                if (!CollationMatches(tag.Collation, _collation)) return PlanReason.UnsupportedKeyType;
            }

            // A FOR-filtered tag indexes only rows that passed FOR — a strict SUBSET, unsafe.
            if (!string.IsNullOrWhiteSpace(tag.ForExpression)) return PlanReason.ForFiltered;
            // A UNIQUE tag keeps one recno per key — under-represents duplicates, unsafe.
            if (tag.IsUnique) return PlanReason.Unique;

            return PlanReason.Optimized;
        }

        private static List<List<Token>> SplitArgs(List<Token> inner)
        {
            var res = new List<List<Token>>();
            var cur = new List<Token>();
            int depth = 0;
            foreach (var t in inner)
            {
                if (t.Type == TokType.LParen) depth++;
                else if (t.Type == TokType.RParen) depth--;
                if (depth == 0 && t.Type == TokType.Comma) { res.Add(cur); cur = new List<Token>(); continue; }
                cur.Add(t);
            }
            if (cur.Count > 0 || res.Count > 0) res.Add(cur);
            return res;
        }

        private static double? AsConst(List<Token> span)
        {
            if (span.Count == 1 && span[0].Type == TokType.Number)
                return span[0].Num;
            if (span.Count == 2 && span[0].Type == TokType.Arith &&
                (span[0].Text == "+" || span[0].Text == "-") && span[1].Type == TokType.Number)
                return span[0].Text == "-" ? -span[1].Num : span[1].Num;
            return null;
        }

        /// <summary>A single string literal operand (e.g. <c>'MILLER'</c>), else null.</summary>
        private static string? AsStringConst(List<Token> span)
            => span.Count == 1 && span[0].Type == TokType.Str ? span[0].Str : null;

        /// <summary>The tag's stored sort sequence (empty ⇒ MACHINE) equals the active collation.</summary>
        private static bool CollationMatches(string? tagCollation, string activeCollation)
        {
            string tc = string.IsNullOrWhiteSpace(tagCollation) ? "MACHINE" : tagCollation.Trim();
            return string.Equals(tc, activeCollation, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the KEY expression contains a word-boundaried <c>NOT</c> (or <c>.NOT.</c>).</summary>
        private static bool KeyHasNot(string? key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string u = key.ToUpperInvariant();
            int idx = 0;
            while ((idx = u.IndexOf("NOT", idx, StringComparison.Ordinal)) >= 0)
            {
                bool leftOk = idx == 0 || !char.IsLetterOrDigit(u[idx - 1]);
                int after = idx + 3;
                bool rightOk = after >= u.Length || !char.IsLetterOrDigit(u[after]);
                if (leftOk && rightOk) return true;
                idx = after;
            }
            return false;
        }

        private static string NormText(List<Token> span)
        {
            var sb = new StringBuilder();
            foreach (var t in span)
            {
                switch (t.Type)
                {
                    case TokType.LParen: sb.Append('('); break;
                    case TokType.RParen: sb.Append(')'); break;
                    case TokType.Comma: sb.Append(','); break;
                    case TokType.Str: sb.Append('\'').Append(t.Str).Append('\''); break;
                    default: sb.Append(t.Text); break;
                }
            }
            return sb.ToString();
        }

        private static string NormKey(string key)
        {
            var sb = new StringBuilder(key.Length);
            foreach (char c in key)
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        // ---- deferred AND-conjuncts (for same-tag bound fusion) ----

        /// <summary>
        /// One operand of an AND chain: EITHER an already-built <see cref="Analysis"/> (the common
        /// case) OR a DEFERRED ordered-key <see cref="OrderedBound"/> whose candidate is not yet
        /// built, so a sibling constraint on the SAME tag can be fused into a single window seek.
        /// </summary>
        private sealed class Conjunct
        {
            public Analysis? Built;
            public OrderedBound? Bound;
            public CharBound? CBound;
            public bool Exact;
            // ---- fusion metadata (deferred bounds only) ----
            // The cost policy's PER-LEAF verdict (carried, NOT yet applied) so the AND-fusion step can
            // OVERRIDE it when a same-tag low+high pair forms a tight window. HasLower/HasUpper record
            // which side(s) this leaf bounds; a fused bound with BOTH is a window driven regardless of
            // the per-leaf threshold. LeafIdx is the optimistic plan-leaf to DEMOTE if cost wins.
            public bool PrefersScan;
            public bool HasLower;
            public bool HasUpper;
            public int LeafIdx = -1;
            public static Conjunct OfBuilt(Analysis a) => new() { Built = a };
            public static Conjunct OfBound(OrderedBound b, bool exact, bool prefersScan, bool hasLower, bool hasUpper, int leafIdx)
                => new() { Bound = b, Exact = exact, PrefersScan = prefersScan, HasLower = hasLower, HasUpper = hasUpper, LeafIdx = leafIdx };
            public static Conjunct OfCharBound(CharBound b, bool prefersScan, bool hasLower, bool hasUpper, int leafIdx)
                => new() { CBound = b, PrefersScan = prefersScan, HasLower = hasLower, HasUpper = hasUpper, LeafIdx = leafIdx };
        }

        /// <summary>
        /// The per-tag fold of every same-tag ordered bound in an AND group: the intersected window
        /// plus the AND-ed cost verdict (the merged window is at least as selective as its tightest
        /// leaf), the OR-ed side flags and the optimistic plan-leaf indices. A fold with BOTH a
        /// lower and an upper contributing leaf (<see cref="HasLower"/> &amp;&amp; <see cref="HasUpper"/>)
        /// is a genuine WINDOW — driven regardless of the per-leaf cost verdict; otherwise the cost
        /// verdict (<see cref="PrefersScan"/>) decides whether the bound is driven or demoted.
        /// </summary>
        private readonly struct OrderedAcc
        {
            public readonly OrderedBound Bound;
            public readonly bool Exact;
            public readonly bool PrefersScan;
            public readonly bool HasLower;
            public readonly bool HasUpper;
            public readonly List<int> LeafIdx;
            public OrderedAcc(OrderedBound bound, bool exact, bool prefersScan, bool hasLower, bool hasUpper, List<int> leafIdx)
            { Bound = bound; Exact = exact; PrefersScan = prefersScan; HasLower = hasLower; HasUpper = hasUpper; LeafIdx = leafIdx; }

            public static OrderedAcc From(OrderedBound b, Conjunct c)
                => new(b, c.Exact, c.PrefersScan, c.HasLower, c.HasUpper, c.LeafIdx >= 0 ? new List<int> { c.LeafIdx } : new List<int>());

            public OrderedAcc Merge(OrderedBound b, Conjunct c)
            {
                if (c.LeafIdx >= 0) LeafIdx.Add(c.LeafIdx);
                // PrefersScan is AND-ed, NOT OR-ed: intersecting two same-tag bounds only TIGHTENS the
                // window (the merged true-set ⊆ each leaf's), so if EVEN ONE contributing leaf is
                // selective enough to drive (PrefersScan false) the merged window is at least that
                // selective and must be driven. OR-ing here let a wide bound's "prefer scan" poison a
                // selective sibling (e.g. FIELD >= bigSelectiveLo AND FIELD >= 0 → full scan). Side
                // flags stay OR-ed (a window needs a lower AND an upper from anywhere in the group).
                return new OrderedAcc(Bound.Intersect(b), Exact && c.Exact, PrefersScan && c.PrefersScan,
                    HasLower || c.HasLower, HasUpper || c.HasUpper, LeafIdx);
            }
        }

        /// <summary>The character analogue of <see cref="OrderedAcc"/> (no exactness — char candidates are supersets).</summary>
        private readonly struct CharAcc
        {
            public readonly CharBound Bound;
            public readonly bool PrefersScan;
            public readonly bool HasLower;
            public readonly bool HasUpper;
            public readonly List<int> LeafIdx;
            public CharAcc(CharBound bound, bool prefersScan, bool hasLower, bool hasUpper, List<int> leafIdx)
            { Bound = bound; PrefersScan = prefersScan; HasLower = hasLower; HasUpper = hasUpper; LeafIdx = leafIdx; }

            public static CharAcc From(CharBound b, Conjunct c)
                => new(b, c.PrefersScan, c.HasLower, c.HasUpper, c.LeafIdx >= 0 ? new List<int> { c.LeafIdx } : new List<int>());

            public CharAcc Merge(CharBound b, Conjunct c)
            {
                if (c.LeafIdx >= 0) LeafIdx.Add(c.LeafIdx);
                // PrefersScan is AND-ed (see OrderedAcc.Merge): intersection only tightens the collated
                // window, so a single selective leaf rescues the merged candidate from a wide sibling's
                // "prefer scan". Side flags stay OR-ed.
                return new CharAcc(Bound.Intersect(b), PrefersScan && c.PrefersScan,
                    HasLower || c.HasLower, HasUpper || c.HasUpper, LeafIdx);
            }
        }

        /// <summary>
        /// Resolve a fused ordered bound to a concrete candidate. A genuine WINDOW (low+high on the
        /// same tag) — OR a bound the cost policy is happy to drive — builds the SEEK. A non-window
        /// bound the cost policy dislikes is DEMOTED to the all-records superset (its optimistic plan
        /// leaves re-marked residual), exactly the per-leaf cost-scan behaviour, only deferred so the
        /// fusion could first rescue any low+high pair.
        /// </summary>
        private Analysis ResolveOrdered(OrderedBound b, bool exact, bool prefersScan, bool isWindow, List<int> leafIdx)
        {
            if (isWindow || !prefersScan) return BuildOrderedFromBound(b, exact);
            foreach (int i in leafIdx) DemoteLeaf(i, PlanReason.CostPrefersScan);
            return Analysis.NonOptimized(_rc);
        }

        /// <summary>The character analogue of <see cref="ResolveOrdered"/>.</summary>
        private Analysis ResolveChar(CharBound b, bool prefersScan, bool isWindow, List<int> leafIdx)
        {
            if (isWindow || !prefersScan) return BuildCharFromBound(b);
            foreach (int i in leafIdx) DemoteLeaf(i, PlanReason.CostPrefersScan);
            return Analysis.NonOptimized(_rc);
        }

        /// <summary>Re-mark a single optimistically-recorded leaf as a residual with the given reason.</summary>
        private void DemoteLeaf(int index, PlanReason reason)
        {
            if (index < 0 || index >= Leaves.Count) return;
            var c = Leaves[index];
            Leaves[index] = new ConditionPlan(c.Condition, optimizable: false, tagName: null,
                tagKeyExpression: null, reason, ReasonText(reason));
        }

        /// <summary>
        /// A deferred CHARACTER-key candidate: the contiguous COLLATED-byte window <c>[Lo, Hi)</c>
        /// (either bound open when null) that is a guaranteed SUPERSET of the matches, plus the
        /// per-entry confirmation <see cref="Pred"/> (byte-for-byte the historical full-scan take).
        /// Intersecting two same-tag bounds fuses a low+high pair into one window whose predicate is
        /// the conjunction — so a single seek over the run yields exactly the AND result. Bytes sort
        /// in the SAME unsigned, shorter-first order the CDX stores collation keys in.
        /// </summary>
        private readonly struct CharBound
        {
            public readonly CdxTag Tag;
            public readonly byte[]? Lo;
            public readonly byte[]? Hi;
            public readonly Func<byte[], bool> Pred;

            public CharBound(CdxTag tag, byte[]? lo, byte[]? hi, Func<byte[], bool> pred)
            {
                Tag = tag; Lo = lo; Hi = hi; Pred = pred;
            }

            public CharBound Intersect(CharBound o)
            {
                var pa = Pred; var pb = o.Pred;
                return new CharBound(Tag, MaxBytes(Lo, o.Lo), MinBytes(Hi, o.Hi), k => pa(k) && pb(k));
            }
        }

        /// <summary>Tightest (largest) lower bound: null = open low (−∞).</summary>
        private static byte[]? MaxBytes(byte[]? a, byte[]? b)
            => a is null ? b : b is null ? a : CompareBytes(a, b) >= 0 ? a : b;
        /// <summary>Tightest (smallest) upper bound: null = open high (+∞).</summary>
        private static byte[]? MinBytes(byte[]? a, byte[]? b)
            => a is null ? b : b is null ? a : CompareBytes(a, b) <= 0 ? a : b;

        /// <summary>
        /// A deferred ordered-key candidate: the contiguous value window <c>[Lo, Hi]</c> (either bound
        /// open when null) the matches live in, plus the EXACT predicate to confirm each entry in that
        /// window. Intersecting two same-tag bounds fuses a low+high pair into one window whose
        /// predicate is the conjunction — so a single seek over the run yields exactly the AND result.
        /// </summary>
        private readonly struct OrderedBound
        {
            public readonly CdxTag Tag;
            public readonly double? Lo;
            public readonly double? Hi;
            public readonly Func<double, bool> Pred;

            public OrderedBound(CdxTag tag, double? lo, double? hi, Func<double, bool> pred)
            {
                Tag = tag; Lo = lo; Hi = hi; Pred = pred;
            }

            public OrderedBound Intersect(OrderedBound o)
            {
                var pa = Pred; var pb = o.Pred;
                return new OrderedBound(Tag, MaxN(Lo, o.Lo), MinN(Hi, o.Hi), d => pa(d) && pb(d));
            }
        }

        private static double? MaxN(double? a, double? b)
            => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
        private static double? MinN(double? a, double? b)
            => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

        /// <summary>Resolve a conjunct to a concrete <see cref="Analysis"/> (building a deferred bound now).</summary>
        private Analysis Force(Conjunct c)
        {
            if (c.Built is not null) return c.Built.Value;
            // A standalone deferred bound: no AND sibling to fuse with, so resolve it directly through
            // the same window/cost gate (a one-sided bound the cost policy dislikes is demoted here too).
            var leaf = c.LeafIdx >= 0 ? new List<int> { c.LeafIdx } : new List<int>();
            if (c.Bound is OrderedBound ob)
                return ResolveOrdered(ob, c.Exact, c.PrefersScan, c.HasLower && c.HasUpper, leaf);
            return ResolveChar(c.CBound!.Value, c.PrefersScan, c.HasLower && c.HasUpper, leaf);
        }

        /// <summary>Build the candidate for an ordered bound via a SEEK, falling back to a full scan.</summary>
        private Analysis BuildOrderedFromBound(OrderedBound b, bool exact)
            => TryOrderedSeek(b.Tag, b.Pred, b.Lo, b.Hi, exact)
               ?? BuildPredicate(b.Tag, b.Pred, exact);

        /// <summary>
        /// Translate a single ordered comparison into its value WINDOW + EXACT predicate (byte-for-byte
        /// the old per-op semantics). DateTime keys round-trip only to ~ms, so their bounds are widened
        /// by an epsilon (still a superset; the residual trims) and marked inexact; Integer/Numeric/Date
        /// stay exact (so a wrapping NOT may safely flip them).
        /// </summary>
        private static (double? Lo, double? Hi, Func<double, bool> Pred, bool Exact) OrderedSpec(CdxTag tag, CmpOp op, double v)
        {
            bool dt = tag.KeyType == IndexKeyType.DateTime;
            double eps = dt ? 1e-6 : 0.0;

            Func<double, bool> pred = op switch
            {
                CmpOp.Eq => d => Math.Abs(d - v) <= eps,
                CmpOp.Lt => d => d < v + eps,
                CmpOp.Le => d => d <= v + eps,
                CmpOp.Gt => d => d > v - eps,
                CmpOp.Ge => d => d >= v - eps,
                _ => _ => false,
            };
            double? lo, hi;
            switch (op)
            {
                case CmpOp.Eq: lo = v - eps; hi = v + eps; break;
                case CmpOp.Lt:
                case CmpOp.Le: lo = null; hi = v + eps; break;
                case CmpOp.Gt:
                case CmpOp.Ge: lo = v - eps; hi = null; break;
                default: lo = null; hi = null; break;
            }
            return (lo, hi, pred, !dt);
        }

        // ---- Rushmore SEEK: build the candidate over the CONTIGUOUS matching run (O(log n + matches)) ----

        /// <summary>
        /// Build an ordered-key candidate by SEEKING the contiguous run whose decoded value lies in the
        /// window <c>[lo, hi]</c> (a guaranteed SUPERSET of the matches) and applying <paramref name="pred"/>
        /// to each entry in it — so the result is EXACTLY the old full-scan candidate, found in
        /// O(log n + matches). The CACHED (warm) path binary-searches the decoded sorted directory; the
        /// COLD path descends the B-tree to the lower bound and walks the leaf chain forward. Returns
        /// <see langword="null"/> (→ caller full-scans) for a DESCENDING tag, a non-ordered key, an
        /// undecodable probe, or any uncertainty — never narrowing the result.
        /// </summary>
        private Analysis? TryOrderedSeek(CdxTag tag, Func<double, bool> pred, double? lo, double? hi, bool exact)
        {
            // EXPLAIN needs only the verdict, not the candidate — skip the seek (as BuildPredicate does).
            if (_planOnly) return new Analysis(new RecordBitmap(_rc), exact, usedIndex: true);

            // The forward seek/binary search both assume ASCENDING value order; a DESCENDING tag is
            // logically reversed → fall back to the (correct) full scan. Only ordered keys are seekable.
            if (tag.Descending) return null;
            if (tag.KeyType is not (IndexKeyType.Integer or IndexKeyType.Numeric
                                    or IndexKeyType.Date or IndexKeyType.DateTime))
                return null;

            try
            {
                if (_entrySource is not null)
                {
                    var src = EntriesOf(tag);
                    var list = src as IList<IndexEntry> ?? src.ToList();
                    return SeekCached(tag, list, pred, lo, hi, exact);
                }
                return SeekCold(tag, pred, lo, hi, exact);
            }
            catch
            {
                return null; // any seek uncertainty → caller full-scans (still correct)
            }
        }

        /// <summary>
        /// CACHED (Highlike warm) seek: the decoded directory is already materialized and sorted
        /// ascending by value, so binary-search the lower + upper window bounds and confirm each entry
        /// in the slice. An undecodable probe (no total order) returns <see langword="null"/> → full scan.
        /// </summary>
        private Analysis? SeekCached(CdxTag tag, IList<IndexEntry> list, Func<double, bool> pred, double? lo, double? hi, bool exact)
        {
            int n = list.Count;
            int start = 0, end = n;

            if (lo is not null)
            {
                int l = 0, h = n;
                while (l < h)
                {
                    int mid = (int)(((uint)l + (uint)h) >> 1);
                    _onExamine?.Invoke(tag);
                    double? d = NumOf(tag.DecodeKey(list[mid].Key));
                    if (d is null) return null;
                    if (d.Value < lo.Value) l = mid + 1; else h = mid;
                }
                start = l; // first index with value >= lo
            }
            if (hi is not null)
            {
                int l = 0, h = n;
                while (l < h)
                {
                    int mid = (int)(((uint)l + (uint)h) >> 1);
                    _onExamine?.Invoke(tag);
                    double? d = NumOf(tag.DecodeKey(list[mid].Key));
                    if (d is null) return null;
                    if (d.Value <= hi.Value) l = mid + 1; else h = mid;
                }
                end = l; // first index with value > hi
            }

            var bm = new RecordBitmap(_rc);
            for (int i = start; i < end; i++)
            {
                _onExamine?.Invoke(tag);
                double? d = NumOf(tag.DecodeKey(list[i].Key));
                if (d is null) continue;
                if (pred(d.Value))
                {
                    int idx = (int)list[i].RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact, usedIndex: true);
        }

        /// <summary>
        /// COLD seek: descend the B-tree to the leaf holding the lower bound (or the left-most leaf when
        /// open), then walk the leaf chain forward confirming each entry and STOPPING once the value
        /// passes the upper bound — so only O(log n + matches) entries are touched.
        /// </summary>
        private Analysis? SeekCold(CdxTag tag, Func<double, bool> pred, double? lo, double? hi, bool exact)
        {
            byte[]? loBytes = lo is null ? null : IndexKey.EncodeOrderedBound(tag.KeyType, lo.Value);
            var bm = new RecordBitmap(_rc);
            foreach (var entry in tag.EnumerateFrom(loBytes))
            {
                _onExamine?.Invoke(tag);
                double? d = NumOf(tag.DecodeKey(entry.Key));
                if (d is null) continue;
                if (hi is not null && d.Value > hi.Value) break; // past the run (ascending) → done
                if (pred(d.Value))
                {
                    int idx = (int)entry.RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact, usedIndex: true);
        }

        /// <summary>
        /// Translate a single CHARACTER comparison into a deferred <see cref="CharBound"/>: the
        /// contiguous COLLATED-byte window <c>[Lo, Hi)</c> that is a guaranteed SUPERSET of the
        /// matches, plus the per-entry confirmation predicate (byte-for-byte the historical
        /// full-scan take). Returns <see langword="null"/> for shapes that cannot be narrowed
        /// (<c>&lt;&gt;</c>, empty literal, collation failure) → caller records a residual leaf.
        /// <para>
        /// The literal is transformed ONCE into the tag's collation weight bytes (the SAME
        /// function that produced the stored keys). For equality (= with EXACT OFF is a PREFIX
        /// match; == / EXACT ON is exact — both subsets of "starts with") the superset is every
        /// key that (a) shares the literal's FIRST weight byte and (b) sorts at-or-after the
        /// literal; a SELECTIVE upper bound (the head run byte-incremented) tightens (a) to the
        /// literal's first-letter bucket. This stays correct even for the GENERAL head/tail key
        /// where a plain byte-prefix test would not. One-sided comparisons use a widened prefix
        /// bound. The window bounds (Lo/Hi) are exactly the byte ranges the predicate accepts, so
        /// seeking the run and applying the predicate inside it yields the SAME set as the scan.
        /// </para>
        /// </summary>
        private CharBound? MakeCharBound(CdxTag tag, CmpOp op, string literal)
        {
            // <> over a collation depends on SET EXACT in a way that is unsafe to narrow here;
            // the residual handles it correctly on the full universe.
            if (op == CmpOp.Ne) return null;

            // The literal in the tag's OWN stored-key form (MACHINE: the table code-page bytes; GENERAL:
            // the weight key) — the same producer the CDX builder used for the stored keys.
            byte[] lit;
            try
            {
                lit = tag.EncodeCharacterKey(literal);
            }
            catch { return null; }
            // Empty literal: EXACT OFF makes every row match and EXACT ON makes it the empty
            // string only — both are cleanly handled by the residual on the full scan.
            if (lit.Length == 0) return null;

            // SELECTIVE equality upper bound (the literal's head run byte-incremented); NULL ⇒
            // widen to the open first-byte fallback (historical coarse-but-correct behaviour).
            byte[]? eqUpper = op == CmpOp.Eq ? CharEqUpperBound(tag, literal, lit) : null;

            // The contiguous collated-byte WINDOW [lo, hi) that is a SUPERSET of the matches,
            // proven per op so the predicate's true-set is entirely contained in [lo, hi):
            //   Eq      → [lit, eqUpper)  (eqUpper, else {lit[0]+1}, else open: key[0]==lit[0])
            //   Lt/Le   → [open, nextPrefix(lit))   (every prefix-or-below key sorts below it)
            //   Gt/Ge   → [lit, open)               (every prefix-or-above key sorts at/after it)
            byte[]? lo, hi;
            switch (op)
            {
                case CmpOp.Eq:
                    lo = lit;
                    hi = eqUpper ?? (lit[0] != 0xFF ? new[] { (byte)(lit[0] + 1) } : null);
                    break;
                case CmpOp.Lt:
                case CmpOp.Le:
                    lo = null;
                    hi = NextPrefixBytes(lit); // null ⇒ all-0xFF literal ⇒ open upper
                    break;
                case CmpOp.Gt:
                case CmpOp.Ge:
                    lo = lit;
                    hi = null;
                    break;
                default:
                    return null;
            }

            // The per-entry confirmation: BYTE-FOR-BYTE the historical full-scan take, so the
            // seek-built candidate is provably the SAME set (the window is only a superset).
            Func<byte[], bool> pred = key =>
            {
                if (key.Length == 0) return false;
                return op switch
                {
                    // Prefix/exact SUPERSET: key >= literal AND (when a selective upper bound was
                    // derived) key < nextPrefix(heads); otherwise the open first-byte fallback.
                    CmpOp.Eq => CompareBytes(key, lit) >= 0
                        && (eqUpper is null ? key[0] == lit[0] : CompareBytes(key, eqUpper) < 0),
                    // One-sided: widened prefix-byte bound (residual confirms strictness/EXACT).
                    CmpOp.Lt or CmpOp.Le => ComparePrefixBytes(key, lit) <= 0,
                    CmpOp.Gt or CmpOp.Ge => ComparePrefixBytes(key, lit) >= 0,
                    _ => false,
                };
            };

            return new CharBound(tag, lo, hi, pred);
        }

        /// <summary>
        /// Build the candidate for a CHARACTER bound: SEEK the collated-byte window (binary-search
        /// the warm directory / B-tree descend + forward leaf walk on the cold path), confirming
        /// each entry in the run with the bound's predicate. Falls back to the full index scan for a
        /// DESCENDING tag or ANY seek uncertainty — never narrowing. Character candidates are
        /// SUPERSETS (collation + EXACT trimmed by the residual), so exact:false (a wrapping NOT may
        /// not flip them).
        /// </summary>
        private Analysis BuildCharFromBound(CharBound b)
        {
            // EXPLAIN (plan-only) needs the optimizability VERDICT, not the candidate — skip the scan.
            if (_planOnly) return new Analysis(new RecordBitmap(_rc), exact: false, usedIndex: true);
            return TryCharSeek(b.Tag, b.Pred, b.Lo, b.Hi) ?? BuildCharScan(b.Tag, b.Pred);
        }

        /// <summary>
        /// Seek the contiguous collated-byte run <c>[lo, hi)</c> (a SUPERSET of the matches) and
        /// confirm each entry with <paramref name="pred"/> — so the result is EXACTLY the old
        /// full-scan candidate, found in O(log n + matches). Returns <see langword="null"/> (→ caller
        /// full-scans) for a DESCENDING tag, a non-character key, or any uncertainty.
        /// </summary>
        private Analysis? TryCharSeek(CdxTag tag, Func<byte[], bool> pred, byte[]? lo, byte[]? hi)
        {
            // The forward seek / binary search both assume ASCENDING byte order; a DESCENDING tag is
            // logically reversed → fall back to the (correct) full scan. Character keys only.
            if (tag.Descending) return null;
            if (!tag.IsCharacterKey) return null;

            try
            {
                if (_entrySource is not null)
                {
                    var src = EntriesOf(tag);
                    var list = src as IList<IndexEntry> ?? src.ToList();
                    return SeekCharCached(tag, list, pred, lo, hi);
                }
                return SeekCharCold(tag, pred, lo, hi);
            }
            catch
            {
                return null; // any seek uncertainty → caller full-scans (still correct)
            }
        }

        /// <summary>
        /// CACHED (Highlike warm) char seek: the decoded directory is already materialized and sorted
        /// ascending by collated bytes, so binary-search the lower + upper window bounds and confirm
        /// each entry in the slice.
        /// </summary>
        private Analysis SeekCharCached(CdxTag tag, IList<IndexEntry> list, Func<byte[], bool> pred, byte[]? lo, byte[]? hi)
        {
            int n = list.Count;
            int start = 0, end = n;

            if (lo is not null)
            {
                int l = 0, h = n;
                while (l < h)
                {
                    int mid = (int)(((uint)l + (uint)h) >> 1);
                    _onExamine?.Invoke(tag);
                    if (CompareBytes(list[mid].Key, lo) < 0) l = mid + 1; else h = mid;
                }
                start = l; // first index with key >= lo
            }
            if (hi is not null)
            {
                int l = 0, h = n;
                while (l < h)
                {
                    int mid = (int)(((uint)l + (uint)h) >> 1);
                    _onExamine?.Invoke(tag);
                    if (CompareBytes(list[mid].Key, hi) < 0) l = mid + 1; else h = mid;
                }
                end = l; // first index with key >= hi
            }

            var bm = new RecordBitmap(_rc);
            for (int i = start; i < end; i++)
            {
                _onExamine?.Invoke(tag);
                if (pred(list[i].Key))
                {
                    int idx = (int)list[i].RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact: false, usedIndex: true);
        }

        /// <summary>
        /// COLD char seek: descend the B-tree to the leaf holding the lower bound (or the left-most
        /// leaf when open), then walk the leaf chain forward confirming each entry and STOPPING once
        /// the key reaches the upper bound — so only O(log n + matches) entries are touched.
        /// </summary>
        private Analysis SeekCharCold(CdxTag tag, Func<byte[], bool> pred, byte[]? lo, byte[]? hi)
        {
            var bm = new RecordBitmap(_rc);
            foreach (var entry in tag.EnumerateFrom(lo))
            {
                _onExamine?.Invoke(tag);
                byte[] key = entry.Key;
                if (hi is not null && CompareBytes(key, hi) >= 0) break; // past the run (ascending) → done
                if (pred(key))
                {
                    int idx = (int)entry.RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact: false, usedIndex: true);
        }

        /// <summary>
        /// Full index scan fallback for a CHARACTER bound (DESCENDING tag / seek uncertainty): apply
        /// the bound's predicate to every entry. Identical result to the seek — only slower.
        /// </summary>
        private Analysis BuildCharScan(CdxTag tag, Func<byte[], bool> pred)
        {
            var bm = new RecordBitmap(_rc);
            foreach (var entry in EntriesOf(tag))
            {
                _onExamine?.Invoke(tag); // INSTRUMENTATION: this entry is being examined by the build
                if (pred(entry.Key))
                {
                    int idx = (int)entry.RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact: false, usedIndex: true);
        }

        /// <summary>
        /// The NEXT-PREFIX bound of a collated key: the key with its last non-<c>0xFF</c> byte
        /// incremented and any trailing <c>0xFF</c> dropped — the first byte string that sorts
        /// strictly after every string carrying <paramref name="b"/> as a prefix. Returns
        /// <see langword="null"/> when <paramref name="b"/> is all <c>0xFF</c> (no finite next
        /// prefix ⇒ open upper bound).
        /// </summary>
        private static byte[]? NextPrefixBytes(byte[] b)
        {
            int i = b.Length - 1;
            while (i >= 0 && b[i] == 0xFF) i--;
            if (i < 0) return null;
            var up = new byte[i + 1];
            Array.Copy(b, up, i + 1);
            up[i]++;
            return up;
        }

        /// <summary>Unsigned, shorter-sorts-first byte comparison of two whole keys.</summary>
        private static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int d = a[i] - b[i];
                if (d != 0) return d < 0 ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// Unsigned comparison of <paramref name="key"/> against <paramref name="lit"/> over the
        /// literal's length (a prefix view): 0 when the key carries the literal as a byte prefix,
        /// negative when it sorts before, positive after.
        /// </summary>
        private static int ComparePrefixBytes(byte[] key, byte[] lit)
        {
            int n = Math.Min(key.Length, lit.Length);
            for (int i = 0; i < n; i++)
            {
                int d = key[i] - lit[i];
                if (d != 0) return d < 0 ? -1 : 1;
            }
            return key.Length >= lit.Length ? 0 : -1;
        }

        /// <summary>
        /// Derives the SELECTIVE upper bound for a CHARACTER equality candidate: the literal's
        /// PRIMARY (head) weight run, byte-incremented to the next prefix. Returns <c>null</c> to
        /// signal "WIDEN" (use the open first-byte fallback) whenever the bound cannot be proven
        /// to be an over-estimate of every true match — never narrowing.
        /// <para>
        /// Why it is a guaranteed superset: a value that prefix-matches the literal collates to
        /// heads(literal)++heads(rest)++tails(...). For BOTH collations the leading bytes are the
        /// literal's head run H; the GENERAL diacritic tail of the literal is pushed AFTER the
        /// match's extra heads, so it never appears in the shared prefix. Thus every match key
        /// begins with H and so sorts strictly below byteIncrement(H). The head boundary is found
        /// collation-agnostically: append two probe chars with distinct primary weights — their
        /// collated keys share EXACTLY the literal's head run, then diverge at the next head.
        /// </para>
        /// </summary>
        private static byte[]? CharEqUpperBound(CdxTag tag, string literal, byte[] lit)
        {
            byte[] a, b;
            try
            {
                a = tag.EncodeCharacterKey(literal + "A");
                b = tag.EncodeCharacterKey(literal + "B");
            }
            catch { return null; }                       // uncertain → widen

            // Length of the shared head run (the literal's primary weights). The two probes share
            // every head byte of the literal, then diverge at the appended char's head weight.
            int max = Math.Min(Math.Min(a.Length, b.Length), lit.Length);
            int hc = 0;
            while (hc < max && a[hc] == b[hc]) hc++;
            if (hc == 0) return null;                                          // no shared run → widen
            if (hc >= a.Length || hc >= b.Length || a[hc] == b[hc]) return null; // probes never diverged → widen
            if (hc > lit.Length) return null;                                 // defensive → widen

            // Byte-increment the head run (next prefix). Drop any trailing 0xFF that cannot carry;
            // if NOTHING can be incremented the bound would overflow the value space → widen.
            int i = hc - 1;
            while (i >= 0 && lit[i] == 0xFF) i--;
            if (i < 0) return null;                       // all-0xFF heads → open upper bound (widen)

            var up = new byte[i + 1];
            Array.Copy(lit, up, i + 1);
            up[i]++;
            return up;
        }

        private Analysis BuildPredicate(CdxTag tag, Func<double, bool> pred, bool exact)
        {
            var bm = new RecordBitmap(_rc);
            // Plan-only: the verdict (usedIndex / exact) is all EXPLAIN needs — skip the scan.
            if (_planOnly) return new Analysis(bm, exact, usedIndex: true);
            foreach (var entry in EntriesOf(tag))
            {
                _onExamine?.Invoke(tag); // INSTRUMENTATION: this entry is being examined by the build
                double? d = NumOf(tag.DecodeKey(entry.Key));
                if (d is null) continue;
                if (pred(d.Value))
                {
                    int idx = (int)entry.RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.Set(idx);
                }
            }
            return new Analysis(bm, exact, usedIndex: true);
        }

        private Analysis BuildNotEqual(CdxTag tag, double v)
        {
            // Set-all then clear the matched-equal range: a strict superset that needs no
            // assumption about index completeness, so the residual still confirms each.
            var bm = new RecordBitmap(_rc);
            bm.SetAll();
            // Plan-only: <> still uses the index (usedIndex true) — skip the clearing scan.
            if (_planOnly) return new Analysis(bm, exact: false, usedIndex: true);
            foreach (var entry in EntriesOf(tag))
            {
                _onExamine?.Invoke(tag); // INSTRUMENTATION: this entry is being examined by the build
                double? d = NumOf(tag.DecodeKey(entry.Key));
                if (d is null) continue;
                if (d.Value == v)
                {
                    int idx = (int)entry.RecordNumber - 1;
                    if ((uint)idx < (uint)_rc) bm.ClearBit(idx);
                }
            }
            return new Analysis(bm, exact: false, usedIndex: true);
        }

        private static double? NumOf(IndexKey k) => k.Value switch
        {
            int i => i,
            // A NaN decode (e.g. an all-zero transformed-double NULL Numeric key, which
            // DecodeTransformedDouble maps to NaN) has NO total order — every comparison
            // against it is false. Returning null (not NaN) makes the SeekCached binary-search
            // guards (`if (d is null) return null`) fire and fall back to the correct full
            // scan, instead of NaN collapsing the window and dropping real matches.
            double d when !double.IsNaN(d) => d,
            decimal m => (double)m,
            long l => l,
            // Date / DateTime decode to the SAME Julian-day (+ fractional day) scale the
            // {^...} literal is parsed into, so ordered range seeks line up byte-for-byte.
            DateOnly dt => IndexKey.YmdToJulianDay(dt.Year, dt.Month, dt.Day),
            DateTime t => IndexKey.YmdToJulianDay(t.Year, t.Month, t.Day) + t.TimeOfDay.TotalDays,
            _ => null,
        };
    }
}
