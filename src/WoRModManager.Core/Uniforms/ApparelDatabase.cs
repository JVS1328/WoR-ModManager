using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WoRModManager.Core.Uniforms;

public sealed record ApparelItem(uint Id, string Name, int Category, IReadOnlyList<uint> Children)
{
    public override string ToString() => Name;
}
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
                if (Entry(zip, "Generated/outfitter.bin") is { } o) uniforms = ByRegiment(uniforms, ReadAll(o));
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

    // uniforms.bin names keep the game's regiments even when a mod renames them (MaW's "8th Alabama" is "8vo de Linea"), and
    // a mod's regiments may use only some uniforms. The game's lists come from outfitter.bin: each regiment record (crc32 of
    // its id, the id, Identifier, Name) is followed by its classes, whose uniform ids appear in it. So each uniform is listed
    // under every regiment that uses it, as "<Identifier> <Name> - <rank>"; uniforms no regiment uses are left out.
    public static IReadOnlyList<UniformDef> ByRegiment(IReadOnlyList<UniformDef> uniforms, byte[] outfitter)
    {
        var regiments = FindRegiments(outfitter);
        if (regiments.Count == 0) return uniforms;
        var byKey = uniforms.GroupBy(u => u.Key).ToDictionary(g => g.Key, g => g.First());
        var result = new List<UniformDef>();
        for (var r = 0; r < regiments.Count; r++)
        {
            var (_, end, title) = regiments[r];
            var stop = r + 1 < regiments.Count ? regiments[r + 1].Start : outfitter.Length;
            var listed = new HashSet<uint>();
            for (var i = end; i + 4 <= stop; i++)
                if (byKey.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(outfitter.AsSpan(i)), out var u) && !u.IsHead && listed.Add(u.Key))
                    result.Add(u with { DisplayName = $"{title} - {Rank(u.DisplayName)}" });
        }
        result.AddRange(uniforms.Where(u => u.IsHead));
        return result;
    }

    // "8th Alabama - Colonel" -> "Colonel"; "- " also catches the game's "Palmetto Sharpshooters- Private"
    static string Rank(string displayName) => displayName.IndexOf("- ", StringComparison.Ordinal) is var i and >= 0 ? displayName[(i + 2)..].Trim() : displayName;

    static List<(int Start, int End, string Title)> FindRegiments(byte[] data)
    {
        var found = new List<(int, int, string)>();
        var s = data.AsSpan();
        for (var p = 4; p < s.Length; p++)
        {
            if (CStringAt(s, p, 4, 100) is not { } idEnd || Crc32.Lowercase(s[p..idEnd]) != BinaryPrimitives.ReadUInt32LittleEndian(s[(p - 4)..])) continue;
            // Identifier may be empty and mods' names may hold accents (MaW's "Batallón"), stored as UTF-8
            if (CStringAt(s, idEnd + 1, 0, 80, utf8: true) is not { } identEnd || CStringAt(s, identEnd + 1, 0, 80, utf8: true) is not { } nameEnd) continue;
            var title = $"{Encoding.UTF8.GetString(s[(idEnd + 1)..identEnd])} {Encoding.UTF8.GetString(s[(identEnd + 1)..nameEnd])}".Trim();
            found.Add((p - 4, nameEnd + 1, title));
            p = nameEnd;
        }
        return found;
    }

    // The index of the NUL ending a printable string of min..max bytes starting at p, or null.
    static int? CStringAt(ReadOnlySpan<byte> s, int p, int min, int max, bool utf8 = false)
    {
        var end = p;
        while (end < s.Length && end - p <= max && (s[end] is >= 0x20 and <= 0x7E || (utf8 && s[end] >= 0x80))) end++;
        return end < s.Length && s[end] == 0 && end - p >= min && end - p <= max ? end : null;
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
