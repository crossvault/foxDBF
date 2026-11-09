using System;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The ADO.NET provider exception type (mirrors <c>SqliteException</c>'s posture): every provider
/// failure surfaces as a <see cref="DbException"/> so generic ADO.NET callers can catch it without a
/// reference to <c>CrossVault.FoxDbf.Sql</c>'s <c>FoxDbfSqlException</c>.
/// </summary>
public sealed class FoxDbfException : DbException, IVfpErrorCode
{
    /// <summary>The VFP9 error number this fault represents, or <see langword="null"/> when the throw
    /// site did not pin one (project-review 5.3; see <see cref="IVfpErrorCode"/>). The DBC RULE / TRIGGER
    /// write-model sets it at the throw site (1582 field rule / 1583 record rule / 1539 trigger failed)
    /// so the number never has to be recovered by sniffing the English message text.</summary>
    public int? VfpErrorNumber { get; }

    public FoxDbfException() { }
    public FoxDbfException(string? message) : base(message) { }
    public FoxDbfException(string? message, Exception? innerException) : base(message, innerException) { }

    public FoxDbfException(string? message, int vfpErrorNumber) : base(message)
        => VfpErrorNumber = vfpErrorNumber;

    internal FoxDbfException(string message, int vfpErrorNumber, Exception innerException)
        : base(message, innerException) => VfpErrorNumber = vfpErrorNumber;
}
