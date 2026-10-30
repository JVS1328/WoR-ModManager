namespace WoRModManager.Core;

public record GamePaths(string GameRoot, string ModsFolder)
{
    public static string DefaultModsFolder(string gameRoot) => Path.Combine(gameRoot, "ModManagerMods");

    public static bool IsValidGameRoot(string dir) =>
        File.Exists(Path.Combine(dir, "bin", "win_x64", "WarOfRights.exe"));

    public string Assets => Path.Combine(GameRoot, "Assets");
    public string ExeDir => Path.Combine(GameRoot, "bin", "win_x64");
    public string Staging => Path.Combine(ModsFolder, "Downloads");
    public string Backup => Path.Combine(ModsFolder, "Backup");
    public string Journal => Path.Combine(ModsFolder, "journal.json");
    public string StateFile => Path.Combine(ModsFolder, "state.json");
    public string HashCacheFile => Path.Combine(ModsFolder, "hashcache.json");
    public string ReShadeDir => Path.Combine(ExeDir, "wormm-reshade");
    public string CustomizationsFile => Path.Combine(GameRoot, "UserConfig", "Cloud", "customizations.json");

    public string DisabledDir(string modId) => Path.Combine(ModsFolder, "Disabled", modId.Replace('/', '_'));

    public bool SameDrive => string.Equals(Path.GetPathRoot(Assets), Path.GetPathRoot(ModsFolder), StringComparison.OrdinalIgnoreCase);
}
