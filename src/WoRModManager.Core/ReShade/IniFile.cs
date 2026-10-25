using System.Text;
using System.Text.RegularExpressions;

namespace WoRModManager.Core.ReShade;

// Line-preserving INI editor: untouched lines (comments, order, line endings, BOM) are written back exactly.
public class IniFile
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    readonly List<string> _lines; // each keeps its own line ending
    readonly bool _bom;
    readonly string _nl;

    IniFile(string text, bool bom)
    {
        _bom = bom;
        _lines = [.. Regex.Split(text, "(?<=\n)").Where(l => l.Length > 0)];
        _nl = text.Contains('\n') && !text.Contains("\r\n") ? "\n" : "\r\n";
    }

    public static IniFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var skip = bom ? Encoding.UTF8.Preamble.Length : 0;
        return new IniFile(Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip), bom);
    }

    public string? Get(string section, string key)
    {
        var i = Find(section, key).Key;
        if (i < 0) return null;
        var c = Content(_lines[i]);
        return c[(c.IndexOf('=') + 1)..].Trim();
    }

    public void Set(string section, string key, string value)
    {
        var (k, last) = Find(section, key);
        if (k >= 0)
        {
            var c = Content(_lines[k]);
            _lines[k] = c[..(c.IndexOf('=') + 1)] + value + _lines[k][c.Length..];
            return;
        }
        if (last == -2)
        {
            EndWithNewline(_lines.Count - 1);
            if (_lines.Count > 0) _lines.Add(_nl);
            _lines.Add($"[{section}]{_nl}");
            last = _lines.Count - 1;
        }
        EndWithNewline(last);
        _lines.Insert(last + 1, $"{key}={value}{_nl}");
    }

    // Written to a temp file first so a crash mid-write can't truncate the player's ReShade.ini.
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Concat(_lines), new UTF8Encoding(_bom));
        File.Move(tmp, path, overwrite: true);
    }

    static string Content(string line) => line.TrimEnd('\r', '\n');

    static string? KeyOf(string trimmed) =>
        trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.IndexOf('=') is not (> 0 and var i) ? null : trimmed[..i].Trim();

    void EndWithNewline(int i)
    {
        if (i >= 0 && !_lines[i].EndsWith('\n')) _lines[i] += _nl;
    }

    // Key = line index of the key or -1; Last = last non-blank line of the section, -1 = before line 0, -2 = no such section.
    (int Key, int Last) Find(string section, string key)
    {
        var cur = "";
        int k = -1, last = section == "" ? -1 : -2;
        for (var i = 0; i < _lines.Count; i++)
        {
            var t = Content(_lines[i]).Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                cur = t[1..^1].Trim();
                if (Ci.Equals(cur, section) && last == -2) last = i;
                continue;
            }
            if (!Ci.Equals(cur, section)) continue;
            if (t.Length > 0) last = i;
            if (k < 0 && KeyOf(t) is { } name && Ci.Equals(name, key)) k = i;
        }
        return (k, last);
    }
}
