using System.Net;
using System.Security.Cryptography;
using System.Text;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
using WoRModManager.Core.State;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class ModServiceTests
{
    private const string MawId = "WoR-MAW-releases";

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static ReleaseAsset A(string name, byte[] data) =>
        new(name, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)), new Uri($"https://github.com/JVS1328/{MawId}/releases/download/v/{name}"));

    private static OfficialMod Maw(string tag, IEnumerable<ReleaseAsset> assets, string? gameBuild = null) =>
        new(new RepoRef("JVS1328", "WoR-MAW-releases"), "MAW", new ModDescription(null, ""), null,
            new ReleaseInfo(tag, "", assets.ToList(), false), null, gameBuild);

    // Serves release assets by file name; names in Fail return 500. Records every request.
    private sealed class Fixture : IDisposable
    {
        public TempGame G { get; } = new();
        public GamePaths P { get; }
        public Dictionary<string, byte[]> Files { get; } = new();
        public HashSet<string> Fail { get; } = new();
        public StubHttpHandler Http { get; }
        public bool Running { get; set; }
        public ModService Svc { get; }

        public Fixture(Func<string, GamePaths>? layout = null)
        {
            P = layout?.Invoke(G.Root) ?? G.Paths;
            Http = new StubHttpHandler(req =>
            {
                var n = req.RequestUri!.Segments[^1];
                return !Fail.Contains(n) && Files.TryGetValue(n, out var b)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            });
            Svc = new ModService(P, new GitHubClient(Http, Path.Combine(G.Root, "cache")), new Downloader(Http), () => Running);
        }

        public OfficialMod Release(string tag, params (string Name, string Body)[] paks)
        {
            foreach (var (n, body) in paks) Files[n] = B(body);
            return Maw(tag, paks.Select(p => A(p.Name, B(p.Body))));
        }

        public List<string> Requested => Http.Requests.Select(r => r.RequestUri!.Segments[^1]).ToList();
        public List<ModRecord> State => new StateStore(P).Load();
        public string Asset(string n) => Path.Combine(P.Assets, n);
        public void Dispose() => G.Dispose();
    }

    [Fact] public async Task Install_FreshMod_WritesPaksAndState()
    {
        using var f = new Fixture();
        var mod = f.Release("v1", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "bbb"));

        await f.Svc.InstallOrUpdateAsync(mod, null, false, default);

        Assert.Equal("aaa", File.ReadAllText(f.Asset("z_maw_A.pak")));
        Assert.Equal("bbb", File.ReadAllText(f.Asset("z_maw_B.pak")));
        var r = Assert.Single(f.State);
        Assert.Equal((MawId, ModSource.Official, MawId, "v1", true), (r.Id, r.Source, r.RepoId, r.Version, r.Enabled));
        Assert.Equal(["z_maw_A.pak", "z_maw_B.pak"], r.Paks.Order());
        Assert.False(Directory.Exists(Path.Combine(f.P.Staging, MawId)));
    }

    [Fact] public async Task Update_OnlyDownloadsChangedPaks()
    {
        using var f = new Fixture();
        await f.Svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "bbb"), ("z_maw_Old.pak", "old")), null, false, default);
        f.Http.Requests.Clear();

        await f.Svc.InstallOrUpdateAsync(f.Release("v2", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "BBB2")), null, false, default);

        Assert.Equal(["z_maw_B.pak"], f.Requested);
        Assert.Equal("BBB2", File.ReadAllText(f.Asset("z_maw_B.pak")));
        Assert.False(File.Exists(f.Asset("z_maw_Old.pak"))); // dropped from the release
        var r = Assert.Single(f.State);
        Assert.Equal("v2", r.Version);
        Assert.Equal(["z_maw_A.pak", "z_maw_B.pak"], r.Paks.Order());
    }

    [Fact] public async Task Update_DownloadFails_AssetsUntouched()
    {
        using var f = new Fixture();
        await f.Svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "bbb")), null, false, default);
        var v2 = f.Release("v2", ("z_maw_A.pak", "AAA2"), ("z_maw_B.pak", "BBB2"));
        f.Fail.Add("z_maw_B.pak");

        await Assert.ThrowsAsync<HttpRequestException>(() => f.Svc.InstallOrUpdateAsync(v2, null, false, default));

        Assert.Equal("aaa", File.ReadAllText(f.Asset("z_maw_A.pak")));
        Assert.Equal("bbb", File.ReadAllText(f.Asset("z_maw_B.pak")));
        Assert.Equal("v1", Assert.Single(f.State).Version);
        Assert.True(File.Exists(Path.Combine(f.P.Staging, MawId, "z_maw_A.pak"))); // kept for resume

        f.Fail.Clear();
        f.Http.Requests.Clear();
        await f.Svc.InstallOrUpdateAsync(v2, null, false, default);
        Assert.Equal(["z_maw_B.pak"], f.Requested); // the finished staged pak is reused
        Assert.Equal("AAA2", File.ReadAllText(f.Asset("z_maw_A.pak")));
    }

    [Fact] public async Task Install_GameRunning_Throws()
    {
        using var f = new Fixture { Running = true };
        await Assert.ThrowsAsync<GameRunningException>(() => f.Svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "a")), null, false, default));
        Assert.Empty(f.Http.Requests);
        Assert.Empty(f.State);
    }

    [Fact] public async Task Update_WhileDisabled_UpdatesDisabledFolder()
    {
        using var f = new Fixture();
        await f.Svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "bbb")), null, false, default);
        f.Svc.SetEnabled(MawId, false);

        await f.Svc.InstallOrUpdateAsync(f.Release("v2", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "BBB2")), null, false, default);

        var dis = f.P.DisabledDir(MawId);
        Assert.Equal("BBB2", File.ReadAllText(Path.Combine(dis, "z_maw_B.pak")));
        Assert.Equal("aaa", File.ReadAllText(Path.Combine(dis, "z_maw_A.pak")));
        Assert.Empty(Directory.GetFiles(f.P.Assets));
        var r = Assert.Single(f.State);
        Assert.False(r.Enabled);
        Assert.Equal("v2", r.Version);
    }

    [Fact] public void SetEnabled_False_MovesPaksToDisabledDir_AndBack()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        var r = f.Svc.Adopt(["z_a.pak"], null, "My mod");

        f.Svc.SetEnabled(r.Id, false);
        Assert.False(File.Exists(f.Asset("z_a.pak")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(f.P.DisabledDir(r.Id), "z_a.pak")));
        Assert.False(Assert.Single(f.State).Enabled);

        f.Svc.SetEnabled(r.Id, true);
        Assert.Equal("a", File.ReadAllText(f.Asset("z_a.pak")));
        Assert.True(Assert.Single(f.State).Enabled);
    }

    [Fact] public void SetEnabled_PakOwnedByOtherEnabledMod_ThrowsAndMovesNothing()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("one"));
        var two = new ModRecord { Id = "two", Name = "Two", Source = ModSource.Imported, Enabled = false, Paks = ["z_a.pak"] };
        Directory.CreateDirectory(f.P.DisabledDir("two"));
        File.WriteAllText(Path.Combine(f.P.DisabledDir("two"), "z_a.pak"), "two");
        new StateStore(f.P).Save([new ModRecord { Id = "one", Name = "One", Source = ModSource.Imported, Enabled = true, Paks = ["z_a.pak"] }, two]);

        var ex = Assert.Throws<InvalidOperationException>(() => f.Svc.SetEnabled("two", true));
        Assert.Contains("One", ex.Message);
        Assert.Equal("one", File.ReadAllText(f.Asset("z_a.pak")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(f.P.DisabledDir("two"), "z_a.pak")));
    }

    [Fact] public void SetEnabled_UnsafeIdInState_Throws()
    {
        using var f = new Fixture();
        new StateStore(f.P).Save([new ModRecord { Id = @"..\..\evil", Name = "Evil", Enabled = true, Paks = ["z_a.pak"] }]);
        f.G.WritePak("z_a.pak", B("a"));
        Assert.Throws<InvalidDataException>(() => f.Svc.SetEnabled(@"..\..\evil", false));
        Assert.True(File.Exists(f.Asset("z_a.pak")));
    }

    [Fact] public async Task Refresh_UnownedZPaks_ListedAsUnmanaged()
    {
        using var f = new Fixture();
        f.G.WritePak("z_w12_UI.pak", B("x"));
        f.G.WritePak("Objects.pak", B("vanilla"));

        var e = Assert.Single(await f.Svc.RefreshLibraryAsync([], default));

        Assert.Equal(ModStatus.Unmanaged, e.Status);
        Assert.Null(e.Record);
        Assert.Equal(["z_w12_UI.pak"], e.UnmanagedPaks);
    }

    [Fact] public async Task Refresh_ImportedModPakMissing_MarksFilesMissing()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        f.G.WritePak("z_b.pak", B("b"));
        f.Svc.Adopt(["z_a.pak", "z_b.pak"], null, "Mine");
        File.Delete(f.Asset("z_b.pak"));

        var e = Assert.Single(await f.Svc.RefreshLibraryAsync([], default));

        Assert.Equal(ModStatus.FilesMissing, e.Status);
        Assert.Equal("Mine", e.Record!.Name);
    }

    [Fact] public async Task Refresh_OfficialStatuses_UpdateAvailableThenRepair()
    {
        using var f = new Fixture();
        await f.Svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "aaa")), null, false, default);
        var v1 = f.Release("v1", ("z_maw_A.pak", "aaa"));
        Assert.Equal(ModStatus.UpToDate, Assert.Single(await f.Svc.RefreshLibraryAsync([v1], default)).Status);

        var v2 = f.Release("v2", ("z_maw_A.pak", "AAA2"));
        var e = Assert.Single(await f.Svc.RefreshLibraryAsync([v2], default));
        Assert.Equal(ModStatus.UpdateAvailable, e.Status);
        Assert.Equal(4, e.Plan!.DownloadBytes);

        File.Delete(f.Asset("z_maw_A.pak"));
        Assert.Equal(ModStatus.Repair, Assert.Single(await f.Svc.RefreshLibraryAsync([v1], default)).Status);
    }

    [Fact] public async Task Refresh_GameBuildDiffers_FlagsMismatch()
    {
        // ReadBuildId looks two folders above the game root: <lib>\steamapps\appmanifest_424030.acf
        using var f = new Fixture(root =>
        {
            var game = Path.Combine(root, "lib", "steamapps", "common", "War of Rights");
            Directory.CreateDirectory(Path.Combine(game, "Assets"));
            File.WriteAllText(Path.Combine(root, "lib", "steamapps", "appmanifest_424030.acf"), "\"AppState\"\n{\n\t\"buildid\"\t\t\"26182310\"\n}");
            return new GamePaths(game, GamePaths.DefaultModsFolder(game));
        });
        File.WriteAllText(f.Asset("z_maw_A.pak"), "aaa");
        f.Svc.Adopt(["z_maw_A.pak"], Maw("v1", [A("z_maw_A.pak", B("aaa"))]), null);

        var old = Maw("v1", [A("z_maw_A.pak", B("aaa"))], gameBuild: "1");
        var e = Assert.Single(await f.Svc.RefreshLibraryAsync([old], default));
        Assert.True(e.BuildMismatch);
        Assert.Equal(ModStatus.UpToDate, e.Status);

        var same = Maw("v1", [A("z_maw_A.pak", B("aaa"))], gameBuild: "26182310");
        Assert.False(Assert.Single(await f.Svc.RefreshLibraryAsync([same], default)).BuildMismatch);
    }

    [Fact] public void MatchOfficial_ByPrefix()
    {
        var maw = Maw("v1", [A("z_maw_UI.pak", B("1")), A("z_maw_Old.pak", B("2")), A("changelog.txt", B("c"))]);
        Assert.Same(maw, ModService.MatchOfficial(["z_maw_UI.pak", "z_maw_New.pak"], [maw]));
        Assert.Same(maw, ModService.MatchOfficial(["z_maw_UI.pak"], [maw]));
        Assert.Null(ModService.MatchOfficial(["z_w12_UI.pak"], [maw]));
        Assert.Null(ModService.MatchOfficial(["z_maw_UI.pak", "zBlackPowder.pak"], [maw]));
    }

    [Fact] public void MatchOfficial_PrefixOnly_WithoutAnyReleaseName_NoMatch()
    {
        var maw = Maw("v1", [A("z_maw_UI.pak", B("1"))]);
        Assert.Null(ModService.MatchOfficial(["z_maw_New.pak"], [maw]));
    }

    [Fact] public async Task Adopt_PrefixOnlyPak_NotRemovedByUpdate()
    {
        using var f = new Fixture();
        f.G.WritePak("z_maw_A.pak", B("aaa"));
        f.G.WritePak("z_maw_Mine.pak", B("mine"));
        var r = f.Svc.Adopt(["z_maw_A.pak", "z_maw_Mine.pak"], f.Release("v1", ("z_maw_A.pak", "aaa")), null);
        Assert.Equal(["z_maw_A.pak"], r.Paks);

        await f.Svc.InstallOrUpdateAsync(f.Release("v2", ("z_maw_A.pak", "AAA2")), null, false, default);

        Assert.Equal("mine", File.ReadAllText(f.Asset("z_maw_Mine.pak")));
        var lib = await f.Svc.RefreshLibraryAsync([], default);
        Assert.Equal(["z_maw_Mine.pak"], Assert.Single(lib, e => e.Status == ModStatus.Unmanaged).UnmanagedPaks);
    }

    [Fact] public void Uninstall_RemovesOnlyOwnedPaks()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        f.G.WritePak("zBlackPowder.pak", B("bp"));
        var r = f.Svc.Adopt(["z_a.pak"], null, "Mine");

        f.Svc.Uninstall(r.Id);

        Assert.False(File.Exists(f.Asset("z_a.pak")));
        Assert.True(File.Exists(f.Asset("zBlackPowder.pak")));
        Assert.Empty(f.State);
    }

    [Fact] public async Task LoadCatalog_ReadsModsAndSkipsUnavailable()
    {
        using var g = new TempGame();
        var raw = "https://raw.githubusercontent.com/JVS1328/";
        var dl = $"https://github.com/JVS1328/{MawId}/releases/download/v1/";
        var http = new StubHttpHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("WoR-Gone-releases")) throw new HttpRequestException("offline");
            HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
            return u switch
            {
                _ when u == raw + "WoR-ModManager/HEAD/catalog.txt" =>
                    Text("# mods\nhttps://github.com/JVS1328/WoR-Gone-releases\nhttps://github.com/JVS1328/WoR-MAW-releases\n"),
                _ when u == raw + "WoR-MAW-releases/HEAD/thumbnail.jpg" => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) },
                _ when u == raw + "WoR-MAW-releases/HEAD/description.txt" => Text("https://www.nexusmods.com/warofrights/mods/1\nBlurb"),
                _ when u == $"https://api.github.com/repos/JVS1328/{MawId}/releases/latest" => Text($$"""
                    {"tag_name":"v1.3.4","body":"","assets":[
                      {"name":"z_maw_UI.pak","size":3,"browser_download_url":"{{dl}}z_maw_UI.pak"},
                      {"name":"changelog.txt","size":11,"browser_download_url":"{{dl}}changelog.txt"},
                      {"name":"gamebuild.txt","size":9,"browser_download_url":"{{dl}}gamebuild.txt"}]}
                    """),
                _ when u == dl + "changelog.txt" => Text("Fixed stuff"),
                _ when u == dl + "gamebuild.txt" => Text("26182310\n"),
                _ => new(HttpStatusCode.NotFound),
            };
        });
        var svc = new ModService(g.Paths, new GitHubClient(http, Path.Combine(g.Root, "cache")), new Downloader(http), () => false);

        var (mods, fromCache) = await svc.LoadCatalogAsync(default);

        Assert.False(fromCache);
        var m = Assert.Single(mods);
        Assert.Equal(("MAW", MawId, "v1.3.4"), (m.Name, m.Repo.Id, m.Release!.Tag));
        Assert.Equal([1, 2], m.Thumbnail);
        Assert.Equal(("https://www.nexusmods.com/warofrights/mods/1", "Blurb"), (m.Description.NexusUrl, m.Description.Blurb));
        Assert.Equal(("Fixed stuff", "26182310"), (m.Changelog, m.GameBuild));
    }

    [Fact] public void RecoverIfNeeded_DeletesOnlyOldPartFiles()
    {
        using var f = new Fixture();
        var dir = Path.Combine(f.P.Staging, "x");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "z_a.pak.part");
        var fresh = Path.Combine(dir, "z_b.pak.part");
        File.WriteAllText(old, "o");
        File.WriteAllText(fresh, "f");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

        f.Svc.RecoverIfNeeded();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact] public async Task Refresh_UnsafeRecord_SkippedOthersListed()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        new StateStore(f.P).Save([
            new ModRecord { Id = @"..\..\evil", Name = "Evil", Enabled = true, Paks = ["z_evil.pak"] },
            new ModRecord { Id = "ok", Name = "Ok", Source = ModSource.Imported, Enabled = true, Paks = ["z_a.pak"] }]);
        var e = Assert.Single(await f.Svc.RefreshLibraryAsync([], default));
        Assert.Equal("ok", e.Record!.Id);
    }

    [Fact] public void Uninstall_TamperedRecordWithVanillaPak_Throws()
    {
        using var f = new Fixture();
        f.G.WritePak("Objects.pak", B("vanilla"));
        new StateStore(f.P).Save([new ModRecord { Id = "bad", Name = "Bad", Enabled = true, Paks = ["Objects.pak"] }]);
        Assert.Throws<InvalidDataException>(() => f.Svc.Uninstall("bad"));
        Assert.True(File.Exists(f.Asset("Objects.pak")));
    }

    [Fact] public async Task Install_UnownedSameNameFile_RefusedUnlessAllowed()
    {
        using var f = new Fixture();
        f.G.WritePak("z_maw_A.pak", B("mine"));
        var mod = f.Release("v1", ("z_maw_A.pak", "aaa"), ("z_maw_B.pak", "bbb"));

        var ex = await Assert.ThrowsAsync<UnmanagedFilesException>(() => f.Svc.InstallOrUpdateAsync(mod, null, false, default));
        Assert.Equal(["z_maw_A.pak"], ex.Names);
        Assert.Equal("mine", File.ReadAllText(f.Asset("z_maw_A.pak")));
        Assert.Empty(f.Http.Requests);
        Assert.Empty(f.State);

        await f.Svc.InstallOrUpdateAsync(mod, null, true, default);
        Assert.Equal("aaa", File.ReadAllText(f.Asset("z_maw_A.pak")));
    }

    [Fact] public async Task Install_GameStartedDuringDownload_ThrowsBeforeCommit_KeepsStaging()
    {
        using var f = new Fixture();
        var checks = 0;
        var svc = new ModService(f.P, new GitHubClient(f.Http, Path.Combine(f.G.Root, "cache")), new Downloader(f.Http), () => ++checks > 1);

        await Assert.ThrowsAsync<GameRunningException>(() => svc.InstallOrUpdateAsync(f.Release("v1", ("z_maw_A.pak", "aaa")), null, false, default));

        Assert.False(File.Exists(f.Asset("z_maw_A.pak")));
        Assert.True(File.Exists(Path.Combine(f.P.Staging, MawId, "z_maw_A.pak")));
        Assert.Empty(f.State);
    }

    [Fact] public async Task Refresh_UnmanagedPaks_OneEntryPerPrefixGroup()
    {
        using var f = new Fixture();
        foreach (var n in new[] { "z_maw_a.pak", "z_maw_b.pak", "zBlackPowder.pak" }) f.G.WritePak(n, B(n));

        var lib = await f.Svc.RefreshLibraryAsync([], default);

        Assert.All(lib, e => Assert.Equal(ModStatus.Unmanaged, e.Status));
        Assert.Equal([["zBlackPowder.pak"], ["z_maw_a.pak", "z_maw_b.pak"]], lib.Select(e => e.UnmanagedPaks.ToArray()).OrderBy(p => p[0], StringComparer.Ordinal));
    }

    [Fact] public void Adopt_Imported_UsesImporterSlugId()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        Assert.Equal("imported/my-mod", f.Svc.Adopt(["z_a.pak"], null, "My mod").Id);
    }

    [Fact] public void Recover_LegacyOwnerId_DropsGitHubOwnerFromStateAndFolders()
    {
        using var f = new Fixture();
        File.WriteAllText(f.P.HashCacheFile, "{}");
        new StateStore(f.P).Save([new ModRecord { Id = "JVS1328/WoR-MAW-releases", RepoId = "JVS1328/WoR-MAW-releases", Name = "MAW",
            Source = ModSource.Official, Version = "v1.3.4", Enabled = false, Paks = ["z_maw_A.pak"] }]);
        var oldDisabled = Path.Combine(f.P.ModsFolder, "Disabled", "JVS1328_WoR-MAW-releases");
        var oldStage = Path.Combine(f.P.Staging, "JVS1328_WoR-MAW-releases");
        Directory.CreateDirectory(oldDisabled); File.WriteAllBytes(Path.Combine(oldDisabled, "z_maw_A.pak"), B("a"));
        Directory.CreateDirectory(oldStage); File.WriteAllBytes(Path.Combine(oldStage, "z_maw_B.pak.part"), B("b"));

        f.Svc.RecoverIfNeeded();

        var rec = Assert.Single(new StateStore(f.P).Load());
        Assert.Equal(MawId, rec.Id);
        Assert.Equal(MawId, rec.RepoId);
        Assert.True(File.Exists(Path.Combine(f.P.DisabledDir(MawId), "z_maw_A.pak")));
        Assert.True(File.Exists(Path.Combine(f.P.Staging, MawId, "z_maw_B.pak.part")));
        Assert.False(Directory.Exists(oldDisabled));
        Assert.False(Directory.Exists(oldStage));
        Assert.DoesNotContain("JVS1328", File.ReadAllText(f.P.StateFile));
        Assert.DoesNotContain("JVS1328", File.ReadAllText(f.P.StateFile + ".bak"));
        Assert.False(File.Exists(f.P.HashCacheFile));
        f.Svc.SetEnabled(MawId, true); // still a working mod after the move
        Assert.True(File.Exists(Path.Combine(f.P.Assets, "z_maw_A.pak")));
    }

    [Fact] public void Uninstall_DisabledMod_RemovesEmptyModFolders()
    {
        using var f = new Fixture();
        f.G.WritePak("z_a.pak", B("a"));
        var r = f.Svc.Adopt(["z_a.pak"], null, "Mine");
        f.Svc.SetEnabled(r.Id, false);
        var stage = Path.Combine(f.P.Staging, "imported_mine");
        Directory.CreateDirectory(stage);

        f.Svc.Uninstall(r.Id);

        Assert.False(Directory.Exists(f.P.DisabledDir(r.Id)));
        Assert.False(Directory.Exists(stage));
    }

    [Fact] public async Task LoadCatalog_MalformedReleaseJson_SkipsOnlyThatMod()
    {
        using var g = new TempGame();
        var raw = "https://raw.githubusercontent.com/JVS1328/";
        var http = new StubHttpHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            HttpResponseMessage Text(string s) => new(HttpStatusCode.OK) { Content = new StringContent(s) };
            return u switch
            {
                _ when u == raw + "WoR-ModManager/HEAD/catalog.txt" => Text(
                    "https://github.com/JVS1328/WoR-Torn-releases\nhttps://github.com/JVS1328/WoR-NoName-releases\nhttps://github.com/JVS1328/WoR-MAW-releases\n"),
                _ when u.EndsWith("WoR-Torn-releases/releases/latest") => Text("{ torn"),
                _ when u.EndsWith("WoR-NoName-releases/releases/latest") => Text("""{"tag_name":"v1","assets":[{"size":3}]}"""),
                _ when u.EndsWith("WoR-MAW-releases/releases/latest") => Text("""{"tag_name":"v1","assets":[]}"""),
                _ => new(HttpStatusCode.NotFound),
            };
        });
        var svc = new ModService(g.Paths, new GitHubClient(http, Path.Combine(g.Root, "cache")), new Downloader(http), () => false);

        Assert.Equal(MawId, Assert.Single((await svc.LoadCatalogAsync(default)).Mods).Repo.Id);
    }
}
