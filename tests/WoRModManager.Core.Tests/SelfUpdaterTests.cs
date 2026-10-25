using System.Net;
using System.Security.Cryptography;
using System.Text;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class SelfUpdaterTests
{
    static readonly byte[] NewExe = "MZ new build"u8.ToArray();
    static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(NewExe));
    static readonly string Dir = Directory.CreateTempSubdirectory("wormm-self").FullName;

    static SelfUpdater Updater(string releaseJson, Version current, out StubHttpHandler gh, Action<string>? start = null)
    {
        gh = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") });
        var dl = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NewExe) });
        return new SelfUpdater(new GitHubClient(gh, Directory.CreateTempSubdirectory("wormm-gh").FullName), new Downloader(dl),
            Path.Combine(Dir, "War of Rights Mod Manager.exe"), current, start);
    }

    static string Release(string tag, string name = "War of Rights Mod Manager.exe", bool digest = true) => $$"""
        {"tag_name":"{{tag}}","body":"","assets":[{"name":"{{name}}","size":{{NewExe.Length}},
          {{(digest ? $"\"digest\":\"sha256:{Sha}\"," : "")}}
          "browser_download_url":"https://github.com/JVS1328/WoR-ModManager/releases/download/{{tag}}/x.exe"}]}
        """;

    [Theory] [InlineData("v1.2.3", "1.2.3")] [InlineData("1.0", "1.0")] [InlineData("beta", null)]
    public void ParseTag(string tag, string? v) => Assert.Equal(v is null ? null : Version.Parse(v), SelfUpdater.ParseTag(tag));

    [Fact] public async Task Check_NewerWithExe_ReturnsRelease()
    {
        var r = await Updater(Release("v1.1.0"), new Version(1, 0, 0, 0), out var gh).CheckAsync(default);
        Assert.Equal("v1.1.0", r!.Tag);
        Assert.Equal("https://api.github.com/repos/JVS1328/WoR-ModManager/releases/latest", gh.Requests.Single().RequestUri!.ToString());
        Assert.Null(await Updater(Release("v1.1.0", name: "Other.exe"), new Version(1, 0, 0, 0), out _).CheckAsync(default));
    }

    [Fact] public async Task Check_GitHubDottedAssetName_ReturnsRelease()
    {
        // GitHub swaps spaces for dots when the exe is uploaded
        var r = await Updater(Release("v1.1.0", name: "War.of.Rights.Mod.Manager.exe"), new Version(1, 0, 0, 0), out _).CheckAsync(default);
        Assert.Equal("v1.1.0", r!.Tag);
    }

    [Fact] public async Task Check_SameVersion_ReturnsNull()
    {
        // the assembly reports 4 parts, tags usually 3 (and the reverse must not loop either)
        Assert.Null(await Updater(Release("v1.2.3"), new Version(1, 2, 3, 0), out _).CheckAsync(default));
        Assert.Null(await Updater(Release("v1.2.3.0"), new Version(1, 2, 3), out _).CheckAsync(default));
        Assert.Null(await Updater(Release("v1.0.0"), new Version(1, 2, 3, 0), out _).CheckAsync(default));
    }

    [Fact] public async Task Check_NoDigest_ReturnsNull() =>
        Assert.Null(await Updater(Release("v9.0.0", digest: false), new Version(1, 0, 0, 0), out _).CheckAsync(default));

    [Fact] public async Task Apply_SwapsFiles()
    {
        var exe = Path.Combine(Dir, "War of Rights Mod Manager.exe");
        File.WriteAllText(exe, "MZ old build");
        string? started = null;
        var u = Updater(Release("v2.0.0"), new Version(1, 0, 0, 0), out _, p => started = p);
        var r = await u.CheckAsync(default);

        await u.ApplyAsync(r!, null, default);
        Assert.Equal(NewExe, File.ReadAllBytes(exe));
        Assert.Equal("MZ old build", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
        Assert.Equal(exe, started);

        SelfUpdater.CleanupOld(exe);
        Assert.False(File.Exists(exe + ".old"));
        SelfUpdater.CleanupOld(exe); // nothing left: no throw
    }
}
