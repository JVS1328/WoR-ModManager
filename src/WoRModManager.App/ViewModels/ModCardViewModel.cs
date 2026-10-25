using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net.Http;
using System.Text.Json;
using WoRModManager.Core;
using WoRModManager.Core.GitHub;
using WoRModManager.Core.Install;
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
        PrimaryCommand = new RelayCommand(_ => _ = owner.InstallAsync(this), _ => owner.IsIdle && CanPrimary);
        CancelCommand = new RelayCommand(_ => owner.Cancel(), _ => IsDownloading);
        NexusCommand = new RelayCommand(_ => MainViewModel.ShellOpen(NexusUrl!), _ => NexusUrl is not null);
        DetailsCommand = new RelayCommand(_ => owner.ShowDetails(this));
        UninstallCommand = new RelayCommand(_ => _ = owner.UninstallAsync(this), _ => owner.IsIdle && Record is not null);
        ManageOfficialCommand = new RelayCommand(_ => _ = owner.AdoptAsync(this, Match), _ => owner.IsIdle && Match is not null);
        ManageNewCommand = new RelayCommand(_ => _ = owner.AdoptAsync(this, null), _ => owner.IsIdle && IsUnmanaged);
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

    public bool IsUnmanaged => Entry?.Status == ModStatus.Unmanaged;
    public string Name => IsUnmanaged ? "Unmanaged files" : Record?.Name ?? Official?.Name ?? Key;
    public string? Version => Record is { } r ? r.Version : Official?.Release?.Tag;
    public Brush Placeholder => Placeholders[Key.Sum(c => c) % Placeholders.Length];

    long ReleaseBytes => Official?.Release?.Assets.Where(a => PakNames.IsModPak(a.Name)).Sum(a => a.Size) ?? 0;
    int FileCount => IsUnmanaged ? Entry!.UnmanagedPaks.Count : Record?.Paks.Count ?? 0;
    public string Meta => string.Join(" · ", new[]
    {
        Version,
        ReleaseBytes > 0 ? Mb(ReleaseBytes) : FileCount > 0 ? $"{FileCount} file{(FileCount == 1 ? "" : "s")}" : null,
    }.OfType<string>());

    public string StatusText => Entry?.Status switch
    {
        null => Official?.Release is null ? "No release published yet" : "Not installed",
        ModStatus.UpToDate => Record?.Source == ModSource.Imported || Official is null ? "Installed" : "Up to date",
        ModStatus.UpdateAvailable => "Update available",
        ModStatus.Repair => "Some files are changed or missing",
        ModStatus.FilesMissing => "Files missing",
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

    public bool ShowToggle => Record is not null;
    public bool IsEnabled
    {
        get => Record?.Enabled ?? false;
        set { if (Record is { } r && r.Enabled != value) _ = owner.SetEnabledAsync(this, value); }
    }

    public string? NexusUrl => Official?.Description.NexusUrl;
    public bool HasNexus => NexusUrl is not null;

    public string? BuildWarning => Official?.GameBuild is { } g && installedBuild is { } b && g != b ? $"Made for game build {g}, you have {b}" : null;
    public bool HasBuildWarning => BuildWarning is not null;
    public bool HasOverlapWarning => OverlapWarning is not null;

    public bool HasTag => inLibrary;
    public string Tag => IsUnmanaged ? "Unmanaged" : Record?.Source == ModSource.Imported ? "Imported" : "Official";
    public bool ShowUninstall => inLibrary && Record is not null;

    // Unmanaged card
    public string FilesText => IsUnmanaged ? string.Join("\n", Entry!.UnmanagedPaks) : "";
    public OfficialMod? Match { get; private set; }
    public string ManageOfficialText => Match is { } m ? $"Manage as {m.Name}" : "";
    public bool HasManageOfficial => Match is not null;
    public bool HasManageNew => IsUnmanaged && Match is null; // never compete with a matching official mod

    // Details window
    public string Blurb => Official?.Description.Blurb is { Length: > 0 } b ? b : "No description.";
    public string Changelog => Official?.Changelog is { Length: > 0 } c ? c.Trim() : "No changelog published.";
    public string GameBuildText => Official?.GameBuild is { } g
        ? $"Made for game build {g}" + (installedBuild is { } b ? $" (you have {b})" : "")
        : "No game build listed";
    public bool CanUninstall => Record is not null;

    // Version history: every published release with its date and changelog, fetched when the details open.
    public bool HasHistorySection => Official is not null;
    IReadOnlyList<HistoryItem> history = [];
    public IReadOnlyList<HistoryItem> History { get => history; private set => Set(ref history, value); }
    string historyStatus = "";
    public string HistoryStatus { get => historyStatus; private set { if (Set(ref historyStatus, value)) OnPropertyChanged(nameof(HasHistoryStatus)); } }
    public bool HasHistoryStatus => historyStatus.Length > 0;

    internal async Task LoadHistoryAsync(GitHubClient gh)
    {
        if (Official is null) return;
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

    double progress;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    string progressText = "";
    public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
    bool isDownloading;
    public bool IsDownloading { get => isDownloading; set { if (Set(ref isDownloading, value)) { Progress = 0; ProgressText = ""; } } }

    internal void Update(OfficialMod? official, LibraryEntry? entry, BitmapImage? thumbnail, string? build, string? overlapWarning, OfficialMod? match)
    {
        Match = match;
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
