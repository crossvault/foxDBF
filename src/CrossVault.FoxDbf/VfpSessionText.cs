using System.IO.Compression;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>VFP9's single-byte CPCONVERT maps for fields, memos and SEEK values.</summary>
internal static class VfpSessionText
{
    // VFP best-fit conversion differs from Unicode/.NET conversion (including representable
    // Greek accents). These tables are measured, not inferred; see SessionCollationVfp9OracleTests.
    private static readonly Dictionary<(int From, int To), byte[]> Maps = LoadMaps();

    internal static Encoding ResolveSession(int codePage) => codePage switch
    {
        620 => Encodings.ResolveEncoding(0x69),
        895 => Encodings.ResolveEncoding(0x68),
        _ => Encoding.GetEncoding(codePage),
    };

    internal static Encoding ForFields(Encoding table, Encoding? session)
    {
        // Native-page tables keep their original encoder; no per-character conversion or map lookup.
        if (session is null || session.CodePage == table.CodePage) return table;
        return Maps.TryGetValue((session.CodePage, table.CodePage), out var write)
            && Maps.TryGetValue((table.CodePage, session.CodePage), out var read)
            ? new VfpTextEncoding(session, write, read) : table;
    }

    internal static byte[] ConvertBytes(byte[] bytes, int from, int to)
    {
        if (from == to) return bytes;
        if (!Maps.TryGetValue((from, to), out var map))
            return Encoding.Convert(ResolveSession(from), ResolveSession(to), bytes);
        for (int i = 0; i < bytes.Length; i++) bytes[i] = map[bytes[i]];
        return bytes;
    }

    internal static string ForTable(string value, Encoding session, Encoding table, bool byteString = false)
    {
        if (session.CodePage == table.CodePage) return value;
        if (!Maps.TryGetValue((session.CodePage, table.CodePage), out var map)) return value;
        if (byteString)
        {
            // The historical default interpreter exposes CHR's C1 bytes as Latin1 controls.
            // Ordinary Unicode literals still encode in the session page.
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
                if (characters[i] is >= '\u0080' and <= '\u009f')
                    characters[i] = session.GetString([(byte)characters[i]])[0];
            value = new string(characters);
        }
        byte[] bytes = session.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++) bytes[i] = map[bytes[i]];
        return table.GetString(bytes);
    }

    private static Dictionary<(int, int), byte[]> LoadMaps()
    {
        using var resource = typeof(VfpSessionText).Assembly.GetManifestResourceStream("CrossVault.FoxDbf.SessionConversions.bin.gz")!;
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        int count = reader.ReadInt32();
        var maps = new Dictionary<(int, int), byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int from = reader.ReadInt32(), to = reader.ReadInt32();
            byte[] map = reader.ReadBytes(256);
            if (map.Length != 256) throw new InvalidDataException("Incomplete VFP session conversion table.");
            maps.Add((from, to), map);
        }
        return maps;
    }
}
