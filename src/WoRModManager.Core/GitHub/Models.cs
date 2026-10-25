namespace WoRModManager.Core.GitHub;

public record ReleaseAsset(string Name, long Size, string? Sha256, Uri DownloadUrl);

public record ReleaseInfo(string Tag, string Body, IReadOnlyList<ReleaseAsset> Assets, bool FromCache);

public class GitHubUnavailableException(string message, bool rateLimited) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}
