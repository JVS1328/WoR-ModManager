using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WoRModManager.Core.Uniforms;

public sealed record ApparelItem(uint Id, string Name, int Category, IReadOnlyList<uint> Children);
public sealed record UniformDef(uint Key, string DisplayName, bool IsHead, IReadOnlyList<uint> Allowed);

// The game's compiled uniform and apparel lists (Generated/*.bin), read from the GameData pak the game loads last.
public sealed class ApparelDatabase
{
    public const uint HumanHeadKey = 1820244368; // crc32("humanhead"): the head tab's key and hierarchy

    ApparelDatabase(string sourcePak, IReadOnlyList<UniformDef> uniforms, IReadOnlyDictionary<uint, ApparelItem> items) =>
        (SourcePak, Uniforms, Items) = (sourcePak, uniforms, items);

    public string SourcePak { get; }
    public IReadOnlyList<UniformDef> Uniforms { get; }
    public IReadOnlyDictionary<uint, ApparelItem> Items { get; }

    public static ApparelDatabase? Load(string assetsDir)
    {
        if (!Directory.Exists(assetsDir)) return null;
        // A mod's z_-prefixed pak loads after the game's own, so its database wins.
        foreach (var pak in Directory.GetFiles(assetsDir, "*.pak").OrderByDescending(f => Path.GetFileName(f).ToUpperInvariant(), StringComparer.Ordinal))
        {
            try
            {
                using var zip = ZipFile.OpenRead(pak);
                if (Entry(zip, "Generated/uniforms.bin") is not { } u) continue;
                var uniforms = ParseUniforms(ReadAll(u));
                var apparel = Entry(zip, "Generated/apparel.bin") is { } a ? ReadAll(a) : [];
                var items = ScanApparel(apparel, uniforms.SelectMany(x => x.Allowed).ToHashSet());
                return new ApparelDatabase(Path.GetFileName(pak), uniforms, items);
            }
            catch (InvalidDataException) { } // not a zip
        }
        return null;
    }

    public static IReadOnlyList<UniformDef> ParseUniforms(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var count = r.ReadUInt32();
        var list = new List<UniformDef>((int)Math.Min(count, 10_000));
        for (var i = 0; i < count; i++)
        {
            var key = r.ReadUInt32();
            var name = ReadCString(r);
            var hier = r.ReadUInt32();
            r.ReadByte(); // flag
            var ids = new uint[r.ReadUInt16()];
            for (var j = 0; j < ids.Length; j++) ids[j] = r.ReadUInt32();
            list.Add(new UniformDef(key, name, hier == HumanHeadKey, ids));
        }
        return list;
    }

    // apparel.bin records carry a layout after their children that isn't decoded, so instead of walking records this
    // finds each wanted id where it's followed by a plausible record head: printable name, category, flags, children.
    public static Dictionary<uint, ApparelItem> ScanApparel(byte[] data, IReadOnlySet<uint> wanted)
    {
        var found = new Dictionary<uint, ApparelItem>();
        var span = data.AsSpan();
        for (var i = 0; i + 4 <= span.Length && found.Count < wanted.Count; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(span[i..]);
            if (!wanted.Contains(id) || found.ContainsKey(id)) continue;
            if (TryRecord(span, i + 4, id) is { } item) found[id] = item;
        }
        return found;
    }

    static ApparelItem? TryRecord(ReadOnlySpan<byte> s, int p, uint id)
    {
        var end = p;
        while (end < s.Length && end - p <= 200 && s[end] is >= 0x20 and <= 0x7E) end++;
        if (end >= s.Length || s[end] != 0 || end - p < 2 || end - p > 200) return null;
        var name = Encoding.ASCII.GetString(s[p..end]);
        p = end + 1;
        if (p + 7 > s.Length) return null;
        var category = s[p];
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(s[(p + 1)..]);
        var n = BinaryPrimitives.ReadUInt16LittleEndian(s[(p + 5)..]);
        p += 7;
        if (category > 20 || flags > 255 || p + 4L * n > s.Length) return null;
        var children = new uint[n];
        for (var j = 0; j < n; j++) children[j] = BinaryPrimitives.ReadUInt32LittleEndian(s[(p + 4 * j)..]);
        return new ApparelItem(id, name, category, children);
    }

    static ZipArchiveEntry? Entry(ZipArchive zip, string path) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    static byte[] ReadAll(ZipArchiveEntry e)
    {
        using var s = e.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>();
        for (byte b; (b = r.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
