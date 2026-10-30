using System.Text.Json.Nodes;
using WoRModManager.Core;
using WoRModManager.Core.Uniforms;

namespace WoRModManager.Core.Tests;

public class UniformProfilesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("wormm").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    bool running;
    string Profiles => Path.Combine(_dir, "profiles");
    string GameFile => Path.Combine(_dir, "game", "UserConfig", "Cloud", "customizations.json");
    UniformProfiles Store() => new(Profiles, GameFile, () => running);

    void Game(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GameFile)!);
        File.WriteAllText(GameFile, json);
    }

    const string A = """{"Uniforms":[{"key":1,"value":{"Apparel":[2]}}]}""";
    const string B = """{"Uniforms":[{"key":1,"value":{"Apparel":[3]}}]}""";

    [Fact] public void Sync_NoGameFile_CreatesEmptyDefaultAndWritesGameFile()
    {
        var s = Store();
        s.Sync();
        Assert.Equal(["Default"], s.Names());
        Assert.Equal("Default", s.Active);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(GameFile))!["Uniforms"]!.AsArray());
    }

    [Fact] public void Sync_FirstRun_DefaultIsCopyOfGameFile()
    {
        Game(A);
        var s = Store();
        s.Sync();
        Assert.Equal(A, s.Read("Default"));
    }

    [Fact] public void Sync_FirstRun_LeavesValidGameFileUntouched()
    {
        Game(A);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(GameFile, stamp);
        Store().Sync();
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(GameFile)); // no needless write for Steam Cloud to sync
    }

    [Fact] public void Sync_CapturesGameChangesIntoActive_AndKeepsBackup()
    {
        Game(A);
        var s = Store();
        s.Sync();
        Game(B);
        s.Sync();
        Assert.Equal(B, s.Read(s.Active));
        var backups = Directory.GetFiles(Path.Combine(Profiles, ".backup", "Default"));
        Assert.Equal(A, File.ReadAllText(Assert.Single(backups)));
    }

    [Fact] public void Sync_CorruptGameFile_AppliesProfileInstead()
    {
        Game(A);
        var s = Store();
        s.Sync();
        Game("{oops");
        s.Sync();
        Assert.Equal(A, File.ReadAllText(GameFile));
        Assert.Equal(A, s.Read("Default"));
    }

    [Fact] public void Sync_WhileGameRunning_DoesNothing()
    {
        running = true;
        var s = Store();
        s.Sync();
        Assert.Empty(s.Names());
        Assert.False(File.Exists(GameFile));
    }

    [Fact] public void Switch_CapturesThenApplies()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Create("MaW");
        s.Switch("MaW");
        Game(B);
        s.Switch("Default");
        Assert.Equal(B, s.Read("MaW"));
        Assert.Equal(s.Read("Default"), File.ReadAllText(GameFile));
        Assert.Equal("Default", s.Active);
        Assert.Equal("Default", Store().Active); // persisted
    }

    [Fact] public void Switch_WhileGameRunning_Throws()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Create("MaW");
        running = true;
        Assert.Throws<GameRunningException>(() => s.Switch("MaW"));
        Assert.Equal(A, File.ReadAllText(GameFile));
        Assert.Equal("Default", s.Active);
    }

    [Fact] public void Save_Active_WritesGameFile_AndThrowsWhileRunning()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Save("Default", B);
        Assert.Equal(B, File.ReadAllText(GameFile));
        running = true;
        Assert.Throws<GameRunningException>(() => s.Save("Default", A));
        s.Create("Other");
        s.Save("Other", A); // not active: allowed, game file untouched
        Assert.Equal(B, File.ReadAllText(GameFile));
    }

    [Fact] public void Backups_KeepNewestFive()
    {
        Game("{\"n\":0}");
        var s = Store();
        s.Sync();
        for (var i = 1; i <= 7; i++)
        {
            Game($"{{\"n\":{i}}}");
            s.Sync();
        }
        var backups = Directory.GetFiles(Path.Combine(Profiles, ".backup", "Default"));
        Assert.Equal(5, backups.Length);
        Assert.Contains(backups, f => File.ReadAllText(f) == "{\"n\":6}");
        Assert.DoesNotContain(backups, f => File.ReadAllText(f) == "{\"n\":0}");
    }

    [Fact] public void Create_RejectsBadNames()
    {
        var s = Store();
        s.Sync();
        foreach (var bad in new[] { "", "  ", "a/b", "..", ".x", "CON", "com1", "default" })
            Assert.Throws<ArgumentException>(() => s.Create(bad));
        Assert.Equal(["Default"], s.Names());
    }

    [Fact] public void Rename_ActiveUpdatesActive_AndCaseOnlyRenameWorks()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Rename("Default", "Base game");
        Assert.Equal("Base game", s.Active);
        s.Rename("Base game", "Base Game");
        Assert.Equal(["Base Game"], s.Names());
        Assert.Equal(A, s.Read("Base Game"));
        s.Create("MaW");
        Assert.Throws<ArgumentException>(() => s.Rename("MaW", "base game"));
    }

    [Fact] public void Delete_Active_Throws_OtherDeletes()
    {
        var s = Store();
        s.Sync();
        s.Create("MaW");
        Assert.Throws<InvalidOperationException>(() => s.Delete("Default"));
        s.Delete("MaW");
        Assert.Equal(["Default"], s.Names());
    }

    [Fact] public void Switch_ApplyFails_KeepsPreviousActive()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Create("MaW");
        s.Save("MaW", B);
        File.SetAttributes(GameFile, FileAttributes.ReadOnly);
        try { Assert.ThrowsAny<Exception>(() => s.Switch("MaW")); }
        finally { File.SetAttributes(GameFile, FileAttributes.Normal); }
        Assert.Equal("Default", s.Active);
        Assert.Equal(A, File.ReadAllText(GameFile));
        Assert.Equal(B, s.Read("MaW"));
    }

    [Fact] public void Save_Active_ApplyFails_LeavesProfileUnchanged()
    {
        Game(A);
        var s = Store();
        s.Sync();
        File.SetAttributes(GameFile, FileAttributes.ReadOnly);
        try { Assert.ThrowsAny<Exception>(() => s.Save("Default", B)); }
        finally { File.SetAttributes(GameFile, FileAttributes.Normal); }
        Assert.Equal(A, s.Read("Default"));
    }

    [Fact] public void Save_KeepsBackup_AndRefusesStaleEdits()
    {
        Game(A);
        var s = Store();
        s.Sync();
        Assert.Throws<InvalidOperationException>(() => s.Save("Default", B, expected: "something older"));
        Assert.Equal(A, s.Read("Default"));
        s.Save("Default", B, expected: A);
        Assert.Equal(A, File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(Profiles, ".backup", "Default")))));
    }

    [Fact] public void Rename_OntoLeftoverBackupFolder_StillMovesActive()
    {
        Game(A);
        var s = Store();
        s.Sync();
        Directory.CreateDirectory(Path.Combine(Profiles, ".backup", "Union"));
        Game(B);
        s.Sync(); // gives Default a backup folder too
        s.Rename("Default", "Union");
        Assert.Equal("Union", s.Active);
        Assert.Equal(["Union"], s.Names());
        s.Rename("Union", "Union"); // same name: nothing to do
        Assert.Equal("Union", s.Active);
    }

    [Fact] public void Delete_RemovesBackups()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Create("MaW");
        s.Switch("MaW");
        Game(B);
        s.Sync(); // MaW gets a backup
        s.Switch("Default");
        s.Delete("MaW");
        Assert.False(Directory.Exists(Path.Combine(Profiles, ".backup", "MaW")));
    }

    [Fact] public void Sync_ActiveProfileFileMissing_RecreatesItInsteadOfOverwritingAnother()
    {
        Game(A);
        var s = Store();
        s.Sync();
        s.Create("Aaa"); // sorts first
        File.Delete(Path.Combine(Profiles, "Default.json"));
        Game(B);
        s.Sync();
        Assert.Equal("Default", s.Active);
        Assert.Equal(B, s.Read("Default"));
        Assert.Equal(A, s.Read("Aaa"));
    }
}
