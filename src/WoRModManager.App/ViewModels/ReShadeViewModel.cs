using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using WoRModManager.App.Views;
using WoRModManager.Core;
using WoRModManager.Core.ReShade;

namespace WoRModManager.App.ViewModels;

public sealed record PresetItem(ReShadePreset Preset)
{
    public string Name => Preset.Name;
    public string MissingText => $"Missing shaders: {string.Join(", ", Preset.MissingEffects)}";
    public bool HasMissing => Preset.MissingEffects.Count > 0;
}

public sealed class ReShadeViewModel : ObservableObject
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    readonly MainViewModel owner;
    ReShadeService? service;
    bool loading;

    public ReShadeViewModel(MainViewModel owner)
    {
        this.owner = owner;
        ImportCommand = new RelayCommand(_ => Import(), _ => owner.CanTouchGame && IsInstalled);
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync((PresetItem)p!), _ => owner.CanTouchGame);
        OpenSiteCommand = new RelayCommand(_ => MainViewModel.ShellOpen("https://reshade.me"));
    }

    public ICommand ImportCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenSiteCommand { get; }

    public bool IsInstalled { get; private set; }
    public bool IsNotInstalled => !IsInstalled;
    public ObservableCollection<PresetItem> Presets { get; } = [];
    public bool HasPresets => Presets.Count > 0;
    public string ActivePresetText { get; private set; } = "";

    bool isOn;
    public bool IsOn
    {
        get => isOn;
        set { if (value != isOn) _ = ChangeAsync(value ? "Turning ReShade on…" : "Turning ReShade off…", () => service!.SetEnabled(value)); }
    }

    PresetItem? selected;
    public PresetItem? SelectedPreset
    {
        get => selected;
        set
        {
            if (loading || value is null || value == selected) return;
            _ = ChangeAsync($"Switching to {value.Name}…", () => service!.SetActivePreset(value.Preset.IniPath));
        }
    }

    internal void Reload(ReShadeService s)
    {
        service = s;
        loading = true;
        try
        {
            var status = s.Detect();
            IsInstalled = status.Installed;
            isOn = status.Enabled;
            Presets.Clear();
            if (status.Installed)
                foreach (var p in s.ListPresets()) Presets.Add(new PresetItem(p));
            var active = status.Installed ? s.ActivePreset() : null;
            selected = active is null ? null : Presets.FirstOrDefault(p => Ci.Equals(Path.GetFullPath(p.Preset.IniPath), active));
            ActivePresetText = active is null ? "No preset is active."
                : selected is null ? $"Active preset: {Path.GetFileName(active)} (set up outside the manager)" : "";
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            loading = false;
        }
    }

    // ReShadeService doesn't check for the game, so every change does it here.
    async Task ChangeAsync(string status, Action change)
    {
        await Task.Yield(); // leave the binding setter before any dialog shows
        var ran = await owner.RunAsync(status, ct =>
        {
            if (MainViewModel.IsGameRunning()) throw new GameRunningException();
            return Task.Run(change, ct);
        });
        if (!ran) OnPropertyChanged(string.Empty);
    }

    void Import()
    {
        var d = new OpenFileDialog { Filter = "ReShade preset (*.zip)|*.zip" };
        if (d.ShowDialog() == true) _ = owner.AddFilesAsync([d.FileName]);
    }

    async Task DeleteAsync(PresetItem item)
    {
        // a preset is its top folder under wormm-reshade (one zip may hold several .ini files)
        var folder = Path.GetRelativePath(owner.Paths.ReShadeDir, item.Preset.IniPath).Split(Path.DirectorySeparatorChar)[0];
        if (!PromptWindow.Ask($"Delete the ReShade preset '{folder}' and its shaders?", "Delete")) return;
        await ChangeAsync($"Deleting {folder}…", () => service!.DeletePreset(folder));
    }
}
