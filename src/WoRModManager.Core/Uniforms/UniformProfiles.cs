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
        var game = GameText();
        var names = Names();
        // First run, or the active profile's file has gone: rebuild it from the game rather than adopt (and overwrite) another one.
        if (!names.Contains(Active, Ci))
        {
            var name = Active;
            WriteAtomic(FileOf(name), game ?? EmptyDoc);
            SetActive(name);
            if (game is null) Apply(EmptyDoc); // a valid game file already matches; rewriting it would only make Steam Cloud sync it
            return;
        }
        if (game is not null) Capture(game);
        else Apply(Read(Active));
    }

    // The game file is written before active.txt, so a failed write leaves the old profile active and in place.
    public void Switch(string name)
    {
        name = Existing(name);
        if (isGameRunning()) throw new GameRunningException();
        if (GameText() is { } game) Capture(game);
        Apply(Read(name));
        SetActive(name);
    }

    public void Create(string name) => WriteAtomic(FileOf(Validate(name)), Read(Active));

    public void Rename(string from, string to)
    {
        from = Existing(from);
        if (from == to.Trim()) return;
        to = Validate(to, except: from);
        var wasActive = Ci.Equals(from, Active);
        File.Move(FileOf(from), FileOf(to)); // a case-only rename is a plain rename on NTFS
        if (wasActive) SetActive(to);
        try { Directory.Move(Path.Combine(BackupDir, from), Path.Combine(BackupDir, to)); }
        catch (IOException) { } // best effort: no backups yet, or a leftover folder already has the name
    }

    public void Delete(string name)
    {
        name = Existing(name);
        if (Ci.Equals(name, Active)) throw new InvalidOperationException("Switch to another profile before deleting this one.");
        File.Delete(FileOf(name));
        if (Directory.Exists(Path.Combine(BackupDir, name))) Directory.Delete(Path.Combine(BackupDir, name), recursive: true);
    }

    // expected: the profile text the editor started from. If the game saved into the profile since, the edit is refused.
    public void Save(string name, string json, string? expected = null)
    {
        name = Existing(name);
        var current = File.ReadAllText(FileOf(name));
        if (expected is not null && current != expected)
            throw new InvalidOperationException("This profile changed while you were editing it (War of Rights saved into it). Close the customizer and open it again.");
        var active = Ci.Equals(name, Active);
        if (active && isGameRunning()) throw new GameRunningException();
        if (active) Apply(json); // game file first: if that fails nothing has changed, and if the profile write fails the next capture heals it
        if (current == json) return;
        Backup(name, current);
        WriteAtomic(FileOf(name), json);
    }

    // The game file's text, or null when it's missing or not a JSON object (then it must not overwrite a profile).
    string? GameText()
    {
        if (!File.Exists(gameFile)) return null;
        var text = File.ReadAllText(gameFile);
        try { return JsonNode.Parse(text) is JsonObject ? text : null; }
        catch (JsonException) { return null; }
    }

    // Takes the text already read and checked, so a game file rewritten in between can't empty the profile.
    void Capture(string gameText)
    {
        var name = Active;
        var current = File.ReadAllText(FileOf(name));
        if (current == gameText) return;
        Backup(name, current);
        WriteAtomic(FileOf(name), gameText);
    }

    void Apply(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gameFile))!);
        // copied as text: File.Copy would carry a read-only flag over and block the next backup
        if (File.Exists(gameFile)) WriteAtomic(Path.Combine(BackupDir, "game-customizations.json"), File.ReadAllText(gameFile));
        WriteAtomic(gameFile, text);
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
