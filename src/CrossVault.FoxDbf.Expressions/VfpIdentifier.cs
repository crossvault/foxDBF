namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// The one character rule for FoxPro identifiers (field, variable, alias, procedure names), shared by the
/// expression lexer and every microVFP scanner so they cannot drift apart: a name starts with a letter or
/// <c>_</c> and continues with letters, decimal digits or <c>_</c>, where "letter" is any Unicode letter.
/// VFP9 accepts <c>razónSoc</c> as a field name and <c>razónSocial</c> as a DBC long name.
/// <para>
/// Decision (2026-10-22): deliberately LENIENT. A byte-by-byte VFP9 probe of CP1252 (0x80–0xFF, both as a
/// variable name and as a <c>CREATE CURSOR</c> field name, first and later position) accepted exactly the
/// letters .NET accepts EXCEPT six: ˆ (0x88, a Unicode modifier letter), ª (0xAA), µ (0xB5), º (0xBA), Ž (0x8E)
/// and ž (0x9E) — Ž/ž were added to CP1252 after VFP's character tables were frozen (VFP's UPPER('ž') also
/// leaves it unchanged). The oracle UnicodeNamesVfp9OracleTests re-runs that probe. Rejecting
/// them here would only make sense per code page (the rule is VFP's per-code-page character table, and the
/// probe covers CP1252 only), while this lexer works on already-decoded text. Accepting a superset costs
/// nothing on the read side: such a name can only come from a non-VFP writer, and we can still address it.
/// The write side refuses only what a code page cannot store; it does not enforce VFP's letter table.
/// </para>
/// </summary>
internal static class VfpIdentifier
{
    /// <summary>True when <paramref name="c"/> may start an identifier (a Unicode letter or <c>_</c>).</summary>
    public static bool IsStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>True when <paramref name="c"/> may continue an identifier (a Unicode letter, decimal digit or <c>_</c>).</summary>
    public static bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
