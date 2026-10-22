using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// The microVFP PRG (stored-procedure) parser: turns VFP <c>.prg</c> source into a
/// <see cref="PrgProgram"/> (procedures/functions + top-level statements). It owns only the
/// LINE/STATEMENT structure — every expression fragment is delegated to the existing
/// <c>CrossVault.FoxDbf.Expressions</c> engine via <see cref="PrgExpr"/>, and embedded
/// <c>SELECT … FROM</c> / <c>INSERT INTO</c> are routed to <c>CrossVault.FoxDbf.Sql</c>.
/// </summary>
public static class PrgParser
{
    /// <summary>Parses <paramref name="source"/> (a whole .prg) into a <see cref="PrgProgram"/>.</summary>
    public static PrgProgram Parse(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var lines = PreprocessConditionals(BuildLogicalLines(source));
        return new Impl(lines).ParseProgram();
    }

    /// <summary>Reads <paramref name="path"/> (Windows-1252 / Latin-1, as VFP exports it) and
    /// parses it. Deliberately Latin-1, not CP1252: the interpreter models a VFP string as one char per byte
    /// (CHR(135) is U+0087), and source literals must match that — real stored-procedure code compares CHR()
    /// output with a #DEFINE of raw 0x87… bytes. The price: CP1252 letters in 0x80–0x9F (Š Œ š œ Ÿ ƒ) used in an
    /// IDENTIFIER inside a .prg file decode to C1 controls and do not scan as letters.</summary>
    public static PrgProgram ParseFile(string path)
        => Parse(File.ReadAllText(path, Encoding.Latin1));

    // ── logical-line model ───────────────────────────────────────────────────

    internal readonly record struct LogicalLine(int LineNo, string Text);

    /// <summary>
    /// Joins <c>;</c>-continuations and drops blank/comment lines, yielding the logical lines
    /// the parser consumes. Implements the VFP gotcha that a <c>;</c> on a COMMENT line swallows
    /// the next physical line into the comment (the joined line is still a comment → dropped).
    /// </summary>
    internal static List<LogicalLine> BuildLogicalLines(string source)
    {
        var phys = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new List<LogicalLine>();
        int i = 0;
        while (i < phys.Length)
        {
            int startLine = i + 1;
            bool isComment = PrgScan.IsCommentLine(phys[i].TrimStart());
            var sb = new StringBuilder();
            while (true)
            {
                string line = phys[i];
                i++;
                bool continues;
                if (isComment)
                {
                    // The whole logical line is a comment: never accumulate, just chase the
                    // trailing ';' so a comment-continuation swallows the following line too.
                    continues = line.TrimEnd().EndsWith(';');
                }
                else
                {
                    var (code, cont) = PrgScan.StripCommentAndContinuation(line);
                    continues = cont;
                    if (code.Trim().Length > 0)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(code.Trim());
                    }
                }
                if (!continues || i >= phys.Length) break;
            }
            if (!isComment)
            {
                string text = sb.ToString().Trim();
                if (text.Length > 0) result.Add(new LogicalLine(startLine, text));
            }
        }
        return result;
    }

    // ── #IF / #IFDEF / #IFNDEF / #ELIF / #ELSE / #ENDIF preprocessor ─────────────

    /// <summary>Compile-time conditional inclusion (VFP preprocessor, runs BEFORE p-code compilation): drops
    /// the logical lines inside a <c>#IF</c>/<c>#IFDEF</c>/<c>#IFNDEF</c> branch whose (constant) condition is
    /// false, evaluating <c>#IF</c>/<c>#ELIF</c> expressions after substituting the <c>#DEFINE</c> constants
    /// seen so far. Surviving <c>#DEFINE</c> lines are KEPT so the interpreter's runtime <c>#DEFINE</c> pass
    /// still records them (macro-in-expression); the conditional directives themselves are consumed here.
    /// Lenient by design — an unbalanced <c>#ELSE</c>/<c>#ENDIF</c> is ignored, never a parse error.</summary>
    internal static List<LogicalLine> PreprocessConditionals(List<LogicalLine> lines)
    {
        // Fast path: nothing to do when the source carries no conditional directive at all.
        bool anyCond = false;
        foreach (var l in lines)
            if (IsConditionalDirective(l.Text)) { anyCond = true; break; }
        if (!anyCond) return lines;

        var defines = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<CondFrame>();
        var outp = new List<LogicalLine>(lines.Count);

        foreach (var ll in lines)
        {
            bool emittingNow = stack.Count == 0 || stack.Peek().Emitting;
            string t = ll.Text.TrimStart();

            if (t.Length > 0 && t[0] == '#' && TryDirective(t, out string dir, out string rest))
            {
                switch (dir)
                {
                    case "IF":
                    case "IFDEF":
                    case "IFNDEF":
                    {
                        bool parent = emittingNow;
                        bool cond = parent && dir switch
                        {
                            "IFDEF" => defines.ContainsKey(FirstIdent(rest)),
                            "IFNDEF" => !defines.ContainsKey(FirstIdent(rest)),
                            _ => EvalConstCondition(rest, defines),
                        };
                        stack.Push(new CondFrame { Emitting = cond, AnyTaken = cond, ParentEmitting = parent });
                        continue;
                    }
                    case "ELIF":
                    {
                        if (stack.Count == 0) continue;
                        var fr = stack.Peek();
                        bool cond = fr.ParentEmitting && !fr.AnyTaken && EvalConstCondition(rest, defines);
                        fr.Emitting = cond;
                        if (cond) fr.AnyTaken = true;
                        continue;
                    }
                    case "ELSE":
                    {
                        if (stack.Count == 0) continue;
                        var fr = stack.Peek();
                        fr.Emitting = fr.ParentEmitting && !fr.AnyTaken;
                        fr.AnyTaken = true;
                        continue;
                    }
                    case "ENDIF":
                        if (stack.Count > 0) stack.Pop();
                        continue;
                    case "DEFINE":
                        if (emittingNow)
                        {
                            RecordDefine(rest, defines);
                            outp.Add(ll);                 // keep for the runtime #DEFINE pass.
                        }
                        continue;
                    case "UNDEF":
                        if (emittingNow) defines.Remove(FirstIdent(rest));
                        continue;
                    default:
                        // #INCLUDE / other → pass through (only when emitting) as before (runtime no-op).
                        if (emittingNow) outp.Add(ll);
                        continue;
                }
            }

            if (emittingNow) outp.Add(ll);
        }

        return outp;
    }

    private sealed class CondFrame
    {
        public bool Emitting;        // this branch is active AND the parent is emitting.
        public bool AnyTaken;        // some branch of this #IF chain has been selected.
        public bool ParentEmitting;  // the enclosing scope was emitting when this #IF opened.
    }

    private static bool IsConditionalDirective(string text)
    {
        string t = text.TrimStart();
        return t.Length > 0 && t[0] == '#' && TryDirective(t, out string dir, out _) &&
               dir is "IF" or "IFDEF" or "IFNDEF" or "ELIF" or "ELSE" or "ENDIF";
    }

    /// <summary>Splits a <c>#</c> line into its UPPER-cased directive word and the remaining text.</summary>
    private static bool TryDirective(string t, out string dir, out string rest)
    {
        dir = string.Empty; rest = string.Empty;
        if (t.Length == 0 || t[0] != '#') return false;
        int i = 1;
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
        int start = i;
        while (i < t.Length && (VfpIdentifier.IsPart(t[i]))) i++;
        if (i == start) return false;
        dir = t.Substring(start, i - start).ToUpperInvariant();
        rest = t.Substring(i).Trim();
        return true;
    }

    private static string FirstIdent(string s)
    {
        s = s.TrimStart();
        int j = 0;
        while (j < s.Length && (VfpIdentifier.IsPart(s[j]))) j++;
        return s.Substring(0, j);
    }

    // #DEFINE NAME [value] — record NAME → value text (null when value-less).
    private static void RecordDefine(string rest, Dictionary<string, string?> defines)
    {
        string name = FirstIdent(rest);
        if (name.Length == 0) return;
        string val = rest.Substring(name.Length).Trim();
        defines[name] = val.Length == 0 ? null : val;
    }

    // Evaluate a #IF / #ELIF constant boolean condition: substitute the known #DEFINE constants textually,
    // then evaluate with the shared expression engine over an empty row. Non-zero numeric ⇒ true.
    private static bool EvalConstCondition(string expr, Dictionary<string, string?> defines)
    {
        string sub = SubstituteDefines(expr, defines);
        try
        {
            var v = VfpExpression.Parse(MicroVfpExprRewrite.Normalize(sub)).Evaluate(NullRow.Instance);
            return v.Type switch
            {
                VfpType.Logical => v.AsLogical,
                VfpType.Numeric or VfpType.Integer or VfpType.Currency => v.AsNumber != 0m,
                _ => false,
            };
        }
        catch { return false; }
    }

    // Replace every identifier that names a #DEFINE constant with its value text (value-less ⇒ ".T.").
    // Bounded iteration handles a define whose value references another define.
    private static string SubstituteDefines(string expr, Dictionary<string, string?> defines)
    {
        if (defines.Count == 0) return expr;
        for (int pass = 0; pass < 10; pass++)
        {
            var sb = new StringBuilder(expr.Length);
            bool changed = false;
            bool inStr = false; char q = '\0';
            for (int i = 0; i < expr.Length;)
            {
                char c = expr[i];
                if (inStr) { sb.Append(c); if (c == q) inStr = false; i++; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; sb.Append(c); i++; continue; }
                if (VfpIdentifier.IsStart(c))
                {
                    int s = i; i++;
                    while (i < expr.Length && (VfpIdentifier.IsPart(expr[i]))) i++;
                    string word = expr.Substring(s, i - s);
                    if (defines.TryGetValue(word, out var val)) { sb.Append(string.IsNullOrEmpty(val) ? ".T." : val); changed = true; }
                    else sb.Append(word);
                    continue;
                }
                sb.Append(c); i++;
            }
            expr = sb.ToString();
            if (!changed) break;
        }
        return expr;
    }

    /// <summary>An empty row for constant #IF evaluation — no fields, no record.</summary>
    private sealed class NullRow : IRowContext
    {
        public static readonly NullRow Instance = new();
        public object? GetField(string name) => null;
        public int RecNo => 0;
        public bool Deleted => false;
        public int RecCount => 0;
    }

    // ── the recursive-descent parser ───────────────────────────────────────────

    private sealed class Impl
    {
        private readonly List<LogicalLine> _lines;
        private int _i;

        public Impl(List<LogicalLine> lines) => _lines = lines;

        private bool Eof => _i >= _lines.Count;
        private LogicalLine Peek => _lines[_i];
        private LogicalLine Next() => _lines[_i++];
        private string PeekWord => Eof ? string.Empty : PrgScan.FirstWord(Peek.Text);

        private static readonly HashSet<string> Terminators = new(StringComparer.Ordinal)
        {
            "ENDIF", "ELSE", "ENDDO", "ENDFOR", "NEXT", "ENDSCAN",
            "ENDCASE", "OTHERWISE", "CASE", "ENDPROC", "ENDFUNC",
        };

        // ---- top level ----

        public PrgProgram ParseProgram()
        {
            var procs = new List<ProcDef>();
            var main = new List<PrgStatement>();
            while (!Eof)
            {
                string kw = PeekWord;
                if (kw is "PROCEDURE" or "FUNCTION")
                    procs.Add(ParseProc());
                else if (Terminators.Contains(kw))
                    throw new MicroVfpSyntaxException($"Unexpected '{kw}' outside a block.", Peek.LineNo);
                else
                    main.Add(ParseStatement());
            }
            return new PrgProgram(procs, main);
        }

        private ProcDef ParseProc()
        {
            var ll = Next();
            var kind = PrgScan.FirstWord(ll.Text) == "FUNCTION" ? ProcKind.Function : ProcKind.Procedure;
            string rest = PrgScan.AfterFirstWord(ll.Text).TrimStart();

            int j = 0;
            while (j < rest.Length && (VfpIdentifier.IsPart(rest[j]))) j++;
            string name = rest.Substring(0, j);
            if (name.Length == 0)
                throw new MicroVfpSyntaxException("PROCEDURE/FUNCTION without a name.", ll.LineNo);

            var parameters = new List<string>();
            string after = rest.Substring(j).TrimStart();
            if (after.StartsWith('('))
            {
                int close = MatchParen(after, 0);
                if (close < 1)
                    throw new MicroVfpSyntaxException("Unbalanced parameter list in PROCEDURE/FUNCTION header.", ll.LineNo);
                string inner = after.Substring(1, close - 1);
                foreach (var p in PrgScan.SplitTopCommas(inner))
                {
                    var id = LeadingIdent(p);
                    if (id.Length > 0) parameters.Add(id);
                }
            }

            var body = ParseBlock("ENDPROC", "ENDFUNC");
            if (!Eof && PeekWord is "ENDPROC" or "ENDFUNC") Next();   // explicit close (else implicit)
            return new ProcDef(name, kind, parameters, body) { Line = ll.LineNo };
        }

        /// <summary>Collects statements until a <paramref name="stop"/> keyword, a procedure
        /// boundary, or EOF — WITHOUT consuming the stopper (the block owner consumes it).</summary>
        private List<PrgStatement> ParseBlock(params string[] stop)
        {
            var stmts = new List<PrgStatement>();
            while (!Eof)
            {
                string kw = PeekWord;
                if (stop.Contains(kw)) return stmts;
                // A procedure boundary (or its explicit close) always ends a block: a missing
                // ENDIF/ENDDO/… then surfaces at the block OWNER with the opener's line number.
                if (kw is "PROCEDURE" or "FUNCTION" or "ENDPROC" or "ENDFUNC") return stmts;
                stmts.Add(ParseStatement());
            }
            return stmts;
        }

        // ---- statement dispatch (handles block-statements; delegates the rest) ----

        private PrgStatement ParseStatement()
        {
            // Cap block nesting so deeply-nested IF/FOR/SCAN/DO sources throw a catchable syntax error
            // rather than a fatal StackOverflowException.
            if (++_blockDepth > MaxBlockDepth)
            {
                _blockDepth--;
                throw new MicroVfpSyntaxException("Block nesting is too deep.", Peek.LineNo);
            }
            try { return ParseStatementCore(); }
            finally { _blockDepth--; }
        }

        private const int MaxBlockDepth = 400;
        private int _blockDepth;

        private PrgStatement ParseStatementCore()
        {
            var ll = Peek;
            string kw = PrgScan.FirstWord(ll.Text);
            switch (kw)
            {
                case "IF": return ParseIf();
                case "FOR": return PrgScan.SecondWord(ll.Text) == "EACH" ? ParseForEach() : ParseFor();
                case "SCAN": return ParseScan();
                case "DO":
                    string sw = PrgScan.SecondWord(ll.Text);
                    if (sw == "CASE") return ParseDoCase();
                    if (sw == "WHILE") return ParseDoWhile();
                    Next();
                    return BuildSimple(ll.Text, ll.LineNo);
                default:
                    if (Terminators.Contains(kw))
                        throw new MicroVfpSyntaxException($"Unexpected '{kw}' (no matching block opener).", ll.LineNo);
                    Next();
                    return BuildSimple(ll.Text, ll.LineNo);
            }
        }

        // ---- block statements ----

        private IfStmt ParseIf()
        {
            var ll = Next();
            string cond = PrgScan.AfterFirstWord(ll.Text);
            int thenPos = PrgScan.IndexOfKeyword(cond, "THEN");
            if (thenPos >= 0 && cond.Substring(thenPos).Trim().Equals("THEN", StringComparison.OrdinalIgnoreCase))
                cond = cond.Substring(0, thenPos);

            var thenBlock = ParseBlock("ELSE", "ENDIF");
            IReadOnlyList<PrgStatement> elseBlock = Array.Empty<PrgStatement>();
            if (!Eof && PeekWord == "ELSE")
            {
                Next();
                elseBlock = ParseBlock("ENDIF");
            }
            Require("ENDIF", ll.LineNo);
            return new IfStmt(PrgExpr.Parse(cond), thenBlock, elseBlock) { Line = ll.LineNo };
        }

        private DoCaseStmt ParseDoCase()
        {
            var ll = Next(); // DO CASE
            var cases = new List<CaseClause>();
            IReadOnlyList<PrgStatement>? otherwise = null;
            while (true)
            {
                if (Eof) throw new MicroVfpSyntaxException("Missing ENDCASE.", ll.LineNo);
                string kw = PeekWord;
                if (kw == "CASE")
                {
                    var cl = Next();
                    var body = ParseBlock("CASE", "OTHERWISE", "ENDCASE");
                    cases.Add(new CaseClause(PrgExpr.Parse(PrgScan.AfterFirstWord(cl.Text)), body));
                }
                else if (kw == "OTHERWISE")
                {
                    Next();
                    otherwise = ParseBlock("CASE", "OTHERWISE", "ENDCASE");
                }
                else if (kw == "ENDCASE")
                {
                    Next();
                    break;
                }
                else
                {
                    throw new MicroVfpSyntaxException($"Expected CASE/OTHERWISE/ENDCASE but found '{kw}'.", Peek.LineNo);
                }
            }
            return new DoCaseStmt(cases, otherwise) { Line = ll.LineNo };
        }

        private DoWhileStmt ParseDoWhile()
        {
            var ll = Next();
            string cond = PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(ll.Text)); // drop DO, then WHILE
            var body = ParseBlock("ENDDO");
            Require("ENDDO", ll.LineNo);
            return new DoWhileStmt(PrgExpr.Parse(cond), body) { Line = ll.LineNo };
        }

        private ForStmt ParseFor()
        {
            var ll = Next();
            string rest = PrgScan.AfterFirstWord(ll.Text);
            int eq = PrgScan.IndexOfAssign(rest);
            if (eq < 0) throw new MicroVfpSyntaxException("Malformed FOR (missing '=').", ll.LineNo);
            string variable = rest.Substring(0, eq).Trim();
            string bounds = rest.Substring(eq + 1);
            int toPos = PrgScan.IndexOfKeyword(bounds, "TO");
            if (toPos < 0) throw new MicroVfpSyntaxException("Malformed FOR (missing TO).", ll.LineNo);
            string fromText = bounds.Substring(0, toPos);
            string afterTo = bounds.Substring(toPos + 2);
            int stepPos = PrgScan.IndexOfKeyword(afterTo, "STEP");
            string toText = stepPos < 0 ? afterTo : afterTo.Substring(0, stepPos);
            PrgExpr? step = stepPos < 0 ? null : PrgExpr.Parse(afterTo.Substring(stepPos + 4));

            var body = ParseBlock("ENDFOR", "NEXT");
            if (!Eof && PeekWord is "ENDFOR" or "NEXT") Next();
            else throw new MicroVfpSyntaxException("Missing ENDFOR/NEXT.", ll.LineNo);

            return new ForStmt(variable, PrgExpr.Parse(fromText), PrgExpr.Parse(toText), step, body) { Line = ll.LineNo };
        }

        // FOR EACH uVar IN aArrayOrCollection [FOXORDER] … ENDFOR|NEXT
        private ForEachStmt ParseForEach()
        {
            var ll = Next();
            // Drop the leading "FOR" then "EACH", leaving "uVar IN aArray [FOXORDER]".
            string rest = PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(ll.Text));
            int inPos = PrgScan.IndexOfKeyword(rest, "IN");
            if (inPos < 0) throw new MicroVfpSyntaxException("Malformed FOR EACH (missing IN).", ll.LineNo);
            string variable = rest.Substring(0, inPos).Trim();
            if (variable.Length == 0) throw new MicroVfpSyntaxException("Malformed FOR EACH (missing loop variable).", ll.LineNo);
            string collection = rest.Substring(inPos + 2).Trim();
            // Strip a trailing FOXORDER keyword (2-D traversal-order flag) — microVFP iterates linear order.
            var words = PrgScan.TopWords(collection);
            if (words.Count > 0 && words[^1].Upper == "FOXORDER")
                collection = collection.Substring(0, words[^1].Start).TrimEnd();
            if (collection.Length == 0) throw new MicroVfpSyntaxException("Malformed FOR EACH (missing collection).", ll.LineNo);

            var body = ParseBlock("ENDFOR", "NEXT");
            if (!Eof && PeekWord is "ENDFOR" or "NEXT") Next();
            else throw new MicroVfpSyntaxException("Missing ENDFOR/NEXT.", ll.LineNo);

            return new ForEachStmt(variable, PrgExpr.Parse(collection), body) { Line = ll.LineNo };
        }

        private ScanStmt ParseScan()
        {
            var ll = Next();
            var (scope, forE, whileE) = ScopeForWhile(PrgScan.AfterFirstWord(ll.Text));
            var body = ParseBlock("ENDSCAN");
            Require("ENDSCAN", ll.LineNo);
            return new ScanStmt(scope, forE, whileE, body) { Line = ll.LineNo };
        }

        private void Require(string keyword, int openLine)
        {
            if (Eof || PeekWord != keyword)
                throw new MicroVfpSyntaxException($"Missing {keyword}.", openLine);
            Next();
        }

        // ---- simple (single-line) statements ----

        private PrgStatement BuildSimple(string text, int line)
        {
            string kw = PrgScan.FirstWord(text);
            // Generalised &macro: any line (other than the already-macro-aware forms — a line that STARTS
            // with '&', ON ERROR &var, or a # directive) that embeds a top-level &var[.] is deferred as a
            // MacroSubstStmt and expanded+re-parsed at RUNTIME (textual substitution before execution).
            if (kw is not ("&" or "ON" or "#") && PrgScan.ContainsTopLevelMacro(text))
                return new MacroSubstStmt(new MacroSubst(text.Trim())) { Line = line };
            // VFP disambiguates a STORE (`<lvalue> = <expr>`, a single '=', not '==') from a command by
            // SHAPE, not by the first word: `total = 0` is an assignment even though TOTAL is a command
            // verb. Detect that shape BEFORE dispatching to a same-named builder, otherwise common
            // variable names that collide with command keywords (total/sum/pack/count/date/type/…) would
            // route to the command grammar, fail it, and be silently dropped as an UnknownCommand.
            if (kw is not ("=" or "&" or "#"))
            {
                int eqPos = PrgScan.IndexOfAssign(text);
                if (eqPos > 0 && IsAssignTarget(text.Substring(0, eqPos)))
                    return new Assignment(text.Substring(0, eqPos).Trim(),
                                          PrgExpr.Parse(text.Substring(eqPos + 1))) { Line = line };
            }
            PrgStatement node = kw switch
            {
                "LOCAL" => BuildVarDecl(DeclScope.Local, text),
                "PRIVATE" => BuildVarDecl(DeclScope.Private, text),
                "PUBLIC" => BuildVarDecl(DeclScope.Public, text),
                "RELEASE" => BuildRelease(text),
                "DIMENSION" or "REDIMENSION" => BuildDimension(text),
                "PARAMETERS" => new ParametersStmt(false, IdentList(PrgScan.AfterFirstWord(text))),
                "LPARAMETERS" => new ParametersStmt(true, IdentList(PrgScan.AfterFirstWord(text))),
                "STORE" => BuildStore(text),
                "RETURN" => BuildReturn(text),
                "EXIT" => new ExitStmt(),
                "LOOP" => new LoopStmt(),
                "DO" => BuildDoCall(text),
                "SELECT" => BuildSelect(text),
                "USE" => BuildUse(text),
                "SEEK" => BuildSeek(text),
                "INDEX" => BuildIndex(text),
                "REINDEX" => BuildReindex(text),
                "LOCATE" => BuildLocate(text),
                "CONTINUE" => new ContinueStmt(),
                "GO" or "GOTO" => BuildGo(text),
                "SKIP" => BuildSkip(text),
                "SET" => BuildSet(text, line),
                "ON" => BuildOn(text, line),
                "REPLACE" => BuildReplace(text),
                "DELETE" => BuildDelete(text),
                "RECALL" => BuildRecall(text),
                "INSERT" => new InsertStmt(text.Trim(), TrySql(text)),
                "GATHER" => BuildGather(text),
                "SCATTER" => BuildScatter(text),
                "APPEND" => BuildAppend(text),
                "BLANK" => BuildBlank(text),
                "COPY" => BuildCopy(text),
                "TOTAL" => BuildTotal(text),
                "PACK" => BuildPack(text),
                "RENAME" => BuildRename(text),
                "CREATE" => BuildCreate(text),
                "FLUSH" => BuildFlush(text),
                "SUM" => BuildSum(text),
                "CLEAR" => BuildClear(text),
                "UNLOCK" => BuildUnlock(text),
                "ZAP" => BuildZap(text),
                "WAIT" => BuildWait(text),
                "SAVE" => BuildSave(text),
                "RESTORE" => BuildRestore(text),
                "VALIDATE" => BuildValidate(text),
                "DISPLAY" or "LIST" => BuildDisplayOrList(text),
                "ROLLBACK" => new RollbackStmt(),
                "BEGIN" => PrgScan.SecondWord(text) == "TRANSACTION"
                    ? new BeginTxnStmt()
                    : new UnknownCommand(kw, PrgScan.AfterFirstWord(text)),
                "END" => PrgScan.SecondWord(text) == "TRANSACTION"
                    ? new EndTxnStmt()
                    : new UnknownCommand(kw, PrgScan.AfterFirstWord(text)),
                "=" => new ExprStatement(PrgExpr.Parse(text.TrimStart().Substring(1))),
                "&" => new MacroSubstStmt(new MacroSubst(text.Trim())),
                "#" => new DirectiveStmt(text.Trim()),
                _ => BuildDefault(text, kw),
            };
            return node with { Line = line };
        }

        private static PrgStatement BuildDefault(string text, string kw)
        {
            int eq = PrgScan.IndexOfAssign(text);
            if (eq > 0 && IsAssignTarget(text.Substring(0, eq)))
            {
                string target = text.Substring(0, eq).Trim();
                return new Assignment(target, PrgExpr.Parse(text.Substring(eq + 1)));
            }
            return new UnknownCommand(kw, PrgScan.AfterFirstWord(text));
        }

        // A left-hand side is an assignable REFERENCE — an identifier optionally followed by member
        // access (<c>.name</c>) and/or balanced subscripts (<c>[…]</c> / <c>(…)</c>), and NOTHING else.
        // A top-level space (a second bare word, e.g. "SET FILTER TO x", "DELETE FOR a") disqualifies it,
        // which is exactly what separates a store (<c>total = 0</c>) from a command that merely contains a
        // '=' further along (<c>SET FILTER TO x = y</c>). IndexOfAssign already guarantees the '=' sits at
        // bracket-depth 0 outside any string, so the target's brackets/strings here are balanced/closed.
        private static bool IsAssignTarget(string s)
        {
            s = s.Trim();
            if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c is '\'' or '"') { inStr = true; q = c; continue; }
                if (c is '(' or '[') { depth++; continue; }
                if (c is ')' or ']') { if (depth == 0) return false; depth--; continue; }
                if (depth > 0) continue;                                  // inside a subscript: anything goes
                if (c == '.' || char.IsLetterOrDigit(c) || c == '_') continue;   // member-access chain
                return false;                                            // top-level space/operator ⇒ command
            }
            return depth == 0 && !inStr;
        }

        private static VarDecl BuildVarDecl(DeclScope scope, string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (all, like, except, names, dims) = ParseDeclBody(rest);
            return new VarDecl(scope, names, all, like, except, dims);
        }

        private static ReleaseStmt BuildRelease(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (all, like, except, names, _) = ParseDeclBody(rest);
            return new ReleaseStmt(names, all, like, except);
        }

        // CLEAR [MEMORY | ALL | WINDOWS | GETS | …]. MEMORY/ALL release memory; every other form is a UI
        // subsystem reset with no headless-interpreter effect (flagged as a no-op ClearKind.Ui).
        private static ClearStmt BuildClear(string text)
        {
            string what = PrgScan.FirstWord(PrgScan.AfterFirstWord(text));
            var kind = what switch
            {
                "MEMORY" => ClearKind.Memory,
                "ALL" => ClearKind.All,
                _ => ClearKind.Ui,
            };
            return new ClearStmt(kind);
        }

        private static DimensionStmt BuildDimension(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (_, _, _, names, dims) = ParseDeclBody(rest);
            return new DimensionStmt(names, dims);
        }

        private static (bool All, string? Like, string? Except, IReadOnlyList<string> Names, IReadOnlyList<string?> Dimensions) ParseDeclBody(string rest)
        {
            if (PrgScan.FirstWord(rest) == "ALL")
            {
                string tail = PrgScan.AfterFirstWord(rest);
                string? like = null, except = null;
                int likePos = PrgScan.IndexOfKeyword(tail, "LIKE");
                int exceptPos = PrgScan.IndexOfKeyword(tail, "EXCEPT");
                if (likePos >= 0) like = PrgScan.AfterFirstWord(tail.Substring(likePos)).Trim();
                if (exceptPos >= 0) except = PrgScan.AfterFirstWord(tail.Substring(exceptPos)).Trim();
                return (true, like, except, Array.Empty<string>(), Array.Empty<string?>());
            }
            var names = new List<string>();
            var dims = new List<string?>();
            foreach (var piece in PrgScan.SplitTopCommas(rest))
            {
                var (name, dim) = SplitNameAndDim(piece);
                if (name.Length == 0) continue;
                names.Add(name);
                dims.Add(dim);
            }
            return (false, null, null, names, dims);
        }

        /// <summary>Splits a single declaration entry into its identifier and the captured
        /// array-dimension bracket text (incl. the brackets), e.g. <c>"gaErrors(1,12)"</c> →
        /// <c>("gaErrors", "(1,12)")</c>; a scalar yields <c>(name, null)</c>.</summary>
        private static (string Name, string? Dim) SplitNameAndDim(string piece)
        {
            string p = piece.TrimStart();
            string name = LeadingIdent(p);
            if (name.Length == 0) return (string.Empty, null);
            string after = p.Substring(name.Length).TrimStart();
            if (after.Length > 0 && (after[0] == '(' || after[0] == '['))
            {
                int close = MatchBracket(after, 0);
                if (close >= 0) return (name, after.Substring(0, close + 1));
            }
            return (name, null);
        }

        private static StoreStmt BuildStore(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            int toPos = PrgScan.IndexOfKeyword(rest, "TO");
            if (toPos < 0)
                return new StoreStmt(PrgExpr.Parse(rest), Array.Empty<string>());
            string value = rest.Substring(0, toPos);
            var targets = PrgScan.SplitTopCommas(rest.Substring(toPos + 2))
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            return new StoreStmt(PrgExpr.Parse(value), targets);
        }

        private static ReturnStmt BuildReturn(string text)
        {
            string rest = PrgScan.AfterFirstWord(text).Trim();
            return new ReturnStmt(rest.Length == 0 ? null : PrgExpr.Parse(rest));
        }

        private static DoCall BuildDoCall(string text)
        {
            string rest = PrgScan.AfterFirstWord(text).TrimStart();
            string name = LeadingIdent(rest);
            string remainder = rest.Substring(name.Length);
            var (_, segs) = Carve(remainder, "WITH", "IN");
            var args = new List<PrgExpr>();
            NameRef? inRef = null;
            foreach (var (k, body) in segs)
            {
                if (k == "WITH")
                    args.AddRange(PrgScan.SplitTopCommas(body).Where(p => p.Length > 0).Select(PrgExpr.Parse));
                else if (k == "IN")
                    inRef = ToNameRef(FirstToken(body));
            }
            return new DoCall(name, args, inRef);
        }

        private static PrgStatement BuildSelect(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            if (PrgScan.IndexOfKeyword(rest, "FROM") >= 0)
                return new SqlSelectStmt(text.Trim(), TrySql(text));
            return new SelectAreaStmt(ToNameRef(FirstToken(rest)));
        }

        private static UseStmt BuildUse(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var toks = PrgScan.ClauseTokens(rest);
            string? database = null;
            NameRef? table = null, alias = null, order = null, inArea = null;
            var index = new List<NameRef>();
            bool again = false, noUpdate = false;
            var mode = UseMode.Default;

            int idx = 0;
            if (toks.Count > 0 && !IsUseKeyword(toks[0]))
            {
                string t = toks[0];
                idx = 1;
                if (!t.StartsWith('(') && t.Contains('!'))
                {
                    int bang = t.IndexOf('!');
                    database = t.Substring(0, bang);
                    table = NameRef.OfName(t.Substring(bang + 1));
                }
                else
                {
                    table = ToNameRef(t);
                }
            }
            while (idx < toks.Count)
            {
                switch (toks[idx].ToUpperInvariant())
                {
                    case "IN": inArea = ToNameRef(NextTok(toks, ref idx)); break;
                    case "AGAIN": again = true; idx++; break;
                    case "ALIAS": alias = ToNameRef(NextTok(toks, ref idx)); break;
                    case "ORDER": order = ToNameRef(NextTok(toks, ref idx)); break;
                    case "EXCLUSIVE": mode = UseMode.Exclusive; idx++; break;
                    case "SHARED":
                    case "SHARE": mode = UseMode.Shared; idx++; break;
                    case "NOUPDATE": noUpdate = true; idx++; break;
                    case "INDEX":
                        // Collect the comma-separated index-file list up to the next USE keyword and
                        // open+track each (was a parse-and-discard no-op).
                        idx++;
                        var sbIdx = new StringBuilder();
                        while (idx < toks.Count && !IsUseKeyword(toks[idx]))
                            sbIdx.Append(toks[idx++]).Append(' ');
                        foreach (var piece in PrgScan.SplitTopCommas(sbIdx.ToString()))
                        {
                            string tok = FirstToken(piece);
                            if (tok.Length > 0) index.Add(ToNameRef(tok));
                        }
                        break;
                    default: idx++; break;
                }
            }
            return new UseStmt(database, table, again, alias, order, inArea, mode, noUpdate, table is null, index);
        }

        private static SeekStmt BuildSeek(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (key, segs) = Carve(rest, "ORDER", "IN");
            NameRef? order = null, inArea = null;
            foreach (var (k, body) in segs)
            {
                if (k == "ORDER") order = ToNameRef(FirstToken(body));
                else if (k == "IN") inArea = ToNameRef(FirstToken(body));
            }
            return new SeekStmt(PrgExpr.Parse(key), order, inArea);
        }

        /// <summary>The bare flag words of <c>INDEX ON</c> (they carry no operand, so they are stripped
        /// out before the key/TAG/OF/TO/FOR clauses are carved).</summary>
        private static readonly string[] IndexFlagWords =
            { "ASCENDING", "DESCENDING", "UNIQUE", "CANDIDATE", "ADDITIVE", "COMPACT" };

        private static PrgStatement BuildIndex(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);                          // drop INDEX
            if (PrgScan.FirstWord(rest) == "ON") rest = PrgScan.AfterFirstWord(rest); // drop ON

            // Blank out the bare flag words in place (preserving offsets) so the key expression and the
            // TAG/OF/TO/FOR clause bodies carve cleanly and never swallow a trailing DESCENDING/UNIQUE/….
            bool descending = false, unique = false, candidate = false, additive = false;
            var chars = rest.ToCharArray();
            foreach (var (start, len, upper) in PrgScan.TopWords(rest))
            {
                if (Array.IndexOf(IndexFlagWords, upper) < 0) continue;
                switch (upper)
                {
                    case "DESCENDING": descending = true; break;
                    case "UNIQUE": unique = true; break;
                    case "CANDIDATE": candidate = true; break;
                    case "ADDITIVE": additive = true; break;
                    // ASCENDING / COMPACT are the defaults — recognised so they are stripped, no flag set.
                }
                for (int i = start; i < start + len; i++) chars[i] = ' ';
            }
            string cleaned = new string(chars);

            var (key, segs) = Carve(cleaned, "TAG", "OF", "TO", "FOR");
            NameRef? tag = null, ofCdx = null, toIdx = null;
            PrgExpr? forE = null;
            foreach (var (k, body) in segs)
            {
                switch (k)
                {
                    case "TAG": tag = ToNameRef(FirstToken(body)); break;
                    case "OF": ofCdx = ToNameRef(FirstToken(body)); break;
                    case "TO": toIdx = ToNameRef(FirstToken(body)); break;
                    case "FOR": if (body.Trim().Length > 0) forE = PrgExpr.Parse(body); break;
                }
            }

            string keyText = key.Trim();
            if (keyText.Length == 0)
                return new UnknownCommand("INDEX", PrgScan.AfterFirstWord(text));
            return new IndexStmt(PrgExpr.Parse(keyText), tag, ofCdx, toIdx, forE,
                descending, unique, candidate, additive);
        }

        private static ReindexStmt BuildReindex(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (_, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            return new ReindexStmt(inArea);
        }

        // LOCATE [Scope] [FOR lExpr] [WHILE lExpr] [NOOPTIMIZE]. Unlike SCAN, VFP also tolerates the Scope
        // AFTER the FOR clause (e.g. `LOCATE FOR x NEXT 5`) and a trailing NOOPTIMIZE optimizer hint — both
        // VFP9-verified as legal. Carve on FOR/WHILE (leading scope becomes the head, as for SCAN), then peel
        // any scope/NOOPTIMIZE that trails a FOR/WHILE expression so it is not fed to the expression parser
        // (which would otherwise fail and yield a silent never-matching Null predicate — project-review 5.2).
        private static LocateStmt BuildLocate(string text)
        {
            var (head, segs) = Carve(PrgScan.AfterFirstWord(text), "FOR", "WHILE");
            string? scope = head.Length == 0 ? null : head;
            PrgExpr? forE = null, whileE = null;
            foreach (var (k, b) in segs)
            {
                // Only peel when the body does NOT parse as a whole: a clean parse means there is no trailing
                // clause, which leaves a valid predicate that happens to end in a field named e.g. "record"
                // untouched (PrgExpr.Parse is error-tolerant — IsParsed is false only on genuine junk).
                var expr = PrgExpr.Parse(b);
                if (!expr.IsParsed)
                {
                    string body = b;
                    string? trailScope = PeelTrailingScope(ref body);
                    if (body.Length != b.Length)            // something was peeled ⇒ re-parse the expression.
                    {
                        scope ??= trailScope;               // leading (head) scope, if any, always wins.
                        expr = PrgExpr.Parse(body);
                    }
                }
                if (k == "FOR") forE = expr; else whileE = expr;
            }
            return new LocateStmt(scope, forE, whileE);
        }

        /// <summary>Peels a trailing NOOPTIMIZE hint and/or a trailing scope clause (REST / ALL / NEXT n /
        /// RECORD n) off the END of a LOCATE FOR/WHILE body and returns the scope text (null if none);
        /// <paramref name="body"/> is trimmed back to just the expression. Called only when the body already
        /// failed to parse, so a scope keyword appearing here is genuine trailing junk, not a field name in a
        /// valid predicate.</summary>
        private static string? PeelTrailingScope(ref string body)
        {
            // (1) trailing NOOPTIMIZE — a bare optimizer hint microVFP does not model (single-process; no
            // Rushmore toggle). Drop it so it is not parsed as part of the expression.
            var words = PrgScan.TopWords(body);
            if (words.Count > 0 && words[^1].Upper == "NOOPTIMIZE")
            {
                body = body.Substring(0, words[^1].Start).TrimEnd();
                words = PrgScan.TopWords(body);
            }
            // (2) trailing scope clause. REST/ALL are bare words; NEXT/RECORD take a trailing count expression,
            // so the keyword is the LAST top-level word when its count is numeric/parenthesised (both are
            // non-words) and the second-to-last when the count is a bare identifier (NEXT lnN).
            if (words.Count == 0) return null;
            int kwIdx = -1;
            if (words[^1].Upper is "REST" or "ALL" or "NEXT" or "RECORD") kwIdx = words.Count - 1;
            else if (words.Count >= 2 && words[^2].Upper is "NEXT" or "RECORD") kwIdx = words.Count - 2;
            if (kwIdx < 0) return null;
            int start = words[kwIdx].Start;
            string scope = body.Substring(start).Trim();
            body = body.Substring(0, start).TrimEnd();
            return scope;
        }

        private static GoStmt BuildGo(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (target, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            target = target.Trim();
            if (PrgScan.FirstWord(target) == "RECORD") target = PrgScan.AfterFirstWord(target).Trim();
            string up = target.ToUpperInvariant();
            if (up is "TOP" or "BOTTOM")
                return new GoStmt(up, null, inArea);
            return new GoStmt(null, PrgExpr.Parse(target), inArea);
        }

        private static SkipStmt BuildSkip(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (count, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            count = count.Trim();
            return new SkipStmt(count.Length == 0 ? null : PrgExpr.Parse(count), inArea);
        }

        private static PrgStatement BuildSet(string text, int line)
        {
            string rest = PrgScan.AfterFirstWord(text).TrimStart();
            string setting = LeadingIdent(rest).ToUpperInvariant();
            string args = rest.Substring(setting.Length).Trim();
            if (setting == "ORDER")
                return BuildSetOrder(args);
            if (setting == "INDEX")
                return BuildSetIndex(args);
            if (setting == "RELATION")
                return BuildSetRelation(args);
            if (setting == "SKIP")
                return BuildSetSkip(args);
            return new SetStmt(setting, args);
        }

        /// <summary><c>SET RELATION [OFF] [TO [eExpr INTO area [, …]] [ADDITIVE]]</c>. The clear forms
        /// (<c>SET RELATION TO</c> / <c>SET RELATION OFF [INTO area]</c>) yield empty-target /
        /// <see cref="SetRelationOffStmt"/> nodes; each <c>eExpr INTO area</c> pair becomes a
        /// <see cref="RelationTarget"/>. Trailing top-level ADDITIVE sets the additive flag.</summary>
        private static PrgStatement BuildSetRelation(string args)
        {
            if (PrgScan.FirstWord(args) == "OFF")
            {
                string rest = PrgScan.AfterFirstWord(args);
                var (_, offSegs) = Carve(rest, "INTO");
                NameRef? into = offSegs.Count > 0 ? ToNameRef(FirstToken(offSegs[0].Body)) : null;
                return new SetRelationOffStmt(into);
            }
            if (PrgScan.FirstWord(args) == "TO") args = PrgScan.AfterFirstWord(args);
            args = args.Trim();

            // A trailing top-level ADDITIVE keyword adds to (rather than replaces) the prior relations.
            bool additive = false;
            var words = PrgScan.TopWords(args);
            if (words.Count > 0 && words[^1].Upper == "ADDITIVE")
            {
                additive = true;
                args = args.Substring(0, words[^1].Start).TrimEnd();
            }

            var targets = new List<RelationTarget>();
            foreach (var piece in PrgScan.SplitTopCommas(args))
            {
                if (piece.Trim().Length == 0) continue;
                var (key, segs) = Carve(piece, "INTO");
                if (segs.Count == 0 || key.Trim().Length == 0) continue;   // malformed pair — skip.
                targets.Add(new RelationTarget(PrgExpr.Parse(key), ToNameRef(FirstToken(segs[0].Body))));
            }
            return new SetRelationStmt(targets, additive);
        }

        /// <summary><c>SET SKIP TO [alias [, …]]</c> — a comma-list of already-related child aliases to
        /// mark one-to-many; an empty list clears all marks.</summary>
        private static SetSkipStmt BuildSetSkip(string args)
        {
            if (PrgScan.FirstWord(args) == "TO") args = PrgScan.AfterFirstWord(args);
            var aliases = new List<NameRef>();
            foreach (var piece in PrgScan.SplitTopCommas(args))
            {
                string tok = FirstToken(piece);
                if (tok.Length > 0) aliases.Add(ToNameRef(tok));
            }
            return new SetSkipStmt(aliases);
        }

        private static SetOrderStmt BuildSetOrder(string args)
        {
            if (PrgScan.FirstWord(args) == "TO") args = PrgScan.AfterFirstWord(args);
            var (head, segs) = Carve(args, "IN", "DESCENDING", "ASCENDING", "TAG");
            NameRef? order = head.Trim().Length == 0 ? null : ToNameRef(FirstToken(head));
            NameRef? inArea = null;
            bool? direction = null;   // null = no explicit clause; true = DESCENDING; false = ASCENDING.
            foreach (var (k, body) in segs)
            {
                if (k == "IN") inArea = ToNameRef(FirstToken(body));
                else if (k == "DESCENDING") direction = true;
                else if (k == "ASCENDING") direction = false;
                else if (k == "TAG" && order is null) order = ToNameRef(FirstToken(body));
            }
            return new SetOrderStmt(order, inArea, direction);
        }

        private PrgStatement BuildOn(string text, int line)
        {
            if (PrgScan.SecondWord(text) != "ERROR")
                return new UnknownCommand("ON", PrgScan.AfterFirstWord(text));
            string command = PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(text)).Trim(); // drop ON, ERROR
            if (command.Length == 0)
                return new OnErrorStmt(OnErrorKind.Clear, null, null);
            if (command.StartsWith('&'))
                return new OnErrorStmt(OnErrorKind.Macro, null, new MacroSubst(command));
            return new OnErrorStmt(OnErrorKind.Command, BuildSimple(command, line), null, command);
        }

        private static PrgStatement BuildReplace(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            // REPLACE FROM ARRAY aArray [FIELDS cList] [scope] [FOR][WHILE][IN] — the SCATTER inverse: a
            // structurally different grammar (no field-WITH pairs), so it is peeled off up front.
            if (PrgScan.FirstWord(rest) == "FROM" && PrgScan.SecondWord(rest) == "ARRAY")
                return BuildReplaceFromArray(PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(rest)));

            var (body, segs) = Carve(rest, "FOR", "WHILE", "IN");
            // A leading scope (ALL / REST / NEXT n / RECORD n) precedes the first field-WITH pair;
            // peel it off so ToNameRef never builds a malformed field like "ALL setup.value".
            var (scope, fieldList) = CarveLeadingScope(body);
            var clauses = new List<ReplaceClause>();
            foreach (var piece in PrgScan.SplitTopCommas(fieldList))
            {
                if (piece.Trim().Length == 0) continue;
                int withPos = PrgScan.IndexOfKeyword(piece, "WITH");
                if (withPos < 0) continue;
                var field = ToNameRef(piece.Substring(0, withPos).Trim());
                string valText = piece.Substring(withPos + 4).Trim();
                bool additive = false;
                int addPos = PrgScan.IndexOfKeyword(valText, "ADDITIVE");
                if (addPos >= 0 && valText.Substring(addPos).Trim().Equals("ADDITIVE", StringComparison.OrdinalIgnoreCase))
                {
                    additive = true;
                    valText = valText.Substring(0, addPos).Trim();
                }
                clauses.Add(new ReplaceClause(field, PrgExpr.Parse(valText), additive));
            }
            PrgExpr? forE = null, whileE = null;
            NameRef? inArea = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
                else if (k == "IN") inArea = ToNameRef(FirstToken(b));
            }
            return new ReplaceStmt(clauses, scope, forE, whileE, inArea);
        }

        /// <summary><c>REPLACE FROM ARRAY aArray [FIELDS cList] [scope] [FOR][WHILE][IN]</c> (the text after
        /// <c>FROM ARRAY</c>). The array name leads; FIELDS/scope/FOR/WHILE/IN follow.</summary>
        private static PrgStatement BuildReplaceFromArray(string rest)
        {
            var (head, segs) = Carve(rest, "FIELDS", "FOR", "WHILE", "IN");
            var (leadScope, arrayPart) = CarveLeadingScope(head);
            // Per VFP syntax the scope TRAILS the array name (REPLACE FROM ARRAY a ALL) — peel it so the
            // executor sees the scope (and rejects the unmodelled multi-record form) instead of silently
            // dropping "ALL" and doing a current-record-only write.
            string? trailScope = PeelTrailingScope(ref arrayPart);
            string? scope = leadScope ?? trailScope;
            string arrayName = FirstToken(arrayPart);
            PrgExpr? forE = SegBody(segs, "FOR") is { Length: > 0 } fb ? PrgExpr.Parse(fb) : null;
            PrgExpr? whileE = SegBody(segs, "WHILE") is { Length: > 0 } wb ? PrgExpr.Parse(wb) : null;
            NameRef? inArea = SegBody(segs, "IN") is { Length: > 0 } ib ? ToNameRef(FirstToken(ib)) : null;
            return new ReplaceFromArrayStmt(arrayName, FieldsOf(segs), scope, forE, whileE, inArea);
        }

        /// <summary><c>BLANK [FIELDS cList] [scope] [FOR lExpr] [WHILE lExpr] [IN area]</c>. An absent FIELDS
        /// list blanks every field of the (scoped) record(s).</summary>
        private static PrgStatement BuildBlank(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after BLANK
            var (head, segs) = Carve(rest, "FIELDS", "FOR", "WHILE", "IN");
            var (scope, _) = CarveLeadingScope(head);
            PrgExpr? forE = SegBody(segs, "FOR") is { Length: > 0 } fb ? PrgExpr.Parse(fb) : null;
            PrgExpr? whileE = SegBody(segs, "WHILE") is { Length: > 0 } wb ? PrgExpr.Parse(wb) : null;
            NameRef? inArea = SegBody(segs, "IN") is { Length: > 0 } ib ? ToNameRef(FirstToken(ib)) : null;
            return new BlankStmt(FieldsOf(segs), scope, forE, whileE, inArea);
        }

        /// <summary>Peels a leading record scope (<c>ALL</c> / <c>REST</c> / <c>NEXT n</c> /
        /// <c>RECORD n</c>) off the front of a command body, returning the scope text (or null) and
        /// the remainder. Leaves a bare field/clause body untouched.</summary>
        private static (string? Scope, string Remainder) CarveLeadingScope(string s)
        {
            s = s.TrimStart();
            string w = PrgScan.FirstWord(s);
            if (w is "ALL" or "REST")
                return (w, PrgScan.AfterFirstWord(s));
            if (w is "NEXT" or "RECORD")
            {
                string after = PrgScan.AfterFirstWord(s);
                int k = 0;
                while (k < after.Length && !char.IsWhiteSpace(after[k])) k++;
                string num = after.Substring(0, k);
                string remainder = after.Substring(k).TrimStart();
                return ($"{w} {num}".Trim(), remainder);
            }
            return (null, s);
        }

        private static PrgStatement BuildDelete(string text)
        {
            string rest0 = PrgScan.AfterFirstWord(text);
            if (PrgScan.FirstWord(rest0).Equals("TAG", StringComparison.OrdinalIgnoreCase))
                return BuildDeleteTag(PrgScan.AfterFirstWord(rest0));
            string rest = rest0;
            var (head, segs) = Carve(rest, "FOR", "WHILE", "IN");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = null, whileE = null;
            NameRef? inArea = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
                else if (k == "IN") inArea = ToNameRef(FirstToken(b));
            }
            return new DeleteStmt(scope, forE, whileE, inArea);
        }

        /// <summary><c>DELETE TAG &lt;name&gt;[, …] | ALL [OF &lt;cdx&gt;] [IN area]</c>. A single trailing
        /// <c>OF &lt;cdx&gt;</c> applies to the whole (structural-by-default) tag list; <c>ALL</c> removes
        /// every tag.</summary>
        private static DeleteTagStmt BuildDeleteTag(string rest)
        {
            var (head, segs) = Carve(rest, "OF", "IN");
            NameRef? ofCdx = null, inArea = null;
            foreach (var (k, body) in segs)
            {
                if (k == "OF") ofCdx = ToNameRef(FirstToken(body));
                else if (k == "IN") inArea = ToNameRef(FirstToken(body));
            }
            head = head.Trim();
            if (PrgScan.FirstWord(head).Equals("ALL", StringComparison.OrdinalIgnoreCase))
                return new DeleteTagStmt(Array.Empty<string>(), All: true, ofCdx, inArea);
            var tags = new List<string>();
            foreach (var piece in PrgScan.SplitTopCommas(head))
            {
                string tok = FirstToken(piece);
                if (tok.Length > 0) tags.Add(tok.Trim('\'', '"'));
            }
            return new DeleteTagStmt(tags, All: false, ofCdx, inArea);
        }

        /// <summary><c>SET INDEX TO [&lt;list&gt;] [ORDER &lt;tag|n&gt; [ASCENDING|DESCENDING]]
        /// [ADDITIVE]</c>.</summary>
        private static SetIndexStmt BuildSetIndex(string args)
        {
            if (PrgScan.FirstWord(args).Equals("TO", StringComparison.OrdinalIgnoreCase))
                args = PrgScan.AfterFirstWord(args);
            args = args.Trim();

            bool additive = false;
            var words = PrgScan.TopWords(args);
            if (words.Count > 0 && words[^1].Upper == "ADDITIVE")
            {
                additive = true;
                args = args.Substring(0, words[^1].Start).TrimEnd();
            }

            var (head, segs) = Carve(args, "ORDER", "ASCENDING", "DESCENDING");
            NameRef? order = null;
            bool? direction = null;
            foreach (var (k, body) in segs)
            {
                if (k == "ORDER" && body.Trim().Length > 0) order = ToNameRef(FirstToken(body));
                else if (k == "ASCENDING") direction = false;
                else if (k == "DESCENDING") direction = true;
            }
            var files = new List<NameRef>();
            foreach (var piece in PrgScan.SplitTopCommas(head))
            {
                string tok = FirstToken(piece);
                if (tok.Length > 0) files.Add(ToNameRef(tok));
            }
            return new SetIndexStmt(files, order, direction, additive);
        }

        private static RecallStmt BuildRecall(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (head, segs) = Carve(rest, "FOR", "WHILE");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = segs.FirstOrDefault(s => s.Kw == "FOR").Body is { Length: > 0 } b ? PrgExpr.Parse(b) : null;
            PrgExpr? whileE = segs.FirstOrDefault(s => s.Kw == "WHILE").Body is { Length: > 0 } w ? PrgExpr.Parse(w) : null;
            return new RecallStmt(scope, forE, whileE);
        }

        private static SumStmt BuildSum(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (head, segs) = Carve(rest, "FOR", "WHILE", "TO");
            var exprs = PrgScan.SplitTopCommas(head).Where(p => p.Length > 0).Select(PrgExpr.Parse).ToList();
            PrgExpr? forE = null, whileE = null;
            var targets = new List<string>();
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
                else if (k == "TO") targets.AddRange(
                    PrgScan.SplitTopCommas(b).Select(p => p.Trim()).Where(p => p.Length > 0));
            }
            return new SumStmt(exprs, null, forE, whileE, targets);
        }

        // ---- P2 table/record movers + whole-table I/O ----

        /// <summary><c>GATHER FROM aArray | MEMVAR | NAME oObj [FIELDS cList] [MEMO]</c>. The MEMVAR/NAME
        /// forms may omit FROM (<c>GATHER MEMVAR</c>); the array form always carries FROM.</summary>
        private static PrgStatement BuildGather(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after GATHER
            var (head, segs) = Carve(rest, "FROM", "FIELDS", "MEMO");
            string srcSpec = SegBody(segs, "FROM") ?? head;
            var (kind, name) = ParseScatterSpec(srcSpec);
            return new GatherStmt(kind, name, FieldsOf(segs), HasKw(segs, "MEMO"));
        }

        /// <summary><c>SCATTER TO aArray | MEMVAR [BLANK] | NAME oObj [FIELDS cList] [MEMO]</c>. The array
        /// form carries TO; MEMVAR/NAME may stand alone.</summary>
        private static PrgStatement BuildScatter(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after SCATTER
            var (head, segs) = Carve(rest, "TO", "FIELDS", "MEMO", "BLANK", "ADDITIVE");
            ScatterKind kind;
            string? name;
            if (SegBody(segs, "TO") is { } toBody)
            {
                kind = ScatterKind.Array;
                name = FirstToken(toBody);
            }
            else
            {
                (kind, name) = ParseScatterSpec(head);
            }
            return new ScatterStmt(kind, name, FieldsOf(segs), HasKw(segs, "MEMO"), HasKw(segs, "BLANK"));
        }

        /// <summary>Resolve a GATHER/SCATTER source/target spec (<c>MEMVAR</c> / <c>NAME oObj</c> / an array
        /// name) to its kind + name.</summary>
        private static (ScatterKind Kind, string? Name) ParseScatterSpec(string spec)
        {
            spec = spec.Trim();
            string fw = PrgScan.FirstWord(spec);
            if (fw == "MEMVAR") return (ScatterKind.Memvar, null);
            if (fw == "NAME") return (ScatterKind.Name, FirstToken(PrgScan.AfterFirstWord(spec)));
            if (spec.Length == 0) return (ScatterKind.Memvar, null);
            return (ScatterKind.Array, FirstToken(spec));
        }

        /// <summary><c>APPEND FROM cFile [FIELDS cList] [FOR lExpr] [TYPE cType]</c>. A bare
        /// <c>APPEND</c>/<c>APPEND BLANK</c> (no FROM) is left un-modelled.</summary>
        private static PrgStatement BuildAppend(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after APPEND
            // APPEND PROCEDURES FROM cFile — append text into the current DBC's stored-procedure source (§C.7).
            if (PrgScan.FirstWord(rest) == "PROCEDURES")
            {
                var (_, psegs) = Carve(PrgScan.AfterFirstWord(rest), "FROM");
                return new ProceduresStmt(Append: true, ToNameRef(FirstToken(SegBody(psegs, "FROM") ?? string.Empty)));
            }
            // APPEND MEMO mField FROM cFile [OVERWRITE] [AS nCodePage] — copy a file's content into a memo.
            if (PrgScan.FirstWord(rest) == "MEMO")
            {
                string r2 = PrgScan.AfterFirstWord(rest);   // after MEMO
                var (memoHead, memoSegs) = Carve(r2, "FROM", "OVERWRITE", "AS");
                var memoField = ToNameRef(FirstToken(memoHead));
                var memoSource = ToNameRef(FirstToken(SegBody(memoSegs, "FROM") ?? string.Empty));
                string? asCp = SegBody(memoSegs, "AS") is { Length: > 0 } asBody ? asBody.Trim() : null;
                return new AppendMemoStmt(memoField, memoSource, HasKw(memoSegs, "OVERWRITE"), asCp);
            }
            if (PrgScan.FirstWord(rest) != "FROM")
                return new UnknownCommand("APPEND", rest);
            string body = PrgScan.AfterFirstWord(rest);   // after FROM
            var (head, segs) = Carve(body, "FIELDS", "FOR", "WHILE", "TYPE");
            var source = ToNameRef(FirstToken(head));
            PrgExpr? forE = SegBody(segs, "FOR") is { Length: > 0 } fb ? PrgExpr.Parse(fb) : null;
            string? type = SegBody(segs, "TYPE") is { } tb && tb.Length > 0 ? FirstToken(tb) : null;
            return new AppendFromStmt(source, FieldsOf(segs), forE, type);
        }

        /// <summary><c>COPY STRUCTURE [EXTENDED] TO … / COPY TO … [FIELDS][FOR][TYPE] / COPY MEMO … TO …</c>.
        /// Other COPY forms (COPY FILE / COPY PROCEDURES) are left un-modelled.</summary>
        private static PrgStatement BuildCopy(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after COPY
            string fw = PrgScan.FirstWord(rest);
            if (fw == "STRUCTURE")
            {
                string r2 = PrgScan.AfterFirstWord(rest);
                bool extended = PrgScan.FirstWord(r2) == "EXTENDED";
                if (extended) r2 = PrgScan.AfterFirstWord(r2);
                var (_, segs) = Carve(r2, "TO", "FIELDS");
                var target = ToNameRef(FirstToken(SegBody(segs, "TO") ?? string.Empty));
                return new CopyStructureStmt(target, extended, FieldsOf(segs));
            }
            // COPY PROCEDURES TO cFile — extract the current DBC's stored-procedure source (§C.7).
            if (fw == "PROCEDURES")
            {
                var (_, psegs) = Carve(PrgScan.AfterFirstWord(rest), "TO", "ADDITIVE");
                return new ProceduresStmt(Append: false, ToNameRef(FirstToken(SegBody(psegs, "TO") ?? string.Empty)));
            }
            if (fw == "TO")
            {
                string r2 = PrgScan.AfterFirstWord(rest);   // after TO
                // COPY TO ARRAY aName [FIELDS][scope][FOR][WHILE] — the read counterpart of APPEND FROM ARRAY.
                if (PrgScan.FirstWord(r2).Equals("ARRAY", StringComparison.OrdinalIgnoreCase))
                {
                    string r3 = PrgScan.AfterFirstWord(r2);   // after ARRAY
                    var (ahead, asegs) = Carve(r3, "FIELDS", "FOR", "WHILE");
                    // the array name is the first token of ahead; any scope keyword (ALL/NEXT n/…) follows it.
                    var (scope, _) = CarveLeadingScope(PrgScan.AfterFirstWord(ahead));
                    string arrName = FirstToken(ahead);
                    PrgExpr? aFor = SegBody(asegs, "FOR") is { Length: > 0 } afb ? PrgExpr.Parse(afb) : null;
                    PrgExpr? aWhile = SegBody(asegs, "WHILE") is { Length: > 0 } awb ? PrgExpr.Parse(awb) : null;
                    return new CopyToArrayStmt(arrName, FieldsOf(asegs), scope, aFor, aWhile);
                }
                var (head, segs) = Carve(r2, "FIELDS", "FOR", "WHILE", "TYPE");
                var target = ToNameRef(FirstToken(head));
                PrgExpr? forE = SegBody(segs, "FOR") is { Length: > 0 } fb ? PrgExpr.Parse(fb) : null;
                string? type = SegBody(segs, "TYPE") is { } tb && tb.Length > 0 ? FirstToken(tb) : null;
                return new CopyToStmt(target, FieldsOf(segs), forE, type, Memo: false, MemoField: null);
            }
            if (fw == "MEMO")
            {
                string r2 = PrgScan.AfterFirstWord(rest);   // after MEMO
                var (head, segs) = Carve(r2, "TO", "ADDITIVE");
                var memoField = ToNameRef(FirstToken(head));
                var target = ToNameRef(FirstToken(SegBody(segs, "TO") ?? string.Empty));
                return new CopyToStmt(target, Array.Empty<string>(), null, null, Memo: true, memoField);
            }
            // COPY INDEXES cIdxList | ALL [TO cCdx] — convert open standalone .idx files into cdx tags.
            if (fw is "INDEXES" or "INDEX")
            {
                string r2 = PrgScan.AfterFirstWord(rest);   // after INDEXES
                var (head, segs) = Carve(r2, "TO");
                bool all = PrgScan.FirstWord(head).Equals("ALL", StringComparison.OrdinalIgnoreCase);
                var sources = all
                    ? (IReadOnlyList<NameRef>)Array.Empty<NameRef>()
                    : PrgScan.SplitTopCommas(head).Select(FirstToken).Where(t => t.Length > 0).Select(ToNameRef).ToList();
                NameRef? toCdx = SegBody(segs, "TO") is { Length: > 0 } tb ? ToNameRef(FirstToken(tb)) : null;
                return new CopyIndexesStmt(sources, all, toCdx);
            }
            // COPY TAG cTag [OF cCdx] TO cIdx — extract one cdx tag as a standalone .idx.
            if (fw == "TAG")
            {
                string r2 = PrgScan.AfterFirstWord(rest);   // after TAG
                var (head, segs) = Carve(r2, "OF", "TO");
                var tag = ToNameRef(FirstToken(head));
                NameRef? ofCdx = SegBody(segs, "OF") is { Length: > 0 } ob ? ToNameRef(FirstToken(ob)) : null;
                var toIdx = ToNameRef(FirstToken(SegBody(segs, "TO") ?? string.Empty));
                return new CopyTagStmt(tag, ofCdx, toIdx);
            }
            return new UnknownCommand("COPY", rest);
        }

        /// <summary><c>TOTAL ON eKey TO cFile [FIELDS nList] [FOR lExpr]</c>.</summary>
        private static PrgStatement BuildTotal(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after TOTAL
            if (PrgScan.FirstWord(rest) != "ON")
                return new UnknownCommand("TOTAL", rest);
            string body = PrgScan.AfterFirstWord(rest);   // after ON
            var (head, segs) = Carve(body, "TO", "FIELDS", "FOR", "WHILE");
            if (head.Trim().Length == 0)
                return new UnknownCommand("TOTAL", rest);
            var key = PrgExpr.Parse(head);
            var target = ToNameRef(FirstToken(SegBody(segs, "TO") ?? string.Empty));
            PrgExpr? forE = SegBody(segs, "FOR") is { Length: > 0 } fb ? PrgExpr.Parse(fb) : null;
            return new TotalStmt(target, key, FieldsOf(segs), forE);
        }

        /// <summary><c>PACK [MEMO | DBF]</c> or <c>PACK DATABASE</c> (all member tables of the current DBC).</summary>
        private static PrgStatement BuildPack(string text)
        {
            string kind = PrgScan.FirstWord(PrgScan.AfterFirstWord(text));
            if (kind == "DATABASE") return new PackDatabaseStmt();
            return new PackStmt(kind is "MEMO" or "DBF" ? kind : null);
        }

        /// <summary><c>ZAP [IN nArea | cAlias]</c> — remove all records of the (current or named) table.</summary>
        private static PrgStatement BuildZap(string text)
        {
            var (_, segs) = Carve(PrgScan.AfterFirstWord(text), "IN");
            NameRef? inArea = SegBody(segs, "IN") is { Length: > 0 } ib ? ToNameRef(FirstToken(ib)) : null;
            return new ZapStmt(inArea);
        }

        /// <summary><c>WAIT [cMsg] [WINDOW …] [TIMEOUT n] [TO mVar] [NOWAIT] [CLEAR]</c> — only the TO target
        /// is modelled (headless: never blocks; the message/window/timeout are ignored).</summary>
        private static PrgStatement BuildWait(string text)
        {
            var (_, segs) = Carve(PrgScan.AfterFirstWord(text), "TO", "WINDOW", "TIMEOUT", "NOWAIT", "CLEAR", "AT", "NOCLEAR");
            // TO mVar: the memvar receiving the (empty, headless) keypress.
            string? toVar = SegBody(segs, "TO") is { Length: > 0 } tb ? LeadingIdent(tb) : null;
            return new WaitStmt(toVar is { Length: > 0 } ? toVar : null);
        }

        /// <summary><c>SAVE TO cFile [ALL LIKE skel | ALL EXCEPT skel]</c>. The <c>TO MEMO</c> memofield form
        /// is left un-modelled (FLAG).</summary>
        private static PrgStatement BuildSave(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after SAVE
            if (!PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
                return new UnknownCommand("SAVE", rest);
            string body = PrgScan.AfterFirstWord(rest);   // after TO
            var (all, like, except, _, _) = ParseAllLikeExcept(body, out string target);
            return new SaveToStmt(ToNameRef(target), all ? like : null, all ? except : null);
        }

        /// <summary><c>RESTORE FROM cFile [ADDITIVE]</c>. The <c>FROM MEMO</c> memofield form is un-modelled.</summary>
        private static PrgStatement BuildRestore(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after RESTORE
            if (!PrgScan.FirstWord(rest).Equals("FROM", StringComparison.OrdinalIgnoreCase))
                return new UnknownCommand("RESTORE", rest);
            var (_, segs) = Carve(PrgScan.AfterFirstWord(rest), "ADDITIVE");
            // the file is the head (first token before ADDITIVE).
            string body = PrgScan.AfterFirstWord(rest);
            var (head, _) = Carve(body, "ADDITIVE");
            return new RestoreFromStmt(ToNameRef(FirstToken(head)), HasKw(segs, "ADDITIVE"));
        }

        /// <summary><c>VALIDATE DATABASE [NOCONSOLE] [RECOVER]</c> — read-only diagnostic (RECOVER FLAGGED).</summary>
        private static PrgStatement BuildValidate(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after VALIDATE
            if (!PrgScan.FirstWord(rest).Equals("DATABASE", StringComparison.OrdinalIgnoreCase))
                return new UnknownCommand("VALIDATE", rest);
            var words = PrgScan.TopWords(rest).Select(w => w.Upper).ToList();
            return new ValidateDatabaseStmt(words.Contains("RECOVER"));
        }

        /// <summary><c>DISPLAY | LIST MEMORY | STRUCTURE | TABLES [TO FILE cFile]</c> (+ MEMORY <c>LIKE skel</c>).
        /// Other DISPLAY/LIST forms (record listings, STATUS, …) stay un-modelled no-ops.</summary>
        private static PrgStatement BuildDisplayOrList(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after DISPLAY/LIST
            string what = PrgScan.FirstWord(rest);
            string body = PrgScan.AfterFirstWord(rest);   // after MEMORY/STRUCTURE/TABLES

            NameRef? toFile = ToFileTarget(body);
            switch (what)
            {
                case "MEMORY":
                {
                    var (_, segs) = Carve(body, "LIKE", "TO");
                    string? like = SegBody(segs, "LIKE") is { Length: > 0 } lb ? FirstToken(lb) : null;
                    return new MemoryDumpStmt(like, toFile);
                }
                case "STRUCTURE" or "STRU":
                {
                    var (_, segs) = Carve(body, "IN", "TO");
                    NameRef? inArea = SegBody(segs, "IN") is { Length: > 0 } ib ? ToNameRef(FirstToken(ib)) : null;
                    return new DisplayStructureStmt(inArea, toFile);
                }
                case "TABLES":
                    return new DisplayTablesStmt(toFile);
                default:
                    return new UnknownCommand(PrgScan.FirstWord(text), rest);
            }
        }

        /// <summary>Extract the <c>TO FILE cFile</c> target of a DISPLAY/LIST clause (null when absent —
        /// e.g. TO PRINTER / no redirection, a headless no-op).</summary>
        private static NameRef? ToFileTarget(string body)
        {
            var (_, segs) = Carve(body, "TO");
            if (SegBody(segs, "TO") is not { Length: > 0 } tb) return null;
            string first = PrgScan.FirstWord(tb);
            if (!first.Equals("FILE", StringComparison.OrdinalIgnoreCase)) return null;   // TO PRINTER/… → no file.
            return ToNameRef(FirstToken(PrgScan.AfterFirstWord(tb)));
        }

        /// <summary>Parse <c>cFile [ALL LIKE skel | ALL EXCEPT skel]</c>: the leading token is the target
        /// file; ALL LIKE/EXCEPT supply the wildcard skeletons.</summary>
        private static (bool All, string? Like, string? Except, string F1, string F2) ParseAllLikeExcept(string body, out string target)
        {
            var (head, segs) = Carve(body, "ALL", "LIKE", "EXCEPT");
            target = FirstToken(head);
            bool all = HasKw(segs, "ALL");
            string? like = SegBody(segs, "LIKE") is { Length: > 0 } lb ? FirstToken(lb) : null;
            string? except = SegBody(segs, "EXCEPT") is { Length: > 0 } eb ? FirstToken(eb) : null;
            if (like is not null || except is not null) all = true;   // LIKE/EXCEPT imply the ALL form.
            return (all, like, except, string.Empty, string.Empty);
        }

        /// <summary><c>RENAME TABLE cOld TO cNew</c> (the DBC-member form). A file-level
        /// <c>RENAME cOld TO cNew</c> is left un-modelled.</summary>
        private static PrgStatement BuildRename(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after RENAME
            if (PrgScan.FirstWord(rest) != "TABLE")
                return new UnknownCommand("RENAME", rest);
            string body = PrgScan.AfterFirstWord(rest);   // after TABLE
            var (head, segs) = Carve(body, "TO");
            var from = ToNameRef(FirstToken(head));
            var to = ToNameRef(FirstToken(SegBody(segs, "TO") ?? string.Empty));
            return new RenameTableStmt(from, to);
        }

        /// <summary><c>CREATE cTable FROM cStruct</c> (structure-descriptor form) or CREATE TABLE / CURSOR /
        /// DATABASE (routed to the SQL/DDL executor).</summary>
        private static PrgStatement BuildCreate(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);   // after CREATE
            var (head, segs) = Carve(rest, "FROM");
            if (SegBody(segs, "FROM") is { } fromBody
                && PrgScan.FirstWord(fromBody) != "ARRAY")
            {
                var toks = PrgScan.ClauseTokens(head);
                string name = toks.Count > 0 ? toks[^1] : string.Empty;   // last token before FROM (skips TABLE/DBF).
                if (name.Length > 0 && !name.Equals("ARRAY", StringComparison.OrdinalIgnoreCase))
                    return new CreateFromStmt(ToNameRef(name), ToNameRef(FirstToken(fromBody)));
            }
            return new SqlPassthroughStmt(text.Trim());
        }

        private static PrgStatement BuildFlush(string text)
        {
            string kind = PrgScan.FirstWord(PrgScan.AfterFirstWord(text));
            return new FlushStmt(kind == "FORCE");
        }

        private static bool HasKw(List<(string Kw, string Body)> segs, string kw)
            => segs.Any(s => s.Kw == kw);

        private static string? SegBody(List<(string Kw, string Body)> segs, string kw)
        {
            foreach (var (k, b) in segs) if (k == kw) return b;
            return null;
        }

        private static IReadOnlyList<string> FieldsOf(List<(string Kw, string Body)> segs)
            => SegBody(segs, "FIELDS") is { } b ? IdentList(b) : Array.Empty<string>();

        private static UnlockStmt BuildUnlock(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var toks = PrgScan.ClauseTokens(rest);
            bool all = false;
            NameRef? record = null, inArea = null;
            for (int idx = 0; idx < toks.Count;)
            {
                switch (toks[idx].ToUpperInvariant())
                {
                    case "ALL": all = true; idx++; break;
                    case "RECORD": record = ToNameRef(NextTok(toks, ref idx)); break;
                    case "IN": inArea = ToNameRef(NextTok(toks, ref idx)); break;
                    default: idx++; break;
                }
            }
            return new UnlockStmt(all, record, inArea);
        }

        // ---- shared little helpers ----

        private static (string? Scope, PrgExpr? For, PrgExpr? While) ScopeForWhile(string rest)
        {
            var (head, segs) = Carve(rest, "FOR", "WHILE");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = null, whileE = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
            }
            return (scope, forE, whileE);
        }

        /// <summary>Splits <paramref name="s"/> at top-level occurrences of the given keywords:
        /// returns the text before the first keyword plus, per keyword, the text up to the next
        /// keyword.</summary>
        private static (string Head, List<(string Kw, string Body)> Segments) Carve(string s, params string[] kws)
        {
            var set = new HashSet<string>(kws, StringComparer.Ordinal);
            var marks = new List<(int Pos, int Len, string Kw)>();
            foreach (var (start, len, upper) in PrgScan.TopWords(s))
                if (set.Contains(upper)) marks.Add((start, len, upper));

            string head = marks.Count == 0 ? s : s.Substring(0, marks[0].Pos);
            var segs = new List<(string, string)>();
            for (int m = 0; m < marks.Count; m++)
            {
                int bodyStart = marks[m].Pos + marks[m].Len;
                int bodyEnd = m + 1 < marks.Count ? marks[m + 1].Pos : s.Length;
                segs.Add((marks[m].Kw, s.Substring(bodyStart, bodyEnd - bodyStart).Trim()));
            }
            return (head.Trim(), segs);
        }

        private static SqlStatement? TrySql(string sql)
        {
            try { return SqlParser.Parse(sql.Trim()); }
            catch (Exception) { return null; }
        }

        private static IReadOnlyList<string> IdentList(string s)
            => PrgScan.SplitTopCommas(s).Select(LeadingIdent).Where(n => n.Length > 0).ToList();

        private static string LeadingIdent(string s)
        {
            s = s.TrimStart();
            int j = 0;
            while (j < s.Length && (VfpIdentifier.IsPart(s[j]))) j++;
            return s.Substring(0, j);
        }

        private static string FirstToken(string s)
        {
            var toks = PrgScan.ClauseTokens(s);
            return toks.Count > 0 ? toks[0] : string.Empty;
        }

        private static string NextTok(List<string> toks, ref int idx)
        {
            idx++; // consume the keyword
            return idx < toks.Count ? toks[idx++] : string.Empty;
        }

        private static NameRef ToNameRef(string token)
        {
            string t = token.Trim();
            if (t.Length == 0) return NameRef.OfName(string.Empty);
            if (t.StartsWith('(') && t.EndsWith(')'))
                return NameRef.OfExpr(new NameExpr(PrgExpr.Parse(t.Substring(1, t.Length - 2))));
            if ((t[0] == '\'' || t[0] == '"') && t.Length >= 2 && t[^1] == t[0])
                return NameRef.OfName(t.Substring(1, t.Length - 2));
            return NameRef.OfName(t);
        }

        private static bool IsUseKeyword(string t) => t.ToUpperInvariant() switch
        {
            "IN" or "AGAIN" or "ALIAS" or "ORDER" or "EXCLUSIVE" or "SHARED"
                or "SHARE" or "NOUPDATE" or "INDEX" or "NODATA" or "ONLINE" => true,
            _ => false,
        };

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>Index of the closing bracket matching the opener at <paramref name="open"/>
        /// (string-aware, supports both <c>()</c> and <c>[]</c>), or -1 if unbalanced.</summary>
        private static int MatchBracket(string s, int open)
        {
            char openCh = s[open];
            char closeCh = openCh == '(' ? ')' : ']';
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
                if (c == openCh) depth++;
                else if (c == closeCh) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
