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
using WoRModManager.Core.Install;
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
    ModService service = null!;
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
        library = await Task.Run(() => service.RefreshLibraryAsync(cat, CancellationToken.None));
        Sync(Official, false, catalog.Select(m => (m.Repo.Id, (OfficialMod?)m,
            library.FirstOrDefault(e => e.Record is { Source: ModSource.Official } r && Ci.Equals(r.RepoId, m.Repo.Id)))));
        CommandManager.InvalidateRequerySuggested();
    }

    // Updates cards in place so focus and scroll position survive a refresh.
    void Sync(ObservableCollection<ModCardViewModel> cards, bool inLibrary, IEnumerable<(string Key, OfficialMod? Mod, LibraryEntry? Entry)> items,
        Func<LibraryEntry?, string?>? overlap = null)
    {
        var old = cards.ToDictionary(c => c.Key, Ci);
        var i = 0;
        foreach (var (key, mod, entry) in items)
        {
            if (!old.TryGetValue(key, out var card)) card = new ModCardViewModel(this, key, inLibrary);
            card.Update(mod, entry, Thumb(mod), installedBuild, overlap?.Invoke(entry));
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
            await Task.Run(() => service.SetEnabled(r.Id, on), ct);
        });
        if (!ran) card.RefreshEnabled();
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
