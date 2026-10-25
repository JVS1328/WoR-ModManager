using System.Security.Cryptography;
using WoRModManager.Core.State;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class HashCacheTests : IDisposable
{
    readonly TempGame game = new();
    public void Dispose() => game.Dispose();

    [Fact] public async Task HashCache_RecomputesWhenFileChanges()
    {
        var cache = new HashCache(game.Paths.HashCacheFile);
        var p = game.WritePak("z_a.pak", "one"u8.ToArray()); var h1 = await cache.GetSha256Async(p, default);
        game.WritePak("z_a.pak", "two"u8.ToArray()); var h2 = await cache.GetSha256Async(p, default);
        Assert.NotEqual(h1, h2);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("two"u8)), h2);
    }

    [Fact] public async Task HashCache_PersistsAcrossInstances()
    {
        var p = game.WritePak("z_a.pak", "one"u8.ToArray());
        var c1 = new HashCache(game.Paths.HashCacheFile);
        var h = await c1.GetSha256Async(p, default); c1.Save();
        Assert.True(File.Exists(game.Paths.HashCacheFile));
        Assert.Equal(h, await new HashCache(game.Paths.HashCacheFile).GetSha256Async(p, default));
    }
}
