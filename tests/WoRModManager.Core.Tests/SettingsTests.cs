using WoRModManager.Core;

namespace WoRModManager.Core.Tests;

public class SettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("wormm").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] public void SettingsStore_CorruptFile_ReturnsEmpty()
    {
        var f = Path.Combine(_dir, "s.json");
        File.WriteAllText(f, "{not json");
        var s = SettingsStore.Load(f);
        Assert.Null(s.GameRoot); Assert.Null(s.ModsFolder);
        Assert.Null(SettingsStore.Load(Path.Combine(_dir, "missing.json")).GameRoot);
    }

    [Fact] public void SettingsStore_RoundTrips()
    {
        var f = Path.Combine(_dir, "sub", "s.json");
        SettingsStore.Save(f, new AppSettings(@"D:\G", @"E:\M"));
        Assert.Equal(new AppSettings(@"D:\G", @"E:\M"), SettingsStore.Load(f));
    }

    [Fact] public void ModsFolderMover_MovesContents_AndRefusesNested()
    {
        var a = Path.Combine(_dir, "a"); var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(Path.Combine(a, "d"));
        File.WriteAllText(Path.Combine(a, "x.pak"), "1");
        File.WriteAllText(Path.Combine(a, "d", "y.txt"), "2");
        Assert.Throws<InvalidOperationException>(() => ModsFolderMover.Move(a, Path.Combine(a, "sub")));
        Assert.Throws<InvalidOperationException>(() => ModsFolderMover.Move(Path.Combine(a, "d"), a));
        ModsFolderMover.Move(a, b);
        Assert.True(File.Exists(Path.Combine(b, "x.pak")));
        Assert.True(File.Exists(Path.Combine(b, "d", "y.txt")));
        Assert.False(Directory.Exists(a));
    }

    [Fact] public void ModsFolderMover_RefusesNonEmptyTarget()
    {
        var a = Path.Combine(_dir, "a"); var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "z"), "");
        Assert.Throws<InvalidOperationException>(() => ModsFolderMover.Move(a, b));
    }
}
