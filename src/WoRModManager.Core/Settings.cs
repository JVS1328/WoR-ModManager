using System.Text.Json;

namespace WoRModManager.Core;

public record AppSettings(string? GameRoot, string? ModsFolder);

public static class SettingsStore
{
    public static AppSettings Load(string file)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new(null, null); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(null, null); }
    }

    public static void Save(string file, AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, file, overwrite: true);
    }
}

public static class ModsFolderMover
{
    public static void Move(string from, string to)
    {
        var f = Norm(from); var t = Norm(to);
        if (t.StartsWith(f, StringComparison.OrdinalIgnoreCase) || f.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new folder can't be inside the old one (or the reverse).");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new InvalidOperationException("The new folder isn't empty.");
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(file, dest);
        }
        Directory.Delete(from, true);
    }

    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
}
