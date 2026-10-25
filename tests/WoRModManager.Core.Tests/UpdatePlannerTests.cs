using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;

namespace WoRModManager.Core.Tests;

public class UpdatePlannerTests
{
    static ReleaseAsset A(string name, string? sha = "h", long size = 1) => new(name, size, sha, new Uri("https://github.com/x/" + name));

    [Fact] public void Plan_OnlyChangedPaksDownloaded()
    {
        var p = UpdatePlanner.Plan([A("z_a.pak", "1"), A("z_b.pak", "2")], ["z_a.pak", "z_b.pak"], n => n == "z_a.pak" ? "1" : "old");
        Assert.Equal(new[] { "z_b.pak" }, p.ToDownload.Select(x => x.Name));
        Assert.Empty(p.ToRemove);
    }

    [Fact] public void Plan_DroppedPakRemoved()
    {
        var p = UpdatePlanner.Plan([A("z_a.pak")], ["z_a.pak", "z_c.pak"], _ => "h");
        Assert.Equal(new[] { "z_c.pak" }, p.ToRemove);
        Assert.False(p.IsEmpty);
    }

    [Fact] public void Plan_LocalPakMissing_Downloads() =>
        Assert.Single(UpdatePlanner.Plan([A("z_a.pak")], [], _ => null).ToDownload);

    [Fact] public void Plan_MissingDigest_TreatedAsChanged() =>
        Assert.Single(UpdatePlanner.Plan([A("z_a.pak", null)], ["z_a.pak"], _ => "h").ToDownload);

    [Fact] public void Plan_IgnoresUnsafeAndNonPakAssets()
    {
        var rel = new[] { A("../evil.pak"), A("x.exe"), A("changelog.txt"), A("thumbnail.png"), A("z_maw_UI.pak") };
        var p = UpdatePlanner.Plan(rel, Array.Empty<string>(), _ => null);
        Assert.Equal(new[] { "z_maw_UI.pak" }, p.ToDownload.Select(x => x.Name));
    }

    [Fact] public void Plan_DownloadBytesSumsSizes()
    {
        var p = UpdatePlanner.Plan([A("z_a.pak", size: 10), A("z_b.pak", size: 32)], [], _ => null);
        Assert.Equal(42, p.DownloadBytes);
    }

    [Fact] public void Plan_UpToDate_IsEmpty() =>
        Assert.True(UpdatePlanner.Plan([A("z_a.pak")], ["z_a.pak"], _ => "H").IsEmpty);
}
