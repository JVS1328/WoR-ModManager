using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WoRModManager.App.Views;
using WoRModManager.Core;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Import;
using WoRModManager.Core.Install;
using WoRModManager.Core.ReShade;
using WoRModManager.Core.State;

namespace WoRModManager.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    // The LOCALAPPDATA variable wins over the shell folder so a launch can be pointed at a scratch settings folder.
    public static readonly string DataDir = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } d ? d : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WoRModManager");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static Version AppVersion => Assembly.GetEntryAssembly()!.GetName().Version!;

    readonly GitHubClient gh;
    readonly Downloader dl;
    readonly SelfUpdater updater;
    readonly Dictionary<string, (byte[] Bytes, BitmapImage? Image)> thumbs = new(Ci);
    readonly OverlapDetector overlaps = new();
    ModService service = null!;
    ReShadeService reshade = null!;
    IReadOnlyList<OfficialMod> catalog = [];
    IReadOnlyList<LibraryEntry> library = [];
    CancellationTokenSource cts = new();
    string? installedBuild;

    public MainViewModel(GamePaths paths)
    {
        var handler = GitHubClient.CreateDefaultHandler(); // R2: one allowlisted handler guards API calls and downloads
        gh = new GitHubClient(handler, Path.Combine(DataDir, "HttpCache"));
        dl = new Downloader(handler);
        updater = new SelfUpdater(gh, dl, Environment.ProcessPath!, AppVersion);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => IsIdle);
        UpdateManagerCommand = new RelayCommand(_ => _ = UpdateManagerAsync(), _ => IsIdle && ManagerUpdate is not null);
        Paths = paths;
        Load(paths);
    }

    public GamePaths Paths { get; private set; }
    public ObservableCollection<ModCardViewModel> Official { get; } = [];
    public ObservableCollection<ModCardViewModel> Library { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand UpdateManagerCommand { get; }

    string statusText = "";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }

    string gameBuild = "";
    public string GameBuild { get => gameBuild; private set => Set(ref gameBuild, value); }

    bool isOffline;
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }

    ReleaseInfo? managerUpdate;
    public ReleaseInfo? ManagerUpdate
    {
        get => managerUpdate;
        private set { if (Set(ref managerUpdate, value)) OnPropertyChanged(nameof(ManagerUpdateText)); }
    }
    public string ManagerUpdateText => ManagerUpdate is { } r ? $"Manager {r.Tag} is available —" : "";

    bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsIdle => !IsBusy;

    public static bool IsGameRunning() => Process.GetProcessesByName("WarOfRights").Length > 0;

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    void Load(GamePaths p)
    {
        Paths = p;
        service = new ModService(p, gh, dl, IsGameRunning);
        reshade = new ReShadeService(p);
        service.RecoverIfNeeded();
        installedBuild = SteamLocator.ReadBuildId(p.GameRoot);
        GameBuild = installedBuild is { } b ? $"Game build {b}" : "Game build unknown";
    }

    public async Task StartAsync()
    {
        var check = CheckManagerUpdateAsync();
        await RefreshAsync();
        await check;
    }

    async Task CheckManagerUpdateAsync()
    {
        try { ManagerUpdate = await Task.Run(() => updater.CheckAsync(CancellationToken.None)); }
        catch (Exception) { } // best effort: a failed check must not interrupt the player
    }

    public Task RefreshAsync() => RunAsync("Checking GitHub for mods…", async ct =>
    {
        try
        {
            var (mods, fromCache) = await Task.Run(() => service.LoadCatalogAsync(ct), ct);
            catalog = mods;
            IsOffline = fromCache;
        }
        catch (GitHubUnavailableException)
        {
            IsOffline = true;
        }
    });

    // ModService is not thread-safe: one operation at a time, heavy work off the UI thread, library reloaded after.
    internal async Task<bool> RunAsync(string status, Func<CancellationToken, Task> op, bool reload = true)
    {
        if (IsBusy) return false;
        IsBusy = true;
        StatusText = status;
        cts = new CancellationTokenSource();
        try
        {
            await op(cts.Token);
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception e)
        {
            StatusText = "";
            HandleError(e);
        }
        if (reload)
        {
            try { await LoadLibraryAsync(); }
            catch (Exception e) { HandleError(e); }
        }
        IsBusy = false;
        return true;
    }

    internal void Cancel() => cts.Cancel();

    void HandleError(Exception e)
    {
        if (e is GitHubUnavailableException) IsOffline = true;
        else App.ShowError(e);
    }

    async Task LoadLibraryAsync()
    {
        var cat = catalog;
        var paths = Paths;
        (library, var found) = await Task.Run(async () =>
        {
            var lib = await service.RefreshLibraryAsync(cat, CancellationToken.None);
            return (lib, overlaps.Find([.. Records(lib).Where(r => r.Enabled)], paths));
        });

        var overlapsWith = new Dictionary<string, List<string>>(Ci);
        foreach (var o in found)
        {
            overlapsWith.TryAdd(o.ModA, []);
            overlapsWith.TryAdd(o.ModB, []);
            overlapsWith[o.ModA].Add(NameOf(o.ModB));
            overlapsWith[o.ModB].Add(NameOf(o.ModA));
        }
        string? OverlapText(LibraryEntry? e) =>
            e?.Record is { } r && overlapsWith.TryGetValue(r.Id, out var names) ? $"Overlaps with {string.Join(", ", names)}" : null;

        Sync(Official, false, catalog.Select(m => (m.Repo.Id, (OfficialMod?)m,
            library.FirstOrDefault(e => e.Record is { Source: ModSource.Official } r && Ci.Equals(r.RepoId, m.Repo.Id)))), OverlapText);
        Sync(Library, true, library.Select(e => (e.Record?.Id ?? "unmanaged", e.Official, (LibraryEntry?)e)), OverlapText);
        CommandManager.InvalidateRequerySuggested();
    }

    static IEnumerable<ModRecord> Records(IEnumerable<LibraryEntry> lib) => lib.Select(e => e.Record).OfType<ModRecord>();

    string NameOf(string modId) => Records(library).FirstOrDefault(r => Ci.Equals(r.Id, modId))?.Name ?? modId;

    // Updates cards in place so focus and scroll position survive a refresh.
    void Sync(ObservableCollection<ModCardViewModel> cards, bool inLibrary, IEnumerable<(string Key, OfficialMod? Mod, LibraryEntry? Entry)> items,
        Func<LibraryEntry?, string?> overlap)
    {
        var old = cards.ToDictionary(c => c.Key, Ci);
        var i = 0;
        foreach (var (key, mod, entry) in items)
        {
            if (!old.TryGetValue(key, out var card)) card = new ModCardViewModel(this, key, inLibrary);
            var match = entry is { Status: ModStatus.Unmanaged } ? ModService.MatchOfficial(entry.UnmanagedPaks, catalog) : null;
            card.Update(mod, entry, Thumb(mod ?? match), installedBuild, overlap(entry), match);
            var at = cards.IndexOf(card);
            if (at < 0) cards.Insert(i, card);
            else if (at != i) cards.Move(at, i);
            i++;
        }
        while (cards.Count > i) cards.RemoveAt(i);
    }

    BitmapImage? Thumb(OfficialMod? mod)
    {
        if (mod?.Thumbnail is not { } bytes) return null;
        if (!thumbs.TryGetValue(mod.Repo.Id, out var t) || !t.Bytes.AsSpan().SequenceEqual(bytes))
            thumbs[mod.Repo.Id] = t = (bytes, Decode(bytes));
        return t.Image;
    }

    static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 600; // caps memory for an oversized image from GitHub
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null; // a broken thumbnail just shows the placeholder
        }
    }

    internal Task InstallAsync(ModCardViewModel card) => RunAsync($"Installing {card.Name}…", async ct =>
    {
        card.IsDownloading = true;
        try
        {
            var progress = new Progress<DownloadProgress>(card.Report);
            await Task.Run(() => service.InstallOrUpdateAsync(card.Official!, progress, ct), ct);
        }
        finally
        {
            card.IsDownloading = false;
        }
    });

    internal async Task SetEnabledAsync(ModCardViewModel card, bool on)
    {
        await Task.Yield(); // leave the binding setter before any dialog shows
        var r = card.Record!;
        var ran = await RunAsync(on ? $"Enabling {r.Name}…" : $"Disabling {r.Name}…", async ct =>
        {
            if (IsGameRunning()) throw new GameRunningException();
            if (on)
            {
                var enabled = Records(library).Where(m => m.Enabled).ToList();
                var found = await Task.Run(() => overlaps.FindFor(r, enabled, Paths), ct);
                if (found.Count > 0 && !PromptWindow.Ask(string.Join("\n\n", found.Select(o => Overlap.Message(r.Name, NameOf(o.ModB), o))), "Enable anyway"))
                    return;
            }
            await Task.Run(() => service.SetEnabled(r.Id, on), ct);
        });
        if (!ran) card.RefreshEnabled();
    }

    internal Task AdoptAsync(ModCardViewModel card, OfficialMod? official) =>
        RunAsync("Taking over the files…", ct => Task.Run(() => service.Adopt(card.Entry!.UnmanagedPaks, official, null), ct));

    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        foreach (var f in files)
            if (!await RunAsync($"Adding {Path.GetFileName(f)}…", ct => AddFileAsync(f, ct)))
            {
                PromptWindow.Show("Wait for the current operation to finish, then add the file again.");
                return;
            }
    }

    async Task AddFileAsync(string path, CancellationToken ct)
    {
        if (IsGameRunning()) throw new GameRunningException();
        var mods = new StateStore(Paths).Load();
        var cat = catalog;
        var a = await Task.Run(() => ZipImporter.Analyze(path, mods, cat), ct);
        string? name = null;
        switch (a.Kind)
        {
            case ImportKind.Nothing:
                PromptWindow.Show("No mod files found in this file");
                return;
            case ImportKind.ReShadePreset:
                if (!PromptWindow.Ask($"Import ReShade preset '{a.SuggestedName}'", "Import")) return;
                if (IsGameRunning()) throw new GameRunningException();
                await Task.Run(() => reshade.Import(path, a.SuggestedName), ct);
                return;
            case ImportKind.NewMod:
                name = PromptWindow.AskText($"Add new mod '{a.SuggestedName}'", a.SuggestedName, "Add");
                if (name is null) return;
                break;
            case ImportKind.UpdateOf:
                if (!PromptWindow.Ask($"Update {mods.First(m => Ci.Equals(m.Id, a.TargetModId)).Name} ({a.PakNames.Count} files)", "Update")) return;
                break;
            case ImportKind.AdoptOfficial:
                if (!PromptWindow.Ask($"Add as official {a.Official!.Name}", "Add")) return;
                break;
        }
        var overwrite = a.Conflicts.Count > 0;
        if (overwrite && !PromptWindow.Ask(
                $"These files already belong to another mod and will be overwritten:\n\n{string.Join("\n", a.Conflicts)}", "Overwrite"))
            return;
        if (IsGameRunning()) throw new GameRunningException(); // ZipImporter doesn't check, and the game may have started meanwhile
        await Task.Run(() => ZipImporter.Apply(a, path, Paths, mods, name, overwrite), ct);
    }

    internal Task UninstallAsync(ModCardViewModel card)
    {
        var r = card.Record!;
        if (IsBusy || !PromptWindow.Ask($"Delete {r.Paks.Count} files of {r.Name} from the game?", "Delete")) return Task.CompletedTask;
        return RunAsync($"Uninstalling {r.Name}…", ct => Task.Run(() => service.Uninstall(r.Id), ct));
    }

    internal void ShowDetails(ModCardViewModel card) =>
        new ModDetailsWindow { DataContext = card, Owner = Application.Current.MainWindow }.ShowDialog();

    Task UpdateManagerAsync() => RunAsync("Downloading the manager update…", async ct =>
    {
        var progress = new Progress<DownloadProgress>(p =>
            StatusText = $"Downloading the manager update… {(p.BytesTotal > 0 ? 100 * p.BytesDone / p.BytesTotal : 0)}%");
        await Task.Run(() => updater.ApplyAsync(ManagerUpdate!, progress, ct), ct);
        Application.Current.Shutdown();
    }, reload: false);

    internal Task<bool> ChangeGameRootAsync(string root) => RunAsync("Loading the game folder…", async ct =>
    {
        // A Mods folder left at the old game's default follows the game; a custom one stays.
        var mods = SamePath(Paths.ModsFolder, GamePaths.DefaultModsFolder(Paths.GameRoot)) ? GamePaths.DefaultModsFolder(root) : Paths.ModsFolder;
        await Task.Run(() => Directory.CreateDirectory(mods), ct);
        SettingsStore.Save(SettingsFile, new AppSettings(root, mods));
        Load(new GamePaths(root, mods));
    });

    internal static bool SamePath(string a, string b) =>
        Ci.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
}
