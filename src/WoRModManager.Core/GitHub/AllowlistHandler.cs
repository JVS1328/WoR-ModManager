using System.Net;

namespace WoRModManager.Core.GitHub;

// The only gate to the network: https + host allowlist, re-checked on every redirect hop.
public class AllowlistHandler : DelegatingHandler
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "raw.githubusercontent.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "api.nexusmods.com", // mod info for mods linked to a Nexus page; no mod files are downloaded from Nexus
        "staticdelivery.nexusmods.com", // those mods' pictures
    };
    private const int MaxHops = 5;

    private static void Check(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(uri.Host))
            throw new HttpRequestException($"Blocked by allowlist: {uri}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            Check(request.RequestUri);
            var resp = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (resp.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || resp.Headers.Location is null)
                return resp;
            var next = new Uri(request.RequestUri!, resp.Headers.Location);
            resp.Dispose();
            if (hop >= MaxHops) throw new HttpRequestException("Too many redirects");
            Check(next);
            var copy = new HttpRequestMessage(HttpMethod.Get, next);
            foreach (var h in request.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            request = copy;
        }
    }
}
