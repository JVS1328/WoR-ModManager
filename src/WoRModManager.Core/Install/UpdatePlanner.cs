using WoRModManager.Core.GitHub;

namespace WoRModManager.Core.Install;

public record UpdatePlan(IReadOnlyList<ReleaseAsset> ToDownload, IReadOnlyList<string> ToRemove, long DownloadBytes)
{
    public bool IsEmpty => ToDownload.Count == 0 && ToRemove.Count == 0;
}

public static class UpdatePlanner
{
    public static UpdatePlan Plan(IReadOnlyList<ReleaseAsset> release, IReadOnlyCollection<string> ownedPaks, Func<string, string?> localSha256)
    {
        var paks = release.Where(a => PakNames.IsModPak(a.Name)).ToList();
        var download = paks.Where(a =>
            localSha256(a.Name) is not { } local || a.Sha256 is null
            || !string.Equals(local, a.Sha256, StringComparison.OrdinalIgnoreCase)).ToList();
        var remove = ownedPaks.Where(o => !paks.Any(a => string.Equals(a.Name, o, StringComparison.OrdinalIgnoreCase))).ToList();
        return new UpdatePlan(download, remove, download.Sum(a => a.Size));
    }
}
