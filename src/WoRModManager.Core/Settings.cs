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
    public static void Move(string from, string to, Action<string>? beforeMove = null)
    {
        var f = Norm(from); var t = Norm(to);
        if (t.StartsWith(f, StringComparison.OrdinalIgnoreCase) || f.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The new folder can't be inside the old one (or the reverse).");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
            throw new InvalidOperationException("The new folder isn't empty.");
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        // The mod list moves last, so until then the old folder still holds it and every file it names.
        var files = Directory.GetFiles(from, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(from, f))
            .OrderBy(f => Ci.Equals(f, "state.json") ? 2 : f.StartsWith("state.json", StringComparison.OrdinalIgnoreCase) || Ci.Equals(f, "hashcache.json") ? 1 : 0)
            .ToList();
        var moved = new List<string>();
        try
        {
            foreach (var rel in files)
            {
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                beforeMove?.Invoke(rel);
                File.Move(Path.Combine(from, rel), dest);
                moved.Add(rel);
            }
        }
        catch
        {
            foreach (var rel in moved) File.Move(Path.Combine(to, rel), Path.Combine(from, rel));
            foreach (var d in Directory.GetDirectories(to)) Directory.Delete(d, recursive: true); // only the folders made above, now empty
            throw;
        }
        Directory.Delete(from, true);
    }

    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
}
