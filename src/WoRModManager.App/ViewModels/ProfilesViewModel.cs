using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WoRModManager.App.Views;
using WoRModManager.Core.Uniforms;

namespace WoRModManager.App.ViewModels;

// The uniform profile picker. The selected profile is always the active one: the game saves into it.
public sealed class ProfilesViewModel : ObservableObject
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    readonly MainViewModel owner;
    bool loading;

    public ProfilesViewModel(MainViewModel owner)
    {
        this.owner = owner;
        NewCommand = new RelayCommand(_ => _ = NewAsync(), _ => CanChange);
        RenameCommand = new RelayCommand(_ => _ = RenameAsync(), _ => CanChange);
        DeleteCommand = new RelayCommand(_ => _ = DeleteAsync(), _ => CanChange && Names.Count > 1);
        CustomizeCommand = new RelayCommand(_ => _ = CustomizeAsync(), _ => CanChange);
    }

    public ICommand CustomizeCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    internal UniformProfiles? Store { get; private set; }
    bool CanChange => Store is not null && owner.CanTouchGame;

    public ObservableCollection<string> Names { get; } = [];

    string? selected;
    public string? Selected
    {
        get => selected;
        set
        {
            if (loading || value is null || Ci.Equals(value, selected)) return;
            _ = ChangeAsync($"Switching to the {value} uniforms…", s => s.Switch(value));
        }
    }

    // Runs on load and after the game exits, so the game's own saves land in the active profile.
    internal void Reload(UniformProfiles store)
    {
        Store = store;
        Sync();
    }

    internal void Sync()
    {
        try { Store?.Sync(); }
        catch (Exception e) { App.ShowError(e); }
        Refresh();
    }

    void Refresh()
    {
        loading = true;
        try
        {
            Names.Clear();
            foreach (var n in Store?.Names() ?? []) Names.Add(n);
            selected = Store?.Active;
            OnPropertyChanged(nameof(Selected));
        }
        finally
        {
            loading = false;
        }
    }

    async Task ChangeAsync(string status, Action<UniformProfiles> change)
    {
        await Task.Yield(); // leave the binding setter before any dialog shows
        var store = Store!;
        await owner.RunAsync(status, ct => Task.Run(() => change(store), ct), reload: false);
        Refresh();
    }

    Task NewAsync()
    {
        var name = PromptWindow.AskText($"Name the new uniform profile. It starts as a copy of {Selected}.", "", "Create");
        return name is null ? Task.CompletedTask : ChangeAsync($"Creating {name}…", s =>
        {
            s.Create(name);
            s.Switch(name);
        });
    }

    Task RenameAsync()
    {
        var from = Selected!;
        var name = PromptWindow.AskText($"Rename the uniform profile {from}.", from, "Rename");
        return name is null ? Task.CompletedTask : ChangeAsync($"Renaming {from}…", s => s.Rename(from, name));
    }

    // Reads the database the game will load, so it matches the mods enabled right now.
    async Task CustomizeAsync()
    {
        ApparelDatabase? db = null;
        var assets = owner.Paths.Assets;
        if (!await owner.RunAsync("Reading the game's uniforms…", async ct => db = await Task.Run(() => ApparelDatabase.Load(assets), ct), reload: false))
            return;
        if (db is null)
        {
            PromptWindow.Show("Couldn't find the game's uniform data in the Assets folder.");
            return;
        }
        CustomizerViewModel vm;
        try { vm = new CustomizerViewModel(owner, Selected!, Store!, db); }
        catch (Exception e)
        {
            App.ShowError(e);
            return;
        }
        new CustomizerWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    // The selected profile is the active one, so deleting it switches to another first.
    Task DeleteAsync()
    {
        var name = Selected!;
        var next = Names.First(n => !Ci.Equals(n, name));
        if (!PromptWindow.Ask($"Delete the uniform profile {name}? The manager switches to {next} first.", "Delete")) return Task.CompletedTask;
        return ChangeAsync($"Deleting {name}…", s =>
        {
            s.Switch(next);
            s.Delete(name);
        });
    }
}
