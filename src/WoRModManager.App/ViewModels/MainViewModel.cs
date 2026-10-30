using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WoRModManager.App.Views;
using WoRModManager.Core;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Import;
using WoRModManager.Core.Install;
using WoRModManager.Core.Nexus;
using WoRModManager.Core.ReShade;
using WoRModManager.Core.State;
using WoRModManager.Core.Uniforms;

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
    readonly NexusClient nexus;
    IReadOnlyDictionary<int, NexusModInfo> nexusInfo = new Dictionary<int, NexusModInfo>();
    readonly Dictionary<string, BitmapImage?> nexusPictures = new(Ci); // by picture URL, fetched once per run
    readonly Dictionary<string, (byte[] Bytes, BitmapImage? Image)> thumbs = new(Ci);
    readonly OverlapDetector overlaps = new();
    ModService service = null!;
    ReShadeService reshade = null!;
    IReadOnlyList<OfficialMod> catalog = [];
    IReadOnlyList<LibraryEntry> library = [];
    string? installedBuild;
    bool recoverPending;

    public MainViewModel(GamePaths paths)
    {
        var handler = GitHubClient.CreateDefaultHandler(); // one allowlisted handler guards API calls and downloads
        gh = new GitHubClient(handler, Path.Combine(DataDir, "HttpCache"));
        dl = new Downloader(handler);
        nexus = new NexusClient(handler);
        updater = new SelfUpdater(gh, dl, Environment.ProcessPath!, AppVersion);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => IsIdle);
        UpdateManagerCommand = new RelayCommand(_ => _ = UpdateManagerAsync(), _ => IsIdle && ManagerUpdate is not null);
        PlayCommand = new RelayCommand(_ => Play(), _ => !GameRunning && !IsBusy && !HasModWork);
        ReShade = new ReShadeViewModel(this);
        Profiles = new ProfilesViewModel(this);
        GameExited += Profiles.Sync;
        Paths = paths;
        gameRunning = IsGameRunning();
        Load(paths);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        poll.Tick += (_, _) => PollGame();
        poll.Start();
    }

    public GamePaths Paths { get; private set; }
    public ReShadeViewModel ReShade { get; }
    public ProfilesViewModel Profiles { get; }
    public ObservableCollection<ModCardViewModel> Official { get; } = [];
    public ObservableCollection<ModCardViewModel> Library { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand UpdateManagerCommand { get; }
    public ICommand PlayCommand { get; }

    string statusText = "";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    string exclusiveStatus = "";

    // Mod operations run side by side, one per key (a mod's card key, or a file being added). Both tabs' cards for a mod show its operation.
    sealed class ModOp(string text, bool downloading)
    {
        public string Text = text;
        public readonly bool Downloading = downloading;
        public DownloadProgress? Last;
        public readonly CancellationTokenSource Cts = new();
        public string Line => Downloading && Last is { BytesTotal: > 0 } p ? $"{Text} {100 * p.BytesDone / p.BytesTotal}%" : Text;
    }
    readonly Dictionary<string, ModOp> ops = new(Ci);
    bool reloading, reloadAgain;

    void UpdateStatus() =>
        StatusText = string.Join("   ·   ", new[] { exclusiveStatus, GameRunning ? "War of Rights is running: mod changes are locked until it closes." : "" }
            .Concat(ops.Values.Select(o => o.Line)).Where(s => s.Length > 0));

    void SetStatus(string text)
    {
        exclusiveStatus = text;
        UpdateStatus();
    }

    void SetOpText(string key, string text)
    {
        if (!ops.TryGetValue(key, out var o)) return;
        o.Text = text;
        ApplyOp(key);
        UpdateStatus();
    }

    IEnumerable<ModCardViewModel> CardsFor(string key) => Official.Concat(Library).Where(c => Ci.Equals(c.Key, key));

    void ApplyOp(string key)
    {
        ops.TryGetValue(key, out var o);
        foreach (var c in CardsFor(key)) c.SetOperation(o?.Text, o?.Downloading ?? false, o?.Last);
    }

    IProgress<DownloadProgress> ProgressFor(string key) => new Progress<DownloadProgress>(p =>
    {
        if (!ops.TryGetValue(key, out var o)) return;
        o.Last = p;
        foreach (var c in CardsFor(key)) c.Report(p);
        UpdateStatus();
    });

    public bool IsWorking => IsBusy || ops.Count > 0;
    public bool HasModWork => ops.Count > 0;

    // Runs one mod operation without blocking the others. False if that mod is already busy or settings are being changed.
    internal async Task<bool> RunModAsync(string key, string text, Func<CancellationToken, Task> op, bool downloading = false)
    {
        if (IsLocked || ops.ContainsKey(key)) return false;
        var o = ops[key] = new ModOp(text, downloading);
        OpsChanged(key);
        try { await op(o.Cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception e) { HandleError(e); }
        finally
        {
            ops.Remove(key);
            OpsChanged(key);
        }
        await ReloadLibraryAsync();
        return true;
    }

    void OpsChanged(string key)
    {
        SyncCards();
        ApplyOp(key);
        UpdateStatus();
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(HasModWork));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void Cancel(string key)
    {
        if (ops.TryGetValue(key, out var o)) o.Cts.Cancel();
    }

    internal bool IsOperating(string key) => ops.ContainsKey(key);

    // Many operations finish at different times; their library reloads collapse into one running reload plus at most one more.
    async Task ReloadLibraryAsync()
    {
        if (reloading)
        {
            reloadAgain = true;
            return;
        }
        reloading = true;
        try
        {
            do
            {
                reloadAgain = false;
                await LoadLibraryAsync();
            } while (reloadAgain);
        }
        catch (Exception e) { HandleError(e); }
        finally { reloading = false; }
    }

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
    // An app-wide operation (refresh, ReShade, settings, self-update) is running. Mod operations can still run alongside, except while locked.
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(CanTouchGame));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsIdle => !IsBusy;

    bool isLocked;
    // Changing the game or Mods folder, updating the manager itself, or the game running: nothing else may touch the game's files meanwhile.
    public bool IsLocked
    {
        get => isLocked || GameRunning;
        private set { if (Set(ref isLocked, value)) LockChanged(); }
    }
    public bool CanAddMods => !IsLocked;
    // App-wide changes to the game (ReShade, uniform profiles) also wait for other app-wide work.
    public bool CanTouchGame => IsIdle && !IsLocked;

    void LockChanged()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanAddMods));
        OnPropertyChanged(nameof(CanTouchGame));
        foreach (var c in Official.Concat(Library)) c.RefreshCanAct();
        CommandManager.InvalidateRequerySuggested();
    }

    bool gameRunning;
    // Polled, so a game started from Steam locks the manager too.
    public bool GameRunning
    {
        get => gameRunning;
        private set
        {
            if (!Set(ref gameRunning, value)) return;
            OnPropertyChanged(nameof(PlayText));
            LockChanged();
            UpdateStatus();
            if (!value) GameExited?.Invoke();
        }
    }
    public event Action? GameExited;
    public string PlayText => GameRunning ? "Running" : "Play";

    DateTime launchGraceUntil;

    void Play()
    {
        ShellOpen($"steam://rungameid/{SteamLocator.AppId}");
        launchGraceUntil = DateTime.UtcNow.AddMinutes(1); // Steam takes a while to start the game; stay locked until it shows up
        GameRunning = true;
    }

    void PollGame()
    {
        var running = IsGameRunning();
        if (running) launchGraceUntil = default;
        GameRunning = running || DateTime.UtcNow < launchGraceUntil;
    }

    public static bool IsGameRunning() => Process.GetProcessesByName("WarOfRights").Length > 0;

    public static void ShellOpen(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    void Load(GamePaths p)
    {
        Paths = p;
        service = new ModService(p, gh, dl, IsGameRunning);
        reshade = new ReShadeService(p);
        Profiles.Reload(new UniformProfiles(Path.Combine(DataDir, "UniformProfiles"), p.CustomizationsFile, IsGameRunning));
        recoverPending = true; // done by the next library load, off the UI thread while busy
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
        await RefreshNexusAsync(ct);
    });

    // Best effort: if Nexus can't be reached the cards just keep the last known versions.
    async Task FetchNexusPicturesAsync(CancellationToken ct)
    {
        foreach (var url in nexusInfo.Values.Select(i => i.PictureUrl).OfType<string>().Where(u => !nexusPictures.ContainsKey(u)).ToList())
            nexusPictures[url] = await nexus.GetImageAsync(url, ct) is { } bytes ? Decode(bytes) : null;
    }

    async Task RefreshNexusAsync(CancellationToken ct)
    {
        try
        {
            var ids = new StateStore(Paths).Load().Select(r => r.NexusModId).OfType<int>().ToList();
            nexusInfo = await nexus.GetModsAsync(ids, ct);
            await FetchNexusPicturesAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException
                                   || (e is TaskCanceledException && !ct.IsCancellationRequested)) { }
    }

    // App-wide operations, one at a time. lockMods: nothing may touch the mods meanwhile, so it waits for mod operations to finish first.
    internal async Task<bool> RunAsync(string status, Func<CancellationToken, Task> op, bool reload = true, bool lockMods = false)
    {
        if (IsBusy) return false;
        if (lockMods && ops.Count > 0)
        {
            PromptWindow.Show("Wait for the downloads and other mod changes to finish, then try again.");
            return false;
        }
        IsBusy = true;
        IsLocked = lockMods;
        SetStatus(status);
        try
        {
            await op(CancellationToken.None);
            SetStatus("");
        }
        catch (OperationCanceledException)
        {
            SetStatus("");
        }
        catch (Exception e)
        {
            SetStatus("");
            HandleError(e);
        }
        IsLocked = false;
        IsBusy = false;
        if (reload) await ReloadLibraryAsync();
        return true;
    }

    void HandleError(Exception e)
    {
        if (e is GitHubUnavailableException) IsOffline = true;
        else App.ShowError(e);
    }

    async Task LoadLibraryAsync()
    {
        var cat = catalog;
        var paths = Paths;
        var svc = service;
        IReadOnlyList<ModRecord> adopted = [];
        (library, var found) = await Task.Run(async () =>
        {
            if (recoverPending)
            {
                svc.RecoverIfNeeded(); // an interrupted install is undone before anything reads or writes the game
                recoverPending = false;
            }
            adopted = svc.AdoptExistingInstalls(cat);
            var lib = await svc.RefreshLibraryAsync(cat, CancellationToken.None);
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
        overlapText = e =>
            e?.Record is { } r && overlapsWith.TryGetValue(r.Id, out var names) ? $"Overlaps with {string.Join(", ", names)}" : null;

        SyncCards();
        ReShade.Reload(reshade);
        CommandManager.InvalidateRequerySuggested();
        if (adopted.Count > 0) ShowAdopted(adopted);
    }

    void ShowAdopted(IReadOnlyList<ModRecord> adopted)
    {
        var lines = adopted.Select(r => library.FirstOrDefault(e => e.Record?.Id == r.Id) is { } e
            ? $"{r.Name}: " + (e.Status == ModStatus.UpToDate ? "up to date" : $"{e.Plan?.ToDownload.Count ?? 0} file(s) out of date, update it from Red's Corner")
            : r.Name);
        PromptWindow.Show("Found mods you installed before the manager. They're managed here now and kept up to date:\n\n" + string.Join("\n", lines));
    }

    Func<LibraryEntry?, string?> overlapText = _ => null;

    // Rebuilds both tabs' cards from what's already loaded (no disk or network), so it can run whenever an operation starts or ends.
    void SyncCards()
    {
        LibraryEntry? EntryFor(OfficialMod m) => library.FirstOrDefault(e => e.Record is { Source: ModSource.Official } r && Ci.Equals(r.RepoId, m.Repo.Id));
        Sync(Official, false, catalog.Select(m => (m.Repo.Id, (OfficialMod?)m, EntryFor(m))), overlapText);
        // a Red's Corner mod being installed for the first time is in My Library at once, with its progress
        var installing = catalog.Where(m => ops.ContainsKey(m.Repo.Id) && EntryFor(m) is null).Select(m => (m.Repo.Id, (OfficialMod?)m, (LibraryEntry?)null));
        Sync(Library, true, library.Select(e => (e.Record?.Id ?? "unmanaged:" + e.UnmanagedPaks[0], e.Official, (LibraryEntry?)e)).Concat(installing), overlapText);
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
            var leftover = entry is { Status: ModStatus.Unmanaged } && match is null ? ModService.LeftoverOf(entry.UnmanagedPaks, catalog, Records(library)) : null;
            card.Update(mod, entry, Thumb(mod ?? match ?? leftover), installedBuild, overlap(entry), match, leftover);
            var info = entry?.Record?.NexusModId is int nid && nexusInfo.TryGetValue(nid, out var ni) ? ni : null;
            card.SetNexus(info, info?.PictureUrl is { } pic && nexusPictures.TryGetValue(pic, out var img) ? img : null);
            ops.TryGetValue(key, out var op);
            card.SetOperation(op?.Text, op?.Downloading ?? false, op?.Last);
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

    internal Task InstallAsync(ModCardViewModel card)
    {
        if (card.Entry?.Plan?.ToRemove is { Count: > 0 } removed
            && !PromptWindow.Ask($"This update deletes these files from the game:\n\n{string.Join("\n", removed)}", "Update"))
            return Task.CompletedTask;
        var fresh = card.Record is null;
        var key = card.Key;
        return RunModAsync(key, $"{(fresh ? "Installing" : "Updating")} {card.Name}…", ct => InstallCoreAsync(card, key, fresh, ct), downloading: true);
    }

    async Task InstallCoreAsync(ModCardViewModel card, string key, bool fresh, CancellationToken ct)
    {
        var mod = card.Official!;
        var progress = ProgressFor(key);
        Task Install(bool overwrite) => Task.Run(() => service.InstallOrUpdateAsync(mod, progress, overwrite, ct), ct);
        try { await Install(false); }
        catch (UnmanagedFilesException e)
        {
            if (!AskOverwrite(e.Names)) return;
            await Install(true);
        }
        // A fresh install is enabled, so it gets the same overlap warning as enabling; the paks exist only now.
        if (fresh && new StateStore(Paths).Load().FirstOrDefault(m => m.Source == ModSource.Official && Ci.Equals(m.RepoId, mod.Repo.Id)) is { } r)
        {
            var enabled = Records(library).Where(m => m.Enabled).ToList();
            var found = await Task.Run(() => overlaps.FindFor(r, enabled, Paths), ct);
            if (found.Count > 0 && !PromptWindow.Ask(OverlapPrompt(r, found), "Keep enabled", "Disable"))
                await Task.Run(() => service.SetEnabled(r.Id, false), ct);
        }
    }

    string OverlapPrompt(ModRecord r, IEnumerable<Overlap> found) => string.Join("\n\n", found.Select(o => Overlap.Message(r.Name, NameOf(o.ModB), o)));

    internal async Task SetEnabledAsync(ModCardViewModel card, bool on)
    {
        await Task.Yield(); // leave the binding setter before any dialog shows
        var r = card.Record!;
        var ran = await RunModAsync(card.Key, on ? $"Enabling {r.Name}…" : $"Disabling {r.Name}…", async ct =>
        {
            if (IsGameRunning()) throw new GameRunningException();
            if (on)
            {
                var enabled = Records(library).Where(m => m.Enabled).ToList();
                var found = await Task.Run(() => overlaps.FindFor(r, enabled, Paths), ct);
                if (found.Count > 0 && !PromptWindow.Ask(OverlapPrompt(r, found), "Enable anyway"))
                    return;
            }
            await Task.Run(() => service.SetEnabled(r.Id, on), ct);
        });
        if (!ran) card.RefreshEnabled();
    }

    internal Task AdoptAsync(ModCardViewModel card, OfficialMod? official) =>
        RunModAsync(card.Key, "Taking over the files…", ct => Task.Run(() => service.Adopt(card.Entry!.UnmanagedPaks, official, null), ct));

    public async Task AddFilesAsync(IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            var key = "add:" + Path.GetFullPath(f);
            if (!await RunModAsync(key, $"Adding {Path.GetFileName(f)}…", ct => AddFileAsync(f, key, ct)))
                PromptWindow.Show(GameRunning ? "Close War of Rights first."
                    : IsLocked ? "Wait for the settings change to finish, then add the file again." : $"{Path.GetFileName(f)} is already being added.");
        }
    }

    async Task AddFileAsync(string path, string key, CancellationToken ct)
    {
        if (IsGameRunning()) throw new GameRunningException();
        var mods = new StateStore(Paths).Load();
        var cat = catalog;
        var a = await Task.Run(() => ZipImporter.Analyze(path, mods, cat, Paths), ct);
        string? name = null;
        (int ModId, string Version)? hit = null;
        if (a.Kind is ImportKind.NewMod or ImportKind.UpdateOf)
        {
            SetOpText(key, $"Checking {Path.GetFileName(path)} with Nexus…");
            hit = await IdentifyOnNexusAsync(path, ct);
            // a download of a mod already in My Library (linked to Nexus) replaces it, whatever its files are called
            if (hit is { } h && mods.FirstOrDefault(m => m.NexusModId == h.ModId) is { } linked)
            {
                if (IsOperating(linked.Id)) throw new InvalidOperationException($"{linked.Name} is busy; try again when it's done.");
                await ReplaceModAsync(path, linked, h.Version, null, key, ct);
                return;
            }
            if (hit is { } found && await TryNexusInfoAsync(found.ModId, ct) is { } info)
                a = a with { SuggestedName = info.Name };
        }
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
                if (!PromptWindow.Ask($"Add as {a.Official!.Name} (Red's Corner)", "Add")) return;
                break;
        }
        var overwrite = a.Conflicts.Count > 0;
        if (overwrite && !AskOverwrite(a.Conflicts)) return;
        SetOpText(key, $"Adding {Path.GetFileName(path)}…");
        await Task.Run(() =>
        {
            var rec = service.Import(a, path, name, overwrite);
            if (hit is { } h && rec.Source == ModSource.Imported && rec.NexusModId is null)
                service.LinkNexus(rec.Id, h.ModId, h.Version, nexusInfo.TryGetValue(h.ModId, out var ni) ? ni.Name : null);
            else MarkNexusCurrent(rec); // an update for a Nexus-linked mod is assumed to be the version Nexus has now
        }, ct);
    }

    void MarkNexusCurrent(ModRecord rec)
    {
        if (rec.NexusModId is int id && nexusInfo.TryGetValue(id, out var info) && info.Version.Length > 0)
            service.LinkNexus(rec.Id, id, info.Version);
    }

    internal async Task LinkNexusAsync(ModCardViewModel card)
    {
        var r = card.Record!;
        var url = PromptWindow.AskText($"Paste the Nexus page link for {r.Name}. The manager will tell you when a newer version is posted there.",
            "https://www.nexusmods.com/warofrights/mods/", "Link");
        if (url is null) return;
        if (!NexusClient.TryParseModUrl(url, out var id))
        {
            PromptWindow.Show("That isn't a War of Rights mod page on nexusmods.com. It should look like https://www.nexusmods.com/warofrights/mods/3");
            return;
        }
        await RunModAsync(card.Key, "Checking Nexus…", async ct =>
        {
            NexusModInfo? info = null;
            try { (await nexus.GetModsAsync([id], ct)).TryGetValue(id, out info); }
            catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { }
            if (info is null)
            {
                PromptWindow.Show("Couldn't find that mod on Nexus right now. Check the link, or try again later.");
                return;
            }
            nexusInfo = new Dictionary<int, NexusModInfo>(nexusInfo) { [id] = info };
            await FetchNexusPicturesAsync(ct);
            // The files you have are taken to be the version Nexus has now; later versions show as updates.
            await Task.Run(() => service.LinkNexus(r.Id, id, info.Version, info.Name), ct);
            PromptWindow.Show($"Linked to '{info.Name}' on Nexus (version {info.Version}). This card will show when a newer version is posted.");
        });
    }

    public async Task AddFromNexusLinkAsync()
    {
        var url = PromptWindow.AskText("Paste the mod's Nexus page link. It's added to My Library; download it on Nexus, then pick the file on its card.",
            "https://www.nexusmods.com/warofrights/mods/", "Add");
        if (url is null) return;
        if (!NexusClient.TryParseModUrl(url, out var id))
        {
            PromptWindow.Show("That isn't a War of Rights mod page on nexusmods.com. It should look like https://www.nexusmods.com/warofrights/mods/3");
            return;
        }
        var openPage = false;
        await RunModAsync($"nexus:{id}", "Checking Nexus…", async ct =>
        {
            var info = await TryNexusInfoAsync(id, ct);
            if (info is null)
            {
                PromptWindow.Show("Couldn't find that mod on Nexus right now. Check the link, or try again later.");
                return;
            }
            if (new StateStore(Paths).Load().FirstOrDefault(m => m.NexusModId == id) is { } existing)
            {
                PromptWindow.Show($"{existing.Name} is already in My Library.");
                return;
            }
            await FetchNexusPicturesAsync(ct);
            await Task.Run(() => service.AddFromNexus(id, info.Name), ct);
            openPage = PromptWindow.Ask($"Added '{info.Name}' to My Library. Download it on Nexus now? When it's downloaded, press 'Pick downloaded file…' on its card, or drop the file on this window.", "Open Nexus");
        });
        if (openPage) ShellOpen(NexusClient.FilesUrl(id));
    }

    async Task<NexusModInfo?> TryNexusInfoAsync(int id, CancellationToken ct)
    {
        try
        {
            if ((await nexus.GetModsAsync([id], ct)).TryGetValue(id, out var info))
            {
                nexusInfo = new Dictionary<int, NexusModInfo>(nexusInfo) { [id] = info };
                return info;
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { }
        return null;
    }

    // Asks Nexus which mod and version a downloaded file is, by its MD5. Null if Nexus doesn't know it or can't be reached.
    async Task<(int ModId, string Version)?> IdentifyOnNexusAsync(string path, CancellationToken ct)
    {
        try
        {
            var md5 = await Task.Run(() => { using var s = File.OpenRead(path); return Convert.ToHexStringLower(MD5.HashData(s)); }, ct);
            return await nexus.IdentifyAsync(md5, ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal Task UnlinkNexusAsync(ModCardViewModel card) =>
        RunModAsync(card.Key, "Unlinking…", ct => Task.Run(() => service.LinkNexus(card.Record!.Id, null, null), ct));

    internal async Task ApplyDownloadedUpdateAsync(ModCardViewModel card)
    {
        var d = new OpenFileDialog { Filter = "Mod files (*.zip;*.pak)|*.zip;*.pak", Title = $"Pick the {card.Record!.Name} update you downloaded" };
        if (d.ShowDialog() != true) return;
        var target = card.Record;
        var latest = card.NexusLatestVersion;
        var key = card.Key;
        if (!await RunModAsync(key, $"Updating {target.Name}…", ct => ReplaceModAsync(d.FileName, target, null, latest, key, ct)))
            PromptWindow.Show($"{target.Name} is busy; try again when it's done.");
    }

    // knownVersion: already identified by the file's hash; otherwise it's looked up here, falling back to Nexus's latest version.
    async Task ReplaceModAsync(string path, ModRecord target, string? knownVersion, string? fallbackVersion, string key, CancellationToken ct)
    {
        if (IsGameRunning()) throw new GameRunningException();
        var nexusVersion = knownVersion;
        if (nexusVersion is null && target.NexusModId is int linkedId)
        {
            SetOpText(key, $"Checking {Path.GetFileName(path)} with Nexus…");
            var hit = await IdentifyOnNexusAsync(path, ct);
            if (hit is { } h && h.ModId != linkedId && !PromptWindow.Ask(
                    $"Nexus says this file belongs to a different mod (#{h.ModId}), not {target.Name}. Use it for {target.Name} anyway?", "Use it"))
                return;
            nexusVersion = hit is { } same && same.ModId == linkedId ? same.Version : fallbackVersion;
        }
        var mods = new StateStore(Paths).Load();
        var cat = catalog;
        var a = await Task.Run(() => ZipImporter.Analyze(path, mods, cat, Paths, replaceModId: target.Id), ct);
        if (a.Kind != ImportKind.UpdateOf)
        {
            PromptWindow.Show("No mod files found in this file");
            return;
        }
        var fresh = target.Paks.Count == 0;
        var files = $"{a.PakNames.Count} file{(a.PakNames.Count == 1 ? "" : "s")}" + (nexusVersion is { Length: > 0 } nv ? $", version {nv}" : "");
        if (!PromptWindow.Ask(fresh ? $"Install {target.Name} from {Path.GetFileName(path)} ({files})?"
                                    : $"Replace {target.Name}'s files with {Path.GetFileName(path)} ({files})?", fresh ? "Install" : "Replace"))
            return;
        var overwrite = a.Conflicts.Count > 0;
        if (overwrite && !AskOverwrite(a.Conflicts)) return;
        SetOpText(key, $"Updating {target.Name}…");
        await Task.Run(() =>
        {
            service.Import(a, path, null, overwrite);
            if (target.NexusModId is int id && nexusVersion is { Length: > 0 }) service.LinkNexus(target.Id, id, nexusVersion);
        }, ct);
    }

    static bool AskOverwrite(IEnumerable<string> names) =>
        PromptWindow.Ask($"These files already exist and will be overwritten:\n\n{string.Join("\n", names)}", "Overwrite");

    internal Task RemoveLeftoversAsync(ModCardViewModel card)
    {
        var names = card.Entry!.UnmanagedPaks;
        if (!card.CanAct || !PromptWindow.Ask($"Delete these files left over from an older {card.Leftover!.Name} version? They're not part of the current version but still change the game.\n\n{string.Join("\n", names)}", "Delete"))
            return Task.CompletedTask;
        return RunModAsync(card.Key, "Removing leftover files…", ct => Task.Run(() => service.RemoveUnmanaged(names), ct));
    }

    internal Task UninstallAsync(ModCardViewModel card)
    {
        var r = card.Record!;
        var question = r.Paks.Count == 0 ? $"Remove {r.Name} from My Library?" : $"Delete {r.Paks.Count} files of {r.Name} from the game?";
        if (!card.CanAct || !PromptWindow.Ask(question, r.Paks.Count == 0 ? "Remove" : "Delete")) return Task.CompletedTask;
        return RunModAsync(card.Key, $"Uninstalling {r.Name}…", ct => Task.Run(() => service.Uninstall(r.Id), ct));
    }

    internal void ShowDetails(ModCardViewModel card)
    {
        _ = card.LoadHistoryAsync(gh, nexus); // fills in while the dialog is open (ShowDialog keeps pumping the dispatcher)
        new ModDetailsWindow { DataContext = card, Owner = Application.Current.MainWindow }.ShowDialog();
    }

    Task UpdateManagerAsync() => RunAsync("Downloading the manager update…", async ct =>
    {
        var progress = new Progress<DownloadProgress>(p =>
            SetStatus($"Downloading the manager update… {(p.BytesTotal > 0 ? 100 * p.BytesDone / p.BytesTotal : 0)}%"));
        await Task.Run(() => updater.ApplyAsync(ManagerUpdate!, progress, ct), ct);
        Application.Current.Shutdown();
    }, reload: false, lockMods: true);

    internal Task<bool> ChangeGameRootAsync(string root) => RunAsync("Loading the game folder…", async ct =>
    {
        // A Mods folder left at the old game's default follows the game; a custom one stays.
        var mods = SamePath(Paths.ModsFolder, GamePaths.DefaultModsFolder(Paths.GameRoot)) ? GamePaths.DefaultModsFolder(root) : Paths.ModsFolder;
        await Task.Run(() => Directory.CreateDirectory(mods), ct);
        SettingsStore.Save(SettingsFile, new AppSettings(root, mods));
        Load(new GamePaths(root, mods));
    }, lockMods: true);

    internal async Task ChangeModsFolderAsync(string folder)
    {
        if (SamePath(folder, Paths.ModsFolder)) return;
        if (IsBusy || HasModWork)
        {
            PromptWindow.Show("Wait for the downloads and other changes to finish, then try again.");
            return;
        }
        var next = new GamePaths(Paths.GameRoot, folder);
        if (!next.SameDrive && !PromptWindow.Ask(
                "This folder is on a different drive than the game, so enabling and disabling mods will copy files instead of being instant.", "Continue"))
            return;
        var from = Paths.ModsFolder;
        await RunAsync("Moving the Mods folder…", async ct =>
        {
            await Task.Run(() => ModsFolderMover.Move(from, folder), ct);
            try { SettingsStore.Save(SettingsFile, new AppSettings(next.GameRoot, folder)); }
            catch
            {
                await Task.Run(() => ModsFolderMover.Move(folder, from)); // settings still name the old folder
                throw;
            }
            Load(next);
        }, lockMods: true);
    }

    internal static bool SamePath(string a, string b) =>
        Ci.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
}
