using System.Globalization;
using System.IO.Compression;
using WoRModManager.Core.State;

namespace WoRModManager.Core;

public record Overlap(string ModA, string ModB, int FileCount, IReadOnlyList<string> TopFolders)
{
    public static string Message(string nameA, string nameB, Overlap o) => string.Create(CultureInfo.InvariantCulture,
        $"{nameA} and {nameB} both change {o.FileCount:N0} game files, mainly {string.Join(", ", o.TopFolders)}. Running both may break the game visually or functionally.");
}

public class OverlapDetector
{
    readonly Dictionary<string, HashSet<string>> cache = [];

    public IReadOnlyList<Overlap> Find(IReadOnlyList<ModRecord> enabledMods, GamePaths p)
    {
        var files = enabledMods.Select(m => Files(m, p)).ToList();
        var result = new List<Overlap>();
        for (var i = 0; i < enabledMods.Count; i++)
            for (var j = i + 1; j < enabledMods.Count; j++)
                if (Compare(enabledMods[i], files[i], enabledMods[j], files[j]) is { } o) result.Add(o);
        return result;
    }

    public IReadOnlyList<Overlap> FindFor(ModRecord candidate, IReadOnlyList<ModRecord> enabledMods, GamePaths p)
    {
        var mine = Files(candidate, p);
        return enabledMods.Where(m => !StringComparer.OrdinalIgnoreCase.Equals(m.Id, candidate.Id))
            .Select(m => Compare(candidate, mine, m, Files(m, p))).OfType<Overlap>().ToList();
    }

    static Overlap? Compare(ModRecord a, HashSet<string> fa, ModRecord b, HashSet<string> fb)
    {
        var shared = fa.Where(fb.Contains).ToList();
        if (shared.Count == 0) return null;
        var top = shared.Select(f => f.Split('/')[..^1]).Where(dirs => dirs.Length > 0).Select(dirs => string.Join('/', dirs.Take(2)))
            .GroupBy(d => d).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(3).Select(g => g.Key).ToList();
        return new(a.Id, b.Id, shared.Count, top);
    }

    HashSet<string> Files(ModRecord m, GamePaths p)
    {
        var all = new HashSet<string>();
        foreach (var n in m.Paks.Where(PakNames.IsSafePakName))
        {
            var info = new FileInfo(Path.Combine(m.Location(p), n));
            if (!info.Exists) continue;
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (!cache.TryGetValue(key, out var entries)) cache[key] = entries = Read(info.FullName);
            all.UnionWith(entries);
        }
        return all;
    }

    static HashSet<string> Read(string pak)
    {
        try
        {
            using var z = ZipFile.OpenRead(pak); // reads the central directory only
            return z.Entries.Select(e => e.FullName.Replace('\\', '/').ToLowerInvariant()).Where(f => !f.EndsWith('/')).ToHashSet();
        }
        catch (InvalidDataException)
        {
            return []; // not a zip: skipped for this informational check
        }
    }
}
