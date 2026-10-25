using System.Net;
using System.Text.RegularExpressions;

namespace WoRModManager.Core.Nexus;

// Nexus descriptions are BBCode mixed with HTML. The manager only ever shows plain text, so everything is reduced to that.
public static class NexusText
{
    public static string ToPlain(string bbcode)
    {
        var s = bbcode.Replace("\r", "");
        s = Regex.Replace(s, @"<script.*?</script>|<style.*?</style>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\[img\].*?\[/img\]|\[youtube\].*?\[/youtube\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"\n?<br\s*/?>|</p>|</div>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\[\*\]", "\n• ");
        s = Regex.Replace(s, @"\[/?[a-z*]+(=[^\]]*)?\]", "", RegexOptions.IgnoreCase); // [b], [size=4], [url=...], [/*] ...
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => l.Trim()).ToList();
        var text = string.Join("\n", lines);
        text = Regex.Replace(text, @"\n{3,}", "\n\n"); // at most one blank line in a row
        text = Regex.Replace(text, @"\n\n(?=• )", "\n"); // list items stay together
        return text.Trim();
    }
}
