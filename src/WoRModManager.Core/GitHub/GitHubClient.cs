using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoRModManager.Core.GitHub;

public class GitHubClient
{
    private const long MaxAssetTextBytes = 1 << 20;
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private static readonly string Version =
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3) ?? "1.0.0";

    // R2: the handler is shared with the Downloader, so we must not dispose it.
    public GitHubClient(HttpMessageHandler handler, string cacheDir)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    public static HttpMessageHandler CreateDefaultHandler() =>
        new AllowlistHandler { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } };

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(RepoRef repo, CancellationToken ct)
    {
        var r = await FetchAsync($"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/releases/latest", api: true, int.MaxValue, ct);
        if (r.Body is null) return null;
        using var doc = JsonDocument.Parse(r.Body);
        var root = doc.RootElement;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr))
            foreach (var a in arr.EnumerateArray())
            {
                string? sha = null;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = ds[7..].ToLowerInvariant();
                assets.Add(new ReleaseAsset(a.GetProperty("name").GetString()!, a.GetProperty("size").GetInt64(), sha,
                    new Uri(a.GetProperty("browser_download_url").GetString()!)));
            }
        return new ReleaseInfo(Str(root, "tag_name"), Str(root, "body"), assets, r.FromCache);
    }

    public async Task<string?> GetRawTextAsync(RepoRef repo, string path, CancellationToken ct) =>
        await GetRawBytesAsync(repo, path, ct) is { } b ? Encoding.UTF8.GetString(b) : null;

    public async Task<(string? Text, bool FromCache)> GetRawTextWithCacheFlagAsync(RepoRef repo, string path, CancellationToken ct)
    {
        var (b, fromCache) = await FetchAsync($"https://raw.githubusercontent.com/{repo.Owner}/{repo.Repo}/HEAD/{path}", api: false, int.MaxValue, ct);
        return (b is null ? null : Encoding.UTF8.GetString(b), fromCache);
    }

    public async Task<byte[]?> GetRawBytesAsync(RepoRef repo, string path, CancellationToken ct) =>
        (await FetchAsync($"https://raw.githubusercontent.com/{repo.Owner}/{repo.Repo}/HEAD/{path}", api: false, int.MaxValue, ct)).Body;

    public async Task<string?> GetAssetTextAsync(ReleaseAsset asset, CancellationToken ct) =>
        (await FetchAsync(asset.DownloadUrl.ToString(), api: false, (int)MaxAssetTextBytes, ct)).Body is { } b ? Encoding.UTF8.GetString(b) : null;

    // HttpClient.Timeout stops at the headers; without this a stalled body read would wait forever.
    internal static async ValueTask<int> ReadOrStallAsync(Stream s, Memory<byte> buf, CancellationTokenSource idle, TimeSpan timeout, CancellationToken ct)
    {
        idle.CancelAfter(timeout);
        try { return await s.ReadAsync(buf, idle.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("The connection stalled."); }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private async Task<(byte[]? Body, bool FromCache)> FetchAsync(string url, bool api, int maxBytes, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        var bodyFile = Path.Combine(_cacheDir, key + ".body");
        var etagFile = Path.Combine(_cacheDir, key + ".etag");
        var cached = File.Exists(bodyFile);

        async Task<(byte[]?, bool)> Fallback(string why, bool rateLimited) =>
            cached ? (await File.ReadAllBytesAsync(bodyFile, ct), true) : throw new GitHubUnavailableException(why, rateLimited);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", $"WoRModManager/{Version}");
            if (api)
            {
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
            if (cached && File.Exists(etagFile)) req.Headers.TryAddWithoutValidation("If-None-Match", await File.ReadAllTextAsync(etagFile, ct));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return (null, false);
            if (resp.StatusCode == HttpStatusCode.NotModified && cached) return (await File.ReadAllBytesAsync(bodyFile, ct), false);
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0")
                return await Fallback("GitHub rate limit reached", true);
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException($"Response too large: {url}");
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buf = new byte[81920];
            for (int n; (n = await ReadOrStallAsync(src, buf, idle, IdleTimeout, ct)) > 0;)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new InvalidDataException($"Response too large: {url}");
            }
            var body = ms.ToArray();
            await File.WriteAllBytesAsync(bodyFile, body, ct);
            if (resp.Headers.ETag is { } et) await File.WriteAllTextAsync(etagFile, et.ToString(), ct);
            else File.Delete(etagFile);
            return (body, false);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return await Fallback($"GitHub unreachable: {e.Message}", false);
        }
    }
}
