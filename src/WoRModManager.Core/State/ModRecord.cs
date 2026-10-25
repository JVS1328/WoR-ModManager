using System.Text.RegularExpressions;

namespace WoRModManager.Core.State;

public enum ModSource { Official, Imported }

public class ModRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ModSource Source { get; set; }
    public string? RepoId { get; set; }
    public string? Version { get; set; }
    public bool Enabled { get; set; }
    public List<string> Paks { get; set; } = [];

    public string Location(GamePaths p) => Enabled ? p.Assets : p.DisabledDir(Id);

    public static string ImportedId(string name, IEnumerable<ModRecord> existing)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        var id = "imported/" + (slug.Length > 0 ? slug : "mod");
        var taken = existing.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(id)) return id;
        var n = 2;
        while (taken.Contains($"{id}-{n}")) n++;
        return $"{id}-{n}";
    }
}
