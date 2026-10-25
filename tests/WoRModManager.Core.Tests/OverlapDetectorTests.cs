using System.IO.Compression;
using WoRModManager.Core.State;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class OverlapDetectorTests : IDisposable
{
    readonly TempGame game = new();
    public void Dispose() => game.Dispose();

    ModRecord Mod(string id, string pak, params string[] entries) => ModIn(game.Paths.Assets, true, id, pak, entries);

    ModRecord ModIn(string dir, bool enabled, string id, string pak, params string[] entries)
    {
        Directory.CreateDirectory(dir);
        using (var z = ZipFile.Open(Path.Combine(dir, pak), ZipArchiveMode.Create))
            foreach (var e in entries) z.CreateEntry(e);
        return new() { Id = id, Name = id, Enabled = enabled, Paks = [pak] };
    }

    [Fact] public void Find_TwoModsSameInnerPath_ReportsOverlap()
    {
        var a = Mod("maw", "z_maw_a.pak", "objects/", "Objects/Characters/coat.dds", "objects/ui/x.dds");
        var b = Mod("1812", "z_1812_a.pak", "objects/", @"objects\characters\COAT.dds", "textures/y.dds");
        var o = Assert.Single(new OverlapDetector().Find([a, b], game.Paths));
        Assert.Equal(("maw", "1812", 1), (o.ModA, o.ModB, o.FileCount));
        Assert.Equal(["objects/characters"], o.TopFolders);
    }

    [Fact] public void Find_DifferentFiles_NoOverlap()
    {
        var a = Mod("a", "z_a.pak", "objects/", "objects/a.dds");
        var b = Mod("b", "z_b.pak", "objects/", "objects/b.dds");
        Assert.Empty(new OverlapDetector().Find([a, b], game.Paths));
    }

    [Fact] public void Find_NonZipPak_Skipped()
    {
        var a = Mod("a", "z_a.pak", "objects/a.dds");
        game.WritePak("z_b.pak", "not a zip"u8.ToArray());
        var b = new ModRecord { Id = "b", Name = "b", Enabled = true, Paks = ["z_b.pak", "z_missing.pak"] };
        Assert.Empty(new OverlapDetector().Find([a, b], game.Paths));
    }

    [Fact] public void Find_TopFolders_MostCommonFirst_UpToThree()
    {
        string[] files = ["e/f/1", "a/b/1", "a/b/c/2", "c/d/1", "c/d/2", "c/d/3", "g/h/1", "root.txt"];
        var o = Assert.Single(new OverlapDetector().Find([Mod("a", "z_a.pak", files), Mod("b", "z_b.pak", files)], game.Paths));
        Assert.Equal(8, o.FileCount);
        Assert.Equal(["c/d", "a/b", "e/f"], o.TopFolders);
    }

    [Fact] public void FindFor_DisabledCandidate_ComparedWithEachEnabledMod()
    {
        var cand = ModIn(game.Paths.DisabledDir("imported/c"), false, "imported/c", "z_c.pak", "objects/x.dds", "objects/y.dds");
        var a = Mod("a", "z_a.pak", "objects/x.dds");
        var b = Mod("b", "z_b.pak", "objects/z.dds");
        var o = Assert.Single(new OverlapDetector().FindFor(cand, [a, b, cand], game.Paths));
        Assert.Equal(("imported/c", "a", 1), (o.ModA, o.ModB, o.FileCount));
    }

    [Fact] public void Find_ChangedPak_IsReadAgain()
    {
        var d = new OverlapDetector();
        var a = Mod("a", "z_a.pak", "objects/x.dds");
        var b = Mod("b", "z_b.pak", "objects/x.dds");
        Assert.Single(d.Find([a, b], game.Paths));

        var pak = Path.Combine(game.Paths.Assets, "z_b.pak");
        File.Delete(pak);
        Mod("b", "z_b.pak", "objects/other.dds");
        File.SetLastWriteTimeUtc(pak, DateTime.UtcNow.AddMinutes(1));
        Assert.Empty(d.Find([a, b], game.Paths));
    }

    [Fact] public void Message_UsesSpecCopy() =>
        Assert.Equal("MAW and 1812 both change 2 game files, mainly objects/characters. Running both may break the game visually or functionally.",
            Overlap.Message("MAW", "1812", new("a", "b", 2, ["objects/characters"])));

    [Fact] public void Message_OneFileAtRoot_SingularWithoutFolders() =>
        Assert.Equal("MAW and 1812 both change 1 game file. Running both may break the game visually or functionally.",
            Overlap.Message("MAW", "1812", new("a", "b", 1, [])));
}
