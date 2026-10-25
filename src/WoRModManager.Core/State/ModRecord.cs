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
}
