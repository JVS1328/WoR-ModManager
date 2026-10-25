using System.Diagnostics;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;

namespace WoRModManager.Core;

public class SelfUpdater(GitHubClient gh, Downloader dl, string exePath, Version current, Action<string>? startProcess = null)
{
    public static readonly RepoRef Repo = new("JVS1328", "WoR-ModManager");
    public const string AssetName = "War of Rights Mod Manager.exe";

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct) =>
        await gh.GetLatestReleaseAsync(Repo, ct) is { } r && ParseTag(r.Tag) is { } v && Full(v) > Full(current) && Exe(r) is not null ? r : null;

    public static Version? ParseTag(string tag) =>
        Version.TryParse(tag is ['v' or 'V', .. var rest] ? rest : tag, out var v) ? v : null;

    public async Task ApplyAsync(ReleaseInfo r, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var asset = Exe(r) ?? throw new InvalidOperationException("The release has no verified manager exe.");
        string fresh = exePath + ".new", old = exePath + ".old";
        await dl.DownloadAsync(asset, fresh, progress, ct);
        File.Move(exePath, old, overwrite: true); // Windows allows renaming a running exe
        try { File.Move(fresh, exePath); }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
        (startProcess ?? (p => Process.Start(new ProcessStartInfo(p, "--cleanup-old") { UseShellExecute = false })?.Dispose()))(exePath);
    }

    public static void CleanupOld(string exePath)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.Delete(exePath + ".old");
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // the old process may still be exiting; a leftover .old is harmless
            }
        }
    }

    // GitHub turns spaces into dots in uploaded file names, so "War.of.Rights.Mod.Manager.exe" is the same asset.
    static ReleaseAsset? Exe(ReleaseInfo r) =>
        r.Assets.FirstOrDefault(a => (a.Name == AssetName || a.Name == AssetName.Replace(' ', '.')) && a.Sha256 is not null);

    // Missing parts count as 0, so "1.2.3" equals the assembly's "1.2.3.0".
    static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
