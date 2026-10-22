using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// Per-tag statistics captured in the <c>.stx</c> sidecar (Phase A, design §6): the
/// number of distinct values (<see cref="Ndv"/>), the null count (<see cref="Nulls"/>,
/// counted from the DBF null-flag bytes — never the CDX), and the minimum / maximum
/// decoded key (<see cref="Min"/> / <see cref="Max"/>, stored as human-readable strings).
/// </summary>
/// <remarks>
/// These numbers are HINTS ONLY: they feed the cost-based planner (selectivity / pruning)
/// and may only change the PLAN, never the result set. Stale, missing, or deliberately
/// wrong values can never make a query return a different set of records.
/// </remarks>
public sealed class StxTagStats
{
    /// <summary>
    /// The tag's KEY expression (e.g. <c>UPPER(NAME)</c>, <c>ID</c>). Carried on the stat itself so a
    /// lookup by key expression survives even when the per-tag stats are keyed by tag NAME (the
    /// disambiguation that lets two tags share a key expression under different collation/order).
    /// </summary>
    public string? KeyExpr { get; init; }

    /// <summary>
    /// The collation under which <see cref="Min"/> / <see cref="Max"/> were captured (e.g.
    /// <c>MACHINE</c> / <c>GENERAL</c>). REQUIRED to interpret the bounds: only a MACHINE (identity)
    /// collation renders to readable text; any other collation stores the raw collation-WEIGHT bytes.
    /// </summary>
    public string? Collation { get; init; }

    /// <summary>True when this is a DESCENDING tag. <see cref="Min"/> is always the smallest stored key
    /// and <see cref="Max"/> the largest REGARDLESS of this flag (the bounds are direction-independent).</summary>
    public bool Descending { get; init; }

    /// <summary>The number of distinct key values in the tag (a key change while walking the sorted entries increments it).</summary>
    public long Ndv { get; init; }

    /// <summary>The number of records whose underlying field is NULL (counted from the DBF null-flag bytes).</summary>
    public long Nulls { get; init; }

    /// <summary>
    /// The MINIMUM stored key (the smallest key in collation-byte order, independent of the tag's
    /// ascending/descending traversal); null when the tag is empty. For a MACHINE (identity) collation
    /// this is the readable decoded value; for any other collation (e.g. GENERAL) the stored CDX key
    /// bytes are collation WEIGHTS, not characters, so the bound is the raw weight bytes as a
    /// <c>0x…</c> hex string — comparable in collation-byte space but NOT a human-readable value
    /// (read <see cref="Collation"/> to interpret it).
    /// </summary>
    public string? Min { get; init; }

    /// <summary>
    /// The MAXIMUM stored key (the largest key in collation-byte order, independent of the tag's
    /// ascending/descending traversal); null when the tag is empty. Same encoding rules as
    /// <see cref="Min"/> (readable only under a MACHINE collation; otherwise a <c>0x…</c> hex of the
    /// raw collation-weight bytes).
    /// </summary>
    public string? Max { get; init; }

    /// <summary>
    /// PHASE C (design §9): an EQUI-DEPTH HISTOGRAM — the bucket BOUNDARY keys harvested from the SAME
    /// single sorted leaf walk that produces NDV / Min / Max. For <c>N</c> buckets this holds
    /// <c>N + 1</c> boundaries (<c>boundary[j]</c> = the key at sorted position <c>j · count / N</c>),
    /// so each bucket <c>[boundary[j], boundary[j+1]]</c> covers ≈ <c>count / N</c> entries. The
    /// boundaries use the SAME encoding as <see cref="Min"/> / <see cref="Max"/>. Null on an older
    /// sidecar that predates the field (additive — the planner then falls back to linear interpolation)
    /// or when the key is not comparable. HINT-ONLY: a stale / missing / wrong histogram may only change
    /// the PLAN, never the result.
    /// </summary>
    public IReadOnlyList<string>? Histogram { get; init; }

    /// <summary>
    /// PHASE C (design §9): the MOST-COMMON-VALUES list — the top-K (value, count) pairs harvested from
    /// the SAME single sorted walk (a run of equal keys IS a value frequency), ordered by descending
    /// <see cref="StxMcvEntry.Count"/>. Lets the planner estimate a HOT (skewed) equality value by its
    /// real count instead of the uniform <c>reccount / ndv</c>. Null on an older sidecar that predates
    /// the field (additive). HINT-ONLY.
    /// </summary>
    public IReadOnlyList<StxMcvEntry>? Mcv { get; init; }
}

/// <summary>
/// PHASE C (design §9): one MOST-COMMON-VALUES entry — a value (encoded exactly like
/// <see cref="StxTagStats.Min"/> / <see cref="StxTagStats.Max"/>) and the number of records that carry
/// it (a run of equal keys in the sorted walk). HINT-ONLY: feeds the skew-aware equality estimate; it
/// may only change the plan, never the result set.
/// </summary>
public sealed class StxMcvEntry
{
    /// <summary>The value (same encoding rules as <see cref="StxTagStats.Min"/> / <see cref="StxTagStats.Max"/>).</summary>
    public string? Value { get; init; }

    /// <summary>The number of records carrying <see cref="Value"/> (the length of the equal-key run in the sorted walk).</summary>
    public long Count { get; init; }
}

/// <summary>
/// The <c>src</c> block of a <c>.stx</c> sidecar (design §6): the source DBF identity plus the
/// STALENESS TOKEN — the table <see cref="RecCount"/> and the DBF last-update stamp
/// (<see cref="UpdStamp"/>). On load these are compared against the current table; any mismatch
/// marks the stats STALE (they are then ignored and may be lazily rebuilt). <see cref="Deleted"/>
/// is the deleted-record count captured at build time.
/// </summary>
public sealed class StxSource
{
    /// <summary>The source DBF file name (informational; the path is resolved from the table at load time).</summary>
    public string? Dbf { get; init; }

    /// <summary>The table record count captured at build time — the primary staleness signal.</summary>
    public int RecCount { get; init; }

    /// <summary>The DBF header last-update stamp captured at build time (e.g. <c>yyyy-MM-dd</c>) — a secondary staleness signal (DATE-ONLY: blind to a same-day in-place edit).</summary>
    public string? UpdStamp { get; init; }

    /// <summary>
    /// The DBF file last-write timestamp (full UTC, round-trip "O" format) captured at build time —
    /// the STRONG staleness signal. Unlike <see cref="UpdStamp"/> (date-only) this catches a same-day
    /// in-place key edit / reindex that changes neither the record count nor the header date.
    /// </summary>
    public string? DbfWriteUtc { get; init; }

    /// <summary>
    /// The CDX file last-write timestamp (full UTC, round-trip "O") captured at build time — catches a
    /// reindex (CDX rebuilt) that does not touch the DBF. Optional; null when there is no CDX.
    /// </summary>
    public string? CdxWriteUtc { get; init; }

    /// <summary>The deleted-record count captured at build time (feeds the DELETED() cost decision).</summary>
    public int Deleted { get; init; }
}

/// <summary>
/// The in-memory model of a Highlike <c>.stx</c> statistics sidecar (Phase A, design §6): a
/// <see cref="Source"/> staleness token, a <see cref="Built"/> note, and per-tag
/// <see cref="StxTagStats"/> keyed by the tag KEY expression. Serialised as a small,
/// human-readable JSON file via <see cref="ToJson"/> / <see cref="FromJson"/> (System.Text.Json,
/// in-box only).
/// </summary>
/// <remarks>
/// NON-NEGOTIABLE INVARIANT: every value here is a HINT. Stale / missing / corrupt statistics
/// may only change the PLAN (speed), never the result set.
/// </remarks>
public sealed class StxStatistics
{
    /// <summary>The <c>src</c> staleness token + source identity.</summary>
    public StxSource Source { get; init; } = new();

    /// <summary>When these statistics were built (UTC).</summary>
    public DateTime Built { get; init; }

    /// <summary>
    /// Per-tag statistics, keyed by the tag NAME (unique within a CDX). Keying by name — rather than by
    /// key expression — prevents two tags that share a KEY expression but differ in collation or
    /// ascending/descending from colliding (the second would otherwise silently overwrite the first).
    /// Each <see cref="StxTagStats"/> still carries its own <see cref="StxTagStats.KeyExpr"/> so a
    /// lookup by key expression (<see cref="ForKey"/>) keeps working.
    /// </summary>
    public IReadOnlyDictionary<string, StxTagStats> Tags { get; init; }
        = new Dictionary<string, StxTagStats>();

    /// <summary>
    /// Look up the stats for a tag by its KEY expression, matching the way the optimizer normalises
    /// keys (whitespace-insensitive, case-insensitive). When several tags share a key expression this
    /// returns the FIRST match (ambiguous by design — use <see cref="ForTag"/> to disambiguate by the
    /// exact tag the optimizer chose). Returns null when no such tag was captured.
    /// </summary>
    public StxTagStats? ForKey(string keyExpression)
    {
        if (keyExpression is null) return null;
        string want = NormKey(keyExpression);
        foreach (var kv in Tags)
        {
            // Match on the stat's own KeyExpr first (the dictionary is keyed by tag NAME), then fall
            // back to the dictionary key itself so a sidecar keyed by key expression still resolves.
            if (kv.Value.KeyExpr is not null && NormKey(kv.Value.KeyExpr) == want) return kv.Value;
            if (NormKey(kv.Key) == want) return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// Look up the stats for the SPECIFIC tag the optimizer chose, disambiguating tags that share a
    /// key expression. Matches by tag NAME first; then by the (KeyExpr, Collation, Descending) triple;
    /// then degrades to a key-expression match. Returns null when no matching stat was captured. The
    /// result is a HINT only — a wrong match can never change the result set, only the plan.
    /// </summary>
    public StxTagStats? ForTag(CdxTag tag)
    {
        if (tag is null) return null;

        // 1) Exact: stats we built are keyed by the tag name.
        if (Tags.TryGetValue(tag.Name, out var byName)) return byName;

        // 2) (KeyExpr, Collation, Descending) — distinguishes tags sharing a key expression.
        string wantKey = NormKey(tag.KeyExpression ?? string.Empty);
        string wantColl = tag.Collation ?? "MACHINE";
        foreach (var kv in Tags)
        {
            var s = kv.Value;
            string sKey = NormKey(s.KeyExpr ?? kv.Key);
            if (sKey == wantKey
                && string.Equals(s.Collation ?? "MACHINE", wantColl, StringComparison.OrdinalIgnoreCase)
                && s.Descending == tag.Descending)
                return s;
        }

        // 3) Last resort: any tag with this key expression (HINT only).
        return ForKey(tag.KeyExpression ?? string.Empty);
    }

    /// <summary>Serialise to a small, human-readable JSON sidecar (System.Text.Json).</summary>
    public string ToJson()
    {
        var dto = new Dto
        {
            Src = new SrcDto
            {
                Dbf = Source.Dbf,
                RecCount = Source.RecCount,
                UpdStamp = Source.UpdStamp,
                DbfWriteUtc = Source.DbfWriteUtc,
                CdxWriteUtc = Source.CdxWriteUtc,
                Deleted = Source.Deleted,
            },
            Built = Built.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Tags = new Dictionary<string, TagDto>(StringComparer.Ordinal),
        };
        foreach (var kv in Tags)
        {
            dto.Tags[kv.Key] = new TagDto
            {
                Key = kv.Value.KeyExpr,
                Coll = kv.Value.Collation,
                Desc = kv.Value.Descending ? true : null,
                Ndv = kv.Value.Ndv,
                Nulls = kv.Value.Nulls,
                Min = kv.Value.Min,
                Max = kv.Value.Max,
                // PHASE C (additive): omit when absent so an older reader is unaffected.
                Hist = kv.Value.Histogram is { Count: > 0 } h ? new List<string>(h) : null,
                Mcv = kv.Value.Mcv is { Count: > 0 } m
                    ? m.Select(e => new McvDto { V = e.Value, C = e.Count }).ToList()
                    : null,
            };
        }
        return JsonSerializer.Serialize(dto, WriteOptions);
    }

    /// <summary>
    /// Parse a <c>.stx</c> JSON document. Returns null on null/empty/corrupt input — never throws
    /// (corrupt stats are HINTS that simply get ignored).
    /// </summary>
    public static StxStatistics? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(json, ReadOptions);
            if (dto is null) return null;

            var src = dto.Src is null
                ? new StxSource()
                : new StxSource
                {
                    Dbf = dto.Src.Dbf,
                    RecCount = dto.Src.RecCount,
                    UpdStamp = dto.Src.UpdStamp,
                    DbfWriteUtc = dto.Src.DbfWriteUtc,
                    CdxWriteUtc = dto.Src.CdxWriteUtc,
                    Deleted = dto.Src.Deleted,
                };

            var tags = new Dictionary<string, StxTagStats>(StringComparer.Ordinal);
            if (dto.Tags is not null)
                foreach (var kv in dto.Tags)
                {
                    if (kv.Value is null) continue;
                    tags[kv.Key] = new StxTagStats
                    {
                        // KeyExpr defaults to the dictionary key (back-compat with a sidecar keyed by
                        // key expression and lacking the explicit "key" field).
                        KeyExpr = kv.Value.Key ?? kv.Key,
                        Collation = kv.Value.Coll,
                        Descending = kv.Value.Desc ?? false,
                        Ndv = kv.Value.Ndv,
                        Nulls = kv.Value.Nulls,
                        Min = kv.Value.Min,
                        Max = kv.Value.Max,
                        // PHASE C (additive): a sidecar lacking these fields decodes to null — older
                        // readers ignore them, newer ones simply have no histogram / MCV to use.
                        Histogram = kv.Value.Hist is { Count: > 0 } ? kv.Value.Hist : null,
                        Mcv = kv.Value.Mcv is { Count: > 0 } mcv
                            ? mcv.Where(e => e is not null)
                                 .Select(e => new StxMcvEntry { Value = e!.V, Count = e.C })
                                 .ToList()
                            : null,
                    };
                }

            DateTime built = default;
            if (!string.IsNullOrEmpty(dto.Built))
                _ = DateTime.TryParse(dto.Built, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out built);

            return new StxStatistics { Source = src, Built = built, Tags = tags };
        }
        catch
        {
            // Corrupt sidecar → ignored (HINT-ONLY): never throw.
            return null;
        }
    }

    /// <summary>
    /// True when this <see cref="Source"/> staleness token still matches <paramref name="table"/>:
    /// the record count PLUS the DBF header date stamp PLUS the strong file last-write timestamps
    /// (DBF and CDX). A mismatch on ANY known signal means the stats are STALE and must be ignored.
    /// Every signal degrades gracefully — a missing token on either side is skipped, never treated as
    /// a mismatch — so an older sidecar still works, just with weaker staleness detection. Staleness
    /// affects ONLY whether the stats are used (the plan); it can never change the result set.
    /// </summary>
    public bool IsFreshFor(DbfTable table)
    {
        if (table is null) return false;
        if (Source.RecCount != table.RecordCount) return false;

        // Secondary signal: the DBF header last-update stamp (DATE-ONLY — blind to same-day edits).
        if (Differ(HighlikeStatistics.ReadUpdStamp(table), Source.UpdStamp))
            return false;

        // STRONG signal: the DBF file last-write timestamp (full precision). This is what catches a
        // same-day in-place key UPDATE / reindex that touches neither the record count nor the header
        // date — the gap the date-only stamp cannot see.
        if (Differ(HighlikeStatistics.ReadDbfWriteUtc(table), Source.DbfWriteUtc))
            return false;

        // STRONG signal: the CDX file last-write timestamp (catches a reindex that rebuilt the CDX
        // without rewriting the DBF).
        if (Differ(HighlikeStatistics.ReadCdxWriteUtc(table), Source.CdxWriteUtc))
            return false;

        return true;
    }

    /// <summary>Two staleness tokens DIFFER only when both are known and unequal (a missing side is ignored).</summary>
    private static bool Differ(string? current, string? stored)
        => !string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(stored)
           && !string.Equals(current, stored, StringComparison.Ordinal);

    // ---- key normalisation: the SAME form the Core optimizer matches tag keys in ----

    internal static string NormKey(string key) => CrossVault.FoxDbf.Query.QueryOptimizer.KeyMatchForm(key);

    // ---- JSON DTOs + options (System.Text.Json, in-box only) ----

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed class Dto
    {
        [JsonPropertyName("src")] public SrcDto? Src { get; set; }
        [JsonPropertyName("built")] public string? Built { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, TagDto>? Tags { get; set; }
    }

    private sealed class SrcDto
    {
        [JsonPropertyName("dbf")] public string? Dbf { get; set; }
        [JsonPropertyName("reccount")] public int RecCount { get; set; }
        [JsonPropertyName("updstamp")] public string? UpdStamp { get; set; }
        [JsonPropertyName("dbfutc")] public string? DbfWriteUtc { get; set; }
        [JsonPropertyName("cdxutc")] public string? CdxWriteUtc { get; set; }
        [JsonPropertyName("deleted")] public int Deleted { get; set; }
    }

    private sealed class TagDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("coll")] public string? Coll { get; set; }
        [JsonPropertyName("desc")] public bool? Desc { get; set; }
        [JsonPropertyName("ndv")] public long Ndv { get; set; }
        [JsonPropertyName("nulls")] public long Nulls { get; set; }
        [JsonPropertyName("min")] public string? Min { get; set; }
        [JsonPropertyName("max")] public string? Max { get; set; }
        // PHASE C (additive). Absent on an older sidecar → null (back-compat).
        [JsonPropertyName("hist")] public List<string>? Hist { get; set; }
        [JsonPropertyName("mcv")] public List<McvDto>? Mcv { get; set; }
    }

    private sealed class McvDto
    {
        [JsonPropertyName("v")] public string? V { get; set; }
        [JsonPropertyName("c")] public long C { get; set; }
    }
}
