using System.Text.RegularExpressions;

namespace WoRModManager.Core;

public static partial class PakNames
{
    public static bool IsSafePakName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 // covers / \ and control chars
        && !name.Contains("..")
        && name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    public static bool IsModPak(string name) => IsSafePakName(name) && (name[0] is 'z' or 'Z');

    public static string? Prefix(string name) => PrefixRx().Match(name) is { Success: true } m ? m.Value : null;

    [GeneratedRegex("^(z_[A-Za-z0-9]+_)")] private static partial Regex PrefixRx();
}
