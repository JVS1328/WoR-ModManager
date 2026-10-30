using System.Text.Json;
using System.Text.Json.Nodes;

namespace WoRModManager.Core.Uniforms;

// Named copies of the game's customizations.json. The game file always belongs to the active profile:
// the game's own saves are captured into it, and switching applies another profile in its place.
public sealed class UniformProfiles(string dir, string gameFile, Func<bool> isGameRunning)
{
    public const string DefaultName = "Default";
    const string EmptyDoc = """{"Uniforms":[]}""";
    const int BackupsKept = 5;
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    string ActiveFile => Path.Combine(dir, "active.txt");
    string BackupDir => Path.Combine(dir, ".backup");
    string FileOf(string name) => Path.Combine(dir, name + ".json");

    public string Active => File.Exists(ActiveFile) ? File.ReadAllText(ActiveFile).Trim() : DefaultName;

    public IReadOnlyList<string> Names() => Directory.Exists(dir)
        ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(Ci).ToList()
        : [];

    public string Read(string name) => File.ReadAllText(FileOf(Existing(name)));

    public void Sync()
    {
        if (isGameRunning()) return;
        var names = Names();
        if (names.Count == 0)
        {
            var game = GameText();
            WriteAtomic(FileOf(DefaultName), game ?? EmptyDoc);
            SetActive(DefaultName);
            if (game is null) Apply(); // a valid game file already matches Default; rewriting it would only make Steam Cloud sync it
            return;
        }
        if (!names.Contains(Active, Ci)) SetActive(names[0]);
        if (GameText() is not null) Capture();
        else Apply();
    }

    public void Switch(string name)
    {
        name = Existing(name);
        if (isGameRunning()) throw new GameRunningException();
        if (GameText() is not null) Capture();
        SetActive(name);
        Apply();
    }

    public void Create(string name) => WriteAtomic(FileOf(Validate(name)), Read(Active));

    public void Rename(string from, string to)
    {
        from = Existing(from);
        to = Validate(to, except: from);
        var wasActive = Ci.Equals(from, Active);
        File.Move(FileOf(from), FileOf(to)); // a case-only rename is a plain rename on NTFS
        if (Directory.Exists(Path.Combine(BackupDir, from))) Directory.Move(Path.Combine(BackupDir, from), Path.Combine(BackupDir, to));
        if (wasActive) SetActive(to);
    }

    public void Delete(string name)
    {
        name = Existing(name);
        if (Ci.Equals(name, Active)) throw new InvalidOperationException("Switch to another profile before deleting this one.");
        File.Delete(FileOf(name));
    }

    public void Save(string name, string json)
    {
        name = Existing(name);
        var active = Ci.Equals(name, Active);
        if (active && isGameRunning()) throw new GameRunningException();
        WriteAtomic(FileOf(name), json);
        if (active) Apply();
    }

    // The game file's text, or null when it's missing or not a JSON object (then it must not overwrite a profile).
    string? GameText()
    {
        if (!File.Exists(gameFile)) return null;
        var text = File.ReadAllText(gameFile);
        try { return JsonNode.Parse(text) is JsonObject ? text : null; }
        catch (JsonException) { return null; }
    }

    void Capture()
    {
        var name = Active;
        var text = GameText()!;
        var current = File.ReadAllText(FileOf(name));
        if (current == text) return;
        Backup(name, current);
        WriteAtomic(FileOf(name), text);
    }

    void Apply()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gameFile))!);
        if (File.Exists(gameFile))
        {
            Directory.CreateDirectory(BackupDir);
            File.Copy(gameFile, Path.Combine(BackupDir, "game-customizations.json"), overwrite: true);
        }
        WriteAtomic(gameFile, File.ReadAllText(FileOf(Active)));
    }

    void Backup(string name, string text)
    {
        var d = Path.Combine(BackupDir, name);
        Directory.CreateDirectory(d);
        // tick-named so ordinal order is age order, even for several backups within one clock tick
        var ticks = DateTime.UtcNow.Ticks;
        while (File.Exists(Path.Combine(d, $"{ticks:D19}.json"))) ticks++;
        File.WriteAllText(Path.Combine(d, $"{ticks:D19}.json"), text);
        foreach (var old in Directory.GetFiles(d, "*.json").Order(StringComparer.Ordinal).SkipLast(BackupsKept)) File.Delete(old);
    }

    void SetActive(string name)
    {
        Directory.CreateDirectory(dir);
        WriteAtomic(ActiveFile, name);
    }

    string Existing(string name) =>
        Names().FirstOrDefault(n => Ci.Equals(n, name)) ?? throw new ArgumentException($"There's no uniform profile called '{name}'.");

    static readonly HashSet<string> Reserved = new(Ci)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    string Validate(string name, string? except = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Give the profile a name.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.') || Reserved.Contains(name))
            throw new ArgumentException($"'{name}' can't be used as a profile name.");
        if (Names().Any(n => Ci.Equals(n, name) && !Ci.Equals(n, except)))
            throw new ArgumentException($"There's already a profile called '{name}'.");
        return name;
    }

    static void WriteAtomic(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, overwrite: true);
    }
}
