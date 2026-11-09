namespace CrossVault.FoxDbf.Write;

/// <summary>
/// The definition of a single CDX tag to BUILD (plan §D7) — the inverse of the
/// <see cref="CrossVault.FoxDbf.Index.CdxTag"/> reader. Describes the tag BEFORE
/// it exists: its <see cref="Name"/> (≤ 10 ASCII chars, the directory key), the
/// <see cref="KeyExpression"/> evaluated per record to produce the ordered key,
/// an optional sparse <see cref="ForExpression"/> filter, ascending/descending
/// (<see cref="Descending"/>), the <see cref="Collation"/> applied to CHARACTER
/// keys (MACHINE identity / GENERAL byte-exact) and the <see cref="Unique"/> flag
/// (one entry per distinct key).
/// </summary>
public sealed record CdxTagDefinition
{
    /// <summary>The tag name (≤ 10 ASCII chars; the 10-byte directory key, NUL/space padded).</summary>
    public string Name { get; init; }

    /// <summary>The KEY expression evaluated per record (e.g. a bare field name or <c>UPPER(name)</c>).</summary>
    public string KeyExpression { get; init; }

    /// <summary>The optional FOR filter expression; a record is indexed only when it evaluates true. Null/empty = no filter.</summary>
    public string? ForExpression { get; init; }

    /// <summary>True to build a DESCENDING tag (header @502); the on-disk key bytes stay ascending, only traversal reverses.</summary>
    public bool Descending { get; init; }

    /// <summary>The collation applied to CHARACTER keys (e.g. <c>MACHINE</c> / <c>GENERAL</c>); default <c>MACHINE</c>.</summary>
    public string Collation { get; init; } = "MACHINE";

    /// <summary>True to build a UNIQUE tag — only the first record (lowest recno) per distinct key is kept.</summary>
    public bool Unique { get; init; }

    /// <summary>Enforce distinct keys (VFP CANDIDATE / SQL UNIQUE), including deleted records.</summary>
    public bool Candidate { get; init; }

    /// <summary>Store <see cref="KeyExpression"/>/<see cref="ForExpression"/> byte-for-byte as given instead of
    /// the decompiled text VFP's <c>INDEX ON</c> and <c>REINDEX</c> write (<c>UPPER( name )</c> →
    /// <c>UPPER(name)</c>). For a tag that keeps the text already on disk — a sibling preserved while another
    /// tag is added or deleted (VFP leaves those untouched) — and for the <c>.dcx</c> of a new database, whose
    /// tags VFP's CREATE DATABASE stores as typed (<c>!DELETED()</c>, not <c>.NOT.DELETED()</c>).</summary>
    internal bool StoreExpressionsVerbatim { get; init; }

    /// <summary>Create a tag definition.</summary>
    public CdxTagDefinition(
        string name,
        string keyExpression,
        string? forExpression = null,
        bool descending = false,
        string collation = "MACHINE",
        bool unique = false)
    {
        Name = name;
        KeyExpression = keyExpression;
        ForExpression = forExpression;
        Descending = descending;
        Collation = string.IsNullOrWhiteSpace(collation) ? "MACHINE" : collation;
        Unique = unique;
    }
}
