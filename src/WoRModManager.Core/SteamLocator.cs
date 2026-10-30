using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WoRModManager.Core;

public static partial class SteamLocator
{
    public const string AppId = "424030";

    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText) =>
        PathRx().Matches(vdfText).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static string? ParseBuildId(string acfText) => KeyValue(acfText, "buildid");

    public static string? FindGameRoot()
    {
        try { return FindGameRootCore(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? FindGameRootCore()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        steam = steam.Replace('/', '\\');
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var libs = File.Exists(vdf) ? ParseLibraryFolders(File.ReadAllText(vdf)).Append(steam) : [steam];
        foreach (var lib in libs)
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var dir = KeyValue(File.ReadAllText(manifest), "installdir");
            if (dir is not null) return Path.Combine(lib, "steamapps", "common", dir);
        }
        return null;
    }

    public static string? ReadBuildId(string gameRoot)
    {
        var manifest = Path.GetFullPath(Path.Combine(gameRoot, "..", "..", $"appmanifest_{AppId}.acf"));
        try { return File.Exists(manifest) ? ParseBuildId(File.ReadAllText(manifest)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? KeyValue(string text, string key) =>
        Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"") is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")] private static partial Regex PathRx();
}
