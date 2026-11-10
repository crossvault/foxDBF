using System;
using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// Recursive-descent parser for the VFP-SQL v1 grammar. Produces an immutable
/// <see cref="SqlStatement"/> AST. Every scalar / predicate fragment is located by text
/// and handed to <see cref="ParseExpression"/>; this parser owns only the SQL
/// skeleton. Keywords are case-insensitive.
/// </summary>
public static class SqlParser
{
    // All SQL text-to-expression paths enter here. Executor fragments have already been
    // normalized by the statement parser; derived fragments and stored DBC rules retain their xBase parser.
    internal static VfpExpression ParseExpression(string text, Func<string, string>? normalizeExpression = null,
        bool aggregates = false, bool predicates = false, bool allowLookup = false, bool xbase = false)
    {
        if (normalizeExpression is not null) text = normalizeExpression(text);
        return xbase ? VfpExpression.Parse(text)
            : VfpExpression.ParseSql(text, aggregates, predicates, allowLookup);
    }

    /// <summary>Parses a single statement. Throws <see cref="FoxDbfSqlException"/> on bad input.</summary>
    public static SqlStatement Parse(string sql) => Parse(sql, normalizeExpression: null);

    // microVFP supplies its reference-name rewrite here, after the SQL skeleton has
    // identified expressions. Table names, aliases and column lists are never calls.
    internal static SqlStatement Parse(string sql, Func<string, string>? normalizeExpression)
    {
        if (sql is null) throw new FoxDbfSqlException("SQL text is null.");
        sql = SqlLexer.NormalizeArrowQualifiers(sql);
        var p = new State(sql, SqlLexer.Tokenize(sql), normalizeExpression);
        var stmt = p.ParseStatement();
        p.SkipSemicolons();
        if (!p.AtEnd)
            throw new FoxDbfSqlException("Unexpected text after statement.", p.Current.Start);
        return stmt;
    }

    /// <summary>Parses a ';'-separated script into a list of statements.</summary>
    public static IReadOnlyList<SqlStatement> ParseScript(string sql)
    {
        if (sql is null) throw new FoxDbfSqlException("SQL text is null.");
        sql = SqlLexer.NormalizeArrowQualifiers(sql);
        var p = new State(sql, SqlLexer.Tokenize(sql));
        var list = new List<SqlStatement>();
        p.SkipSemicolons();
        while (!p.AtEnd)
        {
            list.Add(p.ParseStatement());
            if (!p.AtEnd && p.Current.Kind != TokKind.Semicolon)
                throw new FoxDbfSqlException("Expected ';' between statements.", p.Current.Start);
            p.SkipSemicolons();
        }
        return list;
    }

    // -------------------------------------------------------------------
    //  Parser state machine
    // -------------------------------------------------------------------
    private sealed class State
    {
        private readonly string _src;
        private readonly List<Token> _t;
        private readonly Func<string, string>? _normalizeExpression;
        private int _pos;

        public State(string src, List<Token> tokens, Func<string, string>? normalizeExpression = null)
        {
            _src = src;
            _t = tokens;
            _normalizeExpression = normalizeExpression;
        }

        // ---- token cursor ------------------------------------------------
        public Token Current => _t[_pos];
        private Token Peek(int k = 1) => _t[Math.Min(_pos + k, _t.Count - 1)];
        public bool AtEnd => Current.Kind == TokKind.Eof;
        private void Advance() { if (_pos < _t.Count - 1) _pos++; }

        public void SkipSemicolons() { while (Current.Kind == TokKind.Semicolon) Advance(); }

        private static bool Kw(Token t, string word)
            => t.Kind == TokKind.Word && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);
        private bool IsKw(string word) => Kw(Current, word);

        private bool IsKwAny(params string[] words)
        {
            foreach (var w in words) if (IsKw(w)) return true;
            return false;
        }

        private FoxDbfSqlException Err(string msg) => new(msg, Current.Start);

        private void ExpectKw(string word)
        {
            if (!IsKw(word)) throw Err($"Expected '{word}'.");
            Advance();
        }

        private string ExpectWord(string what)
        {
            if (Current.Kind != TokKind.Word) throw Err($"Expected {what}.");
            var s = Current.Text;
            Advance();
            return s;
        }

        // A name token: identifier or string literal (e.g. cursor / file name).
        private string ExpectName(string what)
        {
            if (Current.Kind == TokKind.Word) { var s = Current.Text; Advance(); return s; }
            if (Current.Kind == TokKind.String) { var s = Unquote(Current.Text); Advance(); return s; }
            throw Err($"Expected {what}.");
        }

        private static string Unquote(string s)
            => s.Length >= 2 ? s.Substring(1, s.Length - 2) : s;

        // Parses the current Number token as a 32-bit integer, throwing a clear
        // FoxDbfSqlException (with position) on a decimal point or overflow instead
        // of leaking a raw FormatException/OverflowException. Advances past the token.
        private int ExpectInt(string what)
        {
            if (!int.TryParse(Current.Text, out var n))
                throw Err($"Expected {what}, found '{Current.Text}'.");
            Advance();
            return n;
        }

        // ---- statement dispatch -----------------------------------------
        public SqlStatement ParseStatement()
        {
            if (Current.Kind != TokKind.Word) throw Err("Expected a SQL command.");
            if (IsKw("SELECT")) return ParseSelectOrArea();
            if (IsKw("INSERT")) return ParseInsert();
            if (IsKw("UPDATE")) return ParseUpdate();
            if (IsKw("DELETE")) return ParseDelete();
            if (IsKw("USE")) return ParseUse();
            if (IsKw("CREATE")) return ParseCreateTable();
            if (IsKw("ALTER")) return ParseAlterTable();
            if (IsKw("DROP")) return ParseDropTable();
            throw Err($"Unknown command '{Current.Text}'.");
        }

        // ---- fragment extraction ----------------------------------------
        // Scans depth-aware from the cursor until EOF, a Semicolon, a top-level
        // RParen, a top-level Comma (when stopAtComma), or a top-level Word in
        // stopWords. Returns the consumed half-open token range and advances the
        // cursor to the terminator.
        private (int start, int end) ScanRange(HashSet<string>? stopWords, bool stopAtComma)
        {
            int start = _pos;
            int depth = 0;
            while (!AtEnd)
            {
                var t = Current;
                if (t.Kind == TokKind.LParen) depth++;
                else if (t.Kind == TokKind.RParen)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0)
                {
                    if (t.Kind == TokKind.Semicolon) break;
                    if (stopAtComma && t.Kind == TokKind.Comma) break;
                    if (t.Kind == TokKind.Word && stopWords is not null &&
                        stopWords.Contains(t.Text.ToUpperInvariant())) break;
                }
                Advance();
            }
            return (start, _pos);
        }

        private string RangeText(int start, int end)
        {
            int from = _t[start].Start;
            int to = _t[end - 1].End;
            return _src.Substring(from, to - from).Trim();
        }

        private VfpExpression BuildExpr(string text, int srcStart, bool aggregates = false, bool predicates = false, bool allowLookup = false)
        {
            try
            {
                return ParseExpression(text, _normalizeExpression, aggregates, predicates, allowLookup);
            }
            catch (ExpressionException ex)
            {
                int pos = ex.Position >= 0 ? srcStart + ex.Position : srcStart;
                throw new FoxDbfSqlException(
                    $"Invalid expression '{text}': {ex.Message}", ex, pos) { VfpErrorNumber = ex.VfpErrorNumber };
            }
        }

        // Parses an expression fragment terminated per stopWords/stopAtComma.
        private VfpExpression ParseExprFragment(HashSet<string>? stopWords, bool stopAtComma, bool aggregates = false, bool predicates = false, bool allowLookup = false)
        {
            var (s, e) = ScanRange(stopWords, stopAtComma);
            if (e <= s) throw Err("Expected an expression.");
            return BuildExpr(RangeText(s, e), _t[s].Start, aggregates: aggregates, predicates: predicates, allowLookup: allowLookup);
        }

        // =================================================================
        //  WHERE predicate tree (subqueries and SQL-only infix conditions)
        // =================================================================

        private static readonly HashSet<string> WhereClauseStops =
            new(StringComparer.Ordinal) { "GROUP", "HAVING", "ORDER", "INTO", "UNION" };

        private static readonly HashSet<string> HavingClauseStops =
            new(StringComparer.Ordinal) { "ORDER", "INTO", "UNION" };

        // Depth-aware look-ahead (does NOT advance): true iff a '(' immediately followed by SELECT
        // appears before the clause terminates (a top-level stop word, a closing ')' of an enclosing
        // group, a ';' or EOF). Used to choose the single-VfpExpression fast path vs. the boolean tree,
        // and to reject a sub-SELECT in HAVING.
        private bool FragmentHasSubquery(HashSet<string> stopWords)
        {
            int p = _pos, depth = 0;
            while (p < _t.Count)
            {
                var t = _t[p];
                if (t.Kind == TokKind.LParen)
                {
                    if (p + 1 < _t.Count && Kw(_t[p + 1], "SELECT")) return true;
                    depth++;
                }
                else if (t.Kind == TokKind.RParen)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0)
                {
                    if (t.Kind is TokKind.Semicolon or TokKind.Eof) break;
                    if (t.Kind == TokKind.Word && stopWords.Contains(t.Text.ToUpperInvariant())) break;
                }
                p++;
            }
            return false;
        }

        private SqlPredicate ParseBoolOr()
        {
            var left = ParseBoolAnd();
            while (IsKw("OR")) { Advance(); left = new OrPredicate(left, ParseBoolAnd()); }
            return left;
        }

        private SqlPredicate ParseBoolAnd()
        {
            var left = ParseBoolNot();
            while (IsKw("AND")) { Advance(); left = new AndPredicate(left, ParseBoolNot()); }
            return left;
        }

        private SqlPredicate ParseBoolNot()
        {
            // A leading NOT is a BOOLEAN negation only when it negates a subquery atom:
            //   'NOT EXISTS (…)'                         (Peek(1) is EXISTS)
            //   'NOT ( … subquery … )'                   (Peek(1) is a group containing a sub-SELECT)
            //   'NOT <expr> IN (SELECT …)' / 'NOT <expr> <op> (SELECT …)' / 'NOT NOT EXISTS (…)'
            //                                            (the upcoming leaf carries a top-level subquery atom)
            // A 'NOT' that begins a plain expression (e.g. NOT ISNULL(x), NOT active) stays part of the
            // VfpExpression leaf, so the engine handles it unchanged (single-VfpExpression fast path).
            if (IsKw("NOT"))
            {
                // Look through repeated NOTs before deciding which parser owns the operand.
                int operand = _pos + 1;
                while (Kw(_t[operand], "NOT")) operand++;
                if (Kw(_t[operand], "EXISTS") ||
                    (_t[operand].Kind == TokKind.LParen && ParenContainsSubquery(operand)) ||
                    LeafHasTopLevelSubquery(operand))
                {
                    Advance();
                    return new NotPredicate(ParseBoolNot());
                }
            }
            return ParsePrimaryPred();
        }

        // Depth-aware look-ahead (does NOT advance): true iff the leaf token run beginning at 'from' —
        // up to the next top-level AND/OR, clause stop, enclosing ')', ';' or EOF — contains a top-level
        // subquery atom (a '(' immediately followed by SELECT at the leaf's own nesting depth, i.e. the
        // right operand of IN or of a comparison operator). Mirrors the scan in ParseLeafPred so the
        // no-subquery fast path stays a single VfpExpression leaf.
        private bool LeafHasTopLevelSubquery(int from)
        {
            int p = from, depth = 0;
            while (p < _t.Count)
            {
                var t = _t[p];
                if (t.Kind == TokKind.LParen)
                {
                    if (depth == 0 && p + 1 < _t.Count && Kw(_t[p + 1], "SELECT")) return true;
                    depth++;
                }
                else if (t.Kind == TokKind.RParen)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0)
                {
                    if (t.Kind is TokKind.Semicolon or TokKind.Eof) break;
                    if (t.Kind == TokKind.Word && (Kw(t, "AND") || Kw(t, "OR"))) break;
                    if (t.Kind == TokKind.Word && WhereClauseStops.Contains(t.Text.ToUpperInvariant())) break;
                }
                p++;
            }
            return false;
        }

        private SqlPredicate ParsePrimaryPred()
        {
            // EXISTS (SELECT …)
            if (IsKw("EXISTS"))
            {
                Advance();
                if (Current.Kind != TokKind.LParen) throw Err("Expected '(' after EXISTS.");
                Advance(); // (
                var sub = ParseSubquerySelect();
                if (Current.Kind != TokKind.RParen) throw Err("Expected ')' to close the EXISTS subquery.");
                Advance(); // )
                return new ExistsSubqueryPredicate(sub);
            }

            // ( boolean group )  — a parenthesized region that is itself a predicate (NOT the left side
            // of a comparison / IN, which ParseLeafPred owns).
            if (Current.Kind == TokKind.LParen && ParenIsBooleanGroup(_pos))
            {
                Advance(); // (
                var inner = ParseBoolOr();
                if (Current.Kind != TokKind.RParen) throw Err("Expected ')' to close the group.");
                Advance(); // )
                return inner;
            }

            return ParseLeafPred();
        }

        // A leaf: a maximal run of tokens up to the next top-level AND/OR/stop/')'. It is either a plain
        // expression (no sub-SELECT) or 'expr [NOT] IN (SELECT …)' / 'expr <op> (SELECT …)'.
        private SqlPredicate ParseLeafPred()
        {
            int start = _pos, depth = 0, sqOpen = -1;
            bool betweenBound = false;
            int p = _pos;
            while (p < _t.Count)
            {
                var t = _t[p];
                if (t.Kind == TokKind.LParen)
                {
                    if (depth == 0 && sqOpen == -1 && p + 1 < _t.Count && Kw(_t[p + 1], "SELECT"))
                        sqOpen = p;
                    depth++;
                }
                else if (t.Kind == TokKind.RParen)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0)
                {
                    if (t.Kind is TokKind.Semicolon or TokKind.Eof) break;
                    if (Kw(t, "BETWEEN"))
                    {
                        // An infix [NOT] BETWEEN owns its first AND, even with parenthesized
                        // bounds. The scalar BETWEEN(...) function has no preceding operand.
                        int operand = p - 1;
                        if (operand >= start && Kw(_t[operand], "NOT")) operand--;
                        // The SQL lexer splits .T./.F./.NULL. into dot, word, dot.
                        // Do not mistake a dotted .NOT. before scalar BETWEEN(...) for an operand.
                        bool dottedLiteral = operand >= start + 2 && _t[operand].Kind == TokKind.Dot &&
                            _t[operand - 2].Kind == TokKind.Dot &&
                            (Kw(_t[operand - 1], "T") || Kw(_t[operand - 1], "F") ||
                             Kw(_t[operand - 1], "Y") || Kw(_t[operand - 1], "N") || Kw(_t[operand - 1], "NULL"));
                        if (operand >= start && !Kw(_t[operand], "NOT") &&
                            (dottedLiteral || _t[operand].Kind is TokKind.Word or TokKind.Number or TokKind.String or TokKind.Date or TokKind.RParen))
                            betweenBound = true;
                    }
                    if (Kw(t, "AND") && betweenBound) betweenBound = false;
                    else if (Kw(t, "AND") || Kw(t, "OR")) break;
                    if (t.Kind == TokKind.Word && WhereClauseStops.Contains(t.Text.ToUpperInvariant())) break;
                }
                p++;
            }
            int leafEnd = p;

            if (sqOpen < 0)
            {
                if (leafEnd <= start) throw Err("Expected a predicate.");
                var expr = BuildExpr(RangeText(start, leafEnd), _t[start].Start, predicates: true);
                _pos = leafEnd;
                return new ExprPredicate(expr);
            }

            if (sqOpen <= start) throw Err("Expected an expression before the subquery.");
            int sqClose = MatchParen(sqOpen);
            var before = _t[sqOpen - 1];

            SqlPredicate result;
            if (before.Kind == TokKind.Word && Kw(before, "IN"))
            {
                bool negated = sqOpen - 2 >= start && _t[sqOpen - 2].Kind == TokKind.Word && Kw(_t[sqOpen - 2], "NOT");
                int leftEnd = negated ? sqOpen - 2 : sqOpen - 1;
                if (leftEnd <= start) throw Err("Expected an expression before IN.");
                var left = BuildExpr(RangeText(start, leftEnd), _t[start].Start);
                var sub = ParseSubqueryAt(sqOpen);
                result = new InSubqueryPredicate(left, negated, sub);
            }
            else
            {
                // scalar comparison: collect the 1-2 operator tokens that precede the subquery.
                int j = sqOpen - 1;
                var ops = new List<string>();
                while (j >= start && IsCmpOpToken(_t[j])) { ops.Insert(0, _t[j].Text); j--; }
                if (ops.Count == 0) throw Err("Expected IN or a comparison operator before the subquery.");
                int leftEnd = j + 1;
                if (leftEnd <= start) throw Err("Expected an expression before the comparison.");
                var left = BuildExpr(RangeText(start, leftEnd), _t[start].Start);
                var sub = ParseSubqueryAt(sqOpen);
                result = new ScalarSubqueryPredicate(left, string.Concat(ops), sub);
            }

            _pos = sqClose + 1;
            return result;
        }

        private static bool IsCmpOpToken(Token t)
            => t.Kind == TokKind.Eq || t.Kind == TokKind.Bang ||
               (t.Kind == TokKind.Op && t.Text is "<" or ">" or "#");

        // Parses the subquery whose opening '(' is at openIdx, leaving the cursor just past its ')'.
        private SelectStatement ParseSubqueryAt(int openIdx)
        {
            _pos = openIdx + 1; // consume '('
            var sub = ParseSubquerySelect();
            if (Current.Kind != TokKind.RParen) throw Err("Expected ')' to close the subquery.");
            // caller advances past ')' (it already computed the matching index).
            return sub;
        }

        private SelectStatement ParseSubquerySelect()
        {
            ExpectKw("SELECT");
            return ParseSelectBody();
        }

        // Index of the ')' matching the '(' at openIdx.
        private int MatchParen(int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < _t.Count; i++)
            {
                if (_t[i].Kind == TokKind.LParen) depth++;
                else if (_t[i].Kind == TokKind.RParen) { if (--depth == 0) return i; }
            }
            throw Err("Unbalanced parentheses.");
        }

        // True iff the '(' at openIdx encloses a sub-SELECT (at any nesting inside it).
        private bool ParenContainsSubquery(int openIdx)
        {
            int close = MatchParen(openIdx);
            for (int i = openIdx; i < close; i++)
                if (_t[i].Kind == TokKind.LParen && i + 1 < _t.Count && Kw(_t[i + 1], "SELECT"))
                    return true;
            return false;
        }

        // True iff the '(' at openIdx is a standalone boolean GROUP: what follows its matching ')' is a
        // boolean operator / clause stop / ')' / end (so the parens are not the left side of a larger
        // expression such as '(a+b) > (SELECT …)').
        private bool ParenIsBooleanGroup(int openIdx)
        {
            int after = MatchParen(openIdx) + 1;
            if (after >= _t.Count) return true;
            var t = _t[after];
            if (t.Kind is TokKind.Eof or TokKind.Semicolon or TokKind.RParen) return true;
            if (t.Kind == TokKind.Word && (Kw(t, "AND") || Kw(t, "OR"))) return true;
            if (t.Kind == TokKind.Word && WhereClauseStops.Contains(t.Text.ToUpperInvariant())) return true;
            return false;
        }

        // =================================================================
        //  SELECT  (SQL query)  +  SELECT <area>  (work-area switch)
        // =================================================================
        private SqlStatement ParseSelectOrArea()
        {
            Advance(); // SELECT

            // Disambiguate the work-area form: SELECT followed by a single number,
            // or a single non-keyword identifier, with nothing after it.
            if (Current.Kind == TokKind.Number && IsClauseEnd(Peek()))
            {
                int area = ExpectInt("a work-area number");
                return new SelectAreaCommand(area, null);
            }
            if (Current.Kind == TokKind.Word && !IsSelectHeadKeyword(Current) && IsClauseEnd(Peek()))
            {
                string alias = Current.Text;
                Advance();
                return new SelectAreaCommand(null, alias);
            }

            return ParseSelectBody();
        }

        private static bool IsClauseEnd(Token t) => t.Kind is TokKind.Eof or TokKind.Semicolon;

        // Keywords that legitimately begin a real SELECT (so they are NOT a work-area alias).
        private static bool IsSelectHeadKeyword(Token t)
            => Kw(t, "ALL") || Kw(t, "DISTINCT") || Kw(t, "TOP");

        private static readonly HashSet<string> SelectClauseStops = new(StringComparer.Ordinal)
            { "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "INTO", "UNION" };

        private SelectStatement ParseSelectBody()
        {
            bool distinct = false;
            if (IsKw("ALL")) Advance();
            else if (IsKw("DISTINCT")) { Advance(); distinct = true; }

            int? top = null;
            bool topPercent = false;
            if (IsKw("TOP"))
            {
                Advance();
                if (Current.Kind != TokKind.Number) throw Err("Expected a number after TOP.");
                top = ExpectInt("an integer after TOP");
                if (IsKw("PERCENT")) { Advance(); topPercent = true; }
            }

            var items = ParseSelectList();

            if (!IsKw("FROM")) throw Err("Expected FROM.");
            Advance();
            var from = ParseFromList();
            var joins = ParseJoins();

            VfpExpression? where = null;
            SqlPredicate? wherePredicate = null;
            if (IsKw("WHERE"))
            {
                Advance();
                if (FragmentHasSubquery(WhereClauseStops))
                    wherePredicate = ParseBoolOr();
                else
                {
                    where = ParseExprFragment(WhereClauseStops, stopAtComma: false, predicates: true);
                    // SQL-only predicates cannot be reparsed as xBase optimizer residuals.
                    // Ordinary WHERE expressions retain their existing fast path.
                    if (where.HasSqlInfix)
                    {
                        wherePredicate = new ExprPredicate(where);
                        where = null;
                    }
                }
            }

            var groupBy = new List<VfpExpression>();
            if (IsKw("GROUP"))
            {
                Advance();
                ExpectKw("BY");
                var stop = new HashSet<string>(StringComparer.Ordinal) { "HAVING", "ORDER", "INTO", "UNION" };
                do { groupBy.Add(ParseExprFragment(stop, stopAtComma: true)); }
                while (TryComma());
            }

            VfpExpression? having = null;
            if (IsKw("HAVING"))
            {
                Advance();
                // VFP rejects a sub-SELECT in HAVING ("SQL: Invalid use of subquery."); mirror that
                // rather than silently mis-evaluate it.
                if (FragmentHasSubquery(HavingClauseStops))
                    throw new FoxDbfSqlException("SQL: Invalid use of subquery in HAVING.", Current.Start) { VfpErrorNumber = 1810 };
                having = ParseExprFragment(HavingClauseStops, stopAtComma: false, aggregates: true, predicates: true);
            }

            var orderBy = new List<OrderItem>();
            if (IsKw("ORDER"))
            {
                Advance();
                ExpectKw("BY");
                do { orderBy.Add(ParseOrderItem()); }
                while (TryComma());
            }

            IntoClause? into = IsKw("INTO") ? ParseInto() : null;

            UnionClause? union = null;
            if (IsKw("UNION"))
            {
                Advance();
                bool all = false;
                if (IsKw("ALL")) { Advance(); all = true; }
                ExpectKw("SELECT");
                union = new UnionClause(all, ParseSelectBody());
            }

            return new SelectStatement(distinct, top, topPercent, items, from, joins,
                where, groupBy, having, orderBy, into, union, wherePredicate);
        }

        private bool TryComma()
        {
            if (Current.Kind == TokKind.Comma) { Advance(); return true; }
            return false;
        }

        private List<SelectItem> ParseSelectList()
        {
            int start = _pos;
            var items = new List<SelectItem>();
            do { items.Add(ParseSelectItem()); }
            while (TryComma());
            int distinctAggregates = 0;
            for (int i = start + 2; i < _pos; i++)
            {
                if (Kw(_t[i], "DISTINCT") && _t[i - 1].Kind == TokKind.LParen
                    && (Kw(_t[i - 2], "COUNT") || Kw(_t[i - 2], "CNT")
                        || Kw(_t[i - 2], "SUM") || Kw(_t[i - 2], "AVG"))
                    && ++distinctAggregates > 1)
                    throw new FoxDbfSqlException("SQL: DISTINCT is invalid.", _t[i].Start) { VfpErrorNumber = 1819 };
            }
            return items;
        }

        private static readonly HashSet<string> SelectItemStops =
            new(StringComparer.Ordinal) { "FROM", "AS" };

        private SelectItem ParseSelectItem()
        {
            // *
            if (Current.Kind == TokKind.Star)
            {
                Advance();
                return new SelectItem(true, null, null, null);
            }
            // alias.*
            if (Current.Kind == TokKind.Word && Peek(1).Kind == TokKind.Dot && Peek(2).Kind == TokKind.Star)
            {
                string a = Current.Text;
                Advance(); Advance(); Advance();
                return new SelectItem(true, a, null, null);
            }
            // FUNC(*) aggregate (the expression engine cannot represent a bare '*' arg)
            if (Current.Kind == TokKind.Word && Peek(1).Kind == TokKind.LParen &&
                Peek(2).Kind == TokKind.Star && Peek(3).Kind == TokKind.RParen
                && (IsItemBoundary(Peek(4)) || Peek(4).Kind == TokKind.Word))
            {
                string fn = Current.Text.ToUpperInvariant();
                if (fn is "MIN" or "MAX" or "SUM" or "AVG")
                    throw new FoxDbfSqlException("Expression is invalid. A valid expression is required.") { VfpErrorNumber = 152 };
                if (fn == "CNT") fn = "COUNT";
                Advance(); Advance(); Advance(); Advance();
                string? aggAlias = TryParseAsAlias();
                return new SelectItem(false, null, null, aggAlias, fn);
            }

            var (s, e) = ScanRange(SelectItemStops, stopAtComma: true);
            if (e <= s) throw Err("Expected a select expression.");
            string text = RangeText(s, e);

            // explicit AS alias
            if (IsKw("AS"))
            {
                Advance();
                string alias = ExpectWord("an alias name");
                return new SelectItem(false, null, BuildExpr(text, _t[s].Start, aggregates: true), alias);
            }

            // no AS: maybe an implicit trailing alias (expr alias). Try whole text first;
            // only if it fails to parse, retry stripping a trailing identifier as alias.
            try
            {
                return new SelectItem(false, null, BuildExpr(text, _t[s].Start, aggregates: true), null);
            }
            catch (FoxDbfSqlException)
            {
                if (e - s >= 2 && _t[e - 1].Kind == TokKind.Word)
                {
                    string aliasText = RangeText(e - 1, e);
                    string exprText = RangeText(s, e - 1);
                    try
                    {
                        return new SelectItem(false, null, BuildExpr(exprText, _t[s].Start, aggregates: true), aliasText);
                    }
                    catch (FoxDbfSqlException) { /* fall through to report the original error */ }
                }
                throw;
            }
        }

        private static bool IsItemBoundary(Token t)
            => t.Kind is TokKind.Comma or TokKind.Eof or TokKind.Semicolon || Kw(t, "FROM") || Kw(t, "AS");

        private string? TryParseAsAlias()
        {
            if (IsKw("AS")) { Advance(); return ExpectWord("an alias name"); }
            // implicit alias for FUNC(*): a single trailing identifier before a boundary
            if (Current.Kind == TokKind.Word && IsItemBoundary(Peek()))
            {
                string a = Current.Text;
                Advance();
                return a;
            }
            return null;
        }

        private OrderItem ParseOrderItem()
        {
            // ordinal: a lone integer followed by a clause/item boundary
            if (Current.Kind == TokKind.Number && IsOrderBoundary(Peek()))
            {
                int ord = ExpectInt("an ORDER BY column ordinal");
                bool d = ReadAscDesc();
                return new OrderItem(null, ord, d);
            }

            var stop = new HashSet<string>(StringComparer.Ordinal)
                { "ASC", "DESC", "INTO", "UNION" };
            int start = _pos;
            var expr = ParseExprFragment(stop, stopAtComma: true, aggregates: true);
            for (int i = start; i + 1 < _pos; i++)
                if (Kw(_t[i], "CNT") && _t[i + 1].Kind == TokKind.LParen)
                    throw new FoxDbfSqlException("Function name is missing ( ).") { VfpErrorNumber = 1300 };
            bool desc = ReadAscDesc();
            return new OrderItem(expr, null, desc);
        }

        private static bool IsOrderBoundary(Token t)
            => t.Kind is TokKind.Comma or TokKind.Eof or TokKind.Semicolon
               || Kw(t, "ASC") || Kw(t, "DESC") || Kw(t, "INTO") || Kw(t, "UNION");

        private bool ReadAscDesc()
        {
            if (IsKw("ASC")) { Advance(); return false; }
            if (IsKw("DESC")) { Advance(); return true; }
            return false;
        }

        private IntoClause ParseInto()
        {
            ExpectKw("INTO");
            if (IsKw("CURSOR"))
            {
                Advance();
                string name = ExpectName("a cursor name");
                bool rw = false, nf = false;
                // READWRITE / NOFILTER, order-independent
                for (int guard = 0; guard < 2; guard++)
                {
                    if (IsKw("READWRITE") && !rw) { Advance(); rw = true; }
                    else if (IsKw("NOFILTER") && !nf) { Advance(); nf = true; }
                    else break;
                }
                return new IntoClause(IntoKind.Cursor, name, rw, nf);
            }
            if (IsKw("TABLE") || IsKw("DBF"))
            {
                Advance();
                return new IntoClause(IntoKind.Table, ExpectName("a table name"), false, false);
            }
            if (IsKw("ARRAY"))
            {
                Advance();
                return new IntoClause(IntoKind.Array, ExpectName("an array name"), false, false);
            }
            throw Err("Expected CURSOR, TABLE, DBF or ARRAY after INTO.");
        }

        private List<FromSource> ParseFromList()
        {
            var list = new List<FromSource>();
            do { list.Add(ParseSource()); }
            while (TryComma());
            return list;
        }

        private static bool IsSourceTrailKeyword(Token t)
            => Kw(t, "INNER") || Kw(t, "LEFT") || Kw(t, "RIGHT") || Kw(t, "FULL") ||
               Kw(t, "OUTER") || Kw(t, "JOIN") || Kw(t, "ON") || Kw(t, "WHERE") ||
               Kw(t, "GROUP") || Kw(t, "HAVING") || Kw(t, "ORDER") || Kw(t, "INTO") ||
               Kw(t, "UNION") || Kw(t, "AS");

        private FromSource ParseSource()
        {
            if (Current.Kind != TokKind.Word) throw Err("Expected a table name.");
            string first = Current.Text;
            Advance();

            string? db = null;
            string table;
            if (Current.Kind == TokKind.Bang)
            {
                db = first;
                Advance();
                table = ExpectWord("a table name after '!'");
            }
            else
            {
                table = first;
            }

            string? alias = null;
            if (IsKw("AS"))
            {
                Advance();
                alias = ExpectWord("an alias name");
            }
            else if (Current.Kind == TokKind.Word && !IsSourceTrailKeyword(Current))
            {
                alias = Current.Text;
                Advance();
            }

            return new FromSource(db, table, alias);
        }

        private static readonly HashSet<string> JoinOnStops = new(StringComparer.Ordinal)
            { "INNER", "LEFT", "RIGHT", "FULL", "JOIN", "WHERE", "GROUP", "HAVING", "ORDER", "INTO", "UNION" };

        private List<JoinClause> ParseJoins()
        {
            var joins = new List<JoinClause>();
            while (true)
            {
                JoinType jt;
                if (IsKw("INNER")) { Advance(); ExpectKw("JOIN"); jt = JoinType.Inner; }
                else if (IsKw("LEFT")) { Advance(); if (IsKw("OUTER")) Advance(); ExpectKw("JOIN"); jt = JoinType.Left; }
                else if (IsKw("RIGHT")) { Advance(); if (IsKw("OUTER")) Advance(); ExpectKw("JOIN"); jt = JoinType.Right; }
                else if (IsKw("FULL")) { Advance(); if (IsKw("OUTER")) Advance(); ExpectKw("JOIN"); jt = JoinType.Full; }
                else if (IsKw("JOIN")) { Advance(); jt = JoinType.Inner; }
                else break;

                var src = ParseSource();
                ExpectKw("ON");
                var on = ParseExprFragment(JoinOnStops, stopAtComma: false, predicates: true);
                joins.Add(new JoinClause(jt, src, on));
            }
            return joins;
        }

        // =================================================================
        //  INSERT
        // =================================================================
        private InsertStatement ParseInsert()
        {
            ExpectKw("INSERT");
            ExpectKw("INTO");
            var (db, table) = ParseDbTable();

            List<string>? cols = null;
            if (Current.Kind == TokKind.LParen)
            {
                Advance();
                cols = new List<string>();
                do { cols.Add(ExpectWord("a column name")); }
                while (TryComma());
                if (Current.Kind != TokKind.RParen) throw Err("Expected ')' after column list.");
                Advance();
            }

            // Memory-source forms: INSERT INTO tbl FROM ARRAY name | FROM MEMVAR (no column list / VALUES).
            if (IsKw("FROM"))
            {
                // A column list is ONLY valid with VALUES. VFP9 rejects INSERT INTO tbl (cols) FROM ARRAY|MEMVAR
                // with error 10 "Syntax error" (oracle-pinned, MicroVfpTypedErrorOracleTests). Mirror it here
                // rather than silently ignoring the list and mapping to the table's full physical field order —
                // which would put the array/memvar data into the WRONG columns (a statement VFP would never run).
                if (cols is not null)
                    throw new FoxDbfSqlException(
                        "A column list is not allowed with INSERT ... FROM ARRAY/MEMVAR (only with VALUES).",
                        Current.Start) { VfpErrorNumber = 10 };
                Advance();
                if (IsKw("ARRAY"))
                {
                    Advance();
                    string arrName = ExpectWord("an array name after FROM ARRAY");
                    return new InsertStatement(db, table, cols, System.Array.Empty<VfpExpression>(),
                        InsertSourceKind.Array, arrName);
                }
                if (IsKw("MEMVAR"))
                {
                    Advance();
                    return new InsertStatement(db, table, cols, System.Array.Empty<VfpExpression>(),
                        InsertSourceKind.Memvar, null);
                }
                throw Err("Expected ARRAY or MEMVAR after INSERT ... FROM.");
            }

            if (IsKw("SELECT"))
                return new InsertStatement(db, table, cols, System.Array.Empty<VfpExpression>(),
                    InsertSourceKind.Select, Query: ParseSubquerySelect());

            ExpectKw("VALUES");
            if (Current.Kind != TokKind.LParen) throw Err("Expected '(' after VALUES.");
            Advance();
            var values = new List<VfpExpression>();
            // VFP9 accepts LOOKUP's field references in VALUES, unlike SELECT and SQL DML clauses.
            do { values.Add(ParseExprFragment(null, stopAtComma: true, allowLookup: true)); }
            while (TryComma());
            if (Current.Kind != TokKind.RParen) throw Err("Expected ')' to close VALUES.");
            Advance();

            return new InsertStatement(db, table, cols, values);
        }

        // =================================================================
        //  UPDATE
        // =================================================================
        private static readonly HashSet<string> UpdateValueStops =
            new(StringComparer.Ordinal) { "WHERE" };

        private UpdateStatement ParseUpdate()
        {
            ExpectKw("UPDATE");
            var (db, table) = ParseDbTable();
            ExpectKw("SET");

            var sets = new List<SetClause>();
            do
            {
                string col = ExpectWord("a column name");
                // optional alias.field qualifier
                if (Current.Kind == TokKind.Dot)
                {
                    Advance();
                    col = col + "." + ExpectWord("a field name");
                }
                if (Current.Kind != TokKind.Eq) throw Err("Expected '=' in SET assignment.");
                Advance();
                var val = ParseExprFragment(UpdateValueStops, stopAtComma: true);
                sets.Add(new SetClause(col, val));
            }
            while (TryComma());

            VfpExpression? where = null;
            if (IsKw("WHERE"))
            {
                Advance();
                where = ParseExprFragment(null, stopAtComma: false, predicates: true);
            }

            return new UpdateStatement(db, table, sets, where);
        }

        // =================================================================
        //  DELETE
        // =================================================================
        private DeleteStatement ParseDelete()
        {
            ExpectKw("DELETE");
            ExpectKw("FROM");
            var (db, table) = Current.Kind == TokKind.String
                ? ((string?)null, ExpectName("a table name")) : ParseDbTable();

            VfpExpression? where = null;
            if (IsKw("WHERE"))
            {
                Advance();
                where = ParseExprFragment(null, stopAtComma: false, predicates: true);
            }
            return new DeleteStatement(db, table, where);
        }

        // =================================================================
        //  USE  +  helpers
        // =================================================================
        private static bool IsUseOption(Token t)
            => Kw(t, "IN") || Kw(t, "AGAIN") || Kw(t, "ALIAS") || Kw(t, "AS") ||
               Kw(t, "EXCLUSIVE") || Kw(t, "SHARED") || Kw(t, "NOUPDATE");

        private UseCommand ParseUse()
        {
            ExpectKw("USE");

            // bare USE = close current work area
            if (IsClauseEnd(Current))
                return new UseCommand(null, null, false, null, null, false, null, UseMode.Default, false);

            string? db = null, table = null;
            bool prompt = false;

            if (Current.Kind == TokKind.Question)
            {
                prompt = true;
                Advance();
            }
            else if (Current.Kind == TokKind.String ||
                     (Current.Kind == TokKind.Word && !IsUseOption(Current)))
            {
                if (Current.Kind == TokKind.Word && Peek(1).Kind == TokKind.Bang)
                {
                    db = Current.Text;
                    Advance(); // db
                    Advance(); // !
                    table = ExpectName("a table name after '!'");
                }
                else
                {
                    table = ExpectName("a table name");
                }
            }

            int? inArea = null;
            string? inAlias = null;
            bool again = false, noUpdate = false;
            string? alias = null;
            var mode = UseMode.Default;

            while (Current.Kind == TokKind.Word && IsUseOption(Current))
            {
                if (IsKw("IN"))
                {
                    Advance();
                    if (Current.Kind == TokKind.Number) { inArea = ExpectInt("a work-area number after IN"); }
                    else if (Current.Kind == TokKind.Word) { inAlias = Current.Text; Advance(); }
                    else throw Err("Expected a work-area number or alias after IN.");
                }
                else if (IsKw("AGAIN")) { Advance(); again = true; }
                else if (IsKw("ALIAS") || IsKw("AS")) { Advance(); alias = ExpectName("an alias name"); }
                else if (IsKw("EXCLUSIVE")) { Advance(); mode = UseMode.Exclusive; }
                else if (IsKw("SHARED")) { Advance(); mode = UseMode.Shared; }
                else if (IsKw("NOUPDATE")) { Advance(); noUpdate = true; }
            }

            return new UseCommand(db, table, prompt, inArea, inAlias, again, alias, mode, noUpdate);
        }

        // =================================================================
        //  CREATE TABLE
        // =================================================================
        private CreateTableStatement ParseCreateTable()
        {
            ExpectKw("CREATE");
            // CREATE TABLE | CREATE DBF are equivalent VFP forms.
            if (!IsKw("TABLE") && !IsKw("DBF")) throw Err("Expected TABLE after CREATE.");
            Advance();

            var (db, table) = ParseDbTable();

            string? longName = null;
            bool free = false;
            VfpExpression? codePage = null;
            if (IsKw("NAME"))
            {
                Advance();
                longName = ExpectName("a long table name");
            }
            else if (IsKw("FREE"))
            {
                Advance();
                free = true;
            }
            if (IsKw("CODEPAGE"))
            {
                Advance();
                if (Current.Kind != TokKind.Eq) throw CreateSyntaxError();
                Advance();
                codePage = ParseCreateCodePage();
            }

            string? fromArray = null, check = null, error = null;
            var keys = new List<CreateTableKey>();
            var cols = new List<ColumnDefinition>();
            if (IsKw("FROM"))
            {
                Advance();
                ExpectKw("ARRAY");
                fromArray = ExpectWord("an array name");
            }
            else
            {
                if (Current.Kind != TokKind.LParen) throw CreateSyntaxError();
                Advance();
                do
                {
                    if (IsKw("CHECK"))
                    {
                        Advance();
                        check = ReadCreateExpression("ERROR");
                        if (IsKw("ERROR")) { Advance(); error = ReadCreateExpression(); }
                    }
                    else if (IsKwAny("PRIMARY", "FOREIGN", "UNIQUE"))
                        keys.Add(ParseCreateKey(null));
                    else
                        cols.Add(ParseColumnDefinition(keys));
                }
                while (TryComma());
                if (Current.Kind != TokKind.RParen) throw CreateSyntaxError();
                Advance();
            }

            // VFP9 places constraints INSIDE the column list. Text after ')' is error 36.
            if (!AtEnd && Current.Kind != TokKind.Semicolon)
                throw new FoxDbfSqlException("Command contains unrecognized phrase/keyword.", Current.Start)
                    { VfpErrorNumber = 36 };

            return new CreateTableStatement(db, table, cols)
            {
                Free = free,
                LongName = longName,
                CodePage = codePage,
                FromArray = fromArray,
                Keys = keys,
                Check = check,
                Error = error,
                RequiresDatabase = check is not null || keys.Any(k => k.Kind != "UNIQUE") ||
                    cols.Any(c => c.Default is not null || c.Check is not null),
            };
        }

        private FoxDbfSqlException CreateSyntaxError()
            => new("Syntax error.", Current.Start) { VfpErrorNumber = 10 };

        private VfpExpression ParseCreateCodePage()
        {
            int start = _pos;
            int depth = 0;
            while (!AtEnd && Current.Kind != TokKind.Semicolon)
            {
                if (depth == 0)
                {
                    if (IsKwAny("FREE", "NAME", "CODEPAGE")) throw CreateSyntaxError();
                    if (IsKw("FROM")) break;
                    // A column declaration starts with name + type; parentheses belonging to
                    // the numeric expression (including function calls) must stay in that expression.
                    if (Current.Kind == TokKind.LParen && _pos > start &&
                        Peek().Kind == TokKind.Word && Peek(2).Kind == TokKind.Word) break;
                }
                if (Current.Kind == TokKind.LParen) depth++;
                if (Current.Kind == TokKind.RParen) depth--;
                Advance();
            }
            if (_pos == start) throw CreateSyntaxError();
            try { return BuildExpr(RangeText(start, _pos), _t[start].Start); }
            catch (FoxDbfSqlException) { throw CreateSyntaxError(); }
        }

        private string ReadCreateExpression(params string[] stops)
        {
            int start = _pos, depth = 0;
            while (!AtEnd)
            {
                if (depth == 0 && (Current.Kind is TokKind.Comma or TokKind.RParen or TokKind.Semicolon ||
                    stops.Any(stop => IsKw(stop) && (stop != "NOT" || Kw(Peek(), "NULL"))))) break;
                if (Current.Kind == TokKind.LParen) depth++;
                if (Current.Kind == TokKind.RParen) depth--;
                Advance();
            }
            if (_pos == start) throw CreateSyntaxError();
            return RangeText(start, _pos).Trim();
        }

        private CreateTableKey ParseCreateKey(string? field)
        {
            string kind = Current.Text.ToUpperInvariant();
            Advance();
            if (kind is "PRIMARY" or "FOREIGN") ExpectKw("KEY");
            string expression = field ?? ReadCreateExpression("TAG", "REFERENCES");
            string tag = field ?? "";
            if (IsKw("TAG")) { Advance(); tag = ExpectWord("a tag name"); }
            if (tag.Length == 0) throw CreateSyntaxError();
            string? parent = null, parentTag = null;
            if (kind == "REFERENCES" || IsKw("REFERENCES"))
            {
                if (kind != "REFERENCES") Advance();
                parent = ExpectName("a referenced table");
                if (IsKw("TAG")) { Advance(); parentTag = ExpectWord("a referenced tag"); }
            }
            return new CreateTableKey(kind, expression, tag, parent, parentTag);
        }

        private ColumnDefinition ParseColumnDefinition(List<CreateTableKey>? keys = null)
        {
            string name = ExpectWord("a column name");
            string typeWord = ExpectWord("a column type");
            char type = char.ToUpperInvariant(typeWord[0]);
            int? len = null, dec = null;
            if (Current.Kind == TokKind.LParen)
            {
                Advance();
                len = ExpectInt("a column length");
                if (TryComma()) dec = ExpectInt("a column decimal count");
                if (Current.Kind != TokKind.RParen) throw Err("Expected ')' after the column length.");
                Advance();
            }
            if (type == 'B' && len is not null && dec is null) { dec = len; len = 8; }
            bool? nullable = null;
            bool binary = false;
            Write.DbfAutoIncrement? auto = null;
            string? def = null, check = null, error = null;
            string[] modifiers = ["NULL", "NOT", "DEFAULT", "CHECK", "ERROR", "PRIMARY", "UNIQUE", "REFERENCES", "AUTOINC", "NOCPTRANS"];
            while (!AtEnd && Current.Kind is not (TokKind.Comma or TokKind.RParen or TokKind.Semicolon))
            {
                if (IsKw("NULL")) { nullable = true; Advance(); }
                else if (IsKw("NOT")) { Advance(); ExpectKw("NULL"); nullable = false; }
                else if (IsKw("NOCPTRANS")) { Advance(); binary = true; }
                else if (IsKw("DEFAULT")) { Advance(); def = ReadCreateExpression(modifiers); }
                else if (IsKw("CHECK")) { Advance(); check = ReadCreateExpression(modifiers); }
                else if (IsKw("ERROR")) { Advance(); error = ReadCreateExpression(modifiers); }
                else if (IsKwAny("PRIMARY", "UNIQUE", "REFERENCES"))
                {
                    var key = ParseCreateKey(name);
                    keys?.Add(key);
                }
                else if (IsKw("AUTOINC"))
                {
                    Advance();
                    int next = 1, step = 1;
                    if (IsKw("NEXTVALUE"))
                    {
                        Advance();
                        bool negative = Current.Text == "-";
                        if (negative) Advance();
                        next = ExpectInt("an autoincrement seed");
                        if (negative) next = -next;
                    }
                    if (IsKw("STEP")) { Advance(); step = ExpectInt("an autoincrement step"); }
                    if (step is < 1 or > 255) throw CreateSyntaxError();
                    auto = new Write.DbfAutoIncrement(unchecked((uint)next), checked((byte)step));
                }
                else throw CreateSyntaxError();
            }
            return new ColumnDefinition(name, type, len, dec, nullable)
                { NoCpTrans = binary, AutoIncrement = auto, Default = def, Check = check, Error = error };
        }

        // =================================================================
        //  ALTER TABLE
        // =================================================================
        private AlterTableStatement ParseAlterTable()
        {
            ExpectKw("ALTER");
            ExpectKw("TABLE");
            var (db, table) = ParseDbTable();

            AlterTableAction action;
            if (IsKw("ADD"))
            {
                Advance();
                if (IsKw("COLUMN")) Advance();
                action = new AlterTableAction(AlterTableActionKind.AddColumn, Column: ParseColumnDefinition());
            }
            else if (IsKw("ALTER"))
            {
                Advance();
                if (IsKw("COLUMN")) Advance();
                action = new AlterTableAction(AlterTableActionKind.AlterColumn, Column: ParseColumnDefinition());
            }
            else if (IsKw("DROP"))
            {
                Advance();
                if (IsKw("COLUMN")) Advance();
                action = new AlterTableAction(AlterTableActionKind.DropColumn, DropName: ExpectWord("a column name"));
            }
            else if (IsKw("RENAME"))
            {
                Advance();
                if (IsKw("COLUMN")) Advance();
                string from = ExpectWord("the column to rename");
                ExpectKw("TO");
                string to = ExpectWord("the new column name");
                action = new AlterTableAction(AlterTableActionKind.RenameColumn, RenameFrom: from, RenameTo: to);
            }
            else
            {
                throw Err("Expected ADD, ALTER, DROP or RENAME after ALTER TABLE <name>.");
            }

            // Trailing options are accepted and ignored.
            while (!AtEnd && Current.Kind != TokKind.Semicolon) Advance();

            return new AlterTableStatement(db, table, action);
        }

        // =================================================================
        //  DROP TABLE
        // =================================================================
        private DropTableStatement ParseDropTable()
        {
            ExpectKw("DROP");
            if (!IsKw("TABLE") && !IsKw("DBF")) throw Err("Expected TABLE after DROP.");
            Advance();

            bool ifExists = false;
            if (IsKw("IF"))
            {
                Advance();
                ExpectKw("EXISTS");
                ifExists = true;
            }

            var (db, table) = ParseDbTable();

            // Trailing options are accepted and ignored.
            while (!AtEnd && Current.Kind != TokKind.Semicolon) Advance();

            return new DropTableStatement(db, table, ifExists);
        }

        // [db'!']table  (used by INSERT / UPDATE / DELETE)
        private (string? db, string table) ParseDbTable()
        {
            string first = ExpectWord("a table name");
            if (Current.Kind == TokKind.Bang)
            {
                Advance();
                return (first, ExpectWord("a table name after '!'"));
            }
            return (null, first);
        }
    }
}
