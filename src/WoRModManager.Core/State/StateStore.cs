using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoRModManager.Core.State;

public class StateStore(GamePaths paths)
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<ModRecord> Load() =>
        File.Exists(paths.StateFile)
            ? JsonSerializer.Deserialize<List<ModRecord>>(File.ReadAllText(paths.StateFile), Opts) ?? []
            : [];

    public void Save(IEnumerable<ModRecord> mods)
    {
        Directory.CreateDirectory(paths.ModsFolder);
        var tmp = paths.StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(mods, Opts));
        File.Move(tmp, paths.StateFile, overwrite: true);
    }

    public static ModRecord? OwnerOf(IEnumerable<ModRecord> mods, string pakName) =>
        mods.FirstOrDefault(m => m.Paks.Contains(pakName, StringComparer.OrdinalIgnoreCase));
}
