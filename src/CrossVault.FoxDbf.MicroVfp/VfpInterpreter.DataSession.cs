using System;
using System.Collections.Generic;
using System.Text;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP finding 5.14 — the REAL multi-DATA-SESSION model.
//
//  A DATA SESSION is an isolated set of work areas PLUS its own session-scoped SET/evaluation state. The
//  VfpSession owns the swappable WORK-AREA half (areas, aliases, current area, cursor temp tables, the
//  EvaluationContext = SET DELETED/EXACT/ANSI/COLLATE…). The interpreter owns the swappable RUNTIME half
//  (per-area record pointers/orders/relations/buffering/LOCATE via _meta, byte-range locks via _areaLocks +
//  _cachedWriters, PRG transactions via _txn, and the per-session SET NEAR/AUTOSAVE flags). Both halves are
//  keyed by the SAME data-session id and switched IN LOCKSTEP by SwitchDataSession, so every existing code
//  path (USE/SELECT/SEEK/DML/SQL/buffering/locks) keeps operating on the ACTIVE session unchanged — no
//  session id is threaded through call sites.
//
//  Session CREATION surface: microVFP has no form model, so the .NET embedder calls the host API
//  CreateDataSession() (the equivalent of a private-session form with DataSession=2) / ReleaseDataSession(n).
//  A .prg cannot create one (there is no PRG command that does — matching VFP); SET DATASESSION only SWITCHES.
//  (DEFINE CLASS … AS Form WITH DataSession=2 is NOT modelled — microVFP has no class/form model. FLAGGED.)
//
//  SHARED (NOT per-session): the loaded procedures/#DEFINEs, the parse/expression caches, the ON ERROR
//  handler + live/last error state (ERROR()/MESSAGE()/AERROR are global in VFP), the data-source binding,
//  and the candidate-tag registry (path-keyed, a documented free-table limitation). The per-session SETs
//  microVFP scopes are DELETED/EXACT/ANSI/COLLATE/NULL/NEAR/AUTOSAVE plus REPROCESS/UNIQUE/MULTILOCKS (5.14
//  MUST-FIX: those three are DATA-SESSION-scoped in VFP9 — a fresh private session reads the defaults
//  REPROCESS=0/UNIQUE=OFF/MULTILOCKS=OFF regardless of another session's mutations, and its own mutations do
//  not leak back). REPROCESS/UNIQUE/MULTILOCKS keep their LIVE copies on RuntimeState (so RuntimeState's
//  LockFailFast + the public Runtime API stay computed from the ACTIVE session's value) but are SAVED/RESTORED
//  per session by Save/LoadActiveInterpState below — the same swap the other session-scoped state rides.
//  DELETED/EXACT (and UNIQUE) are oracle-pinned session-scoped in MicroVfpDataSessionModelOracleTests.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class VfpInterpreter
{
    /// <summary>The interpreter-side per-data-session runtime state, swapped in lockstep with the VfpSession
    /// work-area bundle. The ACTIVE session's members ARE the live fields (<see cref="_meta"/> etc.); a switch
    /// saves the active scalars back here and re-points the live fields at the target session's state.</summary>
    private sealed class InterpSessionState
    {
        public Dictionary<int, AreaMeta> Meta = new();
        public Dictionary<int, AreaLockSet> AreaLocks = new();
        public Dictionary<string, DbfWriter> CachedWriters = new(StringComparer.OrdinalIgnoreCase);
        public List<TxnFrame> Txn = new();
        public bool SetNear;      // SET NEAR (failed-SEEK pointer parking) is per data session.
        public bool SetAutosave;  // SET AUTOSAVE feeds SET("AUTOSAVE"); per data session.
        // 5.14 MUST-FIX — SET REPROCESS/UNIQUE/MULTILOCKS are per data session (a fresh private session starts
        // at the VFP defaults 0/false/false, which ARE these fields' default values). Their LIVE copies ride
        // RuntimeState (Runtime.Reprocess/.Unique/.Multilocks) so RuntimeState.LockFailFast keeps computing
        // from the ACTIVE session's value; this is their per-session backing, swapped by Save/LoadActiveInterpState.
        public int Reprocess;     // SET REPROCESS TO n (AUTOMATIC = -2); backs Runtime.Reprocess.
        public bool Unique;       // SET UNIQUE (clause-less INDEX default); backs Runtime.Unique.
        public bool Multilocks;   // SET MULTILOCKS (record-lock accumulation); backs Runtime.Multilocks.
    }

    // Every data session's interpreter-side state, INCLUDING the active one (whose members alias the live
    // fields). Keyed by the same id as the VfpSession registry.
    private readonly Dictionary<int, InterpSessionState> _interpSessions = new();

    /// <summary>
    /// HOST API (5.14) — create a new PRIVATE data session (microVFP's headless equivalent of a form with
    /// <c>DataSession=2</c>) and return its id (2, 3, …). The new session starts with its OWN empty work-area
    /// set and DEFAULT (microVFP-baseline) SET state — NOT a copy of the current session's settings. Does NOT
    /// switch to it; issue <c>SET DATASESSION TO &lt;id&gt;</c> (or the return value) to activate it.
    /// </summary>
    public int CreateDataSession()
    {
        int id = Session.CreateDataSession(FreshSessionContext());
        _interpSessions[id] = new InterpSessionState();
        return id;
    }

    /// <summary>
    /// HOST API (5.14) — release the private data session <paramref name="id"/>: close its work areas
    /// (disposing the cached writers releases every byte-range lock those areas held, so a record another
    /// session was denied becomes lockable again), delete its cursor temp tables, and remove its id from
    /// <c>ASESSIONS()</c>. Releasing the CURRENT session first falls back to the default session 1 (VFP: the
    /// caller's session becomes active again). Session 1 is permanent and cannot be released.
    /// </summary>
    public void ReleaseDataSession(int id)
    {
        if (id == 1)
            throw new ArgumentException("The default data session (1) cannot be released.", nameof(id));
        if (!Session.HasDataSession(id))
            throw new ArgumentException($"Data session {id} does not exist.", nameof(id));
        // Releasing the CURRENT session: fall back to the default session 1 first, so we are not standing on
        // the session we are about to drop (its state then lives only in _interpSessions[id], dropped below).
        if (Session.CurrentDataSessionId == id) SwitchDataSession(1);
        if (_interpSessions.Remove(id, out var st)) DropInterpSession(st);
        Session.ReleaseDataSession(id);
    }

    /// <summary>Switch the active data session to <paramref name="id"/> (both halves, in lockstep). The caller
    /// (<see cref="SetDataSession"/>) has already validated the id exists.</summary>
    private void SwitchDataSession(int id)
    {
        int cur = Session.CurrentDataSessionId;
        if (id == cur) return;
        // Batch 4: settle the OUTGOING session's deferred fast-append refreshes while its cached writers +
        // work areas are still the live ones (the pending-paths set is not part of the per-session bundle).
        if (_pendingAppendPaths is { Count: > 0 }) FlushPendingAppends();
        SaveActiveInterpState(cur);        // stash the outgoing session's scalars (maps are shared references).
        Session.SwitchDataSession(id);     // swap the work-area bundle + the session's EvaluationContext.
        LoadActiveInterpState(id);         // re-point the live runtime fields at the target session's state.
        _ctx = Session.Context;
        _ctx.IndexEvaluator = EvaluateIndex;
        _ctx.NormalizeIndexExpression = MicroVfpExprRewrite.Normalize;
        _ctx.ValidateIndexFunction = ValidateIndexFunction;            // mirror the now-active session's SET DELETED/EXACT/… context.
    }

    // Save the active session's SCALAR runtime state back into its record (the maps/list are the SAME
    // references, so no copy is needed for them).
    private void SaveActiveInterpState(int id)
    {
        if (!_interpSessions.TryGetValue(id, out var st)) return;
        st.Meta = _meta; st.AreaLocks = _areaLocks; st.CachedWriters = _cachedWriters; st.Txn = _txn;
        st.SetNear = _setNear; st.SetAutosave = _setAutosave;
        // 5.14 MUST-FIX: stash the ACTIVE session's SET REPROCESS/UNIQUE/MULTILOCKS (they live on Runtime, which
        // mirrors the active session) so switching away preserves them per session.
        st.Reprocess = Runtime.Reprocess; st.Unique = Runtime.Unique; st.Multilocks = Runtime.Multilocks;
    }

    // Re-point the live runtime fields at data session <paramref name="id"/>'s state. Tolerant of a session
    // created directly on the VfpSession (bypassing the host API): lazily materialise its interpreter state.
    private void LoadActiveInterpState(int id)
    {
        if (!_interpSessions.TryGetValue(id, out var st)) st = _interpSessions[id] = new InterpSessionState();
        _meta = st.Meta; _areaLocks = st.AreaLocks; _cachedWriters = st.CachedWriters; _txn = st.Txn;
        _setNear = st.SetNear; _setAutosave = st.SetAutosave;
        // 5.14 MUST-FIX: re-point Runtime's SET REPROCESS/UNIQUE/MULTILOCKS at the target session's values (VFP
        // defaults 0/false/false for a freshly created session), so RuntimeState.LockFailFast + SET("…") readbacks
        // and the clause-less INDEX/MULTILOCKS behaviour all follow the now-active session.
        Runtime.Reprocess = st.Reprocess; Runtime.Unique = st.Unique; Runtime.Multilocks = st.Multilocks;
    }

    // Drop a released session's interpreter state: dispose its cached writers (this releases the OS byte-range
    // locks they held) and forget its bookkeeping. The VfpSession disposes the work-area TABLE handles.
    private static void DropInterpSession(InterpSessionState st)
    {
        foreach (var w in st.CachedWriters.Values)
        {
            try { w.Dispose(); } catch { /* best-effort — Dispose releases held OS byte-range locks */ }
        }
        st.CachedWriters.Clear();
        st.AreaLocks.Clear();
        st.Meta.Clear();
        st.Txn.Clear();
    }

    // Dispose EVERY data session's cached writers — the Session.Disposing hook (callers dispose the session,
    // not the interpreter), so no .dbf stays locked past the session's life across ANY data session.
    private void DisposeCachedWritersAllSessions()
    {
        foreach (var st in _interpSessions.Values)
        {
            foreach (var w in st.CachedWriters.Values)
            {
                try { w.Dispose(); } catch { /* best-effort */ }
            }
            st.CachedWriters.Clear();
        }
    }

    /// <summary>The SET/evaluation context a NEW data session starts with: the microVFP BASELINE defaults
    /// (SET DELETED ON, SET EXACT OFF, Latin1) — the same baseline the ctor applies to session 1, and NOT a
    /// copy of the current session's (possibly mutated) settings. Consistent per-session start point; the
    /// oracle test pins that DELETED/EXACT are session-SCOPED (isolated), which is the load-bearing fact.</summary>
    private static EvaluationContext FreshSessionContext()
    {
        var c = new EvaluationContext { Exact = false, Deleted = true, VfpBlankFields = true };
        c.Encoding ??= Encoding.Latin1;
        return c;
    }
}
