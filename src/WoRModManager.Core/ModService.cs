using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
using WoRModManager.Core.State;

namespace WoRModManager.Core;

public record OfficialMod(RepoRef Repo, string Name, ModDescription Description, byte[]? Thumbnail, ReleaseInfo? Release, string? Changelog, string? GameBuild);

public enum ModStatus { NotInstalled, UpToDate, UpdateAvailable, Repair, FilesMissing, Unmanaged }

public record LibraryEntry(ModRecord? Record, OfficialMod? Official, ModStatus Status, UpdatePlan? Plan, bool BuildMismatch, IReadOnlyList<string> UnmanagedPaks);

public class GameRunningException() : Exception("Close War of Rights first.");

public class UnmanagedFilesException(IReadOnlyList<string> names)
    : Exception($"These files are already in the game but no mod owns them: {string.Join(", ", names)}")
{
    public IReadOnlyList<string> Names { get; } = names;
}

public class ModService(GamePaths paths, GitHubClient gh, Downloader dl, Func<bool> isGameRunning)
{
    static readonly RepoRef CatalogRepo = new("JVS1328", "WoR-ModManager");
    const string CatalogFile = "catalog.txt";
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    readonly StateStore store = new(paths);
    readonly HashCache hashes = new(paths.HashCacheFile);

    public async Task<(IReadOnlyList<OfficialMod> Mods, bool FromCache)> LoadCatalogAsync(CancellationToken ct)
    {
        var (text, catalogCached) = await gh.GetRawTextWithCacheFlagAsync(CatalogRepo, CatalogFile, ct);
        var repos = Catalog.Parse(text ?? "");
        var mods = new OfficialMod?[repos.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, repos.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, ct) => mods[i] = await LoadModAsync(repos[i], ct));
        var list = mods.OfType<OfficialMod>().ToList();
        return (list, catalogCached || list.Any(m => m.Release?.FromCache == true));
    }

    async Task<OfficialMod?> LoadModAsync(RepoRef repo, CancellationToken ct)
    {
        try
        {
            var thumb = await gh.GetRawBytesAsync(repo, "thumbnail.png", ct) ?? await gh.GetRawBytesAsync(repo, "thumbnail.jpg", ct);
            var desc = ModDescription.Parse(await gh.GetRawTextAsync(repo, "description.txt", ct));
            var rel = await gh.GetLatestReleaseAsync(repo, ct);
            async Task<string?> AssetText(string name) =>
                rel?.Assets.FirstOrDefault(a => Ci.Equals(a.Name, name)) is { } a ? await gh.GetAssetTextAsync(a, ct) : null;
            return new OfficialMod(repo, DisplayName(repo.Repo), desc, thumb, rel, await AssetText("changelog.txt"),
                Catalog.ParseGameBuild(await AssetText("gamebuild.txt")));
        }
        catch (Exception e) when (e is GitHubUnavailableException or InvalidDataException)
        {
            return null; // one unreachable or oversized mod must not hide the rest of the catalog
        }
    }

    static string DisplayName(string repo)
    {
        var n = repo.StartsWith("WoR-", StringComparison.OrdinalIgnoreCase) ? repo[4..] : repo;
        if (n.EndsWith("-releases", StringComparison.OrdinalIgnoreCase)) n = n[..^"-releases".Length];
        return n.Replace('-', ' ');
    }

    public async Task<IReadOnlyList<LibraryEntry>> RefreshLibraryAsync(IReadOnlyList<OfficialMod> official, CancellationToken ct)
    {
        var mods = store.Load();
        var build = SteamLocator.ReadBuildId(paths.GameRoot);
        var entries = new List<LibraryEntry>();
        foreach (var r in mods.Where(IsSafe)) // a damaged record is left alone rather than hiding the whole library
        {
            var loc = Location(r);
            var off = r.Source == ModSource.Official ? official.FirstOrDefault(o => Ci.Equals(o.Repo.Id, r.RepoId)) : null;
            UpdatePlan? plan = null;
            ModStatus status;
            if (off?.Release is { } rel)
            {
                plan = await PlanAsync(r, rel, loc, ct);
                status = plan.IsEmpty ? ModStatus.UpToDate : r.Version == rel.Tag ? ModStatus.Repair : ModStatus.UpdateAvailable;
            }
            else status = r.Paks.All(p => File.Exists(Path.Combine(loc, p))) ? ModStatus.UpToDate : ModStatus.FilesMissing;
            entries.Add(new(r, off, status, plan, off?.GameBuild is { } g && build is not null && g != build, []));
        }
        hashes.Save();

        var unmanaged = Directory.Exists(paths.Assets)
            ? Directory.EnumerateFiles(paths.Assets, "z*.pak").Select(Path.GetFileName).OfType<string>()
                .Where(n => PakNames.IsModPak(n) && StateStore.OwnerOf(mods, n) is null).Order(Ci).ToList()
            : [];
        if (unmanaged.Count > 0) entries.Add(new(null, null, ModStatus.Unmanaged, null, false, unmanaged));
        return entries;
    }

    public async Task InstallOrUpdateAsync(OfficialMod mod, IProgress<DownloadProgress>? progress, bool allowOverwriteUnmanaged, CancellationToken ct)
    {
        if (isGameRunning()) throw new GameRunningException();
        var rel = mod.Release ?? throw new InvalidOperationException($"{mod.Name} has no release to install.");
        var mods = store.Load();
        var rec = mods.FirstOrDefault(m => m.Source == ModSource.Official && Ci.Equals(m.RepoId, mod.Repo.Id));
        if (rec is null)
            mods.Add(rec = new ModRecord { Id = mod.Repo.Id, Name = mod.Name, Source = ModSource.Official, RepoId = mod.Repo.Id, Enabled = true });
        var loc = Location(rec);
        var plan = await PlanAsync(rec, rel, loc, ct);
        ThrowIfConflict(mods, rec, loc, plan.ToDownload.Select(a => a.Name));
        var unowned = plan.ToDownload.Select(a => a.Name).Where(n => StateStore.OwnerOf(mods, n) is null && File.Exists(Path.Combine(loc, n))).ToList();
        if (unowned.Count > 0 && !allowOverwriteUnmanaged) throw new UnmanagedFilesException(unowned);

        var stage = Path.Combine(paths.Staging, rec.Id.Replace('/', '_'));
        var staged = new Dictionary<string, string>(Ci);
        long done = 0;
        foreach (var a in plan.ToDownload)
        {
            var dest = Path.Combine(stage, a.Name);
            // a pak finished by an earlier, failed attempt is reused; *.part files are resumed by the Downloader
            if (!(a.Sha256 is not null && File.Exists(dest) && Ci.Equals(await hashes.GetSha256Async(dest, ct), a.Sha256)))
                await dl.DownloadAsync(a, dest, progress is null ? null : new OffsetProgress(progress, done, plan.DownloadBytes), ct);
            staged[a.Name] = dest;
            done += a.Size;
        }

        if (isGameRunning()) throw new GameRunningException(); // it may have started during the download; staging is kept
        Directory.CreateDirectory(loc);
        InstallTransaction.Commit(paths, loc, staged, plan.ToRemove);
        rec.Version = rel.Tag;
        rec.Paks = rel.Assets.Select(a => a.Name).Where(PakNames.IsModPak).ToList();
        store.Save(mods);
        hashes.Save();
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
    }

    async Task<UpdatePlan> PlanAsync(ModRecord r, ReleaseInfo rel, string loc, CancellationToken ct)
    {
        var local = new Dictionary<string, string>(Ci);
        foreach (var a in rel.Assets.Where(a => PakNames.IsModPak(a.Name)))
            if (File.Exists(Path.Combine(loc, a.Name))) local[a.Name] = await hashes.GetSha256Async(Path.Combine(loc, a.Name), ct);
        return UpdatePlanner.Plan(rel.Assets, r.Paks, n => local.GetValueOrDefault(n));
    }

    public void SetEnabled(string modId, bool enabled)
    {
        if (isGameRunning()) throw new GameRunningException();
        var mods = store.Load();
        var r = Find(mods, modId);
        if (r.Enabled == enabled) return;
        var from = Location(r);
        r.Enabled = enabled;
        var to = Location(r);
        ThrowIfConflict(mods, r, to, r.Paks);

        Directory.CreateDirectory(to);
        var moved = new List<string>();
        try
        {
            foreach (var n in r.Paks.Where(n => File.Exists(Path.Combine(from, n))))
            {
                InstallTransaction.Move(Path.Combine(from, n), Path.Combine(to, n), viaTmp: true);
                moved.Add(n);
            }
        }
        catch
        {
            foreach (var n in moved) InstallTransaction.Move(Path.Combine(to, n), Path.Combine(from, n), viaTmp: true); // don't leave a mod split across folders
            throw;
        }
        store.Save(mods);
    }

    public void Uninstall(string modId)
    {
        if (isGameRunning()) throw new GameRunningException();
        var mods = store.Load();
        var r = Find(mods, modId);
        var loc = Location(r);
        foreach (var n in r.Paks) File.Delete(Path.Combine(loc, n));
        mods.Remove(r);
        store.Save(mods);
    }

    public ModRecord Adopt(IReadOnlyList<string> pakNames, OfficialMod? asOfficial, string? importedName)
    {
        if (pakNames.Count == 0 || !pakNames.All(PakNames.IsModPak)) throw new ArgumentException("Only z*.pak files can be adopted.");
        var mods = store.Load();
        if (pakNames.Select(n => StateStore.OwnerOf(mods, n)).FirstOrDefault(o => o is not null) is { } owner)
            throw new InvalidOperationException($"Already managed by {owner.Name}.");
        // An official record owns only its release's names: an update removes owned names the release dropped.
        var owned = asOfficial is null ? pakNames : pakNames.Where(n => ReleasePaks(asOfficial).Contains(n, Ci)).ToList();
        if (owned.Count == 0) throw new InvalidOperationException($"None of these files are in the latest {asOfficial!.Name} release.");
        var name = importedName ?? Path.GetFileNameWithoutExtension(pakNames[0]);
        var r = asOfficial is not null
            ? new ModRecord { Id = asOfficial.Repo.Id, Name = asOfficial.Name, Source = ModSource.Official, RepoId = asOfficial.Repo.Id }
            : new ModRecord { Id = ModRecord.ImportedId(name, mods), Name = name, Source = ModSource.Imported };
        if (mods.Any(m => Ci.Equals(m.Id, r.Id))) throw new InvalidOperationException($"{r.Name} is already installed.");
        r.Enabled = true; // unmanaged paks live in Assets
        r.Paks = [.. owned];
        Location(r);
        mods.Add(r);
        store.Save(mods);
        return r;
    }

    public static OfficialMod? MatchOfficial(IReadOnlyList<string> pakNames, IReadOnlyList<OfficialMod> official)
    {
        if (pakNames.Count == 0) return null;
        var prefixes = pakNames.Select(PakNames.Prefix).Distinct(Ci).ToList();
        foreach (var o in official)
        {
            var rel = ReleasePaks(o);
            if (!pakNames.Any(n => rel.Contains(n, Ci))) continue; // adopting must leave the official record owning something
            if (pakNames.All(n => rel.Contains(n, Ci))) return o;
            var relPrefixes = rel.Select(PakNames.Prefix).Distinct(Ci).ToList();
            if (prefixes is [{ } p] && relPrefixes is [{ } rp] && Ci.Equals(p, rp)) return o;
        }
        return null;
    }

    internal static List<string> ReleasePaks(OfficialMod o) => o.Release?.Assets.Select(a => a.Name).Where(PakNames.IsModPak).ToList() ?? [];

    public void RecoverIfNeeded()
    {
        InstallTransaction.Recover(paths);
        if (!Directory.Exists(paths.Staging)) return;
        foreach (var f in Directory.GetFiles(paths.Staging, "*.part", SearchOption.AllDirectories))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
    }

    ModRecord Find(List<ModRecord> mods, string modId) =>
        mods.FirstOrDefault(m => Ci.Equals(m.Id, modId)) ?? throw new InvalidOperationException($"No installed mod {modId}.");

    // state.json and the catalog are untrusted input: ids become folder names and pak names become file paths.
    // IsModPak, not just a safe name: a tampered record must never make Uninstall delete a vanilla pak.
    static bool IsSafe(ModRecord r) =>
        !string.IsNullOrEmpty(r.Id) && !r.Id.Contains("..") && r.Id.Replace('/', '_').IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && r.Paks.All(PakNames.IsModPak);

    string Location(ModRecord r) => IsSafe(r) ? r.Location(paths) : throw new InvalidDataException($"Unsafe mod record: {r.Id}");

    void ThrowIfConflict(List<ModRecord> mods, ModRecord self, string dir, IEnumerable<string> names)
    {
        foreach (var n in names)
            if (mods.FirstOrDefault(m => m != self && m.Paks.Contains(n, Ci) && Ci.Equals(m.Location(paths), dir)) is { } other
                && File.Exists(Path.Combine(dir, n)))
                throw new InvalidOperationException($"{n} is already used by {other.Name}.");
    }

    sealed class OffsetProgress(IProgress<DownloadProgress> inner, long offset, long total) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress p) => inner.Report(p with { BytesDone = offset + p.BytesDone, BytesTotal = total });
    }
}
