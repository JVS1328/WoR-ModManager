using System.Net;
using System.Text;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Nexus;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class NexusClientTests
{
    [Theory]
    [InlineData("https://www.nexusmods.com/warofrights/mods/3", 3)]
    [InlineData("https://nexusmods.com/warofrights/mods/12?tab=files", 12)]
    [InlineData("www.nexusmods.com/warofrights/mods/2", 2)]
    [InlineData("https://www.nexusmods.com/warofrights/mods/2/", 2)]
    [InlineData("https://www.nexusmods.com/skyrim/mods/3", null)]
    [InlineData("https://evil.com/warofrights/mods/3", null)]
    [InlineData("http://www.nexusmods.com/warofrights/mods/3", null)]
    [InlineData("https://www.nexusmods.com/warofrights/mods/abc", null)]
    [InlineData("", null)]
    public void TryParseModUrl(string url, int? expected) =>
        Assert.Equal(expected, NexusClient.TryParseModUrl(url, out var id) ? id : null);

    [Fact] public void ModUrl_IsCanonical() =>
        Assert.Equal("https://www.nexusmods.com/warofrights/mods/3", NexusClient.ModUrl(3));

    [Fact] public async Task GetMods_PostsOneQuery_ParsesVersions()
    {
        string? body = null;
        var stub = new StubHttpHandler(req =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://api.nexusmods.com/v2/graphql", req.RequestUri!.ToString());
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
              {"data":{"legacyModsByDomain":{"nodes":[
                {"modId":3,"name":"Blackpowder","version":"1.0","updatedAt":"2026-10-16T05:05:27Z"},
                {"modId":2,"name":"The Mexican American War","version":"1.3.5","updatedAt":"2026-10-25T14:08:59Z"}]}}}
            """, Encoding.UTF8, "application/json") };
        });
        var mods = await new NexusClient(stub).GetModsAsync([3, 2, 3], default);

        Assert.Equal("1.3.5", mods[2].Version);
        Assert.Equal("Blackpowder", mods[3].Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 5, 5, 27, TimeSpan.Zero), mods[3].UpdatedAt);
        Assert.Contains("warofrights", body);
        Assert.Single(stub.Requests);
    }

    [Fact] public async Task GetMods_NoIds_NoRequest()
    {
        var stub = new StubHttpHandler();
        Assert.Empty(await new NexusClient(stub).GetModsAsync([], default));
        Assert.Empty(stub.Requests);
    }

    [Fact] public async Task GetMods_GraphQlErrors_Throws()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"errors":[{"message":"nope"}]}""", Encoding.UTF8, "application/json") });
        await Assert.ThrowsAsync<HttpRequestException>(() => new NexusClient(stub).GetModsAsync([3], default));
    }

    static StubHttpHandler Json(string json) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    [Fact] public void Plain_ConvertsNexusBbCodeToReadableText()
    {
        var bb = "[size=4][b]Description[/b][/size]\n<br />Gunshots are [u]snappier[/u] &amp; louder.\n<br />[list]\n<br />[*]Bigger explosions[/*]\n<br />[*]Denser smoke[/*]\n<br />[/list]\n<br />\n<br />\n<br />[url=https://x.y]See this[/url] [img]https://x.y/a.png[/img]";
        Assert.Equal("Description\nGunshots are snappier & louder.\n• Bigger explosions\n• Denser smoke\n\nSee this", NexusText.ToPlain(bb));
    }

    [Fact] public void Plain_StripsHtmlTags() =>
        Assert.Equal("a\nb", NexusText.ToPlain("<p>a</p>b<script>x</script>"));

    [Fact] public async Task GetDetails_DescriptionAndVersionHistory_NewestFirst()
    {
        var stub = Json("""
          {"data":{"legacyModsByDomain":{"nodes":[{"description":"[b]Hi[/b]"}]},
           "modFiles":[
             {"version":"1.3","category":"ARCHIVED","date":1792463745,"changelogText":["Added Colt Patterson","Added Hall Rifle"]},
             {"version":"1.3.1","category":"MAIN","date":1792473330,"changelogText":["Fixed black textures"]},
             {"version":"1.3.1","category":"OPTIONAL","date":1792473400,"changelogText":["Fixed black textures"]},
             {"version":"0.9","category":"DELETED","date":1792000000,"changelogText":[]}]}}
        """);
        var d = await new NexusClient(stub).GetDetailsAsync(2, default);
        Assert.Equal("Hi", d.Description);
        Assert.Equal(["1.3.1", "1.3"], d.Versions.Select(v => v.Version));
        Assert.Equal(["Fixed black textures"], d.Versions[0].Changelog);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792463745), d.Versions[1].Date);
    }

    [Fact] public async Task Identify_KnownHash_ReturnsModAndVersion()
    {
        var stub = Json("""{"data":{"fileHash":[{"modFile":{"modId":3,"version":"1.0","game":{"domainName":"warofrights"}}}]}}""");
        Assert.Equal((3, "1.0"), await new NexusClient(stub).IdentifyAsync("43f4edeaa0ad59b948b8f756a67a6ff3", default));
    }

    [Fact] public async Task Identify_OtherGameOrUnknown_ReturnsNull()
    {
        Assert.Null(await new NexusClient(Json("""{"data":{"fileHash":[{"modFile":{"modId":9,"version":"2","game":{"domainName":"skyrim"}}}]}}"""))
            .IdentifyAsync("43f4edeaa0ad59b948b8f756a67a6ff3", default));
        Assert.Null(await new NexusClient(Json("""{"data":{"fileHash":[]}}""")).IdentifyAsync("43f4edeaa0ad59b948b8f756a67a6ff3", default));
        await Assert.ThrowsAsync<ArgumentException>(() => new NexusClient(Json("{}")).IdentifyAsync("not-a-hash\"}", default));
    }

    [Fact] public async Task Image_OnlyFromNexusImageHost()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var c = new NexusClient(stub);
        Assert.Equal([1, 2, 3], await c.GetImageAsync("https://staticdelivery.nexusmods.com/mods/10062/images/2/a.png", default));
        Assert.Null(await c.GetImageAsync("https://evil.com/a.png", default));
        Assert.Null(await c.GetImageAsync("http://staticdelivery.nexusmods.com/a.png", default));
        Assert.Single(stub.Requests);
    }

    [Fact] public async Task Allowlist_AllowsNexusApi()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var client = new HttpClient(new AllowlistHandler { InnerHandler = stub });
        Assert.Equal("ok", await client.GetStringAsync("https://api.nexusmods.com/v2/graphql"));
        Assert.Equal("ok", await client.GetStringAsync("https://staticdelivery.nexusmods.com/mods/10062/images/2/a.png"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://www.nexusmods.com/warofrights/mods/3"));
    }
}
