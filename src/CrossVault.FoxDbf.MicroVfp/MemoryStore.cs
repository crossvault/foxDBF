using System;
using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P1b — the MEMORY-VARIABLE store with VFP (dynamic) SCOPING.
//
//  Per MICROVFP_SEMANTICS.md §Scoping:
//   • PUBLIC   — global, lives until RELEASE.
//   • PRIVATE  — reserves the NAME and HIDES any same-named outer var for this
//                routine + everything it calls; the outer var is restored when
//                the routine returns.
//   • LOCAL    — visible only in the declaring routine; callees never see it.
//   • undeclared var first assigned in a routine ⇒ implicitly PRIVATE (callees
//                see it; released on routine exit).
//
//  Modelled as a STACK of call frames. Frame 0 is the global/PUBLIC + top-level
//  frame; each routine call pushes a frame. A name resolves DYNAMICALLY: walk
//  frames from the top (current routine) DOWN; a binding in the CURRENT frame is
//  visible whatever its kind; an ANCESTOR frame's binding is visible to the
//  callee ONLY when it is PRIVATE / implicit-PRIVATE / PUBLIC (a LOCAL is private
//  to its frame); the first visible binding wins (so a PRIVATE re-declaration in a
//  closer frame HIDES the outer one, and pops restore it).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The lifetime/visibility class of a memory variable (see <see cref="DeclScope"/> for the
/// declaration syntax; this is the resolved runtime kind, incl. the implicit-private default).</summary>
internal enum VarKind
{
    /// <summary>Undeclared-but-assigned: implicitly PRIVATE, visible to callees, released on exit.</summary>
    ImplicitPrivate,
    Local,
    Private,
    Public,
}

/// <summary>
/// A VFP memory ARRAY: 1-BASED, 1-D or 2-D. Elements default to <c>.F.</c>. A 2-D array may also be
/// addressed with a single linear (row-major) subscript, exactly as VFP allows. See MICROVFP_SEMANTICS.md
/// §Fehlerbehandlung (AERROR) + task gap 1.
/// </summary>
internal sealed class VfpArray
{
    private VfpValue[] _data;

    /// <summary>Row count (first dimension; <c>ALEN(a,1)</c>).</summary>
    public int Rows { get; private set; }

    /// <summary>Column count (second dimension; <c>ALEN(a,2)</c>); <c>0</c> for a 1-D array.</summary>
    public int Cols { get; private set; }

    /// <summary>Creates an array with the given dimensions (<paramref name="cols"/> &lt;= 0 ⇒ 1-D),
    /// every element initialised to <c>.F.</c>.</summary>
    public VfpArray(int rows, int cols)
    {
        Rows = Math.Max(1, rows);
        Cols = cols < 0 ? 0 : cols;
        _data = NewData(Rows, Cols);
    }

    private static VfpValue[] NewData(int rows, int cols)
    {
        var d = new VfpValue[Math.Max(1, rows) * Math.Max(1, cols)];
        for (int i = 0; i < d.Length; i++) d[i] = VfpValue.Logical(false);
        return d;
    }

    /// <summary>Total element count (<c>ALEN(a)</c>).</summary>
    public int Length => _data.Length;

    /// <summary>True for a 2-D array.</summary>
    public bool Is2D => Cols >= 1;

    /// <summary><c>ALEN(a[,n])</c>: n=1 ⇒ rows, n=2 ⇒ cols (0 for 1-D), anything else (incl. 0) ⇒ total.</summary>
    public int ALen(int dim) => dim switch { 1 => Rows, 2 => Is2D ? Cols : 0, _ => Length };

    private int Index(int sub1, int? sub2)
    {
        if (sub2 is int col)
        {
            if (!Is2D || sub1 < 1 || sub1 > Rows || col < 1 || col > Cols) return -1;
            return (sub1 - 1) * Cols + (col - 1);
        }
        return sub1 >= 1 && sub1 <= Length ? sub1 - 1 : -1;
    }

    /// <summary>Reads element <c>(sub1[,sub2])</c> (1-based); out-of-range ⇒ <c>.F.</c>.</summary>
    public VfpValue Get(int sub1, int? sub2)
    {
        int i = Index(sub1, sub2);
        return i >= 0 && i < _data.Length ? _data[i] : VfpValue.Logical(false);
    }

    /// <summary>The element used when the array appears in a SCALAR context — <c>(1,1)</c> for a 2-D
    /// array, <c>(1)</c> for a 1-D array (VFP semantics).</summary>
    public VfpValue First => Get(1, Is2D ? 1 : (int?)null);

    /// <summary>Writes element <c>(sub1[,sub2])</c> (1-based); out-of-range writes are ignored.</summary>
    public void Set(int sub1, int? sub2, VfpValue value)
    {
        int i = Index(sub1, sub2);
        if (i >= 0 && i < _data.Length) _data[i] = value;
    }

    /// <summary>Reads element <paramref name="n"/> (1-based) by LINEAR (row-major) position — the numbering
    /// ACOPY/ASORT/AELEMENT/ASUBSCRIPT use; out-of-range ⇒ <c>.F.</c>.</summary>
    public VfpValue GetLinear(int n) => n >= 1 && n <= _data.Length ? _data[n - 1] : VfpValue.Logical(false);

    /// <summary>Writes element <paramref name="n"/> (1-based) by LINEAR (row-major) position; out-of-range
    /// writes are ignored.</summary>
    public void SetLinear(int n, VfpValue value) { if (n >= 1 && n <= _data.Length) _data[n - 1] = value; }

    // ── ADEL / AINS element·row·column (size UNCHANGED; VFP fills/loses at the tail) ──────────────
    // hackfox s4g211: ADEL/AINS "don't affect the size of the array, only its contents". ADEL shifts
    // toward the front and .F.-fills the freed LAST slot; AINS shifts toward the back, .F.-fills the
    // inserted slot and DROPS the original last element/row/column (data loss — kept 1:1, not "fixed").
    // A 1-D array has no columns: a "row" is a single element and RowWidth is 1.
    private int RowWidth => Is2D ? Cols : 1;

    /// <summary>ADEL row/element: shift rows (or, for a 1-D array, elements) at/after <paramref name="row"/>
    /// one position toward the front; the freed last row is <c>.F.</c>-filled.</summary>
    public void DeleteRow(int row)
    {
        int w = RowWidth;
        if (row < 1 || row > Rows) return;
        for (int r = row; r < Rows; r++)
            for (int c = 0; c < w; c++) _data[(r - 1) * w + c] = _data[r * w + c];
        for (int c = 0; c < w; c++) _data[(Rows - 1) * w + c] = VfpValue.Logical(false);
    }

    /// <summary>AINS row/element: shift rows (or, for a 1-D array, elements) at/after <paramref name="row"/>
    /// one position toward the back; the inserted row is <c>.F.</c>-filled and the original last row is lost.</summary>
    public void InsertRow(int row)
    {
        int w = RowWidth;
        if (row < 1 || row > Rows) return;
        for (int r = Rows - 1; r >= row; r--)
            for (int c = 0; c < w; c++) _data[r * w + c] = _data[(r - 1) * w + c];
        for (int c = 0; c < w; c++) _data[(row - 1) * w + c] = VfpValue.Logical(false);
    }

    /// <summary>ADEL column (2-D only): in every row, shift columns at/after <paramref name="col"/> one
    /// position left; the freed last column is <c>.F.</c>-filled.</summary>
    public void DeleteColumn(int col)
    {
        if (!Is2D || col < 1 || col > Cols) return;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = col; c < Cols; c++) _data[r * Cols + (c - 1)] = _data[r * Cols + c];
            _data[r * Cols + (Cols - 1)] = VfpValue.Logical(false);
        }
    }

    /// <summary>AINS column (2-D only): in every row, shift columns at/after <paramref name="col"/> one
    /// position right; the inserted column is <c>.F.</c>-filled and the original last column is lost.</summary>
    public void InsertColumn(int col)
    {
        if (!Is2D || col < 1 || col > Cols) return;
        for (int r = 0; r < Rows; r++)
        {
            for (int c = Cols - 1; c >= col; c--) _data[r * Cols + c] = _data[r * Cols + (c - 1)];
            _data[r * Cols + (col - 1)] = VfpValue.Logical(false);
        }
    }

    /// <summary>DIMENSION / REDIMENSION: resize, PRESERVING existing elements by linear position and
    /// <c>.F.</c>-filling any growth.</summary>
    public void Redim(int rows, int cols)
    {
        var nd = NewData(rows, cols);
        Array.Copy(_data, nd, Math.Min(nd.Length, _data.Length));
        _data = nd;
        Rows = Math.Max(1, rows);
        Cols = cols < 0 ? 0 : cols;
    }
}

/// <summary>
/// The interpreter's memory-variable store with VFP dynamic scoping (a stack of call frames over a
/// global PUBLIC frame).
/// </summary>
internal sealed class MemoryStore
{
    private sealed class Cell
    {
        public VfpValue Value;
        public bool Defined;     // PRIVATE reserves the name but starts UNDEFINED ("U") until assigned.
        public VarKind Kind;
        public VfpArray? Array;  // non-null ⇒ this binding is a memory ARRAY (Value is then unused).
    }

    private sealed class Frame
    {
        public readonly Dictionary<string, Cell> Vars = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly List<Frame> _frames = new();

    /// <summary>Creates a store with the single global (frame 0) / top-level frame in place.</summary>
    public MemoryStore() => _frames.Add(new Frame());

    private Frame Current => _frames[^1];

    /// <summary>Enter a new call frame (one per routine invocation).</summary>
    public void PushFrame() => _frames.Add(new Frame());

    /// <summary>Leave the current call frame, releasing its LOCAL/implicit-PRIVATE bindings and
    /// restoring any outer vars its PRIVATE declarations hid.</summary>
    public void PopFrame()
    {
        if (_frames.Count <= 1) return; // never pop the global frame.
        _frames.RemoveAt(_frames.Count - 1);
    }

    /// <summary>The current depth (number of frames; 1 = only the global/top-level frame).</summary>
    public int Depth => _frames.Count;

    // ── resolution ────────────────────────────────────────────────────────────

    private Cell? Find(string name)
    {
        int cur = _frames.Count - 1;
        for (int f = cur; f >= 0; f--)
        {
            if (!_frames[f].Vars.TryGetValue(name, out var cell)) continue;
            if (f == cur) return cell;                  // current frame: any kind visible.
            if (cell.Kind == VarKind.Local) continue;   // ancestor LOCAL: invisible to callees.
            return cell;                                // ancestor PRIVATE / implicit / PUBLIC: visible.
        }
        return null;
    }

    // ── declarations ──────────────────────────────────────────────────────────

    /// <summary>Reserve <paramref name="name"/> in the current frame with the given lifetime
    /// (LOCAL / PRIVATE / PUBLIC). PRIVATE hides a same-named outer var for this frame + its callees.</summary>
    public void Declare(string name, VarKind kind)
    {
        switch (kind)
        {
            case VarKind.Public:
                // PUBLIC lives in the global frame; init .F. only when freshly created.
                if (!_frames[0].Vars.ContainsKey(name))
                    _frames[0].Vars[name] = new Cell { Value = VfpValue.Logical(false), Defined = true, Kind = VarKind.Public };
                break;
            case VarKind.Local:
                // LOCAL initialises to .F. in the current frame.
                Current.Vars[name] = new Cell { Value = VfpValue.Logical(false), Defined = true, Kind = VarKind.Local };
                break;
            case VarKind.Private:
                // PRIVATE reserves + hides: a frame-local binding that starts UNDEFINED.
                Current.Vars[name] = new Cell { Value = VfpValue.Null, Defined = false, Kind = VarKind.Private };
                break;
            default:
                Current.Vars[name] = new Cell { Value = VfpValue.Null, Defined = false, Kind = VarKind.ImplicitPrivate };
                break;
        }
    }

    /// <summary>Bind a parameter <paramref name="name"/> = <paramref name="value"/> in the current
    /// frame with the given kind (PARAMETERS → PRIVATE, LPARAMETERS / header params → LOCAL).</summary>
    public void BindParameter(string name, VfpValue value, VarKind kind)
    {
        Current.Vars[name] = new Cell { Value = value, Defined = true, Kind = kind };
    }

    /// <summary>Assign <paramref name="name"/> = <paramref name="value"/>: update the nearest VISIBLE
    /// binding, else create it as an implicit-PRIVATE in the current frame.</summary>
    public void Set(string name, VfpValue value)
    {
        long profileStart = VfpInsertProfile.Start();
        try
        {
            var cell = Find(name);
            if (cell is not null)
            {
                if (cell.Array is { } array)
                {
                    for (int i = 1; i <= array.Length; i++) array.SetLinear(i, value);
                    cell.Defined = true;
                    return;
                }
                cell.Value = value;
                cell.Defined = true;
                return;
            }
            Current.Vars[name] = new Cell { Value = value, Defined = true, Kind = VarKind.ImplicitPrivate };
        }
        finally
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.MemoryStoreAccess, profileStart);
        }
    }

    /// <summary>Read <paramref name="name"/> following VFP visibility, or <see cref="VfpValue.Null"/>
    /// when it does not resolve to a DEFINED binding (callers test <see cref="IsDefined"/> / TYPE()). An
    /// ARRAY binding in this scalar accessor yields element <c>(1,1)</c>/<c>(1)</c>, per VFP.</summary>
    public VfpValue Get(string name)
    {
        long profileStart = VfpInsertProfile.Start();
        try
        {
            var cell = Find(name);
            if (cell is not { Defined: true }) return VfpValue.Null;
            return cell.Array is { } arr ? arr.First : cell.Value;
        }
        finally
        {
            VfpInsertProfile.Stop(VfpInsertProfileBucket.MemoryStoreAccess, profileStart);
        }
    }

    /// <summary>True when <paramref name="name"/> resolves to a visible DEFINED binding.</summary>
    public bool IsDefined(string name) => Find(name) is { Defined: true };

    // ── arrays ──────────────────────────────────────────────────────────────────

    /// <summary>The visible ARRAY binding for <paramref name="name"/>, or <see langword="null"/> when the
    /// name is unbound or a scalar.</summary>
    public VfpArray? FindArray(string name) => Find(name)?.Array;

    /// <summary>Declare <paramref name="name"/> as an ARRAY of <paramref name="rows"/>×<paramref name="cols"/>
    /// (<paramref name="cols"/> &lt;= 0 ⇒ 1-D) with the given lifetime; elements default to <c>.F.</c>.</summary>
    public void DeclareArray(string name, VarKind kind, int rows, int cols)
    {
        var cell = new Cell { Array = new VfpArray(rows, cols), Defined = true, Kind = kind };
        if (kind == VarKind.Public) _frames[0].Vars[name] = cell;
        else Current.Vars[name] = cell;
    }

    /// <summary>DIMENSION / REDIMENSION: resize the existing ARRAY <paramref name="name"/> (preserving +
    /// <c>.F.</c>-filling), or — when it is a scalar / unbound — (re)create it as an array. Returns the
    /// array. A scalar binding is converted in place; an unbound name becomes an implicit-PRIVATE array.</summary>
    public VfpArray RedimOrCreateArray(string name, int rows, int cols)
    {
        var cell = Find(name);
        if (cell is { Array: not null })
        {
            cell.Array.Redim(rows, cols);
            cell.Defined = true;
            return cell.Array;
        }
        if (cell is not null)
        {
            cell.Array = new VfpArray(rows, cols);
            cell.Defined = true;
            return cell.Array;
        }
        var arr = new VfpArray(rows, cols);
        Current.Vars[name] = new Cell { Array = arr, Defined = true, Kind = VarKind.ImplicitPrivate };
        return arr;
    }

    /// <summary>CLEAR MEMORY / CLEAR ALL: release EVERY memory-variable binding (scalars + arrays, every
    /// scope) in every live frame — the whole-store counterpart of <see cref="Release(string)"/>. The frame
    /// STACK itself is preserved (an in-flight call keeps its frames; only their bindings are dropped) so a
    /// later assignment re-creates variables from scratch, exactly as VFP's post-CLEAR "U" state behaves.</summary>
    public void ClearAll()
    {
        foreach (var f in _frames) f.Vars.Clear();
    }

    /// <summary>A visible binding snapshot for LIST/DISPLAY MEMORY and SAVE TO / RESTORE FROM: name + kind
    /// plus EITHER the scalar value OR the array (never both). Walks frames top-down (nearest wins,
    /// ancestor LOCALs hidden) and yields each DEFINED name once.</summary>
    public readonly record struct Binding(string Name, VarKind Kind, VfpValue? Scalar, VfpArray? Array);

    /// <summary>Enumerate the currently VISIBLE, DEFINED memory bindings (scalars + arrays), nearest frame
    /// first, each name once — the read side that LIST MEMORY / SAVE TO share.</summary>
    public IReadOnlyList<Binding> EnumerateVisible()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Binding>();
        int cur = _frames.Count - 1;
        for (int f = cur; f >= 0; f--)
        {
            foreach (var kv in _frames[f].Vars)
            {
                if (f != cur && kv.Value.Kind == VarKind.Local) continue;   // ancestor LOCAL: invisible to callees.
                if (!kv.Value.Defined) continue;                            // reserved-but-unassigned PRIVATE: skip.
                if (!seen.Add(kv.Key)) continue;                            // a nearer frame already shadowed it.
                result.Add(new Binding(kv.Key, kv.Value.Kind,
                    kv.Value.Array is null ? kv.Value.Value : (VfpValue?)null, kv.Value.Array));
            }
        }
        return result;
    }

    /// <summary>Bind <paramref name="name"/> as a restored ARRAY (RESTORE FROM). Always private scope, in the
    /// current frame (VFP restores every variable as PRIVATE — hackfox s4g222).</summary>
    public void SetArray(string name, VfpArray array)
        => Current.Vars[name] = new Cell { Array = array, Defined = true, Kind = VarKind.Private };

    /// <summary>Bind <paramref name="name"/> = <paramref name="value"/> as a restored SCALAR (RESTORE FROM):
    /// UNCONDITIONALLY private in the current frame — the scalar sibling of <see cref="SetArray"/>. Unlike
    /// <see cref="Set"/> it does NOT keep an existing binding's kind, so a RESTORE (ADDITIVE) over a same-named
    /// PUBLIC re-binds it PRIVATE, exactly as VFP restores every variable with private scope (hackfox s4g222).</summary>
    public void SetPrivate(string name, VfpValue value)
        => Current.Vars[name] = new Cell { Value = value, Defined = true, Kind = VarKind.Private };

    /// <summary>RELEASE <paramref name="name"/> (no error if absent): drop the nearest visible binding.</summary>
    public void Release(string name)
    {
        int cur = _frames.Count - 1;
        for (int f = cur; f >= 0; f--)
        {
            if (!_frames[f].Vars.ContainsKey(name)) continue;
            if (f != cur && _frames[f].Vars[name].Kind == VarKind.Local) continue;
            _frames[f].Vars.Remove(name);
            return;
        }
    }
}
