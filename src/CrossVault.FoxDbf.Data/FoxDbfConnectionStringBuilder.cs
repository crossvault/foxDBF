using System;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>Which query accelerator the connection routes SELECT / COUNT through.</summary>
public enum FoxDbfAccelerator
{
    /// <summary>The plain Core Rushmore optimizer (default).</summary>
    None,
    /// <summary>The opt-in Highlike cost-based accelerator (<c>CrossVault.FoxDbf.Highlike</c>).</summary>
    Highlike,
}

/// <summary>
/// Typed connection-string builder for the FoxDbf ADO.NET provider. The keys mirror the VFPOLEDB
/// provider where one exists, and map onto the <c>VfpSession</c>'s <c>EvaluationContext</c> (collation,
/// SET DELETED, SET ANSI), its lock mode (Exclusive / ReadOnly) and the query accelerator.
/// <list type="bullet">
///   <item><c>Data Source</c> — a <c>.dbc</c>, a directory of free tables, or a single <c>.dbf</c>.</item>
///   <item><c>Collate</c> — <c>machine</c> | <c>general</c> (default machine).</item>
///   <item><c>Exclusive</c> — <c>true</c> | <c>false</c> (default false → SHARED).</item>
///   <item><c>Deleted</c> — <c>on</c> | <c>off</c> (default on → exclude deleted).</item>
///   <item><c>Ansi</c> — <c>on</c> | <c>off</c> (default off).</item>
///   <item><c>SessionCodePage</c> — VFP text session code page (default: none → each table's own code page, lossless).</item>
///   <item><c>ReadOnly</c> — <c>true</c> | <c>false</c> (default false).</item>
///   <item><c>Accelerator</c> — <c>Highlike</c> | <c>None</c> (default None).</item>
///   <item><c>EnforceRules</c> (alias <c>EnforceRI</c>) — <c>on</c> | <c>off</c> (default off → RAW writes):
///   when on AND a <c>.dbc</c> is open, INSERT/UPDATE/DELETE run through the VFP write-model (field
///   DEFAULTs, field/record RULEs, NOT NULL, then the bound RI trigger) via microVFP, exactly like VFP9.</item>
/// </list>
/// </summary>
public sealed class FoxDbfConnectionStringBuilder : DbConnectionStringBuilder
{
    public const string DataSourceKey = "Data Source";
    public const string CollateKey = "Collate";
    public const string ExclusiveKey = "Exclusive";
    public const string DeletedKey = "Deleted";
    public const string AnsiKey = "Ansi";
    public const string ReadOnlyKey = "ReadOnly";
    public const string AcceleratorKey = "Accelerator";
    public const string EnforceRulesKey = "EnforceRules";
    public const string SessionCodePageKey = "SessionCodePage";
    /// <summary>Alias for <see cref="EnforceRulesKey"/> — both map to the same opt-in enforcement flag.</summary>
    public const string EnforceRiKey = "EnforceRI";

    public FoxDbfConnectionStringBuilder() { }

    public FoxDbfConnectionStringBuilder(string? connectionString)
    {
        ConnectionString = connectionString ?? string.Empty;
    }

    /// <summary>The data source path: a <c>.dbc</c>, a free-table directory, or a single <c>.dbf</c>.</summary>
    public string DataSource
    {
        get => GetString(DataSourceKey, "");
        set => this[DataSourceKey] = value;
    }

    /// <summary>VFP text session code page. Null (default) reads and writes each table's own code page
    /// losslessly; a value applies VFP9's session-to-table best-fit conversion, as a VFP9 session would.</summary>
    public int? SessionCodePage
    {
        get => TryGetValue(SessionCodePageKey, out var value) && value is not null && value.ToString() is { Length: > 0 } text
            ? Convert.ToInt32(text, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        set
        {
            if (value is null) Remove(SessionCodePageKey);
            else this[SessionCodePageKey] = value.Value;
        }
    }

    /// <summary>The collation: <c>machine</c> (default) or <c>general</c>.</summary>
    public string Collate
    {
        get => GetString(CollateKey, "machine");
        set => this[CollateKey] = value;
    }

    /// <summary>Open EXCLUSIVE (default false → SHARED).</summary>
    public bool Exclusive
    {
        get => GetBool(ExclusiveKey, false);
        set => this[ExclusiveKey] = value;
    }

    /// <summary>SET DELETED (default ON → deleted rows excluded).</summary>
    public bool Deleted
    {
        get => GetOnOff(DeletedKey, true);
        set => this[DeletedKey] = value ? "on" : "off";
    }

    /// <summary>SET ANSI (default OFF).</summary>
    public bool Ansi
    {
        get => GetOnOff(AnsiKey, false);
        set => this[AnsiKey] = value ? "on" : "off";
    }

    /// <summary>Open read-only (default false).</summary>
    public bool ReadOnly
    {
        get => GetBool(ReadOnlyKey, false);
        set => this[ReadOnlyKey] = value;
    }

    /// <summary>The query accelerator (default <see cref="FoxDbfAccelerator.None"/>).</summary>
    public FoxDbfAccelerator Accelerator
    {
        get => Enum.TryParse<FoxDbfAccelerator>(GetString(AcceleratorKey, "None"), ignoreCase: true, out var a)
            ? a : FoxDbfAccelerator.None;
        set => this[AcceleratorKey] = value.ToString();
    }

    /// <summary>
    /// Opt-in DBC rule/RI enforcement on writes (default OFF → RAW DML, byte-for-byte unchanged). When ON
    /// and a <c>.dbc</c> is open, INSERT/UPDATE/DELETE run through the VFP write-model via microVFP. Reads
    /// either the <c>EnforceRules</c> key or its <c>EnforceRI</c> alias (on/off or true/false).
    /// </summary>
    public bool EnforceRules
    {
        get => GetOnOff(EnforceRulesKey, GetOnOff(EnforceRiKey, false));
        set => this[EnforceRulesKey] = value ? "on" : "off";
    }

    private string GetString(string key, string fallback)
        => TryGetValue(key, out var v) && v is not null ? v.ToString() ?? fallback : fallback;

    private bool GetBool(string key, bool fallback)
        => TryGetValue(key, out var v) && v is not null && bool.TryParse(v.ToString(), out var b) ? b : fallback;

    private bool GetOnOff(string key, bool fallback)
    {
        if (!TryGetValue(key, out var v) || v is null) return fallback;
        string s = v.ToString() ?? "";
        if (string.Equals(s, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }
}
