using System;
using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>microVFP ↔ SQL memvar bridge: lets the SQL engine surface <c>SELECT … INTO ARRAY</c> and
/// <c>INSERT … FROM ARRAY | MEMVAR</c> against this interpreter's <see cref="MemoryStore"/>.</summary>
public sealed partial class VfpInterpreter
{
    /// <summary>The <see cref="IVfpMemoryBridge"/> the interpreter registers on its <see cref="Session"/> so
    /// the SQL <c>SelectExecutor</c> / <c>DmlExecutor</c> read and write THIS interpreter's memory-variable
    /// store. It converts CLR result values to/from <see cref="VfpValue"/> and applies the VFP array shape +
    /// <c>_TALLY</c> rules (pinned to the VFP9 runtime).</summary>
    private sealed class MemvarBridge : IVfpMemoryBridge
    {
        private readonly VfpInterpreter _it;
        public MemvarBridge(VfpInterpreter it) => _it = it;

        public bool IsArray(string name) => _it.Memory.FindArray(name) is not null;

        public void SetImportTally(int count) => _it.Memory.Set("_TALLY", VfpValue.Integer(count));

        public void StoreQueryIntoArray(string name, int rows, int cols, IReadOnlyList<object?[]> data)
        {
            // _TALLY = row count in ALL cases (incl. zero rows).
            _it.Memory.Set("_TALLY", VfpValue.Number((decimal)rows).WithDisplayDecimals(0));

            // VFP: zero rows leaves the array UNCHANGED — an existing array is untouched, a new name is never
            // created. So do NOT touch the store at all when there is nothing to land.
            if (rows <= 0) return;

            // VFP builds a 2-D array of nRows × nCols — even for a single result column (an N×1 array, so
            // ALEN(a,2)==1, NOT a genuine 1-D array). RedimOrCreateArray honours the existing binding's scope
            // (or creates an implicit-private one), matching VFP's "create in the current scope" behaviour.
            int c = Math.Max(1, cols);
            var arr = _it.Memory.RedimOrCreateArray(name, rows, c);
            for (int r = 0; r < rows; r++)
            {
                var srcRow = r < data.Count ? data[r] : null;
                for (int j = 0; j < c; j++)
                {
                    object? v = srcRow is not null && j < srcRow.Length ? srcRow[j] : null;
                    arr.Set(r + 1, j + 1, VfpValue.FromClr(v));
                }
            }
        }

        public IReadOnlyList<IReadOnlyList<object?>>? ReadArrayRows(string name)
        {
            var arr = _it.Memory.FindArray(name);
            if (arr is null) return null;

            var result = new List<IReadOnlyList<object?>>();
            if (arr.Is2D)
            {
                // 2-D → one row per array row (each the row's columns), position-mapped downstream.
                for (int r = 1; r <= arr.Rows; r++)
                {
                    var row = new object?[arr.Cols];
                    for (int col = 1; col <= arr.Cols; col++) row[col - 1] = arr.Get(r, col).ToClr();
                    result.Add(row);
                }
            }
            else
            {
                // 1-D → exactly ONE row of all elements (in linear order).
                var row = new object?[arr.Length];
                for (int i = 1; i <= arr.Length; i++) row[i - 1] = arr.GetLinear(i).ToClr();
                result.Add(row);
            }
            return result;
        }

        public bool TryReadMemvar(string name, out object? value)
        {
            if (_it.Memory.IsDefined(name)) { value = _it.Memory.Get(name).ToClr(); return true; }
            value = null;
            return false;
        }

        public bool IsObjectQualifier(string name)
        {
            if (!_it.Memory.IsDefined(name) && _it.Memory.FindArray(name) is null) return false;
            if (_it.TryGetObject(_it.Memory.Get(name), out _)) return true;
            throw new MicroVfpRuntimeException($"{name.ToUpperInvariant()} is not an object.", 1924);
        }
    }
}
