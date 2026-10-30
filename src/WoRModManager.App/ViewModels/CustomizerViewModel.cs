using System.Collections.ObjectModel;
using System.Windows.Input;
using WoRModManager.Core.Uniforms;

namespace WoRModManager.App.ViewModels;

public sealed record UniformRow(UniformDef Def, string Label, bool Customized)
{
    public string Text => Customized ? "●  " + Label : Label;
    public override string ToString() => Customized ? Label + " (customized)" : Label; // what screen readers announce
}

// Bound by id (SelectedValue), so a ComboBox whose list was just replaced finds its item again in the new list.
public sealed class SlotRow(CustomizerViewModel owner, UniformEditor editor, Slot slot) : ObservableObject
{
    public string Label => slot.Label;
    public bool IsEditable => !slot.ReadOnly;
    public IReadOnlyList<ApparelItem> Options { get; private set; } = editor.Options(slot.Category);

    public uint SelectedId
    {
        get => editor.Selected(slot.Category);
        set
        {
            if (value == 0 || value == editor.Selected(slot.Category)) return;
            editor.Select(slot.Category, value);
            owner.SlotPicked(this);
        }
    }

    internal void Refresh()
    {
        Options = editor.Options(slot.Category);
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SelectedId));
    }
}

// Edits one profile's uniforms. Nothing is written until Save.
public sealed class CustomizerViewModel : ObservableObject
{
    readonly MainViewModel owner;
    readonly string opened;
    readonly string profile;
    readonly UniformProfiles store;
    readonly ApparelDatabase db;
    readonly CustomizationsDoc doc;
    readonly Dictionary<uint, UniformEditor> editors = [];
    readonly List<UniformRow> all;

    public CustomizerViewModel(MainViewModel owner, string profile, UniformProfiles store, ApparelDatabase db)
    {
        (this.owner, this.profile, this.store, this.db) = (owner, profile, store, db);
        opened = store.Read(profile);
        doc = CustomizationsDoc.Parse(opened);
        all = db.Uniforms.OrderBy(u => !u.IsHead).ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).Select(Row).ToList();
        ResetCommand = new RelayCommand(_ => Reset(), _ => SelectedUniform is not null);
        SaveCommand = new RelayCommand(_ => _ = SaveAsync(), _ => owner.CanTouchGame);
        Filter();
        SelectedUniform = Uniforms.FirstOrDefault();
    }

    public string Title => $"Uniforms — {profile} (from {db.SourcePak})";
    public ICommand ResetCommand { get; }
    public ICommand SaveCommand { get; }
    public event Action? Saved;

    public ObservableCollection<UniformRow> Uniforms { get; } = [];
    public ObservableCollection<SlotRow> SlotRows { get; } = [];

    string search = "";
    public string Search
    {
        get => search;
        set { if (Set(ref search, value)) Filter(); }
    }

    UniformRow? selectedUniform;
    public UniformRow? SelectedUniform
    {
        get => selectedUniform;
        set
        {
            if (value is null || !Set(ref selectedUniform, value)) return;
            var key = value.Def.Key;
            if (!editors.TryGetValue(key, out var editor)) editors[key] = editor = new UniformEditor(value.Def, db, doc.Apparel(key));
            SlotRows.Clear();
            foreach (var s in editor.Slots) SlotRows.Add(new SlotRow(this, editor, s));
            OnPropertyChanged(nameof(HasUnknown));
        }
    }

    // Saved ids this database doesn't know (most likely from a mod that isn't enabled) are kept but get no row.
    public bool HasUnknown => SelectedUniform is { } u && doc.Apparel(u.Def.Key) is { } ids && ids.Any(id => !db.Items.ContainsKey(id));

    internal void SlotPicked(SlotRow source)
    {
        foreach (var r in SlotRows.Where(r => r != source)) r.Refresh(); // a parent's pick can change its children
        ReplaceRow(SelectedUniform!);
    }

    UniformRow Row(UniformDef u) =>
        new(u, u.IsHead ? "Head" : u.DisplayName, editors.TryGetValue(u.Key, out var e) ? e.IsCustomized : doc.Apparel(u.Key) is not null);

    void Filter()
    {
        var keep = SelectedUniform;
        Uniforms.Clear();
        foreach (var r in all.Where(r => r.Label.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))) Uniforms.Add(r);
        selectedUniform = keep is not null && Uniforms.Contains(keep) ? keep : selectedUniform;
        OnPropertyChanged(nameof(SelectedUniform));
    }

    // Swaps a list row for a fresh one so its "customized" mark follows the edits, keeping it selected.
    void ReplaceRow(UniformRow current)
    {
        var updated = Row(current.Def);
        if (updated == current) return;
        all[all.IndexOf(current)] = updated;
        selectedUniform = updated;
        if (Uniforms.IndexOf(current) is var i and >= 0) Uniforms[i] = updated;
        OnPropertyChanged(nameof(SelectedUniform));
    }

    // Back to the game's default for this uniform: its entry is dropped from the file on Save.
    void Reset()
    {
        var current = SelectedUniform!;
        doc.Remove(current.Def.Key);
        editors.Remove(current.Def.Key);
        ReplaceRow(current);
        var row = SelectedUniform!;
        selectedUniform = null;
        SelectedUniform = row; // rebuild the slots from a fresh editor
    }

    // Only uniforms the user changed are written; the profile must still be what it was when the window opened.
    async Task SaveAsync()
    {
        foreach (var (key, e) in editors.Where(p => p.Value.Edited)) doc.SetApparel(key, e.ToApparel());
        var json = doc.ToJson();
        var saved = false;
        await owner.RunAsync($"Saving the {profile} uniforms…", async ct =>
        {
            await Task.Run(() => store.Save(profile, json, expected: opened), ct);
            saved = true;
        }, reload: false); // errors are shown there and the window stays open
        if (saved) Saved?.Invoke();
    }
}
