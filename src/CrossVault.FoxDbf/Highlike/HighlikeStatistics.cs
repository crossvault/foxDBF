using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// The Phase A statistics facade for the Highlike accelerator (design §6): builds, persists, and
/// loads the <c>.stx</c> sidecar that backs the cost-based planner. <see cref="Analyze"/> is the
/// explicit UPDATE-STATISTICS equivalent VFP never had; <see cref="GetOrBuild"/> is the lazy
/// auto-build used on first use when statistics are enabled and no fresh sidecar exists.
/// </summary>
/// <remarks>
/// Everything here produces HINTS ONLY. The sidecar may be stale, missing, or corrupt; in every
/// such case the engine degrades to the Core heuristics and the RESULT SET is unchanged — only the
/// plan (speed) can differ. Never throws on bad data: a missing / stale / corrupt <c>.stx</c>
/// resolves to "no statistics".
/// </remarks>
public static class HighlikeStatistics
{
    /// <summary>The sidecar file extension.</summary>
    public const string Extension = ".stx";

    /// <summary>
    /// The <c>.stx</c> sidecar path for <paramref name="table"/> (the <c>.dbf</c> path with the
    /// extension swapped to <c>.stx</c>). Throws <see cref="InvalidOperationException"/> for a
    /// stream-only table (no <see cref="DbfTable.SourcePath"/>): a sidecar needs a file location.
    /// </summary>
    public static string StxPath(DbfTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string? path = table.SourcePath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                "Cannot locate a .stx sidecar for a stream-only table (no SourcePath).");
        return Path.ChangeExtension(path, Extension);
    }

    /// <summary>
    /// Build statistics in ONE pass over each tag (via <see cref="CdxTag.EnumerateEntries"/>, in
    /// sorted key order) WITHOUT writing anything. <paramref name="cdx"/> null → no tag stats (only
    /// the table-level <c>src</c> block). Never throws on a malformed tag.
    /// </summary>
    public static StxStatistics Build(DbfTable table, CdxFile? cdx = null, HighlikeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        options ??= HighlikeOptions.Default;

        // ---- one DBF pass: deleted-count (Peak 6) + per-column DBF-null-flag counts (design §6:
        // nulls come from the DBF null flags, NEVER the CDX, whose null-key encoding is unreliable).
        var nullCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        try
        {
            foreach (var rec in table.EnumerateAll(includeDeleted: true))
            {
                if (rec.IsDeleted) deleted++;
                foreach (var col in table.Columns)
                {
                    if (rec[col.Name] is null)
                    {
                        nullCounts.TryGetValue(col.Name, out long c);
                        nullCounts[col.Name] = c + 1;
                    }
                }
            }
        }
        catch
        {
            // Never throw on bad data — partial counts are still HINTS only.
        }

        // ---- per-tag stats: one pass over the sorted CDX entries (NDV / Min / Max). ----
        var tags = new Dictionary<string, StxTagStats>(StringComparer.Ordinal);
        if (cdx is not null)
        {
            foreach (string tagName in cdx.TagNames)
            {
                var tag = cdx.Tag(tagName);
                if (tag is null) continue;
                try
                {
                    // Key by tag NAME (unique within a CDX) so two tags that share a key expression but
                    // differ in collation / ascending-descending cannot overwrite each other.
                    tags[tag.Name] = BuildTag(table, tag, nullCounts, options);
                }
                catch
                {
                    // Skip a malformed tag rather than failing the whole build.
                }
            }
        }

        var source = new StxSource
        {
            Dbf = string.IsNullOrEmpty(table.SourcePath) ? null : Path.GetFileName(table.SourcePath),
            RecCount = table.RecordCount,
            UpdStamp = ReadUpdStamp(table),
            DbfWriteUtc = ReadDbfWriteUtc(table),
            CdxWriteUtc = ReadCdxWriteUtc(table),
            Deleted = deleted,
        };

        return new StxStatistics
        {
            Source = source,
            Built = DateTime.UtcNow,
            Tags = tags,
        };
    }

    /// <summary>
    /// Build statistics (see <see cref="Build"/>) AND persist them to the <c>.stx</c> sidecar — the
    /// explicit UPDATE-STATISTICS equivalent. Idempotent in its captured values. Returns the built
    /// statistics.
    /// </summary>
    public static StxStatistics Analyze(DbfTable table, CdxFile? cdx = null, HighlikeOptions? options = null)
    {
        var stats = Build(table, cdx, options);
        try
        {
            File.WriteAllText(StxPath(table), stats.ToJson());
        }
        catch
        {
            // A failed write leaves us with no (or a stale) sidecar — still HINTS only, never a
            // correctness problem. The built stats are returned regardless.
        }
        return stats;
    }

    /// <summary>
    /// Load the <c>.stx</c> sidecar for <paramref name="table"/> and return it ONLY when fresh
    /// (its staleness token still matches the table). Returns null when the sidecar is missing,
    /// stale, or corrupt — never throws. Does NOT rebuild.
    /// </summary>
    public static StxStatistics? Load(DbfTable table, CdxFile? cdx = null)
    {
        try
        {
            string path = StxPath(table);
            if (!File.Exists(path)) return null;
            var stats = StxStatistics.FromJson(File.ReadAllText(path));
            if (stats is null) return null;
            return stats.IsFreshFor(table) ? stats : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Return fresh statistics for <paramref name="table"/>, lazily building + persisting them (via
    /// <see cref="Analyze"/>) when the sidecar is missing / stale / corrupt. The lazy auto-build
    /// entry point.
    /// </summary>
    public static StxStatistics GetOrBuild(DbfTable table, CdxFile? cdx = null, HighlikeOptions? options = null)
        => Load(table, cdx) ?? Analyze(table, cdx, options);

    // ============================================================ internals

    /// <summary>
    /// Read the DBF header last-update stamp (bytes 1..3 = yy/mm/dd) as <c>yyyy-MM-dd</c>; null
    /// when the table is stream-only or the file cannot be read. Used as the secondary staleness
    /// signal in <see cref="StxStatistics.IsFreshFor"/>.
    /// </summary>
    internal static string? ReadUpdStamp(DbfTable table)
    {
        string? path = table?.SourcePath;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> h = stackalloc byte[4];
            fs.ReadExactly(h);
            int year = 1900 + h[1];
            return string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{h[2]:D2}-{h[3]:D2}");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The DBF file's last-write timestamp (full UTC, round-trip "O") — the STRONG staleness signal in
    /// <see cref="StxStatistics.IsFreshFor"/>. Null when stream-only or unreadable. Unlike the date-only
    /// header stamp this catches a same-day in-place key edit / reindex.
    /// </summary>
    internal static string? ReadDbfWriteUtc(DbfTable table)
        => ReadFileWriteUtc(table?.SourcePath);

    /// <summary>
    /// The CDX file's last-write timestamp (full UTC, round-trip "O") — catches a reindex that rebuilt
    /// the CDX without rewriting the DBF. Null when there is no CDX next to the DBF or it is unreadable.
    /// </summary>
    internal static string? ReadCdxWriteUtc(DbfTable table)
    {
        string? path = table?.SourcePath;
        if (string.IsNullOrEmpty(path)) return null;
        return ReadFileWriteUtc(Path.ChangeExtension(path, ".cdx"));
    }

    private static string? ReadFileWriteUtc(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTimeUtc(path).ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Build one tag's stats by a SINGLE walk over its sorted entries: NDV (a raw-key change
    /// increments it), Min (smallest stored key), Max (largest stored key). Nulls come from the DBF
    /// null-flag pass, resolved by the tag's underlying field.
    /// </summary>
    private static StxTagStats BuildTag(DbfTable table, CdxTag tag, Dictionary<string, long> nullCounts,
        HighlikeOptions options)
    {
        // Feed the SINGLE sorted walk to the harvester in ASCENDING key order. CdxTag.EnumerateEntries()
        // yields a DESCENDING tag in REVERSE of the stored (ascending) order ("DESC == ASC reversed",
        // CdxTag.cs), so reverse it back — still one tree walk. NDV / Min / Max / Histogram / MCV all
        // come from this one pass (design §6/§9: free-rider at warm-up, no second scan).
        IEnumerable<IndexEntry> ascending = tag.Descending
            ? tag.EnumerateEntries().Reverse()
            : tag.EnumerateEntries();

        long nulls = ResolveNullCount(table, tag, nullCounts);
        return BuildTagStats(tag, ascending, nulls, options.HistogramBuckets, options.McvCount);
    }

    /// <summary>
    /// Harvest one tag's statistics from a SINGLE pass over its entries in ASCENDING key order: NDV (a
    /// raw-key change), Min (first key) / Max (last key), the equi-depth HISTOGRAM (design §9: every
    /// <c>count / buckets</c>-th key is a bucket boundary) and the MCV top-K (design §9: a run of equal
    /// keys is a value frequency). The <paramref name="ascendingEntries"/> sequence is enumerated EXACTLY
    /// ONCE — the timing-independent "no second scan" contract. Never throws.
    /// </summary>
    internal static StxTagStats BuildTagStats(CdxTag tag, IEnumerable<IndexEntry> ascendingEntries,
        long nulls, int histogramBuckets, int mcvCount)
    {
        long ndv = 0;
        byte[]? first = null, last = null, prev = null;
        long totalCount = 0;

        // Harvest in ONE pass: NDV, Min/Max, histogram positions, and MCV frequencies.
        var entries = new List<IndexEntry>();
        var keyToCount = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var entry in ascendingEntries)
        {
            byte[] key = entry.Key;
            if (prev is null || !key.AsSpan().SequenceEqual(prev))
                ndv++;
            first ??= key;
            last = key;
            prev = key;
            totalCount++;
            entries.Add(entry);

            // Accumulate the count for this key (by tracking runs of equal keys).
            string keyStr = FormatKey(tag, key) ?? string.Empty;
            keyToCount[keyStr] = keyToCount.TryGetValue(keyStr, out long c) ? c + 1 : 1;
        }

        string? min = first is null ? null : FormatKey(tag, first);
        string? max = last is null ? null : FormatKey(tag, last);

        // Build the equi-depth histogram if requested.
        IReadOnlyList<string>? histogram = null;
        if (histogramBuckets > 0 && entries.Count > 0 && tag.KeyType switch
        {
            IndexKeyType.Integer or IndexKeyType.Numeric or IndexKeyType.Date or IndexKeyType.DateTime => true,
            _ => IsIdentityCollation(tag.Collation), // character keys under MACHINE collation are comparable
        })
        {
            var histBoundaries = new List<string>();
            for (int j = 0; j <= histogramBuckets; j++)
            {
                long pos = (long)j * totalCount / histogramBuckets;
                if (pos < 0) pos = 0;
                if (pos > totalCount - 1) pos = totalCount - 1;
                byte[] boundaryKey = entries[(int)pos].Key;
                string? bound = FormatKey(tag, boundaryKey);
                if (bound is not null)
                    histBoundaries.Add(bound);
            }
            histogram = histBoundaries;
        }

        // Build the MCV (top-K most-common values) if requested.
        IReadOnlyList<StxMcvEntry>? mcv = null;
        if (mcvCount > 0 && keyToCount.Count > 0)
        {
            var topK = keyToCount
                .Select(kv => (value: kv.Key, count: kv.Value))
                .OrderByDescending(t => t.count)
                .ThenBy(t => t.value)
                .Take(mcvCount)
                .Select(t => new StxMcvEntry { Value = t.value, Count = t.count })
                .ToList();
            mcv = topK;
        }

        return new StxTagStats
        {
            KeyExpr = tag.KeyExpression,
            Collation = tag.Collation,
            Descending = tag.Descending,
            Ndv = ndv,
            Nulls = nulls,
            Min = min,
            Max = max,
            Histogram = histogram,
            Mcv = mcv,
        };
    }

    /// <summary>
    /// Render a stored key as an invariant string. Numeric / Date / DateTime / Integer keys are decoded
    /// via the tag's key type. Character / composite / unknown keys (e.g. <c>UPPER(NAME)</c>) depend on
    /// the COLLATION: under a MACHINE (identity) collation the stored bytes ARE the original characters,
    /// so they decode to readable text (trailing pad trimmed). Under any OTHER collation (e.g. GENERAL)
    /// the stored CDX key bytes are collation WEIGHT bytes — NOT characters — so rendering them as text
    /// would emit garbage and silently break comparability; instead we persist the raw weight bytes as a
    /// <c>0x…</c> hex string (trailing pad trimmed), which stays comparable in collation-byte space. The
    /// tag's collation is stored on the stat so a reader knows how to interpret the bound.
    /// </summary>
    private static string? FormatKey(CdxTag tag, byte[] keyBytes) => tag.KeyType switch
    {
        IndexKeyType.Integer or IndexKeyType.Numeric or IndexKeyType.Date or IndexKeyType.DateTime
            => FormatValue(tag.DecodeKey(keyBytes).Value),
        _ => FormatCharacterKey(tag, keyBytes),
    };

    /// <summary>
    /// Render a character / composite key. MACHINE (identity) collation → readable text; any other
    /// collation → the raw collation-weight bytes as a <c>0x…</c> hex string (see <see cref="FormatKey"/>).
    /// </summary>
    private static string? FormatCharacterKey(CdxTag tag, byte[] keyBytes)
    {
        if (IsIdentityCollation(tag.Collation))
            return tag.CharacterEncoding.GetString(keyBytes).TrimEnd(' ', '\0');

        // Non-MACHINE collation: weight bytes. Trim the trailing 0x20 char-key pad (and any trailing
        // 0x00) before hex-encoding so equal-but-differently-padded bounds compare equal.
        ReadOnlySpan<byte> span = keyBytes;
        span = span.TrimEnd((byte)0x20);
        span = span.TrimEnd((byte)0x00);
        return "0x" + Convert.ToHexString(span);
    }

    /// <summary>True for the MACHINE / identity collation (the only one whose stored key bytes are the original characters).</summary>
    private static bool IsIdentityCollation(string? collation)
        => string.IsNullOrEmpty(collation)
           || string.Equals(collation, "MACHINE", StringComparison.OrdinalIgnoreCase);

    private static string? FormatValue(object? v) => v switch
    {
        null => null,
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>
    /// Resolve the DBF-null count for a tag from its underlying field: an exact field-name key
    /// (e.g. <c>CODE</c>) maps directly; a single-field function/composite key (e.g.
    /// <c>UPPER(NAME)</c>) maps to that one referenced column. Anything ambiguous → 0 (HINT only).
    /// </summary>
    private static long ResolveNullCount(DbfTable table, CdxTag tag, Dictionary<string, long> nullCounts)
    {
        string expr = tag.KeyExpression ?? string.Empty;

        // Exact field-name key.
        foreach (var col in table.Columns)
            if (string.Equals(col.Name, expr.Trim(), StringComparison.OrdinalIgnoreCase))
                return nullCounts.TryGetValue(col.Name, out long n) ? n : 0;

        // Single column referenced as an identifier inside the expression.
        var tokens = Tokenize(expr);
        string? only = null;
        foreach (var col in table.Columns)
        {
            if (tokens.Contains(col.Name.ToUpperInvariant()))
            {
                if (only is not null) return 0; // more than one field → ambiguous
                only = col.Name;
            }
        }
        if (only is not null)
            return nullCounts.TryGetValue(only, out long n) ? n : 0;

        return 0;
    }

    /// <summary>Split an expression into upper-cased identifier tokens (letters/digits/underscore).</summary>
    private static HashSet<string> Tokenize(string expr)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        while (i < expr.Length)
        {
            if (char.IsLetter(expr[i]) || expr[i] == '_')
            {
                int start = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                set.Add(expr.Substring(start, i - start).ToUpperInvariant());
            }
            else
            {
                i++;
            }
        }
        return set;
    }
}
