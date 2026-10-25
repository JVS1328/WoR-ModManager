using WoRModManager.Core;

namespace WoRModManager.Core.Tests;

public class CatalogTests
{
    [Fact] public void Parse_SkipsCommentsBlanksDuplicatesAndBadHosts()
    {
        var text = "# official\nhttps://github.com/JVS1328/WoR-MAW-releases\n\nhttps://github.com/JVS1328/WoR-MAW-releases/releases\nhttps://evil.com/a/b\nhttps://github.com/JVS1328/WoR-1812-releases  https://www.nexusmods.com/warofrights/mods/9\n";
        var r = Catalog.Parse(text);
        Assert.Equal(new[] { "WoR-MAW-releases", "WoR-1812-releases" }, r.Select(x => x.Id));
    }

    [Theory]
    [InlineData("https://github.com/o/r", true)]
    [InlineData("https://github.com/o/r/", true)]
    [InlineData("https://github.com/o/r.git", true)]
    [InlineData("https://github.com/o/r/releases/latest", true)]
    [InlineData("https://github.com/../r", false)]
    [InlineData("https://github.com/o/..", false)]
    [InlineData("https://github.com/./r", false)]
    [InlineData("http://github.com/o/r", false)]
    [InlineData("https://github.com/o", false)]
    public void RepoRef_TryParse(string url, bool ok) => Assert.Equal(ok, RepoRef.TryParse(url, out _));

    [Fact] public void Description_FirstLineNexus()
    {
        var d = ModDescription.Parse("https://www.nexusmods.com/warofrights/mods/3\nUniforms of 1846.\n");
        Assert.Equal("https://www.nexusmods.com/warofrights/mods/3", d.NexusUrl);
        Assert.Equal("Uniforms of 1846.", d.Blurb);
    }

    [Fact] public void Description_NonNexusFirstLineIsBlurb()
    {
        var d = ModDescription.Parse("https://evil.com/x\nHi");
        Assert.Null(d.NexusUrl);
        Assert.Equal("https://evil.com/x\nHi", d.Blurb);
    }

    [Fact] public void GameBuild_Parses() => Assert.Equal("26182310", Catalog.ParseGameBuild("26182310\r\n"));
}
