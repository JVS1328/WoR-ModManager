using WoRModManager.Core.State;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class StateStoreTests : IDisposable
{
    readonly TempGame game = new();
    public void Dispose() => game.Dispose();

    [Fact] public void Save_ThenLoad_RoundTrips()
    {
        var store = new StateStore(game.Paths);
        store.Save([new ModRecord { Id = "a/b", Name = "AB", Source = ModSource.Official, RepoId = "a/b", Version = "v1", Enabled = true, Paks = ["z_ab_x.pak", "z_ab_y.pak"] }]);
        var m = Assert.Single(store.Load());
        Assert.Equal("a/b", m.Id); Assert.Equal("AB", m.Name); Assert.Equal(ModSource.Official, m.Source);
        Assert.Equal("a/b", m.RepoId); Assert.Equal("v1", m.Version); Assert.True(m.Enabled);
        Assert.Equal(new[] { "z_ab_x.pak", "z_ab_y.pak" }, m.Paks);
        Assert.False(File.Exists(game.Paths.StateFile + ".tmp"));
    }

    [Fact] public void Load_MissingFile_ReturnsEmpty() => Assert.Empty(new StateStore(game.Paths).Load());

    [Fact] public void OwnerOf_IsCaseInsensitive()
    {
        var mods = new List<ModRecord> { new() { Id = "x", Name = "X", Paks = ["z_X.pak"] } };
        Assert.Same(mods[0], new StateStore(game.Paths).OwnerOf(mods, "Z_x.PAK"));
        Assert.Null(new StateStore(game.Paths).OwnerOf(mods, "other.pak"));
    }

    [Fact] public void Location_FollowsEnabled()
    {
        var m = new ModRecord { Id = "a/b", Name = "AB", Enabled = true };
        Assert.Equal(game.Paths.Assets, m.Location(game.Paths));
        m.Enabled = false;
        Assert.Equal(game.Paths.DisabledDir("a/b"), m.Location(game.Paths));
    }
}
