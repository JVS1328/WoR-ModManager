using System.Net;
using System.Security.Cryptography;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class DownloaderTests
{
    private static readonly byte[] Data = "0123456789abcdef"u8.ToArray();
    private static readonly ReleaseAsset Asset = new("z_a.pak", Data.Length,
        Convert.ToHexStringLower(SHA256.HashData(Data)), new Uri("https://github.com/x/y/releases/download/v1/z_a.pak"));

    private static string Dest() => Path.Combine(Directory.CreateTempSubdirectory("wormm-dl").FullName, "z_a.pak");

    private static HttpResponseMessage Ok(Stream body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StreamContent(body) };

    [Fact] public async Task Download_VerifiesAndMoves()
    {
        var stub = new StubHttpHandler(_ => Ok(new MemoryStream(Data)));
        var dest = Dest();
        await new Downloader(stub).DownloadAsync(Asset, dest, null, default);
        Assert.Equal(Data, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Null(stub.Requests.Single().Headers.Range);
    }

    [Fact] public async Task Download_StreamFailsMidway_ResumesWithRange()
    {
        var stub = new StubHttpHandler(
            _ => Ok(new ScriptedStream(Data[..5], () => throw new IOException("connection reset"))),
            req =>
            {
                Assert.Equal("bytes=5-", req.Headers.Range!.ToString());
                return Ok(new MemoryStream(Data[5..]), HttpStatusCode.PartialContent);
            });
        var dest = Dest();
        var d = new Downloader(stub);
        await Assert.ThrowsAsync<IOException>(() => d.DownloadAsync(Asset, dest, null, default));
        Assert.Equal(5, new FileInfo(dest + ".part").Length);

        await d.DownloadAsync(Asset, dest, null, default);
        Assert.Equal(Data, File.ReadAllBytes(dest));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact] public async Task Download_BadHashTwice_ThrowsAndLeavesNoFiles()
    {
        var bad = "XXXXXXXXXXXXXXXX"u8.ToArray();
        var stub = new StubHttpHandler(_ => Ok(new MemoryStream(bad)));
        var dest = Dest();
        await Assert.ThrowsAsync<ChecksumMismatchException>(() => new Downloader(stub).DownloadAsync(Asset, dest, null, default));
        Assert.False(File.Exists(dest)); Assert.False(File.Exists(dest + ".part"));
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Null(r.Headers.Range));
    }

    [Fact] public async Task Download_Cancelled_KeepsPartForResume()
    {
        using var cts = new CancellationTokenSource();
        var stub = new StubHttpHandler(_ => Ok(new ScriptedStream(Data[..5], () => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); })));
        var dest = Dest();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Downloader(stub).DownloadAsync(Asset, dest, null, cts.Token));
        Assert.Equal(5, new FileInfo(dest + ".part").Length);
        Assert.False(File.Exists(dest));
    }

    // Yields the given bytes, then runs onEnd (which throws) instead of reporting end-of-stream.
    private sealed class ScriptedStream(byte[] data, Action onEnd) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) { onEnd(); return 0; }
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
