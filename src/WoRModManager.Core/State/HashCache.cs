using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace WoRModManager.Core.State;

public class HashCache
{
    readonly string file;
    readonly ConcurrentDictionary<string, string> map; // read and filled by operations running at the same time

    public HashCache(string file)
    {
        this.file = file;
        try { map = new(File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [] : []); }
        catch (JsonException) { map = new(); } // corrupt cache is just a cold cache
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
        lock (map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string>(map)));
        }
    }
}
