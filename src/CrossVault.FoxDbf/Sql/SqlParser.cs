using System;
using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// Recursive-descent parser for the VFP-SQL v1 grammar. Produces an immutable
/// <see cref="SqlStatement"/> AST. Every scalar / predicate fragment is located by text
/// and handed to <see cref="VfpExpression.Parse(string)"/>; this parser owns only the SQL
/// skeleton. Keywords are case-insensitive.
/// </summary>
public static class SqlParser
{
    /// <summary>Parses a single statement. Throws <see cref="FoxDbfSqlException"/> on bad input.</summary>
    public static SqlStatement Parse(string sql)
    {
        if (sql is null) throw new FoxDbfSqlException("SQL text is null.");
        sql = SqlLexer.NormalizeArrowQualifiers(sql);
        var p = new State(sql, SqlLexer.Tokenize(sql));
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
        private int _pos;

        public State(string src, List<Token> tokens) { _src = src; _t = tokens; }

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

        // The expression engine cannot represent a bare '*' function argument, so the SQL-only
        // aggregate COUNT(*) — which may appear in HAVING (and is equivalent to COUNT(1)) — is
        // rewritten to COUNT(1) before handing the fragment to VfpExpression.Parse. The executor
        // reads the resulting .Text and treats COUNT(1) exactly as COUNT(*) (count of all members).
        private static readonly System.Text.RegularExpressions.Regex CountStarRx =
            new(@"COUNT\s*\(\s*\*\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private VfpExpression BuildExpr(string text, int srcStart)
        {
            text = CountStarRx.Replace(text, "COUNT(1)");
            try
            {
                return VfpExpression.ParseSql(text);
            }
            catch (ExpressionException ex)
            {
                int pos = ex.Position >= 0 ? srcStart + ex.Position : srcStart;
                throw new FoxDbfSqlException(
                    $"Invalid expression '{text}': {ex.Message}", ex, pos);
            }
        }

        // Parses an expression fragment terminated per stopWords/stopAtComma.
        private VfpExpression ParseExprFragment(HashSet<string>? stopWords, bool stopAtComma)
        {
            var (s, e) = ScanRange(stopWords, stopAtComma);
            if (e <= s) throw Err("Expected an expression.");
            return BuildExpr(RangeText(s, e), _t[s].Start);
        }

        // =================================================================
        //  WHERE predicate tree (built only when a sub-SELECT is present)
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
                if (Kw(Peek(1), "EXISTS") ||
                    (Peek(1).Kind == TokKind.LParen && ParenContainsSubquery(_pos + 1)) ||
                    LeafHasTopLevelSubquery(_pos + 1))
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
                    if (t.Kind == TokKind.Word && (Kw(t, "AND") || Kw(t, "OR"))) break;
                    if (t.Kind == TokKind.Word && WhereClauseStops.Contains(t.Text.ToUpperInvariant())) break;
                }
                p++;
            }
            int leafEnd = p;

            if (sqOpen < 0)
            {
                if (leafEnd <= start) throw Err("Expected a predicate.");
                var expr = BuildExpr(RangeText(start, leafEnd), _t[start].Start);
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
                // No sub-SELECT anywhere in the clause → the historical single-VfpExpression fast path
                // (behaviour identical to before). A sub-SELECT → an SQL-level boolean tree.
                if (FragmentHasSubquery(WhereClauseStops))
                    wherePredicate = ParseBoolOr();
                else
                    where = ParseExprFragment(WhereClauseStops, stopAtComma: false);
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
                    throw Err("SQL: Invalid use of subquery in HAVING.");
                having = ParseExprFragment(HavingClauseStops, stopAtComma: false);
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
            var items = new List<SelectItem>();
            do { items.Add(ParseSelectItem()); }
            while (TryComma());
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
                Peek(2).Kind == TokKind.Star && Peek(3).Kind == TokKind.RParen)
            {
                string fn = Current.Text;
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
                return new SelectItem(false, null, BuildExpr(text, _t[s].Start), alias);
            }

            // no AS: maybe an implicit trailing alias (expr alias). Try whole text first;
            // only if it fails to parse, retry stripping a trailing identifier as alias.
            try
            {
                return new SelectItem(false, null, VfpExpression.Parse(text), null);
            }
            catch (ExpressionException whole)
            {
                if (e - s >= 2 && _t[e - 1].Kind == TokKind.Word)
                {
                    string aliasText = RangeText(e - 1, e);
                    string exprText = RangeText(s, e - 1);
                    try
                    {
                        return new SelectItem(false, null, VfpExpression.Parse(exprText), aliasText);
                    }
                    catch (ExpressionException) { /* fall through to report the original error */ }
                }
                int pos = whole.Position >= 0 ? _t[s].Start + whole.Position : _t[s].Start;
                throw new FoxDbfSqlException($"Invalid expression '{text}': {whole.Message}", whole, pos);
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
            var expr = ParseExprFragment(stop, stopAtComma: true);
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
                var on = ParseExprFragment(JoinOnStops, stopAtComma: false);
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

            ExpectKw("VALUES");
            if (Current.Kind != TokKind.LParen) throw Err("Expected '(' after VALUES.");
            Advance();
            var values = new List<VfpExpression>();
            do { values.Add(ParseExprFragment(null, stopAtComma: true)); }
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
                where = ParseExprFragment(null, stopAtComma: false);
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
            var (db, table) = ParseDbTable();

            VfpExpression? where = null;
            if (IsKw("WHERE"))
            {
                Advance();
                where = ParseExprFragment(null, stopAtComma: false);
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

            if (Current.Kind != TokKind.LParen) throw Err("Expected '(' to open the column list.");
            Advance();

            var cols = new List<ColumnDefinition>();
            do { cols.Add(ParseColumnDefinition()); }
            while (TryComma());

            if (Current.Kind != TokKind.RParen) throw Err("Expected ')' to close the column list.");
            Advance();

            // Trailing options (NOCONSOLE / NAME longname / etc.) are accepted and ignored.
            while (!AtEnd && Current.Kind != TokKind.Semicolon) Advance();

            return new CreateTableStatement(db, table, cols);
        }

        // colname Type[(len[,dec])] [NULL | NOT NULL] [other column constraints, ignored]
        private ColumnDefinition ParseColumnDefinition()
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

            // Scan the rest of this column's clause for NULL / NOT NULL, skipping (but tolerating) any
            // other column constraints (DEFAULT expr, CHECK (expr), PRIMARY KEY, UNIQUE, ...). Stop at a
            // top-level comma (next column) or the closing ')' of the column list.
            bool? nullable = null;
            int depth = 0;
            while (!AtEnd)
            {
                var t = Current;
                if (t.Kind == TokKind.LParen) { depth++; Advance(); continue; }
                if (t.Kind == TokKind.RParen)
                {
                    if (depth == 0) break;
                    depth--; Advance(); continue;
                }
                if (depth == 0 && t.Kind == TokKind.Comma) break;
                if (depth == 0 && t.Kind == TokKind.Semicolon) break;
                if (depth == 0 && IsKw("NULL")) { nullable = true; Advance(); continue; }
                if (depth == 0 && IsKw("NOT"))
                {
                    Advance();
                    if (IsKw("NULL")) { Advance(); nullable = false; }
                    continue;
                }
                Advance();
            }

            return new ColumnDefinition(name, type, len, dec, nullable);
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
