using System.Security.Cryptography;
using System.Text.Json;

namespace WoRModManager.Core.State;

public class HashCache
{
    readonly string file;
    readonly Dictionary<string, string> map;

    public HashCache(string file)
    {
        this.file = file;
        try { map = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : []; }
        catch (JsonException) { map = []; } // corrupt cache is just a cold cache
    }

    public async Task<string> GetSha256Async(string path, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        if (map.TryGetValue(key, out var hit)) return hit;
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
        return map[key] = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(map));
    }
}
