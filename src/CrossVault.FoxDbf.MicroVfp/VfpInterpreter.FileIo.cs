using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P3 — the LOW-LEVEL FILE I/O family (MICROVFP_EXTENSIONS_BACKLOG §C.2) plus DBUSED (§C.5).
//
//  A per-interpreter handle table (int → FileStream) backs FOPEN/FCREATE/…/FCHSIZE. Handles are:
//    • numbered from 1, lowest-available-first, reusing a freed slot (VFP's own base 16 is runtime-internal
//      and never compared — ours is a stable microVFP convention: valid ≥ 1, never 0, never the -1 sentinel);
//    • INDEPENDENT of work areas — a table USE / USE-close never touches them;
//    • swept by `CLOSE ALL` (hackfox s4g194) and by interpreter/session dispose (so no OS handle leaks —
//      the file becomes deletable immediately after either).
//
//  Byte-orientation: I/O is BYTE-based under the session code page (Latin1 default) — no BOM, no newline
//  translation beyond the documented FGETS (strips CR / LF / CRLF terminator) and FPUTS (appends CRLF).
//  Return/error conventions are pinned to the VFP9 runtime (probed authoritatively): the per-function
//  invalid-handle sentinels and the separate FERROR() DOS-code channel (0 reset by every successful op;
//  2 not-found, 5 access-denied, 6 invalid-handle) that — unlike normal VFP errors — never trips ON ERROR.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class VfpInterpreter : IDisposable
{
    // handle → open stream. NOT part of InterpSessionState: low-level handles are global to the interpreter
    // (one VFP session), independent of the SET DATASESSION work-area bundle.
    private readonly Dictionary<int, FileStream> _llFiles = new();

    // FERROR() — the last low-level file operation's DOS error code (0 = no error). Its own channel, NOT the
    // ON ERROR / AERROR() one (hackfox s4g194: low-level I/O reports codes instead of raising trappable errors).
    private int _fError;

    /// <summary>Byte↔string code page for low-level I/O (session encoding; Latin1 by default — 1:1 for 0..255).</summary>
    private Encoding LlEncoding => _ctx.Encoding ?? Encoding.Latin1;

    // Lowest-available handle from 1 (reuses a freed slot, like VFP reusing a closed handle number).
    private int AllocHandle()
    {
        int h = 1;
        while (_llFiles.ContainsKey(h)) h++;
        return h;
    }

    // Map a .NET I/O exception to the VFP DOS-style FERROR() code (probed: missing file → 2, denied/bad-dir/
    // read-only-write → 5). Invalid-handle (6) is detected before any stream op, not via this mapper.
    private static int MapFError(Exception ex) => ex switch
    {
        FileNotFoundException => 2,
        DirectoryNotFoundException => 5,
        UnauthorizedAccessException => 5,
        NotSupportedException => 5,
        _ => 5,
    };

    /// <summary>Closes every open low-level handle (idempotent). Invoked by <c>CLOSE ALL</c>, by
    /// <see cref="Dispose"/>, and by the session's Disposing hook — so no OS handle outlives the interpreter
    /// or its session.</summary>
    private void CloseAllLowLevelHandles()
    {
        if (_llFiles.Count == 0) return;
        foreach (var fs in _llFiles.Values)
        {
            try { fs.Dispose(); } catch { /* best-effort OS handle release */ }
        }
        _llFiles.Clear();
    }

    /// <summary>Releases the interpreter's low-level file handles. Callers usually dispose the bound
    /// <see cref="CrossVault.FoxDbf.Sql.VfpSession"/> (the codebase lifecycle owner, which also triggers this) — this makes the
    /// interpreter itself deterministically releasable too, so a held file is deletable right afterwards.</summary>
    public void Dispose() { CloseAllLowLevelHandles(); DeleteSnapshotTempDir(); }

    // CLOSE ALL additionally sweeps low-level handles (hackfox s4g194). Other CLOSE forms
    // (DATABASES/TABLES/INDEXES) do NOT touch them; work-area/database closing stays as it was.
    private void ExecCloseCommand(string args)
    {
        string what = PrgScan.FirstWord(args);
        if (string.Equals(what, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            CloseAllLowLevelHandles();
            CloseAllWorkAreas();
        }
        // CLOSE TABLES [ALL] / CLOSE DATABASES [ALL] — close every open table of the current data session (it
        // used to be a silent no-op). The session's DBC attachment itself is the host's and stays open.
        else if (what is "TABLES" or "DATABASES")
            CloseAllWorkAreas();
        // CLOSE INDEXES — close every NON-structural index of the current work area (standalone .idx +
        // non-structural .cdx); the structural .cdx (auto-opened by USE) is untouched (hackfox s4g792).
        // Identical effect to SET INDEX TO with no files.
        else if (string.Equals(what, "INDEXES", StringComparison.OrdinalIgnoreCase)
              || string.Equals(what, "INDEX", StringComparison.OrdinalIgnoreCase))
            CloseNonStructuralIndexes(Session.CurrentArea);
    }

    // Close every work area of the ACTIVE data session exactly as `USE IN n` does (locks released first, then
    // the area, its meta and any cached writer that no longer has an area), then select work area 1 (VFP).
    private void CloseAllWorkAreas()
    {
        foreach (var wa in Session.OpenAreas.ToList())
        {
            ReleaseAreaLocks(wa.Area);
            Session.CloseArea(wa.Area);
            _meta.Remove(wa.Area);
        }
        PruneCachedWriters();
        Session.SelectArea(1);
    }

    // Only ReadOnly/Hidden/System are meaningful for FCREATE's nAttribute (0 = normal). Best-effort — the
    // returned handle stays writable regardless (the attribute lives on the directory entry, not the handle).
    private static void ApplyFileAttributes(string path, int attr)
    {
        FileAttributes fa = 0;
        if ((attr & 1) != 0) fa |= FileAttributes.ReadOnly;
        if ((attr & 2) != 0) fa |= FileAttributes.Hidden;
        if ((attr & 4) != 0) fa |= FileAttributes.System;
        if (fa == 0) return;
        try { File.SetAttributes(path, fa); } catch { /* best-effort attribute set */ }
    }

    // ─────────────────────────── FOPEN / FCREATE ───────────────────────────

    private VfpValue FnFOpen(VfpValue[] a)
    {
        if (a.Length < 1) { _fError = 2; return VfpValue.Integer(-1); }
        string path = a[0].AsString;
        int mode = a.Length > 1 ? (int)a[1].AsNumber : 0;
        int m = mode >= 10 ? mode - 10 : mode;   // 10/11/12 = unbuffered variants of 0/1/2 (buffering is a no-op here)
        FileAccess access = m switch { 1 => FileAccess.Write, 2 => FileAccess.ReadWrite, _ => FileAccess.Read };
        try
        {
            var fs = new FileStream(path, FileMode.Open, access, FileShare.ReadWrite);
            int h = AllocHandle();
            _llFiles[h] = fs;
            _fError = 0;
            return VfpValue.Integer(h);
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(-1); }
    }

    private VfpValue FnFCreate(VfpValue[] a)
    {
        if (a.Length < 1) { _fError = 2; return VfpValue.Integer(-1); }
        string path = a[0].AsString;
        int attr = a.Length > 1 ? (int)a[1].AsNumber : 0;
        try
        {
            // FCREATE ignores SET SAFETY — it silently truncates/overwrites an existing file (backlog: the
            // SAFETY bypass is exact VFP behaviour, deliberately NOT coupled to the SET SAFETY state).
            var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            int h = AllocHandle();
            _llFiles[h] = fs;
            ApplyFileAttributes(path, attr);
            _fError = 0;
            return VfpValue.Integer(h);
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(-1); }
    }

    // ─────────────────────────── FCLOSE ───────────────────────────

    private VfpValue FnFClose(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Logical(false); }
        _llFiles.Remove(h);
        try { fs.Flush(); fs.Dispose(); _fError = 0; return VfpValue.Logical(true); }
        catch (Exception ex) { try { fs.Dispose(); } catch { } _fError = MapFError(ex); return VfpValue.Logical(false); }
    }

    // ─────────────────────────── FREAD / FGETS ───────────────────────────

    private VfpValue FnFRead(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        int n = a.Length > 1 ? (int)a[1].AsNumber : 0;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Character(string.Empty); }
        if (n <= 0) { _fError = 0; return VfpValue.Character(string.Empty); }
        try
        {
            byte[] buf = new byte[n];
            int total = 0;
            while (total < n)
            {
                int got = fs.Read(buf, total, n - total);
                if (got <= 0) break;                        // EOF → fewer bytes than requested
                total += got;
            }
            _fError = 0;
            return VfpValue.Character(LlEncoding.GetString(buf, 0, total));
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Character(string.Empty); }
    }

    private VfpValue FnFGets(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        int limit = a.Length > 1 ? (int)a[1].AsNumber : 254;   // default 254 (hackfox s4g194) — easy to miss
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Character(string.Empty); }
        if (limit < 0) limit = 0;
        try
        {
            var buf = new List<byte>(Math.Min(limit, 256));
            while (buf.Count < limit)
            {
                int b = fs.ReadByte();
                if (b < 0) break;                            // EOF
                if (b == 13)                                 // CR terminator (strip); pair a following LF
                {
                    int nb = fs.ReadByte();
                    if (nb >= 0 && nb != 10) fs.Seek(-1, SeekOrigin.Current);   // lone CR → leave the next byte
                    break;
                }
                if (b == 10) break;                          // LF terminator (strip)
                buf.Add((byte)b);
            }
            _fError = 0;
            return VfpValue.Character(LlEncoding.GetString(buf.ToArray()));
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Character(string.Empty); }
    }

    // ─────────────────────────── FWRITE / FPUTS ───────────────────────────

    private VfpValue FnFWrite(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        string s = a.Length > 1 ? a[1].AsString : string.Empty;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Integer(0); }
        if (!fs.CanWrite) { _fError = 5; return VfpValue.Integer(0); }
        if (a.Length > 2)
        {
            int cap = (int)a[2].AsNumber;
            if (cap < 0) cap = 0;
            if (cap < s.Length) s = s.Substring(0, cap);     // nBytes CAPS the string (never pads)
        }
        try
        {
            byte[] bytes = LlEncoding.GetBytes(s);
            fs.Write(bytes, 0, bytes.Length);
            _fError = 0;
            return VfpValue.Integer(bytes.Length);
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(0); }
    }

    private VfpValue FnFPuts(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        string s = a.Length > 1 ? a[1].AsString : string.Empty;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Integer(0); }
        if (!fs.CanWrite) { _fError = 5; return VfpValue.Integer(0); }
        if (a.Length > 2)
        {
            int cap = (int)a[2].AsNumber;
            if (cap < 0) cap = 0;
            if (cap < s.Length) s = s.Substring(0, cap);     // CAP only (no pad), THEN append CRLF
        }
        try
        {
            byte[] body = LlEncoding.GetBytes(s);
            fs.Write(body, 0, body.Length);
            fs.WriteByte(13);
            fs.WriteByte(10);
            _fError = 0;
            return VfpValue.Integer(body.Length + 2);        // return count INCLUDES the appended CRLF
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(0); }
    }

    // ─────────────────────────── FSEEK / FEOF ───────────────────────────

    private VfpValue FnFSeek(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        long off = a.Length > 1 ? (long)a[1].AsNumber : 0;
        int rel = a.Length > 2 ? (int)a[2].AsNumber : 0;      // 0 begin, 1 current, 2 end
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Integer(0); }
        SeekOrigin origin = rel switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin };
        try
        {
            long pos = fs.Seek(off, origin);                 // seeking past EOF is allowed (returns the offset)
            _fError = 0;
            return VfpValue.Number((decimal)pos);
        }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(-1); }
    }

    private VfpValue FnFEof(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        // Documented VFP bug (hackfox s4g194): an invalid/closed handle yields .T. and sets NO low-level
        // error — so FEOF deliberately does NOT touch _fError, on the valid path either.
        if (!_llFiles.TryGetValue(h, out var fs)) return VfpValue.Logical(true);
        try { return VfpValue.Logical(fs.Position >= fs.Length); }
        catch { return VfpValue.Logical(true); }
    }

    // ─────────────────────────── FERROR / FFLUSH / FCHSIZE ───────────────────────────

    private VfpValue FnFError() => VfpValue.Integer(_fError);   // pure read — never resets (only ops reset it)

    private VfpValue FnFFlush(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Logical(false); }
        try { fs.Flush(true); _fError = 0; return VfpValue.Logical(true); }
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Logical(false); }
    }

    private VfpValue FnFChSize(VfpValue[] a)
    {
        int h = a.Length > 0 ? (int)a[0].AsNumber : -1;
        long size = a.Length > 1 ? (long)a[1].AsNumber : 0;
        if (!_llFiles.TryGetValue(h, out var fs)) { _fError = 6; return VfpValue.Integer(-1); }
        if (!fs.CanWrite) { _fError = 5; return VfpValue.Integer(-1); }
        if (size < 0) { _fError = 5; return VfpValue.Integer(-1); }
        try { fs.SetLength(size); _fError = 0; return VfpValue.Number((decimal)size); }   // extend zero-fills
        catch (Exception ex) { _fError = MapFError(ex); return VfpValue.Integer(-1); }
    }

    // ─────────────────────────── DBUSED (§C.5) ───────────────────────────

    // Is the named DBC open? A BARE name (no path, no extension) matches the open container's base name
    // (case-insensitive). Anything with a path or an extension is fully-qualified against the CURRENT
    // directory and compared to the open container's full path — which faithfully reproduces the documented
    // VFP9 bug (hackfox s4g422): "tastrade.dbc" (extension, no path) returns .F. when the DBC lives elsewhere,
    // while a bare "tastrade" or the full path returns .T. Single-DBC session model (see the SET DATABASE item).
    private bool FnDbUsed(VfpValue[] a)
    {
        if (a.Length < 1 || Session.Database is null) return false;
        string? dbPath = Session.DatabasePath;
        if (string.IsNullOrEmpty(dbPath)) return false;
        string arg = a[0].AsString;
        if (string.IsNullOrWhiteSpace(arg)) return false;
        bool hasDir = arg.IndexOf('\\') >= 0 || arg.IndexOf('/') >= 0 || Path.IsPathRooted(arg);
        bool hasExt = Path.HasExtension(arg);
        if (!hasDir && !hasExt)
            return string.Equals(arg, Path.GetFileNameWithoutExtension(dbPath), StringComparison.OrdinalIgnoreCase);
        try
        {
            return string.Equals(Path.GetFullPath(arg), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
