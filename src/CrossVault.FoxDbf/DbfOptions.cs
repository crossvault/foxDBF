using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// Open-time options for a <see cref="DbfTable"/> (plan §A5b). Controls how the
/// hidden VFP system columns (e.g. <c>_NullFlags</c>) surface on the public API and
/// whether the NULL bitmap is applied to decoded values.
/// </summary>
/// <remarks>
/// Both toggles are independent. <see cref="ExposeSystemColumns"/> governs the
/// <em>schema</em> (whether <see cref="DbfTable.Columns"/> and the record accessors
/// include <see cref="DbfColumn.IsSystem"/> columns); <see cref="ApplyNullFlags"/>
/// governs the <em>values</em> (whether a set null bit forces a decoded value to
/// <see langword="null"/>). The full physical column list is always retained
/// internally for record layout and bitmap decoding regardless of these toggles.
/// </remarks>
public sealed record DbfOptions
{
    /// <summary>
    /// When false (the default) system columns such as <c>_NullFlags</c> are hidden
    /// from <see cref="DbfTable.Columns"/> and the public record accessors; when true
    /// they are exposed (e.g. for the golden-master CSV comparison). Default: <c>false</c>.
    /// </summary>
    public bool ExposeSystemColumns { get; init; } = false;

    /// <summary>
    /// When true (the default for VFP versions) a set null bit in the <c>_NullFlags</c>
    /// bitmap overrides the decoded field value with <see langword="null"/>; when false
    /// the raw decoded value is returned. Default: <c>true</c>.
    /// </summary>
    public bool ApplyNullFlags { get; init; } = true;

    /// <summary>
    /// Header-recovery strategy for damaged/zeroed/truncated headers (plan §A12).
    /// Default <see cref="Recovery.Strict"/>: trust the header and throw on inconsistency.
    /// <see cref="Recovery.Tolerant"/> clamps truncation; <see cref="Recovery.Reconstruct"/>
    /// rebuilds the header geometry from the field descriptors.
    /// </summary>
    public Recovery Recovery { get; init; } = Recovery.Strict;

    /// <summary>
    /// When set, force the on-disk layout to be interpreted with this version byte
    /// regardless of the byte in the file (plan §A12 Force-Open — for tools/forensics
    /// on unknown/unsupported-version files such as the encrypted <c>V_usr.dbf</c>
    /// <c>0xee</c>). <see langword="null"/> (the default) honours the file's own
    /// version byte.
    /// </summary>
    public byte? ForceVersion { get; init; }

    /// <summary>
    /// Explicit character-encoding override (plan §A7 selection order, highest
    /// precedence). When set, the table's text fields are decoded with this
    /// <see cref="Encoding"/> regardless of the header code-page byte. <see langword="null"/>
    /// (the default) defers to the header code page, then <see cref="DefaultEncoding"/>.
    /// </summary>
    public Encoding? Encoding { get; init; }

    /// <summary>
    /// Explicit memo-file path override (plan §A6). When set, the sidecar memo file is
    /// opened from this path verbatim instead of being auto-discovered beside the
    /// <c>.dbf</c> (basename + <c>.fpt</c>/<c>.dbt</c>, or <c>.dct</c> for a <c>.dbc</c>).
    /// <see langword="null"/> (the default) uses auto-discovery.
    /// </summary>
    public string? MemoPath { get; init; }

    /// <summary>
    /// Configurable fallback encoding used when neither <see cref="Encoding"/> nor the
    /// header code-page byte resolves to a concrete encoding (plan §A7: an unmapped /
    /// zero code-page byte). <see langword="null"/> (the default) means CP1252.
    /// </summary>
    public Encoding? DefaultEncoding { get; init; }

    /// <summary>Opt in to UTF-8 detection in character fields for non-VFP data. Default false:
    /// VFP9 always interprets bytes in the selected code page. An explicit <see cref="Encoding"/>
    /// override takes precedence even when this option is enabled.</summary>
    public bool DetectUtf8 { get; init; }

    /// <summary>
    /// Concurrency / locking strategy for the WRITE layer (plan §D3). Ignored by the
    /// read-only <see cref="DbfTable"/> path. <see cref="Write.LockMode.Shared"/> (the
    /// default) opens the file <see cref="System.IO.FileShare.ReadWrite"/> and brackets each
    /// mutation with the VFP byte-range locks for coexistence with a running VFP runtime;
    /// <see cref="Write.LockMode.Exclusive"/> opens <see cref="System.IO.FileShare.None"/> and
    /// skips the byte-range locks. Default: <see cref="Write.LockMode.Shared"/>.
    /// </summary>
    public Write.LockMode LockMode { get; init; } = Write.LockMode.Shared;

    /// <summary>
    /// OPT-IN read backend for the read-only path (Highlike Phase B-3, design Peak 1). Default
    /// <see cref="DbfReadBackend.FileStream"/> — the classic per-read syscall path (mapping OFF).
    /// <see cref="DbfReadBackend.Auto"/> memory-maps LOCAL/fixed tables (and falls back to
    /// <see cref="System.IO.FileStream"/> on a network share); <see cref="DbfReadBackend.MemoryMapped"/>
    /// forces the map (still falling back on a network path or any mapping failure). Reads via the
    /// mapped backend are BYTE-IDENTICAL to the <see cref="System.IO.FileStream"/> path; the WRITE
    /// layer (<see cref="Write.DbfWriter"/>) always uses <see cref="System.IO.FileStream"/>.
    /// </summary>
    public DbfReadBackend ReadBackend { get; init; } = DbfReadBackend.FileStream;

    /// <summary>
    /// Overrides the Fixed-vs-Network storage detection used when resolving
    /// <see cref="ReadBackend"/> (Highlike Phase B-3). Default <see cref="DbfDriveKind.Auto"/>
    /// auto-detects from the table path (UNC prefix / <see cref="System.IO.DriveType.Network"/>).
    /// Force <see cref="DbfDriveKind.Network"/> to assert the network fall-back path even for a
    /// local file (testing / known-topology deployments); memory-mapping engages only on
    /// <see cref="DbfDriveKind.Fixed"/>.
    /// </summary>
    public DbfDriveKind DriveKind { get; init; } = DbfDriveKind.Auto;
}
