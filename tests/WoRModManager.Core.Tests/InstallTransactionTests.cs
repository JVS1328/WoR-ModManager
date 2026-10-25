using System.Text.Json;
using WoRModManager.Core.Install;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class InstallTransactionTests
{
    private static readonly byte[] A = [1], A2 = [11], B = [2], C = [3];

    private static string Stage(TempGame g, string name, byte[] bytes)
    {
        Directory.CreateDirectory(g.Paths.Staging);
        var path = Path.Combine(g.Paths.Staging, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string InAssets(TempGame g, string name) => Path.Combine(g.Paths.Assets, name);

    private static void AssertClean(TempGame g)
    {
        Assert.False(File.Exists(g.Paths.Journal));
        Assert.True(!Directory.Exists(g.Paths.Backup) || !Directory.EnumerateFileSystemEntries(g.Paths.Backup).Any());
    }

    private static void WriteJournal(TempGame g, string[] newNames, string[] removeNames, string[] backedUp) =>
        File.WriteAllText(g.Paths.Journal, JsonSerializer.Serialize(new
        {
            targetDir = g.Paths.Assets, newNames, removeNames, backedUp,
        }));

    [Fact] public void Commit_ReplacesAddsAndRemoves()
    {
        using var g = new TempGame();
        g.WritePak("z_a.pak", A); g.WritePak("z_b.pak", B);
        var staged = new Dictionary<string, string> { ["z_a.pak"] = Stage(g, "z_a.pak", A2), ["z_c.pak"] = Stage(g, "z_c.pak", C) };

        InstallTransaction.Commit(g.Paths, g.Paths.Assets, staged, ["z_b.pak"]);

        Assert.Equal(A2, File.ReadAllBytes(InAssets(g, "z_a.pak")));
        Assert.Equal(C, File.ReadAllBytes(InAssets(g, "z_c.pak")));
        Assert.False(File.Exists(InAssets(g, "z_b.pak")));
        Assert.All(staged.Values, s => Assert.False(File.Exists(s)));
        AssertClean(g);
    }

    [Fact] public void Commit_FailureMidway_RollsBack()
    {
        using var g = new TempGame();
        g.WritePak("z_a.pak", A); g.WritePak("z_b.pak", B);
        var staged = new Dictionary<string, string> { ["z_a.pak"] = Stage(g, "z_a.pak", A2), ["z_c.pak"] = Stage(g, "z_c.pak", C) };
        var moves = 0;

        Assert.Throws<IOException>(() => InstallTransaction.Commit(g.Paths, g.Paths.Assets, staged, ["z_b.pak"],
            _ => { if (++moves == 2) throw new IOException("disk full"); }));

        Assert.Equal(A, File.ReadAllBytes(InAssets(g, "z_a.pak")));
        Assert.Equal(B, File.ReadAllBytes(InAssets(g, "z_b.pak")));
        Assert.False(File.Exists(InAssets(g, "z_c.pak")));
        AssertClean(g);
    }

    [Fact] public void Recover_AfterCrashMidCommit_RestoresPreviousFiles()
    {
        using var g = new TempGame();
        WriteJournal(g, ["z_a.pak", "z_c.pak"], [], ["z_a.pak"]);
        Directory.CreateDirectory(g.Paths.Backup);
        File.WriteAllBytes(Path.Combine(g.Paths.Backup, "z_a.pak"), A);
        g.WritePak("z_a.pak", A2);
        g.WritePak("z_c.pak", [3, 3]); // half-copied new file

        Assert.True(InstallTransaction.Recover(g.Paths));

        Assert.Equal(A, File.ReadAllBytes(InAssets(g, "z_a.pak")));
        Assert.False(File.Exists(InAssets(g, "z_c.pak")));
        AssertClean(g);
    }

    [Fact] public void Recover_CrashBeforeOrDuringBackup_KeepsOriginal()
    {
        using var g = new TempGame();
        WriteJournal(g, ["z_a.pak"], ["z_b.pak"], ["z_a.pak", "z_b.pak"]);
        g.WritePak("z_a.pak", A); g.WritePak("z_b.pak", B);
        Directory.CreateDirectory(g.Paths.Backup);
        File.WriteAllBytes(Path.Combine(g.Paths.Backup, "z_b.pak.tmp"), [2, 2]); // cross-drive backup copy killed midway

        Assert.True(InstallTransaction.Recover(g.Paths));

        Assert.Equal(A, File.ReadAllBytes(InAssets(g, "z_a.pak")));
        Assert.Equal(B, File.ReadAllBytes(InAssets(g, "z_b.pak")));
        AssertClean(g);
    }

    [Fact] public void Commit_UnsafeName_Throws_NothingTouched()
    {
        using var g = new TempGame();
        g.WritePak("z_a.pak", A);
        var staged = new Dictionary<string, string> { ["z_a.pak"] = Stage(g, "z_a.pak", A2), ["..\\evil.pak"] = Stage(g, "evil.pak", C) };

        Assert.Throws<ArgumentException>(() => InstallTransaction.Commit(g.Paths, g.Paths.Assets, staged, ["z_b.exe"]));

        Assert.Equal(A, File.ReadAllBytes(InAssets(g, "z_a.pak")));
        Assert.All(staged.Values, s => Assert.True(File.Exists(s)));
        Assert.False(File.Exists(g.Paths.Journal));
        Assert.False(Directory.Exists(g.Paths.Backup));
    }

    [Fact] public void Recover_NoJournal_ReturnsFalse()
    {
        using var g = new TempGame();
        g.WritePak("z_a.pak", A);
        Assert.False(InstallTransaction.Recover(g.Paths));
        Assert.Equal(A, File.ReadAllBytes(InAssets(g, "z_a.pak")));
    }
}
