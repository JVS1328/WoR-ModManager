using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoRModManager.Core.State;

public class StateStore(GamePaths paths)
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    string Backup => paths.StateFile + ".bak";

    public List<ModRecord> Load()
    {
        var damaged = false;
        foreach (var f in new[] { paths.StateFile, Backup })
        {
            if (!File.Exists(f)) continue;
            try { return [.. (JsonSerializer.Deserialize<List<ModRecord>>(File.ReadAllText(f), Opts) ?? []).OfType<ModRecord>()]; }
            catch (JsonException) { damaged = true; }
        }
        return damaged ? throw new InvalidDataException($"Mod list file is damaged: {paths.StateFile}") : [];
    }

    public void Save(IEnumerable<ModRecord> mods)
    {
        Directory.CreateDirectory(paths.ModsFolder);
        var tmp = paths.StateFile + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(fs, mods, Opts);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(paths.StateFile)) File.Copy(paths.StateFile, Backup, overwrite: true);
        File.Move(tmp, paths.StateFile, overwrite: true);
    }

    public static ModRecord? OwnerOf(IEnumerable<ModRecord> mods, string pakName) =>
        mods.FirstOrDefault(m => m.Paks.Contains(pakName, StringComparer.OrdinalIgnoreCase));
}
