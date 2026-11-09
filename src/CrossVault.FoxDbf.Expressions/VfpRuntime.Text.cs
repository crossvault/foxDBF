namespace CrossVault.FoxDbf.Expressions;

internal static partial class VfpRuntime
{
    public static VfpValue DecodeByteLiteral(VfpValue value, EvaluationContext context)
    {
        // ParseFile preserves PRG literal bytes as Latin1. C1 bytes must be interpreted in
        // the active session too, so a literal byte 80 and CHR(128) denote the same value.
        var encoding = context.Encoding;
        if (encoding is null || encoding.CodePage == 28591) return value;
        var chars = value.AsString.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is >= '\u0080' and <= '\u009f')
                chars[i] = encoding.GetString([(byte)chars[i]])[0];
        return VfpValue.Character(new string(chars));
    }
}
