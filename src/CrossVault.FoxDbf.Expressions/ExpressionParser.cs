using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Hand-written recursive-descent + Pratt (precedence-climbing) parser producing
/// an immutable AST. Throws <see cref="ExpressionException"/> on malformed input.
///
/// Binding powers (higher binds tighter):
///   OR 10 | AND 20 | comparison 40 | additive 50 | multiplicative 60 | power 70
/// Prefix:
///   .NOT./!  parses an operand at 30 (looser than comparison, tighter than AND)
///   unary -  parses an operand at 71 (tighter than ^, as in VFP9)
/// </summary>
internal sealed class ExpressionParser
{
    private readonly List<Token> _tokens;
    private int _pos;
    private int _depth;
    private string? _sql;
    private bool _sqlAggregates;
    private bool _sqlPredicates;
    private bool _allowSqlLookup;
    private bool _hasSqlInfix;
    private HashSet<string>? _infixFields;
    private readonly List<(int Start, int End, string Text)> _nullTests = new();

    /// <summary>Recursion-depth cap so a deeply-nested expression (e.g. thousands of parentheses) throws
    /// a catchable <see cref="ExpressionException"/> instead of a fatal StackOverflowException.</summary>
    private const int MaxDepth = 400;

    private ExpressionParser(List<Token> tokens) => _tokens = tokens;

    internal static (AstNode Root, string Text, bool HasSqlInfix, IReadOnlyCollection<string> InfixFields) ParseSql(string text, bool aggregates = false, bool predicates = false, bool allowLookup = false)
    {
        var parser = new ExpressionParser(new ExpressionLexer(text).Tokenize())
        {
            _sql = text,
            _sqlAggregates = aggregates,
            _sqlPredicates = predicates,
            _allowSqlLookup = allowLookup,
        };
        var root = parser.ParseExpr(0);
        if (parser.Peek.Type != TokenType.Eof)
            throw new ExpressionException("Unexpected trailing input.", parser.Peek.Pos);
        return (root, parser.SqlText(0, text.Length), parser._hasSqlInfix, (IReadOnlyCollection<string>?)parser._infixFields ?? Array.Empty<string>());
    }

    // Residual filters are reparsed by the optimizer's xBase expression engine. Keep their
    // text in ISNULL(...) form as well as building the AST, without rewriting string literals.
    private string SqlText(int start, int end)
    {
        var result = new StringBuilder();
        int position = start;
        foreach (var edit in _nullTests.Where(e => e.Start >= start && e.End <= end).OrderBy(e => e.Start))
        {
            result.Append(_sql, position, edit.Start - position);
            result.Append(edit.Text);
            position = edit.End;
        }
        result.Append(_sql, position, end - position);
        return result.ToString();
    }

    public static AstNode Parse(string text)
    {
        var tokens = new ExpressionLexer(text).Tokenize();
        var parser = new ExpressionParser(tokens);
        if (parser.Peek.Type == TokenType.Eof)
            throw new ExpressionException("Empty expression.", 0);
        var node = parser.ParseExpr(0);
        if (parser.Peek.Type != TokenType.Eof)
            throw new ExpressionException("Unexpected trailing input.", parser.Peek.Pos);
        return node;
    }

    private Token Peek => _tokens[_pos];
    private Token Next() => _tokens[_pos++];

    private AstNode ParseExpr(int minBp)
    {
        if (++_depth > MaxDepth)
            throw new ExpressionException("Expression nesting is too deep.", Peek.Pos);
        try
        {
            return ParseExprCore(minBp);
        }
        finally { _depth--; }
    }

    private AstNode ParseExprCore(int minBp)
    {
        int start = Peek.Pos;
        var left = ParsePrefix();
        while (true)
        {
            var t = Peek;
            if (_sql is not null && minBp <= 40 && t.Type == TokenType.Identifier &&
                t.Text.Equals("IS", StringComparison.OrdinalIgnoreCase))
            {
                Next();
                bool negate = Peek.Type == TokenType.Not;
                if (negate) Next();
                if (Peek.Type != TokenType.Identifier || !Peek.Text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    throw new ExpressionException("Expected NULL after IS / IS NOT.", Peek.Pos);
                int end = Next().Pos + 4;
                string operand = SqlText(start, t.Pos);
                _nullTests.RemoveAll(e => e.Start >= start && e.End <= end);
                _nullTests.Add((start, end, (negate ? "NOT " : "") + "ISNULL(" + operand + ")"));
                left = new FunctionNode("ISNULL", new[] { left });
                if (negate) left = new UnaryNode(UnOp.Not, left);
                continue;
            }
            if (_sql is not null && minBp <= 40 && IsSqlInfix(t))
            {
                if (!_sqlPredicates)
                    throw new ExpressionException("SQL predicates are not allowed in this expression.", t.Pos) { VfpErrorNumber = 36 };
                left = ParseSqlInfix(left);
                continue;
            }
            int lbp = LeftBp(t.Type);
            if (lbp < minBp || lbp == 0) break;
            Next();
            int rbp = t.Type == TokenType.Caret ? lbp : lbp + 1; // ^ is right-associative
            bool currencyOperand = Peek.Type == TokenType.Dollar;
            AstNode right;
            try { right = ParseExpr(rbp); }
            catch (ExpressionException error) when (currencyOperand && error.VfpErrorNumber == 36)
            {
                // VFP9 reports a missing operand for an invalid currency after a binary operator.
                throw new ExpressionException("Missing operand.", error.Position) { VfpErrorNumber = 1231 };
            }
            left = new BinaryNode(MapBinOp(t.Type), left, right);
        }
        return left;
    }

    private bool IsSqlInfix(Token token)
    {
        if (token.Type == TokenType.Not && _pos + 1 < _tokens.Count) token = _tokens[_pos + 1];
        return token.Type == TokenType.Identifier && token.Text.ToUpperInvariant() is "IN" or "BETWEEN" or "LIKE";
    }

    private AstNode ParseSqlInfix(AstNode left)
    {
        _hasSqlInfix = true;
        bool negate = Peek.Type == TokenType.Not;
        if (negate) Next();
        string kind = Next().Text.ToUpperInvariant();
        var operands = new List<AstNode> { left };
        try
        {
            if (kind == "IN")
            {
                Expect(TokenType.LParen, "Expected '(' after IN.");
                do
                {
                    operands.Add(ParseExpr(0));
                    if (Peek.Type != TokenType.Comma) break;
                    Next();
                } while (true);
                Expect(TokenType.RParen, "Expected ')' after IN list.");
            }
            else
            {
                operands.Add(ParseExpr(41));
                if (kind == "BETWEEN")
                {
                    Expect(TokenType.And, "Expected AND between bounds.");
                    operands.Add(ParseExpr(41));
                }
                else
                {
                    // A literal NULL is rejected by VFP's SQL LIKE grammar (1833); nullable
                    // fields and expressions are legal and propagate NULL at evaluation time.
                    foreach (var operand in operands)
                        if (operand is LiteralNode && operand.Eval(null!, EvaluationContext.Default).IsNull)
                            throw new ExpressionException("SQL: LIKE requires a character expression.") { VfpErrorNumber = 1833 };
                    if (Peek.Type == TokenType.Identifier && Peek.Text.Equals("ESCAPE", StringComparison.OrdinalIgnoreCase))
                    {
                        Next();
                        operands.Add(ParseExpr(41));
                    }
                }
            }
        }
        catch (ExpressionException ex) when (ex.VfpErrorNumber is null or 1231)
        {
            throw new ExpressionException(ex.Message, ex, ex.Position) { VfpErrorNumber = kind == "LIKE" ? 36 : 10 };
        }
        _infixFields ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operand in operands) operand.CollectFields(_infixFields);
        AstNode node = new SqlInfixNode(kind, operands.ToArray());
        return negate ? new UnaryNode(UnOp.Not, node) : node;
    }

    private AstNode ParsePrefix()
    {
        var t = Peek;
        switch (t.Type)
        {
            case TokenType.Number:
            case TokenType.String:
            case TokenType.DateLit:
            case TokenType.DateTimeLit:
                Next();
                return new LiteralNode(t.Value);
            // A '$' in PREFIX position followed by a number is a currency literal ($0, $-12.50); in infix
            // position '$' stays the substring operator (handled by the binary loop, never reaches here).
            case TokenType.Dollar:
                return ParseCurrencyLiteral();
            case TokenType.True: Next(); return new LiteralNode(VfpValue.Logical(true));
            case TokenType.False: Next(); return new LiteralNode(VfpValue.Logical(false));
            case TokenType.NullLit: Next(); return new LiteralNode(VfpValue.Null);

            case TokenType.Minus:
                Next();
                if (Peek.Type == TokenType.Minus)
                    throw new ExpressionException("Missing operand.", Peek.Pos) { VfpErrorNumber = 1231 };
                return new UnaryNode(UnOp.Neg, ParseExpr(71));
            case TokenType.Plus:
                Next();
                return ParseExpr(65); // unary plus is a no-op
            case TokenType.Not:
            case TokenType.Bang:
                Next();
                return new UnaryNode(UnOp.Not, ParseExpr(30));

            case TokenType.LParen:
            {
                Next();
                var inner = ParseExpr(0);
                Expect(TokenType.RParen, "Expected ')'.");
                // VFP9 ISBLANK((field)) inspects the value; ISBLANK(field) and ISBLANK(+field)
                // inspect the raw field bytes. Keep parentheses distinct despite their identical value.
                return inner is FieldNode field ? new FieldNode(field.Name, isValueExpression: true) : inner;
            }

            case TokenType.Identifier:
                return ParseIdentifier();

            default:
                if (t.Type == TokenType.Eof)
                    throw new ExpressionException("Missing operand.", t.Pos) { VfpErrorNumber = 1231 };
                throw new ExpressionException(
                    $"Unexpected token at position {t.Pos}.",
                    t.Pos);
        }
    }

    // Keep currency temporaries off the recursive ParsePrefix frame so the nesting guard remains reachable.
    private AstNode ParseCurrencyLiteral()
    {
        var dollar = Next();
        bool negative = Peek.Type == TokenType.Minus;
        if (negative) Next();
        // VFP9 accepts the minus after $, but rejects unary plus and parenthesized values (36).
        if (Peek.Type != TokenType.Number)
            throw new ExpressionException("Command contains unrecognized phrase/keyword.", dollar.Pos) { VfpErrorNumber = 36 };
        decimal amount = Next().Value.AsNumber;
        return new LiteralNode(VfpValue.Currency(Math.Round(negative ? -amount : amount, 4, MidpointRounding.AwayFromZero)));
    }

    private AstNode ParseIdentifier()
    {
        var id = Next();
        if (Peek.Type == TokenType.LParen)
        {
            Next(); // '('
            string name = id.Text;
            bool aggregate = _sqlAggregates && name.ToUpperInvariant() is "MIN" or "MAX" or "SUM" or "AVG" or "COUNT" or "CNT";
            if (aggregate && name.Equals("CNT", StringComparison.OrdinalIgnoreCase))
            {
                name = "COUNT";
                _nullTests.Add((id.Pos, id.Pos + id.Text.Length, name));
            }
            bool distinct = aggregate && Peek.Type == TokenType.Identifier
                && Peek.Text.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase);
            if (distinct)
            {
                if (name.ToUpperInvariant() is "MIN" or "MAX")
                    throw new ExpressionException("SQL: DISTINCT is invalid.", Peek.Pos) { VfpErrorNumber = 1819 };
                Next();
            }
            var args = new List<AstNode>();
            if (_sql is not null && name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) && Peek.Type == TokenType.Star
                || aggregate && Peek.Type == TokenType.Star)
            {
                if (distinct || !name.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
                    throw new ExpressionException("Expression is invalid. A valid expression is required.", id.Pos) { VfpErrorNumber = 152 };
                var star = Next();
                _nullTests.Add((star.Pos, star.Pos + 1, "1"));
                args.Add(new LiteralNode(VfpValue.Integer(1)));
            }
            else if (Peek.Type != TokenType.RParen)
            {
                args.Add(ParseExpr(0));
                while (Peek.Type == TokenType.Comma)
                {
                    Next();
                    args.Add(ParseExpr(0));
                }
            }
            Expect(TokenType.RParen, "Expected ')' to close function call.");
            if (aggregate && args.Count != 1)
                throw new ExpressionException(args.Count == 0 ? "Expression is invalid. A valid expression is required." : "Function name is missing ( ).", id.Pos)
                    { VfpErrorNumber = args.Count == 0 ? 152 : 1300 };
            if (distinct) args[0] = new SqlDistinctNode(args[0]);
            // SQL MIN/MAX aggregates take one operand; scalar MIN/MAX use VFP's 2..26 limits.
            return aggregate ? new FunctionNode(name, args.ToArray())
                : VfpFunctionArity.Bind(name, args.ToArray(), id.Pos, sql: _sql is not null, allowLookup: _allowSqlLookup);
        }
        return new FieldNode(id.Text);
    }

    private void Expect(TokenType type, string message)
    {
        if (Peek.Type != type) throw new ExpressionException(message, Peek.Pos);
        Next();
    }

    private static int LeftBp(TokenType t) => t switch
    {
        TokenType.Or => 10,
        TokenType.And => 20,
        TokenType.Eq or TokenType.ExactEq or TokenType.Ne or
        TokenType.Lt or TokenType.Le or TokenType.Gt or TokenType.Ge or
        TokenType.Dollar => 40,
        TokenType.Plus or TokenType.Minus => 50,
        TokenType.Star or TokenType.Slash or TokenType.Percent => 60,
        TokenType.Caret => 70,
        _ => 0,
    };

    private static BinOp MapBinOp(TokenType t) => t switch
    {
        TokenType.Plus => BinOp.Add,
        TokenType.Minus => BinOp.Sub,
        TokenType.Star => BinOp.Mul,
        TokenType.Slash => BinOp.Div,
        TokenType.Percent => BinOp.Mod,
        TokenType.Caret => BinOp.Pow,
        TokenType.Eq => BinOp.Eq,
        TokenType.ExactEq => BinOp.ExactEq,
        TokenType.Ne => BinOp.Ne,
        TokenType.Lt => BinOp.Lt,
        TokenType.Le => BinOp.Le,
        TokenType.Gt => BinOp.Gt,
        TokenType.Ge => BinOp.Ge,
        TokenType.Dollar => BinOp.Dollar,
        TokenType.And => BinOp.And,
        TokenType.Or => BinOp.Or,
        _ => throw new ExpressionException("Invalid binary operator."),
    };
}
