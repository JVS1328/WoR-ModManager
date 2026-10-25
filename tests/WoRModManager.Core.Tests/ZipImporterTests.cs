using System.IO.Compression;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Import;
using WoRModManager.Core.State;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class ZipImporterTests : IDisposable
{
    const string MawId = "JVS1328/WoR-MAW-releases";
    readonly TempGame game = new();
    public void Dispose() => game.Dispose();

    string Zip(string name, params (string Entry, string Body)[] entries)
    {
        var path = Path.Combine(game.Root, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, b) in entries)
            using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write(b);
        return path;
    }

    static string[] MawPaks(int n) => Enumerable.Range(1, n).Select(i => $"z_maw_{i:00}.pak").ToArray();

    static OfficialMod Maw(params string[] paks) =>
        new(new RepoRef("JVS1328", "WoR-MAW-releases"), "MAW", new ModDescription(null, ""), null,
            new ReleaseInfo("v1", "", paks.Select(p => new ReleaseAsset(p, 1, null, new Uri("https://github.com/x"))).ToList(), false), null, null);

    static ModRecord Imported(string id, params string[] paks) =>
        new() { Id = id, Name = id, Source = ModSource.Imported, Enabled = true, Paks = [.. paks] };

    string AssetText(string n) => File.ReadAllText(Path.Combine(game.Paths.Assets, n));

    [Fact] public void Analyze_BarePak_NewMod()
    {
        var pak = Path.Combine(game.Root, "zBlackPowder.pak");
        File.WriteAllText(pak, "x");
        var a = ZipImporter.Analyze(pak, [], []);
        Assert.Equal(ImportKind.NewMod, a.Kind);
        Assert.Equal(["zBlackPowder.pak"], a.PakNames);
        Assert.Equal("zBlackPowder", a.SuggestedName);
        Assert.Null(a.TargetModId);
        Assert.Empty(a.Conflicts);
    }

    [Fact] public void Analyze_UpdateZipWithNewPrefixedPak_IsUpdate()
    {
        var maw = Imported("imported/maw", MawPaks(24));
        var zip = Zip("MAW_update.zip", [.. MawPaks(23).Select(n => ("MAW/" + n, "x")), ("MAW/z_maw_New.pak", "x")]);
        var a = ZipImporter.Analyze(zip, [maw], []);
        Assert.Equal(ImportKind.UpdateOf, a.Kind);
        Assert.Equal("imported/maw", a.TargetModId);
        Assert.Equal(24, a.PakNames.Count);
        Assert.Empty(a.Conflicts);
    }

    [Fact] public void Analyze_MawZipMatchesOfficial_Adopts()
    {
        var off = Maw(MawPaks(3));
        var zip = Zip("MAW.zip", [.. MawPaks(3).Select(n => (n, "x"))]);
        var a = ZipImporter.Analyze(zip, [], [off]);
        Assert.Equal(ImportKind.AdoptOfficial, a.Kind);
        Assert.Equal(MawId, a.TargetModId);
        Assert.Same(off, a.Official);
    }

    [Fact] public void Analyze_TraversalEntry_Ignored()
    {
        var zip = Zip("evil.zip", ("../../z_evil.pak", "x"), ("C:/z_root.pak", "x"), ("/z_abs.pak", "x"), ("ok/z_good.pak", "x"));
        Assert.Equal(["z_good.pak"], ZipImporter.Analyze(zip, [], []).PakNames);
    }

    [Fact] public void Analyze_ReShadePresetZip()
    {
        var zip = Zip("Cinematic.zip", ("Cinematic.ini", "[General]\r\nTechniques=Clarity@Clarity.fx\r\n"), ("Shaders/Clarity.fx", "x"));
        Assert.Equal(ImportKind.ReShadePreset, ZipImporter.Analyze(zip, [], []).Kind);
    }

    [Fact] public void Analyze_ZipWithOnlyTxtAndExe_Nothing()
    {
        var zip = Zip("junk.zip", ("readme.txt", "x"), ("setup.exe", "x"), ("settings.ini", "[a]\r\nb=c"));
        var a = ZipImporter.Analyze(zip, [], []);
        Assert.Equal(ImportKind.Nothing, a.Kind);
        Assert.Empty(a.PakNames);
    }

    [Fact] public void Apply_ExtractsOnlyPaksFlattened()
    {
        var zip = Zip("My_Cool-Mod.zip", ("files/x.txt", "t"), ("deep/dir/z_a.pak", "pak"), ("run.exe", "exe"));
        var mods = new List<ModRecord>();
        var r = ZipImporter.Apply(ZipImporter.Analyze(zip, mods, []), zip, game.Paths, mods, null);

        Assert.Equal(["z_a.pak"], Directory.GetFiles(game.Paths.Assets).Select(Path.GetFileName));
        Assert.Equal("pak", AssetText("z_a.pak"));
        Assert.Equal("imported/my-cool-mod", r.Id);
        Assert.Equal("My Cool Mod", r.Name);
        Assert.Equal(ModSource.Imported, r.Source);
        Assert.Equal(["z_a.pak"], r.Paks);
        Assert.Equal("imported/my-cool-mod", Assert.Single(new StateStore(game.Paths).Load()).Id);
        Assert.False(Directory.Exists(Path.Combine(game.Paths.Staging, "import")));
    }

    [Fact] public void Analyze_PakOwnedByOtherMod_ReportsConflict()
    {
        var other = Imported("imported/x", "z_shared.pak");
        var zip = Zip("new.zip", ("z_shared.pak", "x"), ("z_new.pak", "x"), ("zz.pak", "x"));
        var a = ZipImporter.Analyze(zip, [other], []);
        Assert.Equal(ImportKind.NewMod, a.Kind);
        Assert.Equal(["z_shared.pak"], a.Conflicts);
    }

    [Fact] public void Apply_Conflicts_RefusedUnlessAllowed_ThenOwnershipMoves()
    {
        game.WritePak("z_shared.pak", "old"u8.ToArray());
        var other = Imported("imported/x", "z_shared.pak");
        var mods = new List<ModRecord> { other };
        var zip = Zip("new.zip", ("z_shared.pak", "new"), ("z_new.pak", "x"));
        var a = ZipImporter.Analyze(zip, mods, []);

        Assert.Throws<InvalidOperationException>(() => ZipImporter.Apply(a, zip, game.Paths, mods, null));
        Assert.Equal("old", AssetText("z_shared.pak"));

        var r = ZipImporter.Apply(a, zip, game.Paths, mods, "Mine", allowConflicts: true);
        Assert.Equal("new", AssetText("z_shared.pak"));
        Assert.Equal("Mine", r.Name);
        Assert.Empty(other.Paks);
    }

    [Fact] public void Apply_Update_OverwritesAndAddsKeepingRest()
    {
        game.WritePak("z_maw_01.pak", "one"u8.ToArray());
        game.WritePak("z_maw_02.pak", "old"u8.ToArray());
        game.WritePak("z_maw_03.pak", "old"u8.ToArray());
        var maw = Imported("imported/maw", "z_maw_01.pak", "z_maw_02.pak", "z_maw_03.pak");
        var mods = new List<ModRecord> { maw };
        var zip = Zip("upd.zip", ("z_maw_02.pak", "new"), ("z_maw_03.pak", "new"), ("z_maw_04.pak", "four"));
        var a = ZipImporter.Analyze(zip, mods, []);
        Assert.Equal(ImportKind.UpdateOf, a.Kind);

        var r = ZipImporter.Apply(a, zip, game.Paths, mods, null);
        Assert.Same(maw, r);
        Assert.Equal(["z_maw_01.pak", "z_maw_02.pak", "z_maw_03.pak", "z_maw_04.pak"], r.Paks);
        Assert.Equal("one", AssetText("z_maw_01.pak"));
        Assert.Equal("new", AssetText("z_maw_02.pak"));
        Assert.Equal("four", AssetText("z_maw_04.pak"));
        Assert.Single(new StateStore(game.Paths).Load());
    }

    [Fact] public void Apply_AdoptOfficial_RecordsOfficialWithNoVersion()
    {
        var zip = Zip("MAW.zip", ("z_maw_01.pak", "x"));
        var mods = new List<ModRecord>();
        var r = ZipImporter.Apply(ZipImporter.Analyze(zip, mods, [Maw("z_maw_01.pak")]), zip, game.Paths, mods, null);
        Assert.Equal(MawId, r.Id);
        Assert.Equal(MawId, r.RepoId);
        Assert.Equal("MAW", r.Name);
        Assert.Equal(ModSource.Official, r.Source);
        Assert.Null(r.Version);
        Assert.True(r.Enabled);
        Assert.Equal("x", AssetText("z_maw_01.pak"));
    }
}
