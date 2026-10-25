using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Text.Json;
using WoRModManager.Core;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
using WoRModManager.Core.Nexus;
using WoRModManager.Core.State;

namespace WoRModManager.App.ViewModels;

public sealed class ModCardViewModel : ObservableObject
{
    static readonly Brush[] Placeholders = [Gradient("#5A4630", "#2B2219"), Gradient("#2C3D5A", "#18202D"), Gradient("#3A3A3A", "#1D1D1D")];

    readonly MainViewModel owner;
    readonly bool inLibrary;
    string? installedBuild;

    public ModCardViewModel(MainViewModel owner, string key, bool inLibrary)
    {
        this.owner = owner;
        this.inLibrary = inLibrary;
        Key = key;
        PrimaryCommand = new RelayCommand(_ => _ = owner.InstallAsync(this), _ => CanAct && CanPrimary);
        CancelCommand = new RelayCommand(_ => owner.Cancel(Key), _ => IsDownloading);
        NexusCommand = new RelayCommand(_ => MainViewModel.ShellOpen(NexusUrl!), _ => NexusUrl is not null);
        DetailsCommand = new RelayCommand(_ => owner.ShowDetails(this));
        UninstallCommand = new RelayCommand(_ => _ = owner.UninstallAsync(this), _ => CanAct && Record is not null);
        ManageOfficialCommand = new RelayCommand(_ => _ = owner.AdoptAsync(this, Match), _ => CanAct && Match is not null);
        ManageNewCommand = new RelayCommand(_ => _ = owner.AdoptAsync(this, null), _ => CanAct && IsUnmanaged);
        LinkNexusCommand = new RelayCommand(_ => _ = owner.LinkNexusAsync(this), _ => CanAct && CanLinkNexus);
        UnlinkNexusCommand = new RelayCommand(_ => _ = owner.UnlinkNexusAsync(this), _ => CanAct && IsNexusLinked);
        ApplyUpdateCommand = new RelayCommand(_ => _ = owner.ApplyDownloadedUpdateAsync(this), _ => CanAct && IsNexusLinked);
        RemoveLeftoversCommand = new RelayCommand(_ => _ = owner.RemoveLeftoversAsync(this), _ => CanAct && Leftover is not null);
        DownloadNexusCommand = new RelayCommand(_ => MainViewModel.ShellOpen(NexusClient.FilesUrl(Record!.NexusModId!.Value)), _ => IsNexusLinked);
    }

    public string Key { get; }
    public OfficialMod? Official { get; private set; }
    public LibraryEntry? Entry { get; private set; }
    public ModRecord? Record => Entry?.Record;
    public BitmapImage? Thumbnail { get; private set; }
    public string? OverlapWarning { get; private set; }

    public ICommand PrimaryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand NexusCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand ManageOfficialCommand { get; }
    public ICommand ManageNewCommand { get; }
    public ICommand LinkNexusCommand { get; }
    public ICommand UnlinkNexusCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand DownloadNexusCommand { get; }
    public ICommand RemoveLeftoversCommand { get; }

    public bool IsUnmanaged => Entry?.Status == ModStatus.Unmanaged;
    public string Name => IsUnmanaged ? "Unmanaged files" : (IsNexusLinked && nexus is { Name.Length: > 0 } n ? n.Name : Record?.Name) ?? Official?.Name ?? Key;
    public string? Version => Record is { } r ? (r.Version is { } v ? V(v) : null) : Official?.Release?.Tag;
    public Brush Placeholder => Placeholders[Key.Sum(c => c) % Placeholders.Length];

    long ReleaseBytes => Official?.Release?.Assets.Where(a => PakNames.IsModPak(a.Name)).Sum(a => a.Size) ?? 0;
    int FileCount => IsUnmanaged ? Entry!.UnmanagedPaks.Count : Record?.Paks.Count ?? 0;
    public string Meta => string.Join(" · ", new[]
    {
        Version,
        IsNexusLinked && nexus is { Author.Length: > 0 } n ? $"by {n.Author}" : null,
        ReleaseBytes > 0 ? Mb(ReleaseBytes) : FileCount > 0 ? $"{FileCount} file{(FileCount == 1 ? "" : "s")}" : null,
    }.OfType<string>());

    // This mod's running operation (install, update, enable…), shown the same on its Red's Corner and My Library cards.
    string? operation;
    public bool IsOperating => operation is not null;
    public bool CanAct => !IsOperating && !owner.IsLocked;
    internal void RefreshCanAct() => OnPropertyChanged(nameof(CanAct));

    internal void SetOperation(string? text, bool downloading, DownloadProgress? last)
    {
        operation = text;
        IsDownloading = downloading && text is not null;
        if (last is { } p && IsDownloading) Report(p);
        OnPropertyChanged(nameof(IsOperating));
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(StatusText));
    }

    public string StatusText => operation ?? NormalStatus;
    string NormalStatus => IsWaiting ? "Not downloaded yet. Get it from Nexus, then pick the downloaded file here." : Entry?.Status switch
    {
        null => Official?.Release is null ? "No release published yet" : "Not installed",
        ModStatus.UpToDate => Record?.Source == ModSource.Imported || Official is null ? "Installed" : "Up to date",
        ModStatus.UpdateAvailable => "Update available",
        ModStatus.Repair => "Some files are changed or missing",
        ModStatus.FilesMissing => "Files missing",
        ModStatus.Unmanaged when Leftover is { } lo => $"Left over from an older {lo.Name} version. They're not in the current version but still change the game.",
        ModStatus.Unmanaged => "Mod files in the game that the manager doesn't own yet. Nothing changes until you choose.",
        _ => "",
    };

    // Core never reports NotInstalled: an official mod without a library entry is "not installed".
    public string PrimaryActionText => Entry?.Status switch
    {
        null => "Install",
        ModStatus.UpdateAvailable => $"Update · {UpdatePaks} pak{(UpdatePaks == 1 ? "" : "s")} · {Mb(Entry.Plan?.DownloadBytes ?? 0)}",
        ModStatus.Repair => "Repair",
        _ => "Installed",
    };

    int UpdatePaks => Entry?.Plan?.ToDownload.Count ?? 0;
    bool CanPrimary => Official?.Release is not null && Entry?.Status is null or ModStatus.UpdateAvailable or ModStatus.Repair;
    public bool ShowPrimary => inLibrary ? CanPrimary && Entry is not null : Entry?.Status is not ModStatus.FilesMissing;
    public bool IsUpdate => Entry?.Status == ModStatus.UpdateAvailable;

    public bool ShowToggle => Record is not null && !IsWaiting;
    public bool IsEnabled
    {
        get => Record?.Enabled ?? false;
        set { if (Record is { } r && r.Enabled != value) _ = owner.SetEnabledAsync(this, value); }
    }

    public string? NexusUrl => Official?.Description.NexusUrl ?? (Record?.NexusModId is int id ? NexusClient.ModUrl(id) : null);
    public bool HasNexus => NexusUrl is not null;
    public bool NoNexus => !HasNexus;

    // Mods added from a file can be linked to their Nexus page; the manager then checks Nexus for a newer version.
    NexusModInfo? nexus;
    public bool CanLinkNexus => Record?.Source == ModSource.Imported && Record.NexusModId is null;
    public bool IsNexusLinked => Record?.Source == ModSource.Imported && Record.NexusModId is not null;
    // Added from its Nexus link and not downloaded yet: no files to manage, only "download" and "pick the file".
    public bool IsWaiting => IsNexusLinked && Record!.Paks.Count == 0;
    public bool ShowApplyUpdate => HasNexusUpdate || IsWaiting;
    public string ApplyUpdateText => IsWaiting ? "Pick downloaded file…" : "Apply downloaded update…";
    public string UninstallText => IsWaiting ? "Remove" : "Uninstall";
    public string? NexusUpdateText => IsNexusLinked && !IsWaiting && nexus is { Version.Length: > 0 } n && !string.Equals(n.Version, Record!.NexusVersion, StringComparison.OrdinalIgnoreCase)
        ? $"Update on Nexus: {V(n.Version)}" + (Record.NexusVersion is { Length: > 0 } have ? $" (you have {V(have)})" : "") : null;
    public bool HasNexusUpdate => NexusUpdateText is not null;
    internal string? NexusLatestVersion => nexus?.Version;
    static string V(string v) => v.Length > 0 && char.IsDigit(v[0]) ? "v" + v : v;

    internal void SetNexus(NexusModInfo? info, BitmapImage? picture)
    {
        nexus = info;
        if (Official is null && picture is not null) Thumbnail = picture;
        OnPropertyChanged(string.Empty);
    }

    public string? BuildWarning => Official?.GameBuild is { } g && installedBuild is { } b && g != b ? $"Made for game build {g}, you have {b}" : null;
    public bool HasBuildWarning => BuildWarning is not null;
    public bool HasOverlapWarning => OverlapWarning is not null;

    public bool HasTag => inLibrary;
    public string Tag => IsUnmanaged ? "Unmanaged" : Record?.Source == ModSource.Imported ? "Imported" : "Red's Corner";
    public bool ShowUninstall => inLibrary && Record is not null;

    // Unmanaged card
    public string FilesText => IsUnmanaged ? string.Join("\n", Entry!.UnmanagedPaks) : "";
    public OfficialMod? Match { get; private set; }
    public string ManageOfficialText => Match is { } m ? $"Manage as {m.Name}" : "";
    public bool HasManageOfficial => Match is not null;
    public bool HasManageNew => IsUnmanaged && Match is null && Leftover is null; // never compete with a matching official mod
    public OfficialMod? Leftover { get; private set; }
    public bool HasLeftover => Leftover is not null;

    // Details window
    string? nexusAbout;
    public string Blurb => Official?.Description.Blurb is { Length: > 0 } b ? b
        : nexusAbout ?? (nexus is { Summary.Length: > 0 } n ? n.Summary : "No description.");
    public bool HasGameBuild => Official is not null;
    public string Changelog => Official?.Changelog is { Length: > 0 } c ? c.Trim() : "No changelog published.";
    public string GameBuildText => Official?.GameBuild is { } g
        ? $"Made for game build {g}" + (installedBuild is { } b ? $" (you have {b})" : "")
        : "No game build listed";
    public bool CanUninstall => Record is not null;

    // Version history: every published release with its date and changelog, fetched when the details open.
    public bool HasHistorySection => Official is not null || IsNexusLinked;
    IReadOnlyList<HistoryItem> history = [];
    public IReadOnlyList<HistoryItem> History { get => history; private set => Set(ref history, value); }
    string historyStatus = "";
    public string HistoryStatus { get => historyStatus; private set { if (Set(ref historyStatus, value)) OnPropertyChanged(nameof(HasHistoryStatus)); } }
    public bool HasHistoryStatus => historyStatus.Length > 0;

    internal async Task LoadHistoryAsync(GitHubClient gh, NexusClient nexusClient)
    {
        if (Official is null)
        {
            if (IsNexusLinked) await LoadNexusDetailsAsync(nexusClient);
            return;
        }
        HistoryStatus = "Loading…";
        try
        {
            var releases = await gh.GetReleaseHistoryAsync(Official.Repo, CancellationToken.None);
            History = releases.Select(r => new HistoryItem(
                r.Published is { } d ? $"{r.Tag} · {d.LocalDateTime:MMMM d, yyyy}" : r.Tag,
                r.Notes.Length > 0 ? r.Notes : "No changelog published.")).ToList();
            HistoryStatus = History.Count == 0 ? "No releases published yet." : "";
        }
        catch (Exception e) when (e is GitHubUnavailableException or HttpRequestException or JsonException or InvalidOperationException)
        {
            HistoryStatus = "Couldn't load the version history from GitHub. Latest changelog:\n" + Changelog;
        }
    }

    async Task LoadNexusDetailsAsync(NexusClient nexusClient)
    {
        HistoryStatus = "Loading from Nexus…";
        try
        {
            var d = await nexusClient.GetDetailsAsync(Record!.NexusModId!.Value, CancellationToken.None);
            if (d.Description.Length > 0) nexusAbout = d.Description;
            OnPropertyChanged(nameof(Blurb));
            History = d.Versions.Select(v => new HistoryItem($"{V(v.Version)} · {v.Date.LocalDateTime:MMMM d, yyyy}",
                v.Changelog.Count > 0 ? string.Join("\n", v.Changelog.Select(l => "• " + l.Trim())) : "No changelog posted.")).ToList();
            HistoryStatus = History.Count == 0 ? "No files posted on Nexus yet." : "";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            HistoryStatus = "Couldn't load the description and version history from Nexus right now.";
        }
    }

    double progress;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    string progressText = "";
    public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
    bool isDownloading;
    public bool IsDownloading { get => isDownloading; set { if (Set(ref isDownloading, value)) { Progress = 0; ProgressText = ""; } } }

    internal void Update(OfficialMod? official, LibraryEntry? entry, BitmapImage? thumbnail, string? build, string? overlapWarning, OfficialMod? match,
        OfficialMod? leftover = null)
    {
        Match = match;
        Leftover = leftover;
        Official = official;
        Entry = entry;
        Thumbnail = thumbnail;
        installedBuild = build;
        OverlapWarning = overlapWarning;
        OnPropertyChanged(string.Empty); // every computed property, including IsEnabled after a refused toggle
    }

    internal void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));

    internal void Report(DownloadProgress p)
    {
        Progress = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 0;
        ProgressText = $"{p.BytesPerSecond / 1048576:0.0} MB/s";
    }

    internal static string Mb(long bytes) => $"{bytes / 1048576.0:0.#} MB";

    static Brush Gradient(string from, string to)
    {
        var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 45);
        b.Freeze();
        return b;
    }
}

public sealed record HistoryItem(string Header, string Notes);
