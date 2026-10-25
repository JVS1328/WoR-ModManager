using System.IO.Compression;

namespace WoRModManager.Core.ReShade;

public record ReShadeStatus(bool Installed, string? DllName, bool Enabled, string? IniPath);

public record ReShadePreset(string Name, string IniPath, IReadOnlyList<string> MissingEffects);

public class ReShadeService(GamePaths p)
{
    const string General = "GENERAL", Effects = "EffectSearchPaths", Textures = "TextureSearchPaths", PresetKey = "PresetPath";
    const long MaxIniBytes = 1 << 20;
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    static readonly string[] Dlls = ["dxgi.dll", "d3d11.dll"];
    static readonly HashSet<string> Allowed = new([".ini", ".fx", ".fxh", ".png", ".jpg", ".jpeg", ".dds", ".bmp", ".txt"], Ci);
    static readonly HashSet<string> Images = new([".png", ".jpg", ".jpeg", ".dds", ".bmp"], Ci);
    static readonly HashSet<string> Shaders = new([".fx", ".fxh"], Ci);

    string IniPath => Path.Combine(p.ExeDir, "ReShade.ini");

    public ReShadeStatus Detect()
    {
        var dll = Dlls.FirstOrDefault(d => File.Exists(Path.Combine(p.ExeDir, d)) || File.Exists(Path.Combine(p.ExeDir, d + ".off")));
        var ini = File.Exists(IniPath) ? IniPath : null;
        return new(ini != null && dll != null, dll, dll != null && File.Exists(Path.Combine(p.ExeDir, dll)), ini);
    }

    public void SetEnabled(bool on)
    {
        var s = Detect();
        // Without ReShade.ini a dxgi.dll may belong to another tool; never rename that.
        if (!s.Installed) throw new InvalidOperationException("ReShade is not installed.");
        if (s.Enabled == on) return;
        var dll = Path.Combine(p.ExeDir, s.DllName!);
        if (on) File.Move(dll + ".off", dll);
        else File.Move(dll, dll + ".off");
    }

    public IReadOnlyList<ReShadePreset> ListPresets()
    {
        if (!Directory.Exists(p.ReShadeDir)) return [];
        var available = EffectFiles();
        return [.. Directory.EnumerateDirectories(p.ReShadeDir).Order(Ci).SelectMany(d => PresetsIn(d, available))];
    }

    public string? ActivePreset() => File.Exists(IniPath) ? Active(IniFile.Load(IniPath)) : null;

    public void SetActivePreset(string iniPath)
    {
        if (!File.Exists(iniPath)) throw new FileNotFoundException("Preset not found.", iniPath);
        var ini = IniFile.Load(IniPath);
        ini.Set(General, PresetKey, Relative(iniPath));
        ini.Save(IniPath);
    }

    public ReShadePreset Import(string zipPath, string name)
    {
        var root = PresetDir(name);
        if (!File.Exists(IniPath)) throw new InvalidOperationException("ReShade is not installed (no ReShade.ini next to the game exe).");

        using (var z = ZipFile.OpenRead(zipPath))
        {
            var files = new List<(ZipArchiveEntry Entry, string Dest)>();
            foreach (var e in z.Entries.Where(e => e.Name.Length > 0))
            {
                var dest = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (e.FullName.Contains(':') || !dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe path in zip: {e.FullName}");
                if (Allowed.Contains(Path.GetExtension(dest))) files.Add((e, dest));
            }
            // Checked before the old preset of this name is deleted.
            if (!files.Any(f => IsPresetEntry(f.Entry))) throw new InvalidDataException("The zip contains no ReShade preset.");

            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            foreach (var (e, dest) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }

        List<string> effects = [], textures = [];
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root).OrderBy(d => d.Length))
        {
            var exts = Directory.EnumerateFiles(dir).Select(Path.GetExtension).ToList();
            var rel = Relative(dir);
            var entry = rel + (Directory.EnumerateDirectories(dir).Any() ? @"\**" : "");
            // A parent already added with \** covers this folder.
            bool Covered(List<string> l) => l.Any(a => a.EndsWith(@"\**") && (rel + @"\").StartsWith(a[..^2], StringComparison.OrdinalIgnoreCase));
            if (exts.Any(Shaders.Contains!) && !Covered(effects)) effects.Add(entry);
            if (exts.Any(Images.Contains!) && !Covered(textures)) textures.Add(entry);
        }
        var ini = IniFile.Load(IniPath);
        Append(ini, Effects, effects);
        Append(ini, Textures, textures);
        ini.Save(IniPath);

        return PresetsIn(root, EffectFiles()).First();
    }

    public void DeletePreset(string name)
    {
        var root = PresetDir(name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (!File.Exists(IniPath)) return;

        var prefix = Relative(root);
        bool Ours(string e) => Ci.Equals(e, prefix) || e.StartsWith(prefix + @"\", StringComparison.OrdinalIgnoreCase);
        var ini = IniFile.Load(IniPath);
        foreach (var key in new[] { Effects, Textures })
            if (SearchPaths(ini, key) is var cur && cur.Any(Ours))
                ini.Set(General, key, string.Join(",", cur.Where(e => !Ours(e))));
        if (Active(ini) is { } a && a.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ini.Set(General, PresetKey, "");
        ini.Save(IniPath);
    }

    string PresetDir(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim().TrimEnd('.') != name)
            throw new ArgumentException($"Invalid preset name: '{name}'", nameof(name));
        return Path.GetFullPath(Path.Combine(p.ReShadeDir, name));
    }

    // ReShade resolves relative paths against the exe folder.
    string Relative(string path) => Path.GetRelativePath(p.ExeDir, path) is var r && Path.IsPathRooted(r) ? r : @".\" + r;

    string? Active(IniFile ini) =>
        ini.Get(General, PresetKey) is { Length: > 0 } v ? Path.GetFullPath(Path.Combine(p.ExeDir, v)) : null;

    static string[] SearchPaths(IniFile ini, string key) =>
        (ini.Get(General, key) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    // Appends to the raw value so the player's existing entries stay exactly as written.
    static void Append(IniFile ini, string key, List<string> add)
    {
        var cur = SearchPaths(ini, key);
        var missing = add.Where(a => !cur.Contains(a, Ci)).ToList();
        if (missing.Count == 0) return;
        var raw = ini.Get(General, key) ?? "";
        ini.Set(General, key, string.Join(",", missing.Prepend(raw).Where(s => s.Length > 0)));
    }

    HashSet<string> EffectFiles()
    {
        var found = new HashSet<string>(Ci);
        if (!File.Exists(IniPath)) return found;
        foreach (var entry in SearchPaths(IniFile.Load(IniPath), Effects))
        {
            var recursive = entry.EndsWith("**");
            var dir = Path.GetFullPath(Path.Combine(p.ExeDir, recursive ? entry[..^2] : entry));
            if (Directory.Exists(dir))
                found.UnionWith(Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f)));
        }
        return found;
    }

    // Searched recursively so a zip that wraps everything in one folder still works.
    static List<ReShadePreset> PresetsIn(string dir, HashSet<string> available)
    {
        var inis = Directory.EnumerateFiles(dir, "*.ini", SearchOption.AllDirectories).Order(Ci)
            .Select(f => (Path: f, Techniques: ReadTechniques(f))).Where(x => x.Techniques != null).ToList();
        var folder = Path.GetFileName(dir);
        return [.. inis.Select(x => new ReShadePreset(
            inis.Count == 1 ? folder : $"{folder} ({Path.GetFileNameWithoutExtension(x.Path)})", x.Path,
            [.. x.Techniques!.Split(',').Select(t => t.Split('@')).Where(t => t.Length == 2).Select(t => t[1].Trim())
                .Where(f => f.Length > 0 && !available.Contains(f)).Distinct(Ci)]))];
    }

    static string? ReadTechniques(string iniPath)
    {
        if (new FileInfo(iniPath).Length > MaxIniBytes) return null;
        using var s = File.OpenRead(iniPath);
        return Techniques(s);
    }

    static bool IsPresetEntry(ZipArchiveEntry e)
    {
        if (!Ci.Equals(Path.GetExtension(e.Name), ".ini") || e.Length > MaxIniBytes) return false;
        using var s = e.Open();
        return Techniques(s) != null;
    }

    static string? Techniques(Stream s)
    {
        using var r = new StreamReader(s);
        for (var line = r.ReadLine(); line is not null; line = r.ReadLine())
            if (line.Trim() is var t && t.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)) return t["Techniques=".Length..];
        return null;
    }
}
