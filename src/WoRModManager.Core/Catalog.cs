using System.Text.RegularExpressions;

namespace WoRModManager.Core;

public record RepoRef(string Owner, string Repo)
{
    static readonly Regex Seg = new(@"^[A-Za-z0-9\-_.]+$");

    // Mod id = repo name only: it names folders and state.json entries, which must not show the GitHub owner.
    public string Id => Repo;

    public static bool TryParse(string url, out RepoRef? repo)
    {
        repo = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
            || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var p = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2) return false;
        var o = p[0];
        var r = p[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? p[1][..^4] : p[1];
        if (o is "." or ".." || r is "." or ".." || !Seg.IsMatch(o) || !Seg.IsMatch(r)) return false;
        repo = new RepoRef(o, r);
        return true;
    }
}

public record ModDescription(string? NexusUrl, string Blurb)
{
    public static ModDescription Parse(string? text)
    {
        text = (text ?? "").Trim();
        var nl = text.IndexOf('\n');
        var first = (nl < 0 ? text : text[..nl]).Trim();
        if (Uri.TryCreate(first, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && (u.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
                || u.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            return new(first, nl < 0 ? "" : text[(nl + 1)..].Trim());
        return new(null, text);
    }
}

public static class Catalog
{
    public static IReadOnlyList<RepoRef> Parse(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<RepoRef>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var tok = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (RepoRef.TryParse(tok, out var r) && seen.Add(r!.Id)) list.Add(r);
        }
        return list;
    }

    public static string? ParseGameBuild(string? text)
    {
        var m = Regex.Match(text ?? "", @"\d+");
        return m.Success ? m.Value : null;
    }
}
