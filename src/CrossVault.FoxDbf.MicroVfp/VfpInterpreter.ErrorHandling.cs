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
        string msg = number switch
        {
            UnrecognizedVerbErrorNumber => "Unrecognized command verb.",
            _ => "Command contains unrecognized phrase/keyword."
        };
        return new MicroVfpRuntimeException(msg, number) { Statement = text, LineNo = uc.Line };
    }

    /// <summary>Raise the SQL parser's own error for <paramref name="sql"/> (or a plain syntax error 10 when it
    /// parses but is not the expected statement). Typed as returning a statement so it composes with <c>??</c>.</summary>
    private static SqlStatement ThrowSqlParseError(string sql)
    {
        try { MicroVfpExprRewrite.ParseSql(sql.Trim()); }
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
        catch (ExpressionException ex) when (ex.VfpErrorNumber == 1231)
        {
            throw new MicroVfpRuntimeException(ex.Message, 1231) { Statement = text.Trim() };
        }
        catch (ExpressionException ex) when (ex.VfpErrorNumber is null)
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
        Runtime.LastErrorArea = caught is MicroVfpRuntimeException { TriggerType: > 0 } trigger
            ? trigger.TriggerArea : 0;
        Runtime.LastErrorTrigger = caught is MicroVfpRuntimeException ri ? ri.TriggerType : 0;
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

    // ─────────────────────────── unknown functions ───────────────────────────

    /// <summary>
    /// A call nobody implements (VFP9-verified). A plain name: VFP looks for a program file of that name ⇒ error 1
    /// "File 'name.prg' does not exist." (lower-cased, never truncated; also with a .NULL. argument, through
    /// EVALUATE() and a macro; an undefined <c>x(1)</c> reports 'x.prg'). A method call <c>obj.m()</c> on the
    /// Exception object: a property ignores the subscript (<c>oErr.Message(1)</c> is the message), anything else
    /// is 1734 "Property M is not found."; on a defined non-object name 1924 "X is not an object.", otherwise 1923
    /// "Object X is not found.".
    /// </summary>
    internal VfpValue UnknownFunction(string upperName)
    {
        int dot = upperName.IndexOf('.');
        if (dot < 0)
        {
            string prg = upperName.ToLowerInvariant() + ".prg";
            throw new MicroVfpRuntimeException($"File '{prg}' does not exist.", 1) { Details = prg };
        }
        string head = upperName[..dot];
        string member = upperName[(upperName.LastIndexOf('.') + 1)..];
        if (Memory.IsDefined(head) && TryGetObject(Memory.Get(head), out var obj))
        {
            try { return obj.Get(member); }
            catch (MicroVfpRuntimeException ex) when (ex.VfpErrorNumber == 1734)
            {
                throw new MicroVfpRuntimeException(ex.Message, 1734) { Details = member };
            }
        }
        throw IsDefinedName(head)
            ? new MicroVfpRuntimeException($"{head} is not an object.", 1924) { Details = head }
            : new MicroVfpRuntimeException($"Object {head} is not found.", 1923) { Details = head };
    }

    // ─────────────────────────── the Exception object (CATCH TO oErr) ───────────────────────────
    //
    // Record and Exception objects share property access and reference identity.
    // It lives in a memvar as an opaque reference string (a fresh instance per object, so the weak table
    // keys on reference identity and the object dies with the last memvar that holds it). VARTYPE()/TYPE()
    // report "O"; `oErr.Prop` (and `oErr.UserValue.Prop` for a wrapped THROW) resolves through ResolveName.

    private const string ObjectRefPrefix = "\0microvfp.object#";
    private readonly ConditionalWeakTable<string, VfpObject> _objects = new();
    private int _objectSeq;

    private VfpValue NewObjectRef(VfpObject obj)
    {
        string key = ObjectRefPrefix + (++_objectSeq).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _objects.Add(key, obj);
        return VfpValue.Character(key);
    }

    internal bool TryGetObject(VfpValue v, out VfpObject obj)
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
        VfpObject? obj = first;
        foreach (var prop in rest.Split('.'))
        {
            if (obj is null) throw new MicroVfpRuntimeException("Object is not found.", 1923);
            cur = obj.Get(prop);
            obj = TryGetObject(cur, out var inner) ? inner : null;
        }
        // Keep field types such as currency and varbinary across the expression host boundary.
        value = cur;
        return true;
    }

    internal abstract class VfpObject
    {
        internal Dictionary<string, VfpValue> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public abstract VfpValue Get(string property);
        public abstract void Set(string property, VfpValue value);
    }

    private sealed class VfpRecordObject : VfpObject
    {
        public override VfpValue Get(string property) => Fields.TryGetValue(property, out var value) ? value
            : throw new MicroVfpRuntimeException($"Property {property.ToUpperInvariant()} is not found.", 1734);
        public override void Set(string property, VfpValue value)
        {
            Get(property); // Assignment cannot invent a property (VFP error 1734).
            Fields[property] = value;
        }
    }

    internal sealed class VfpErrorObject : VfpObject
    {
        public int ErrorNo => (int)Get("ErrorNo").AsNumber;
        public string Message => Get("Message").AsString;
        public int LineNo => (int)Get("LineNo").AsNumber;
        public string Details => Get("Details").AsString;
        public VfpValue UserValue => Get("UserValue");

        public VfpErrorObject(Exception ex, VfpInterpreter it)
        {
            Fields["ErrorNo"] = VfpValue.Integer(ErrorNumberOf(ex));
            Fields["Message"] = VfpValue.Character(ex.Message ?? string.Empty);
            Fields["LineNo"] = VfpValue.Integer(ex.Data[DataLine] is int l ? l : 0);
            Fields["Procedure"] = VfpValue.Character((ex.Data[DataProc] as string ?? it.CurrentCall.Name).ToLowerInvariant());
            Fields["StackLevel"] = VfpValue.Integer(ex.Data[DataLevel] is int s ? s : it._callStack.Count);
            Fields["Details"] = VfpValue.Character(ex is MicroVfpRuntimeException { Details: { } d } ? d : string.Empty);
            Fields["LineContents"] = VfpValue.Character(ex.Data[DataSource] as string ?? string.Empty);
            Fields["UserValue"] = ex is MicroVfpRuntimeException { UserValue: { } uv } ? uv : VfpValue.Character(string.Empty);
        }

        public override void Set(string property, VfpValue value)
        {
            Get(property);
            if (!Fields.ContainsKey(property)) throw new MicroVfpRuntimeException("Property is read-only.", 1743);
            Fields[property] = value;
        }

        public override VfpValue Get(string property)
        {
            if (Fields.TryGetValue(property, out var value)) return value;
            if (property.Equals("Class", StringComparison.OrdinalIgnoreCase)
                || property.Equals("BaseClass", StringComparison.OrdinalIgnoreCase)) return VfpValue.Character("Exception");
            throw new MicroVfpRuntimeException($"Property {property.ToUpperInvariant()} is not found.", 1734);
        }

        /// <summary>AERROR()[3] after a user THROW — the object's property dump (VFP9 layout, CRLF-separated).</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("ErrorNo: ").Append(ErrorNo).Append("\r\n");
            sb.Append("Message: ").Append(Message).Append("\r\n");
            sb.Append("UserValue: ").Append(UserValue.Type == VfpType.Character ? UserValue.AsString : FormatScalar(UserValue)).Append("\r\n");
            sb.Append("Details: ").Append(Details).Append("\r\n");
            sb.Append("Procedure: ").Append(Get("Procedure").AsString).Append("\r\n");
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
