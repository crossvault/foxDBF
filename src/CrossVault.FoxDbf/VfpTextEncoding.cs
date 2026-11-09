using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>Field text crosses the session/table boundary as bytes, using VFP's best-fit maps.</summary>
internal sealed class VfpTextEncoding(Encoding session, byte[] writeMap, byte[] readMap) : Encoding(session.CodePage)
{
    public override bool IsSingleByte => true;
    public override int GetByteCount(char[] chars, int index, int count) => session.GetByteCount(chars, index, count);
    public override int GetByteCount(ReadOnlySpan<char> chars) => session.GetByteCount(chars);
    public override int GetMaxByteCount(int charCount) => session.GetMaxByteCount(charCount);
    public override int GetMaxCharCount(int byteCount) => session.GetMaxCharCount(byteCount);
    public override int GetCharCount(byte[] bytes, int index, int count) => count;
    public override int GetCharCount(ReadOnlySpan<byte> bytes) => bytes.Length;
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        => GetBytes(chars.AsSpan(charIndex, charCount), bytes.AsSpan(byteIndex));
    public override int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        int count = session.GetBytes(chars, bytes);
        for (int i = 0; i < count; i++) bytes[i] = writeMap[bytes[i]];
        return count;
    }
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        => GetChars(bytes.AsSpan(byteIndex, byteCount), chars.AsSpan(charIndex));
    public override int GetChars(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        Span<byte> converted = bytes.Length <= 512 ? stackalloc byte[bytes.Length] : new byte[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) converted[i] = readMap[bytes[i]];
        return session.GetChars(converted, chars);
    }
}
