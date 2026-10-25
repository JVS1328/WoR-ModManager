using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoRModManager.Core.Nexus;

public record NexusModInfo(int ModId, string Name, string Version, DateTimeOffset? UpdatedAt,
    string Author = "", string Summary = "", string? PictureUrl = null);

public record NexusFileVersion(string Version, DateTimeOffset Date, IReadOnlyList<string> Changelog);

public record NexusModDetails(string Description, IReadOnlyList<NexusFileVersion> Versions);

// Reads public mod info from Nexus's GraphQL API. No login or key; mod files are never downloaded from Nexus.
public class NexusClient
{
    const string Game = "warofrights";
    const string GameId = "10062";
    const string Endpoint = "https://api.nexusmods.com/v2/graphql";
    const string ImageHost = "staticdelivery.nexusmods.com";
    const long MaxImageBytes = 15 << 20;
    static readonly Regex ModPath = new(@"^/" + Game + @"/mods/(\d{1,9})/?$", RegexOptions.IgnoreCase);
    static readonly Regex Md5 = new("^[0-9a-f]{32}$");
    readonly HttpClient _http;

    // The handler is shared with the GitHub client (allowlist), so it must not be disposed here.
    public NexusClient(HttpMessageHandler handler) =>
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };

    public static bool TryParseModUrl(string url, out int modId)
    {
        modId = 0;
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && u.Host is "www.nexusmods.com" or "nexusmods.com"
            && ModPath.Match(u.AbsolutePath) is { Success: true } m && int.TryParse(m.Groups[1].Value, out modId) && modId > 0;
    }

    public static string ModUrl(int modId) => $"https://www.nexusmods.com/{Game}/mods/{modId}";

    public static string FilesUrl(int modId) => ModUrl(modId) + "?tab=files";

    public async Task<IReadOnlyDictionary<int, NexusModInfo>> GetModsAsync(IEnumerable<int> modIds, CancellationToken ct)
    {
        var ids = modIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, NexusModInfo>();
        using var doc = await QueryAsync($"legacyModsByDomain(ids:[{IdList(ids)}]){{nodes{{modId name version updatedAt author summary pictureUrl}}}}", ct);
        var result = new Dictionary<int, NexusModInfo>();
        foreach (var n in Nodes(doc))
        {
            var id = n.GetProperty("modId").GetInt32();
            result[id] = new NexusModInfo(id, Str(n, "name"), Str(n, "version"),
                DateTimeOffset.TryParse(Str(n, "updatedAt"), out var d) ? d : null, Str(n, "author"), Str(n, "summary"),
                Str(n, "pictureUrl") is { Length: > 0 } pic ? pic : null);
        }
        return result;
    }

    // Full description (as plain text) and the version history built from the mod's files, newest first.
    public async Task<NexusModDetails> GetDetailsAsync(int modId, CancellationToken ct)
    {
        using var doc = await QueryAsync($"legacyModsByDomain(ids:[{IdList([modId])}]){{nodes{{description}}}} " +
            $"modFiles(modId:{modId},gameId:\"{GameId}\"){{version category date changelogText}}", ct);
        var description = Nodes(doc).Select(n => Str(n, "description")).FirstOrDefault() ?? "";
        var versions = Data(doc).TryGetProperty("modFiles", out var files) && files.ValueKind == JsonValueKind.Array
            ? files.EnumerateArray()
                .Where(f => !Str(f, "category").Equals("DELETED", StringComparison.OrdinalIgnoreCase) && Str(f, "version").Length > 0)
                .GroupBy(f => Str(f, "version"), StringComparer.OrdinalIgnoreCase)
                .Select(g => new NexusFileVersion(g.Key,
                    DateTimeOffset.FromUnixTimeSeconds(g.Min(f => f.TryGetProperty("date", out var d) && d.TryGetInt64(out var s) ? s : 0)),
                    g.SelectMany(f => f.TryGetProperty("changelogText", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(l => l.GetString() ?? "") : []).Where(l => l.Trim().Length > 0)
                        .Distinct(StringComparer.Ordinal).ToList()))
                .OrderByDescending(v => v.Date).ToList()
            : [];
        return new NexusModDetails(NexusText.ToPlain(description), versions);
    }

    // Which War of Rights mod file (and version) a downloaded file is, by its MD5. Null when Nexus doesn't know it.
    public async Task<(int ModId, string Version)?> IdentifyAsync(string md5, CancellationToken ct)
    {
        if (!Md5.IsMatch(md5)) throw new ArgumentException("Not an MD5 hash.", nameof(md5));
        using var doc = await QueryAsync($"fileHash(md5:\"{md5}\"){{modFile{{modId version game{{domainName}}}}}}", ct);
        if (!Data(doc).TryGetProperty("fileHash", out var hits) || hits.ValueKind != JsonValueKind.Array) return null;
        foreach (var h in hits.EnumerateArray())
            if (h.TryGetProperty("modFile", out var f) && f.ValueKind == JsonValueKind.Object
                && f.TryGetProperty("game", out var g) && Str(g, "domainName") == Game
                && f.TryGetProperty("modId", out var id) && id.TryGetInt32(out var modId))
                return (modId, Str(f, "version"));
        return null;
    }

    // Mod pictures, only from Nexus's image server; anything else is refused without a request.
    public async Task<byte[]?> GetImageAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host != ImageHost) return null;
        using var resp = await _http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > MaxImageBytes) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length <= MaxImageBytes ? bytes : null;
    }

    async Task<JsonDocument> QueryAsync(string body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { query = $"query{{{body}}}" });
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", "WoRModManager");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) return doc;
        doc.Dispose();
        throw new HttpRequestException("Nexus returned no mod data.");
    }

    static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");

    static IEnumerable<JsonElement> Nodes(JsonDocument doc) =>
        Data(doc).TryGetProperty("legacyModsByDomain", out var mods) && mods.ValueKind == JsonValueKind.Object
        && mods.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.EnumerateArray() : throw new HttpRequestException("Nexus returned no mod data.");

    static string IdList(IEnumerable<int> ids) => string.Join(",", ids.Select(i => $"{{gameDomain:\"{Game}\",modId:{i}}}"));

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
