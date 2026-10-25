using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WoRModManager.Core.GitHub;

namespace WoRModManager.Core.Install;

public record DownloadProgress(long BytesDone, long BytesTotal, double BytesPerSecond);

public class ChecksumMismatchException(string file) : Exception($"Checksum mismatch: {file}");

public class Downloader(HttpMessageHandler handler)
{
    private const int BufferSize = 81920;
    // the handler is shared with GitHubClient, so we must not dispose it.
    private readonly HttpClient _http = new(handler, disposeHandler: false);

    public TimeSpan IdleTimeout { get; init; } = GitHubClient.IdleTimeout;

    public async Task DownloadAsync(ReleaseAsset asset, string destPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        if (asset.Sha256 is null) File.Delete(part); // nothing could tell a stale leftover from the current asset
        for (var attempt = 0; ; attempt++)
        {
            await FetchAsync(asset, part, progress, ct);
            if (await VerifyAsync(asset, part, ct)) break;
            File.Delete(part);
            if (attempt == 1) throw new ChecksumMismatchException(asset.Name);
        }
        File.Move(part, destPath, overwrite: true);
    }

    private async Task FetchAsync(ReleaseAsset asset, string part, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var done = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (File.Exists(part) && done >= asset.Size) return; // complete (or oversized) leftover: verification decides
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        if (done > 0) req.Headers.Range = new RangeHeaderValue(done, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != HttpStatusCode.PartialContent) done = 0; // server ignored Range: restart

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var buf = new byte[BufferSize];
        var clock = Stopwatch.StartNew();
        long fresh = 0, lastReportMs = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int n;
        while ((n = await GitHubClient.ReadOrStallAsync(src, buf, idle, IdleTimeout, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            fresh += n;
            if (progress != null && clock.ElapsedMilliseconds - lastReportMs >= 250)
            {
                lastReportMs = clock.ElapsedMilliseconds;
                progress.Report(new DownloadProgress(done, asset.Size, fresh / clock.Elapsed.TotalSeconds));
            }
        }
        dst.Flush(flushToDisk: true); // the file may be moved into Assets soon; don't let a power loss leave zeros
    }

    private static async Task<bool> VerifyAsync(ReleaseAsset asset, string part, CancellationToken ct)
    {
        if (new FileInfo(part).Length != asset.Size) return false;
        if (asset.Sha256 is null) return true;
        await using var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct)), asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
