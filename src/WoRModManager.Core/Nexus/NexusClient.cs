using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoRModManager.Core.Nexus;

public record NexusModInfo(int ModId, string Name, string Version, DateTimeOffset? UpdatedAt);

// Reads public mod info (name, version, last update) from Nexus's GraphQL API. No login or key; downloads stay manual.
public class NexusClient
{
    const string Game = "warofrights";
    const string Endpoint = "https://api.nexusmods.com/v2/graphql";
    static readonly Regex ModPath = new(@"^/" + Game + @"/mods/(\d{1,9})/?$", RegexOptions.IgnoreCase);
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

    public async Task<IReadOnlyDictionary<int, NexusModInfo>> GetModsAsync(IEnumerable<int> modIds, CancellationToken ct)
    {
        var ids = modIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<int, NexusModInfo>();
        var list = string.Join(",", ids.Select(i => $"{{gameDomain:\"{Game}\",modId:{i}}}"));
        var query = JsonSerializer.Serialize(new { query = $"query{{legacyModsByDomain(ids:[{list}]){{nodes{{modId name version updatedAt}}}}}}" });
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(query, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", "WoRModManager");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("legacyModsByDomain", out var mods) || !mods.TryGetProperty("nodes", out var nodes))
            throw new HttpRequestException("Nexus returned no mod data.");
        var result = new Dictionary<int, NexusModInfo>();
        foreach (var n in nodes.EnumerateArray())
        {
            var id = n.GetProperty("modId").GetInt32();
            result[id] = new NexusModInfo(id, Str(n, "name"), Str(n, "version"),
                DateTimeOffset.TryParse(Str(n, "updatedAt"), out var d) ? d : null);
        }
        return result;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
