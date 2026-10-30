namespace WoRModManager.Core.Uniforms;

// The game's ids: standard CRC-32 (zlib polynomial) of the lowercased definition name, like CryEngine's CCrc32::ComputeLowercase.
public static class Crc32
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Lowercase(string s) => Lowercase(System.Text.Encoding.ASCII.GetBytes(s));

    public static uint Lowercase(ReadOnlySpan<byte> ascii)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in ascii) crc = Table[(crc ^ (b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b)) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
