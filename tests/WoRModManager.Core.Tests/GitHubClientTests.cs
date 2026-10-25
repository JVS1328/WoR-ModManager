using System.Net;
using System.Text;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class GitHubClientTests
{
    private static HttpResponseMessage Json(int code, string body, string? etag = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag != null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }
    private static string TempDir() => Directory.CreateTempSubdirectory("wormm-gh").FullName;
    private static readonly RepoRef Repo = new("JVS1328", "WoR-MAW-releases");
    private const string Rel = """{"tag_name":"v1","body":"n","assets":[]}""";

    [Fact] public async Task GetReleaseHistory_NewestFirst_SkipsDraftsAndPrereleases()
    {
        var stub = new StubHttpHandler(req =>
        {
            Assert.Equal("https://api.github.com/repos/JVS1328/WoR-MAW-releases/releases?per_page=100", req.RequestUri!.ToString());
            return Json(200, """
              [{"tag_name":"v1.3.5","body":"test","draft":false,"prerelease":true,"published_at":"2026-10-26T10:00:00Z"},
               {"tag_name":"v1.3.4","body":"MAW 1.3.4\n- icons","draft":false,"prerelease":false,"published_at":"2026-10-25T09:30:00Z"},
               {"tag_name":"v9","body":"wip","draft":true,"prerelease":false,"published_at":null},
               {"tag_name":"v1.3.3","body":null,"draft":false,"prerelease":false,"published_at":"2026-10-24T12:00:00Z"}]
            """);
        });
        var h = await new GitHubClient(stub, TempDir()).GetReleaseHistoryAsync(Repo, default);
        Assert.Equal(new[] { "v1.3.4", "v1.3.3" }, h.Select(x => x.Tag));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero), h[0].Published);
        Assert.Equal("MAW 1.3.4\n- icons", h[0].Notes);
        Assert.Equal("", h[1].Notes);
    }

    [Fact] public async Task GetLatestRelease_ParsesAssetsAndDigest()
    {
        var stub = new StubHttpHandler(_ => Json(200, """
          {"tag_name":"v1.3.4","body":"notes","assets":[
            {"name":"z_maw_UI.pak","size":10,"digest":"sha256:ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789","browser_download_url":"https://github.com/JVS1328/WoR-MAW-releases/releases/download/v1.3.4/z_maw_UI.pak"},
            {"name":"old.pak","size":1,"browser_download_url":"https://github.com/x/y/releases/download/v1/old.pak"}]}
        """));
        var r = await new GitHubClient(stub, TempDir()).GetLatestReleaseAsync(Repo, default);
        Assert.Equal("v1.3.4", r!.Tag);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.Assets[0].Sha256);
        Assert.Null(r.Assets[1].Sha256);
        var h = stub.Requests[0].Headers;
        Assert.Equal("https://api.github.com/repos/JVS1328/WoR-MAW-releases/releases/latest", stub.Requests[0].RequestUri!.ToString());
        Assert.StartsWith("WoRModManager/", h.UserAgent.ToString());
        Assert.Equal("2022-11-28", h.GetValues("X-GitHub-Api-Version").Single());
    }

    [Fact] public async Task GetLatestRelease_SendsEtagAndUses304Cache()
    {
        var stub = new StubHttpHandler(_ => Json(200, Rel, "\"e1\""),
            req => { Assert.Equal("\"e1\"", req.Headers.IfNoneMatch.ToString()); return new HttpResponseMessage(HttpStatusCode.NotModified); });
        var c = new GitHubClient(stub, TempDir());
        var a = await c.GetLatestReleaseAsync(Repo, default);
        var b = await c.GetLatestReleaseAsync(Repo, default);
        Assert.Equal(a!.Tag, b!.Tag);
        Assert.False(b.FromCache);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact] public async Task GetLatestRelease_RateLimited_ThrowsGitHubUnavailable()
    {
        var stub = new StubHttpHandler(_ =>
        {
            var r = Json(403, "{}");
            r.Headers.Add("X-RateLimit-Remaining", "0");
            return r;
        });
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubClient(stub, TempDir()).GetLatestReleaseAsync(Repo, default));
        Assert.True(ex.RateLimited);
    }

    [Fact] public async Task GetLatestRelease_Offline_ReturnsCachedResponse()
    {
        var stub = new StubHttpHandler(_ => Json(200, Rel, "\"e1\""), _ => throw new HttpRequestException("offline"));
        var c = new GitHubClient(stub, TempDir());
        await c.GetLatestReleaseAsync(Repo, default);
        var r = await c.GetLatestReleaseAsync(Repo, default);
        Assert.True(r!.FromCache);
    }

    [Fact] public async Task GetLatestRelease_OfflineNoCache_ThrowsNotRateLimited()
    {
        var stub = new StubHttpHandler(_ => throw new HttpRequestException("offline"));
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() => new GitHubClient(stub, TempDir()).GetLatestReleaseAsync(Repo, default));
        Assert.False(ex.RateLimited);
    }

    [Fact] public async Task GetRawText_404_ReturnsNull_AndUsesRawUrl()
    {
        var stub = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await new GitHubClient(stub, TempDir()).GetRawTextAsync(Repo, "catalog.txt", default));
        Assert.Equal("https://raw.githubusercontent.com/JVS1328/WoR-MAW-releases/HEAD/catalog.txt", stub.Requests[0].RequestUri!.ToString());
    }

    [Fact] public async Task Allowlist_BlocksRedirectToOtherHost()
    {
        var stub = new StubHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.com/x");
            return r;
        });
        var client = new HttpClient(new AllowlistHandler { InnerHandler = stub });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://github.com/a/b/releases/download/v1/z.pak"));
        Assert.Single(stub.Requests);
    }

    [Fact] public async Task Allowlist_BlocksHttpAndUnknownHost()
    {
        var client = new HttpClient(new AllowlistHandler { InnerHandler = new StubHttpHandler(_ => new HttpResponseMessage()) });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://github.com/x"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.com/x"));
    }

    [Fact] public async Task Allowlist_FollowsRedirectToReleaseAssetsHost()
    {
        var stub = new StubHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://release-assets.githubusercontent.com/a/b");
            return r;
        }, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hi") });
        var client = new HttpClient(new AllowlistHandler { InnerHandler = stub });
        Assert.Equal("hi", await client.GetStringAsync("https://github.com/a/b/releases/download/v1/z.pak"));
    }
}
