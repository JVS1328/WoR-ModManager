using WoRModManager.Core.Tests.TestUtil;
using WoRModManager.Core.Uniforms;

namespace WoRModManager.Core.Tests;

public class ApparelDatabaseTests
{
    const uint UniformHier = 0x12345678;

    [Fact] public void ParseUniforms_ReadsRecords()
    {
        var us = ApparelDatabase.ParseUniforms(ApparelBins.Uniforms(
            (10, "4th Pennsylvania - 2nd Lieutenant", UniformHier, [1, 2]),
            (ApparelDatabase.HumanHeadKey, "Head", ApparelDatabase.HumanHeadKey, [3])));
        Assert.Equal(2, us.Count);
        Assert.Equal(10u, us[0].Key);
        Assert.Equal("4th Pennsylvania - 2nd Lieutenant", us[0].DisplayName);
        Assert.Equal([1u, 2u], us[0].Allowed);
        Assert.False(us[0].IsHead);
        Assert.True(us[1].IsHead);
        Assert.Equal([3u], us[1].Allowed);
    }

    [Fact] public void Scan_FindsItems()
    {
        var items = ApparelDatabase.ScanApparel(ApparelBins.Apparel(
            (1, "US_FrockCoat", 0, [2]), (2, "US_Shirt", 1, []), (3, "US_Kepi", 6, [])), new HashSet<uint> { 1, 2, 3 });
        Assert.Equal(3, items.Count);
        Assert.Equal("US_FrockCoat", items[1].Name);
        Assert.Equal(0, items[1].Category);
        Assert.Equal([2u], items[1].Children);
        Assert.Equal(6, items[3].Category);
    }

    [Fact] public void Scan_SkipsDecoyBytes()
    {
        // id 7's bytes sit in item 5's data followed by a non-printable byte; the real record comes later
        var decoyChildren = new uint[] { 7, 0x00010203 };
        var data = ApparelBins.Apparel((5, "Decoy_Holder", 0, decoyChildren), (7, "Real_Hat", 6, []));
        var items = ApparelDatabase.ScanApparel(data, new HashSet<uint> { 5, 7 });
        Assert.Equal("Real_Hat", items[7].Name);
        Assert.Equal(6, items[7].Category);
    }

    [Fact] public void Load_LastPakWins()
    {
        using var g = new TempGame();
        g.WritePak("GameData.pak", ApparelBins.Pak(ApparelBins.Uniforms((10, "U", UniformHier, [1])), ApparelBins.Apparel((1, "Vanilla Coat", 0, []))));
        g.WritePak("z_maw_GameData.pak", ApparelBins.Pak(ApparelBins.Uniforms((10, "U", UniformHier, [1])), ApparelBins.Apparel((1, "MaW Coat", 0, []))));
        g.WritePak("z_maw_Textures.pak", EmptyZip()); // later name, but no uniform data: must not win
        var db = ApparelDatabase.Load(g.Paths.Assets)!;
        Assert.Equal("z_maw_GameData.pak", db.SourcePak);
        Assert.Equal("MaW Coat", db.Items[1].Name);
        Assert.Single(db.Uniforms);
    }

    [Fact] public void Load_NoPak_ReturnsNull()
    {
        using var g = new TempGame();
        Assert.Null(ApparelDatabase.Load(g.Paths.Assets));
    }

    [Fact] public void Load_SkipsNonZipPak()
    {
        using var g = new TempGame();
        g.WritePak("GameData.pak", ApparelBins.Pak(ApparelBins.Uniforms((10, "U", UniformHier, [1])), ApparelBins.Apparel((1, "Coat", 0, []))));
        g.WritePak("zz_broken.pak", [1, 2, 3, 4, 5]);
        Assert.Equal("GameData.pak", ApparelDatabase.Load(g.Paths.Assets)!.SourcePak);
    }

    static byte[] EmptyZip()
    {
        var ms = new MemoryStream();
        new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true).Dispose();
        return ms.ToArray();
    }
}
