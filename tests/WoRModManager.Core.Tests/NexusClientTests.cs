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

    [Fact] public async Task Allowlist_AllowsNexusApi()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var client = new HttpClient(new AllowlistHandler { InnerHandler = stub });
        Assert.Equal("ok", await client.GetStringAsync("https://api.nexusmods.com/v2/graphql"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("https://www.nexusmods.com/warofrights/mods/3"));
    }
}
