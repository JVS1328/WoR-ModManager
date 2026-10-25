using WoRModManager.Core;

namespace WoRModManager.Core.Tests;

public class GamePathsTests
{
    [Fact] public void DefaultModsFolder_IsInsideGameRoot() =>
        Assert.Equal(@"D:\G\ModManagerMods", GamePaths.DefaultModsFolder(@"D:\G"));

    [Fact] public void Derived_Paths()
    {
        var p = new GamePaths(@"D:\G", @"E:\M");
        Assert.Equal(@"D:\G\Assets", p.Assets);
        Assert.Equal(@"D:\G\bin\win_x64", p.ExeDir);
        Assert.Equal(@"E:\M\Downloads", p.Staging);
        Assert.Equal(@"E:\M\Backup", p.Backup);
        Assert.Equal(@"E:\M\journal.json", p.Journal);
        Assert.Equal(@"E:\M\Disabled\a_b", p.DisabledDir("a/b"));
        Assert.Equal(@"D:\G\bin\win_x64\wormm-reshade", p.ReShadeDir);
        Assert.False(p.SameDrive);
        Assert.True(new GamePaths(@"D:\G", @"d:\M").SameDrive);
    }
}
