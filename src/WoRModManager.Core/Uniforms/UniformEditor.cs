namespace WoRModManager.Core.Uniforms;

public sealed record Slot(int Category, string Label, int? ParentCategory, bool ReadOnly = false);

// One uniform's slot choices under the game's parent/child rules. Ids it doesn't know are carried through untouched.
public sealed class UniformEditor
{
    // Parents come before their children, which is also the order slots are shown.
    public static IReadOnlyList<Slot> UniformSlots { get; } =
    [
        new(0, "Jacket", null), new(1, "Shirt", 0),
        new(3, "Shoes", null), new(2, "Trousers", 3),
        new(4, "Belt", null), new(5, "Buckle", 4), new(12, "Holster", 4), new(14, "Drum", 4),
        new(6, "Hat", null), new(7, "Hat adornment", 6), new(8, "Hat embroidery", 6),
        new(9, "Backpack", null),
        new(10, "Equipment", null), new(11, "Accessory", 10),
        new(13, "Rank insignia", null),
    ];

    // The head pairs with HeadParameters.HeadType, which is edited in-game.
    public static IReadOnlyList<Slot> HeadSlots { get; } = [new(15, "Head", null, ReadOnly: true), new(17, "Hair", null), new(18, "Beard", null)];

    readonly UniformDef uniform;
    readonly ApparelDatabase db;
    readonly IReadOnlyList<uint>? saved;
    readonly Dictionary<int, uint> selected = [];
    bool edited;

    public UniformEditor(UniformDef uniform, ApparelDatabase db, IReadOnlyList<uint>? saved)
    {
        (this.uniform, this.db, this.saved) = (uniform, db, saved);
        var allowed = Allowed().ToList();
        Slots = (uniform.IsHead ? HeadSlots : UniformSlots).Where(s => allowed.Any(i => i.Category == s.Category)).ToList();
        foreach (var slot in Slots)
        {
            var mine = saved?.Select(Item).FirstOrDefault(i => i?.Category == slot.Category);
            if (mine is not null) selected[slot.Category] = mine.Id;
            else if (!slot.ReadOnly) Pick(slot);
        }
    }

    public IReadOnlyList<Slot> Slots { get; }
    public bool IsCustomized => saved is not null || edited;

    public uint Selected(int category) => selected.GetValueOrDefault(category);

    public IReadOnlyList<ApparelItem> Options(int category)
    {
        var slot = Slots.FirstOrDefault(s => s.Category == category);
        if (slot is null) return [];
        var items = Allowed().Where(i => i.Category == category);
        if (slot.ParentCategory is { } p && Slots.Any(s => s.Category == p))
        {
            var parent = Item(Selected(p));
            items = items.Where(i => parent?.Children.Contains(i.Id) == true);
        }
        return items.ToList();
    }

    public void Select(int category, uint id)
    {
        var slot = Slots.FirstOrDefault(s => s.Category == category);
        if (slot is null || slot.ReadOnly || Options(category).All(i => i.Id != id)) return;
        selected[category] = id;
        edited = true;
        foreach (var child in Slots.Where(s => s.ParentCategory == category))
            if (Options(child.Category).All(i => i.Id != Selected(child.Category))) Pick(child);
    }

    public IReadOnlyList<uint> ToApparel()
    {
        var result = saved?.ToList() ?? [];
        foreach (var slot in Slots.Where(s => !s.ReadOnly))
        {
            var at = result.FindIndex(id => Item(id)?.Category == slot.Category);
            if (selected.TryGetValue(slot.Category, out var id))
            {
                if (at >= 0) result[at] = id;
                else result.Add(id);
            }
            else if (at >= 0) result.RemoveAt(at); // the parent allows no item in this slot
        }
        return result;
    }

    // Children default to a "None" item when there is one; with no valid item the slot is left empty.
    void Pick(Slot slot)
    {
        var options = Options(slot.Category);
        var pick = (slot.ParentCategory is null ? null : options.FirstOrDefault(i => i.Name.Contains("None", StringComparison.OrdinalIgnoreCase)))
                   ?? options.FirstOrDefault();
        if (pick is null) selected.Remove(slot.Category);
        else selected[slot.Category] = pick.Id;
    }

    IEnumerable<ApparelItem> Allowed() => uniform.Allowed.Select(Item).OfType<ApparelItem>();
    ApparelItem? Item(uint id) => db.Items.GetValueOrDefault(id);
}
