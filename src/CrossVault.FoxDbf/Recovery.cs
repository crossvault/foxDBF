namespace CrossVault.FoxDbf;

/// <summary>
/// Header-recovery strategy for <see cref="DbfTable.Open(string, DbfOptions)"/> (plan §A12).
/// Controls how much trust is placed in the on-disk header versus the (usually intact)
/// field-descriptor array when the two disagree or the header is zeroed/truncated.
/// </summary>
public enum Recovery
{
    /// <summary>
    /// Default. Trust the header verbatim; throw <see cref="DbfCorruptHeaderException"/>
    /// on a structurally impossible/inconsistent header (e.g. a zeroed
    /// <c>RecordLength</c>/<c>HeaderLength</c>) and on a field of type <c>'7'</c>, which VFP9
    /// cannot read. No silent reconstruction.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Tolerate single faults — a missing <c>0x1A</c> EOF marker or a data region
    /// truncated mid-records — reading what is physically present. <see cref="DbfTable.RecordCount"/>
    /// is clamped to the records actually on disk rather than throwing. A table with a field of type
    /// <c>'7'</c> opens; that field decodes like any unknown type, as text.
    /// </summary>
    Tolerant = 1,

    /// <summary>
    /// Actively recompute the header geometry (<c>RecordLength</c>, <c>HeaderLength</c>,
    /// <c>RecordCount</c>, and the version byte) from the intact field descriptors when
    /// the header is zeroed/invalid (the <c>corrupt.prg</c> first-10-bytes-nulled case).
    /// Implies <see cref="Tolerant"/> truncation clamping.
    /// </summary>
    Reconstruct = 2,
}
