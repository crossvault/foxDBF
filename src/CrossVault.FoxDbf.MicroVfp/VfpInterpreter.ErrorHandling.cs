using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  Loud failure + VFP9 structured error handling (TRY/CATCH/FINALLY/THROW) + COUNT.
//
//  Every fact below is pinned against the VFP9 runtime (MicroVfpTryCatchVfp9OracleTests):
//   • An error inside TRY goes to the first CATCH whose WHEN holds; TRY wins over ON ERROR, also for
//     errors raised in procedures called from the TRY body and even when the callee installs its own
//     ON ERROR. No matching CATCH ⇒ FINALLY runs, then the error propagates outward.
//   • Inside CATCH: ERROR() is 0, MESSAGE() is the caught message (and stays so after ENDTRY), AERROR()
//     fills from the caught error. An error inside CATCH/FINALLY propagates outward after FINALLY ran.
//   • THROW eExpr raises 2071 ("User Thrown Error .") with UserValue = eExpr (THROW oErr wraps the caught
//     object as UserValue); a bare THROW inside CATCH re-raises the caught error unchanged. AERROR()
//     after a user THROW reports 3071 / "Unknown error code 2071" and a multi-line detail; MESSAGE()
//     reports "Unknown error code 2071".
//   • EXIT inside TRY/CATCH leaves the TRY STRUCTURE (FINALLY runs, execution continues after ENDTRY —
//     it does NOT leave an enclosing loop); LOOP raises 1939; RETURN raises 2060.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class VfpInterpreter
{
    /// <summary>VFP error 16 — a command verb microVFP does not know.</summary>
    internal const int UnrecognizedVerbErrorNumber = 16;
    /// <summary>VFP error 36 — a known verb whose clause list microVFP could not parse.</summary>
    internal const int UnrecognizedPhraseErrorNumber = 36;
    /// <summary>VFP error 2071 — a user THROW.</summary>
    internal const int UserThrownErrorNumber = 2071;

    // Exception.Data keys stamped by the innermost ExecBlock that sees a fault (where it happened).
    private const string DataLine = "microvfp.line";
    private const string DataProc = "microvfp.proc";
    private const string DataLevel = "microvfp.level";
    private const string DataSource = "microvfp.source";

    // Active TRY bodies (dynamic, across calls): while > 0 an error is never routed to ON ERROR.
    private int _tryDepth;
    // The errors of the CATCH blocks currently executing (innermost last) — a bare THROW re-raises the top.
    private readonly List<Exception> _catchStack = new();

    /// <summary>The documented headless allow-list: commands that only drive the interactive UI / console
    /// (or declare things a headless runtime never needs) and are therefore deliberately ignored. Every
    /// other command microVFP does not execute raises error 16 (unknown verb) or 36 (unparsed clause).</summary>
    internal static readonly IReadOnlySet<string> HeadlessNoOpVerbs = new HashSet<string>(StringComparer.Ordinal)
    {
        // window / menu / popup / screen management and keyboard/mouse simulation
        "ACTIVATE", "DEACTIVATE", "HIDE", "SHOW", "MOVE", "ZOOM", "KEYBOARD", "MOUSE", "PUSH", "POP",
        // interactive event loop / designers / browse windows
        "READ", "DOEVENTS", "BROWSE", "EDIT", "CHANGE", "MODIFY",
        // debugging aids (ASSERT is inert under the default SET ASSERTS OFF)
        "ASSERT", "DEBUG", "DEBUGOUT", "SUSPEND",
        // compile-time / project directives
        "EXTERNAL",
        // ON KEY / ON ESCAPE / ON SHUTDOWN / ON PAD … — event traps that never fire headless (ON ERROR is modelled)
        "ON",
    };

    // DEFINE is a no-op only for its UI forms; DEFINE CLASS (the object model) must fail loudly.
    private static readonly HashSet<string> UiDefineKinds = new(StringComparer.Ordinal)
        { "WINDOW", "MENU", "POPUP", "BAR", "PAD", "BOX" };

    // CLOSE forms: ALL/TABLES/DATABASES/INDEXES are executed, the rest have no headless effect.
    private static readonly HashSet<string> KnownCloseKinds = new(StringComparer.Ordinal)
        { "ALTERNATE", "DEBUGGER", "FORMAT", "MEMO", "PRINTER", "PROCEDURE", "TABLES", "DATABASES", "ALL", "INDEXES", "INDEX" };

    /// <summary>True when <paramref name="uc"/> is on the documented headless no-op allow-list.</summary>
    internal static bool IsHeadlessNoOp(UnknownCommand uc)
    {
        string verb = uc.Verb;
        if (verb.Length == 0)
        {
            string t = uc.Arguments.TrimStart();
            // ? / ?? / ??? console output and @ … SAY/GET/BOX/CLEAR screen painting.
            return t.StartsWith('?') || t.StartsWith('@');
        }
        if (HeadlessNoOpVerbs.Contains(verb)) return true;
        string second = PrgScan.FirstWord(uc.Arguments);
        return verb switch
        {
            "DEFINE" => UiDefineKinds.Contains(second),
            // DECLARE … IN <dll> — a DLL import; the corpus' only use (CoCreateGuid) is a native built-in.
            "DECLARE" => PrgScan.IndexOfKeyword(uc.Arguments, "IN") >= 0,
            // DISPLAY / LIST forms microVFP does not model only paint the screen — unless redirected TO a file.
            "DISPLAY" or "LIST" => PrgScan.IndexOfKeyword(uc.Arguments, "TO") < 0,
            _ => false,
        };
    }

    private void ExecUnknown(UnknownCommand uc)
    {
        if (uc.Verb == "CLOSE")
        {
            string what = PrgScan.FirstWord(uc.Arguments);
            if (what.Length > 0 && !KnownCloseKinds.Contains(what))
                throw Loud(UnrecognizedPhraseErrorNumber, uc);
            ExecCloseCommand(uc.Arguments);
            return;
        }
        if (IsHeadlessNoOp(uc)) return;
        throw Loud(uc.KnownVerb ? UnrecognizedPhraseErrorNumber : UnrecognizedVerbErrorNumber, uc);
    }

    private static MicroVfpRuntimeException Loud(int number, UnknownCommand uc)
    {
        string text = (uc.Verb + " " + uc.Arguments).Trim();
        string msg = number == UnrecognizedVerbErrorNumber
            ? "Unrecognized command verb."
            : "Command contains unrecognized phrase/keyword.";
        return new MicroVfpRuntimeException(msg, number) { Statement = text, LineNo = uc.Line };
    }

    /// <summary>Raise the SQL parser's own error for <paramref name="sql"/> (or a plain syntax error 10 when it
    /// parses but is not the expected statement). Typed as returning a statement so it composes with <c>??</c>.</summary>
    private static SqlStatement ThrowSqlParseError(string sql)
    {
        try { SqlParser.Parse(sql.Trim()); }
        catch (FoxDbfSqlException ex)
        {
            throw new MicroVfpRuntimeException(ex.Message, ex.VfpErrorNumber ?? 10) { Statement = sql.Trim() };
        }
        catch (Exception ex) when (ex is not MicroVfpRuntimeException)
        {
            throw new MicroVfpRuntimeException(ex.Message, 10) { Statement = sql.Trim() };
        }
        throw new MicroVfpRuntimeException("Syntax error.", 10) { Statement = sql.Trim() };
    }

    /// <summary>Evaluate an expression the parser could not represent: expand macros, re-parse, and RAISE
    /// error 10 when it still does not parse (it used to evaluate silently to .NULL.).</summary>
    private VfpValue EvalUnparsed(string text)
    {
        string t = text.IndexOf('&') >= 0 ? ExpandMacrosInLine(text) : text;
        VfpExpression parsed;
        try { parsed = ParseExpressionCached(MicroVfpExprRewrite.Normalize(t)); }
        catch (ExpressionException)
        {
            var pe = PrgExpr.Parse(t);
            if (!pe.IsParsed)
                throw new MicroVfpRuntimeException("Syntax error.", 10) { Statement = text.Trim() };
            parsed = pe.Parsed!;
        }
        return parsed.Evaluate(_row, _ctx);
    }

    /// <summary>Record where a fault happened (innermost statement, procedure, call depth) on first sight.</summary>
    private void StampFault(Exception ex, int line, string? source = null)
    {
        var data = ex.Data;
        if (data.Contains(DataLine)) return;
        data[DataLine] = line;
        // VFP9 reports LineContents only for code running in a procedure, "" in the main program (verified).
        if (source is not null && _callStack.Count > 1) data[DataSource] = source;
        data[DataProc] = CurrentCall.Name;
        data[DataLevel] = _callStack.Count;
    }

    // ─────────────────────────── TRY / CATCH / FINALLY ───────────────────────────

    private void ExecTry(TryStmt t)
    {
        Exception? pending = null;
        Exception? caught = null;

        _tryDepth++;
        try { ExecBlock(t.Body); }
        catch (ExitSignal) { /* EXIT leaves the TRY structure (FINALLY still runs). */ }
        catch (Exception ex) { caught = AsTryError(ex, t.Line); }
        finally { _tryDepth--; }

        if (caught is not null)
        {
            var obj = new VfpErrorObject(caught, this);
            EnterCatchState(caught, obj);
            CatchClause? chosen = null;
            foreach (var c in t.Catches)
            {
                if (c.ToVar is { } v) AssignTo(v, NewObjectRef(obj));
                if (c.When is null || Truth(Eval(c.When))) { chosen = c; break; }
            }
            if (chosen is null)
            {
                pending = caught;
            }
            else
            {
                _catchStack.Add(caught);
                try { ExecBlock(chosen.Body); }
                catch (ExitSignal) { /* EXIT leaves the structure. */ }
                catch (Exception ex) { pending = AsTryError(ex, t.Line); }
                finally { _catchStack.RemoveAt(_catchStack.Count - 1); }
            }
        }

        if (t.Finally is not null)
        {
            try { ExecBlock(t.Finally); }
            catch (ExitSignal) { /* EXIT leaves the structure. */ }
            catch (Exception ex) when (ex is ReturnSignal or LoopSignal) { throw AsTryError(ex, t.Line); }
        }

        if (pending is not null) ExceptionDispatchInfo.Capture(pending).Throw();
    }

    // RETURN / LOOP reaching a TRY (i.e. not consumed by a nested call or loop) are VFP runtime errors.
    private Exception AsTryError(Exception ex, int line)
    {
        Exception e = ex switch
        {
            ReturnSignal => new MicroVfpRuntimeException("RETURN/RETRY statement not allowed in TRY/CATCH.", 2060),
            LoopSignal => new MicroVfpRuntimeException("WITH/ENDWITH mismatch.", 1939),
            _ => ex,
        };
        StampFault(e, line);
        return e;
    }

    // Inside CATCH: AERROR() reads the caught error, MESSAGE() returns its text, ERROR() stays 0 (VFP9).
    private void EnterCatchState(Exception caught, VfpErrorObject obj)
    {
        if (IsUserThrown(caught))
        {
            Runtime.LastErrorNumber = 3071;
            Runtime.LastErrorMessage = "Unknown error code 2071";
            Runtime.LastErrorDetail = obj.Describe();
        }
        else
        {
            Runtime.LastErrorNumber = obj.ErrorNo;
            Runtime.LastErrorMessage = obj.Message;
            Runtime.LastErrorDetail = obj.Details.Length > 0 ? obj.Details : null;
        }
        Runtime.LastErrorArea = 0;
        Runtime.LastErrorTrigger = 0;
        Runtime.LastErrorField = 0;
        _errNo = 0;
        _errMsg = Runtime.LastErrorMessage ?? string.Empty;
    }

    private void ExecThrow(ThrowStmt th)
    {
        if (th.Value is null && _catchStack.Count > 0)
            ExceptionDispatchInfo.Capture(_catchStack[^1]).Throw();
        var user = th.Value is null ? VfpValue.Character(string.Empty) : Eval(th.Value);
        // A user THROW is error 2071 carrying the value; uncaught it reaches the host as an ordinary
        // MicroVfpRuntimeException.
        throw new MicroVfpRuntimeException("User Thrown Error .", UserThrownErrorNumber) { UserValue = user, LineNo = th.Line };
    }

    private static bool IsUserThrown(Exception ex) => ex is MicroVfpRuntimeException { UserValue: not null };

    // ─────────────────────────── the Exception object (CATCH TO oErr) ───────────────────────────
    //
    // microVFP has no general object model; the caught Exception object is the one object a .prg can hold.
    // It lives in a memvar as an opaque reference string (a fresh instance per object, so the weak table
    // keys on reference identity and the object dies with the last memvar that holds it). VARTYPE()/TYPE()
    // report "O"; `oErr.Prop` (and `oErr.UserValue.Prop` for a wrapped THROW) resolves through ResolveName.

    private const string ObjectRefPrefix = "\0microvfp.object#";
    private readonly ConditionalWeakTable<string, VfpErrorObject> _objects = new();
    private int _objectSeq;

    private VfpValue NewObjectRef(VfpErrorObject obj)
    {
        string key = ObjectRefPrefix + (++_objectSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _objects.Add(key, obj);
        return VfpValue.Character(key);
    }

    internal bool TryGetObject(VfpValue v, out VfpErrorObject obj)
    {
        obj = null!;
        return _objectSeq > 0 && v.Type == VfpType.Character && v.AsString.StartsWith(ObjectRefPrefix, StringComparison.Ordinal)
            && _objects.TryGetValue(v.AsString, out obj!);
    }

    /// <summary>Resolve <c>head.rest</c> when <paramref name="head"/> is a memvar holding an object.</summary>
    private bool TryResolveObjectMember(string head, string rest, out object? value)
    {
        value = null;
        if (_objectSeq == 0 || !Memory.IsDefined(head)) return false;
        var cur = Memory.Get(head);
        if (!TryGetObject(cur, out var first)) return false;
        VfpErrorObject? obj = first;
        foreach (var prop in rest.Split('.'))
        {
            if (obj is null) throw new MicroVfpRuntimeException("Object is not found.", 1923);
            cur = obj.Get(prop);
            obj = TryGetObject(cur, out var inner) ? inner : null;
        }
        value = cur.ToClr();
        return true;
    }

    internal sealed class VfpErrorObject
    {
        public int ErrorNo { get; }
        public string Message { get; }
        public int LineNo { get; }
        public string Details { get; }
        public string Procedure { get; }
        public int StackLevel { get; }
        public VfpValue UserValue { get; }
        public string LineContents { get; }

        public VfpErrorObject(Exception ex, VfpInterpreter it)
        {
            ErrorNo = ErrorNumberOf(ex);
            Message = ex.Message ?? string.Empty;
            LineNo = ex.Data[DataLine] is int l ? l : 0;
            Procedure = (ex.Data[DataProc] as string ?? it.CurrentCall.Name).ToLowerInvariant();
            StackLevel = ex.Data[DataLevel] is int s ? s : it._callStack.Count;
            Details = string.Empty;
            LineContents = ex.Data[DataSource] as string ?? string.Empty;
            UserValue = ex is MicroVfpRuntimeException { UserValue: { } uv } ? uv : VfpValue.Character(string.Empty);
        }

        public VfpValue Get(string prop) => prop.Trim().ToUpperInvariant() switch
        {
            "ERRORNO" => VfpValue.Integer(ErrorNo),
            "MESSAGE" => VfpValue.Character(Message),
            "LINENO" => VfpValue.Integer(LineNo),
            "LINECONTENTS" => VfpValue.Character(LineContents),
            "DETAILS" => VfpValue.Character(Details),
            "PROCEDURE" => VfpValue.Character(Procedure),
            "STACKLEVEL" => VfpValue.Integer(StackLevel),
            "USERVALUE" => UserValue,
            "CLASS" or "BASECLASS" => VfpValue.Character("Exception"),
            _ => throw new MicroVfpRuntimeException($"Property {prop.Trim().ToUpperInvariant()} is not found.", 1734),
        };

        /// <summary>AERROR()[3] after a user THROW — the object's property dump (VFP9 layout, CRLF-separated).</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("ErrorNo: ").Append(ErrorNo).Append("\r\n");
            sb.Append("Message: ").Append(Message).Append("\r\n");
            sb.Append("UserValue: ").Append(UserValue.Type == VfpType.Character ? UserValue.AsString : FormatScalar(UserValue)).Append("\r\n");
            sb.Append("Details: ").Append(Details).Append("\r\n");
            sb.Append("Procedure: ").Append(Procedure).Append("\r\n");
            sb.Append("LineNo: ").Append(LineNo);
            return sb.ToString();
        }

        private static string FormatScalar(VfpValue v) => v.Type switch
        {
            VfpType.Numeric or VfpType.Integer or VfpType.Currency => v.AsNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VfpType.Logical => v.AsLogical ? ".T." : ".F.",
            VfpType.Null => ".NULL.",
            _ => v.AsString,
        };
    }
}
