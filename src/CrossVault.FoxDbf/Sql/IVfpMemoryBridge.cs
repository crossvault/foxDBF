using System.Collections.Generic;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// A bridge from the SQL engine to the microVFP memory-variable store (which lives in the higher
/// <c>CrossVault.FoxDbf.MicroVfp</c> assembly, so the SQL layer cannot reference it directly). The bound
/// <c>VfpInterpreter</c> registers its implementation on the owning <see cref="VfpSession"/> in its
/// constructor; <see langword="null"/> when no interpreter is bound (pure-SQL use, in which case
/// <c>SELECT … INTO ARRAY</c> / <c>INSERT … FROM ARRAY|MEMVAR</c> raise a clear error).
/// <para>
/// The bridge is the seam that lets BOTH the microVFP SP path (a <c>SELECT</c> inside a <c>.prg</c>) and the
/// ADO.NET <c>FoxDbfCommand</c> path surface these idioms against the SAME active-session memvar store.
/// </para>
/// </summary>
internal interface IVfpMemoryBridge
{
    /// <summary>Recognize an array subscript in SQL binding without copying the array's rows.</summary>
    bool IsArray(string name) => false;

    /// <summary>Publish INSERT SELECT's committed prefix, including a partial failure.</summary>
    void SetImportTally(int count) { }

    /// <summary>
    /// <c>SELECT … INTO ARRAY name</c>: (re)create memory array <paramref name="name"/> as a
    /// <paramref name="rows"/> × <paramref name="cols"/> row-major grid of <paramref name="data"/> and set
    /// <c>_TALLY</c> to <paramref name="rows"/>. Per VFP the array is 2-D even for a single result column
    /// (an N×1 array). When <paramref name="rows"/> is 0 the array is left UNCHANGED (an existing array is
    /// untouched; a new name is never created) and <c>_TALLY</c> is set to 0.
    /// </summary>
    void StoreQueryIntoArray(string name, int rows, int cols, IReadOnlyList<object?[]> data, IReadOnlyList<SqlColumn>? columns = null);

    /// <summary>
    /// <c>INSERT … FROM ARRAY name</c>: the array's rows as CLR values — a 1-D array yields exactly ONE row of
    /// all its elements; a 2-D array yields one row per array row (each the row's columns). Returns
    /// <see langword="null"/> when <paramref name="name"/> is not a bound memory array.
    /// </summary>
    IReadOnlyList<IReadOnlyList<object?>>? ReadArrayRows(string name);

    /// <summary>
    /// <c>INSERT … FROM MEMVAR</c>: read the memory variable named <paramref name="name"/> (the target
    /// field's name, VFP's <c>m.&lt;field&gt;</c>). Returns <see langword="true"/> with the CLR
    /// <paramref name="value"/> when a defined memvar exists, else <see langword="false"/> (the field is left
    /// blank).
    /// </summary>
    bool TryReadMemvar(string name, out object? value);

    /// <summary>Whether a SQL qualifier names an object variable. A defined scalar (including NULL)
    /// raises VFP9 error 1924 at bind time; a field in the current area is never a qualifier.</summary>
    bool IsObjectQualifier(string name);
}
