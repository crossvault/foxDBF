using System;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// Thrown by <see cref="VfpInterpreter"/> when a stored procedure cannot be run (e.g. a call to a
/// procedure that is not loaded). Genuine VFP-runtime errors inside a procedure surface as the
/// underlying exception; this type covers the interpreter's own pre-conditions.
/// </summary>
/// <remarks>
/// When the throw site KNOWS the Visual FoxPro error number the fault maps to (project-review 5.3),
/// it passes it via the <see cref="VfpErrorNumber"/> constructor overload so the interpreter's error
/// trap (<c>ErrorNumberOf</c>) can carry the exact number to <c>AERROR()</c>/<c>ERROR()</c> WITHOUT
/// sniffing the English message text. A message-only throw leaves <see cref="VfpErrorNumber"/> null
/// (the trap then classifies it by its last-resort heuristic).
/// </remarks>
public sealed class MicroVfpRuntimeException : Exception, IVfpErrorCode
{
    /// <summary>The VFP9 error number this fault represents, or <see langword="null"/> when the throw
    /// site did not pin one (see <see cref="IVfpErrorCode"/>).</summary>
    public int? VfpErrorNumber { get; }

    /// <summary>The source text of the statement microVFP could not execute (an unrecognised command, an
    /// unparseable expression or SQL statement), or <see langword="null"/>. <see cref="Exception.Message"/>
    /// stays VFP's exact error text (what <c>MESSAGE()</c> returns); this carries the context for the host.</summary>
    public string? Statement { get; init; }

    /// <summary>The 1-based source line of <see cref="Statement"/>, or 0 when unknown.</summary>
    public int LineNo { get; init; }

    /// <summary>VFP's error detail (<c>Exception.Details</c>, <c>AERROR()</c> column 3), e.g. the missing
    /// <c>name.prg</c> of error 1 for an unknown function; <see langword="null"/> when VFP reports none.</summary>
    public string? Details { get; init; }

    // The failing DML's area/type survive rollback, work-area changes and rethrow to AERROR().
    // A failed field ERROR expression aborts the candidate; an error in CHECK itself does not.
    internal bool FromFieldErrorExpression { get; set; }

    internal int TriggerArea { get; init; }
    internal int TriggerType { get; init; }

    /// <summary>The <c>eUserExpression</c> of a user <c>THROW</c> (error 2071), or null for any other error.</summary>
    internal Expressions.VfpValue? UserValue { get; init; }

    public MicroVfpRuntimeException(string message) : base(message) { }

    public MicroVfpRuntimeException(string message, int vfpErrorNumber) : base(message)
        => VfpErrorNumber = vfpErrorNumber;
}
