namespace WoRModManager.Core.Uniforms;

public sealed record Slot(int Category, string Label, int? ParentCategory, bool ReadOnly = false);

// One uniform's slot choices under the game's parent/child rules. Ids it doesn't know are carried through untouched.
public sealed class UniformEditor
{
    // The game's HierarchyRulesets/Uniform.json: the jacket is the root. Parents come before their children,
    // which is also the order slots are shown.
    public static IReadOnlyList<Slot> UniformSlots { get; } =
    [
        new(0, "Jacket", null), new(1, "Shirt", 0),
        new(3, "Shoes", 0), new(2, "Trousers", 3),
        new(4, "Belt", 0), new(5, "Buckle", 4), new(12, "Holster", 4), new(14, "Drum", 4),
        new(6, "Hat", 0), new(7, "Hat adornment", 6), new(8, "Hat embroidery", 6),
        new(9, "Backpack", 0),
        new(10, "Equipment", 0), new(11, "Accessory", 10),
        new(13, "Rank insignia", 0),
    ];

    // HumanHead.json. The head pairs with HeadParameters.HeadType, which is edited in-game.
    public static IReadOnlyList<Slot> HeadSlots { get; } = [new(15, "Head", null, ReadOnly: true), new(17, "Hair", 15), new(18, "Beard", 15)];

    readonly UniformDef uniform;
    readonly ApparelDatabase db;
    readonly IReadOnlyList<uint>? saved;
    readonly Dictionary<int, uint> selected = [];
    // Slots a saved uniform fills with ids this database doesn't know (a mod that's off): any of those ids may be the
    // slot's item, so nothing is picked for them unless the user does it, or the uniform would end up with two.
    readonly HashSet<int> leaveEmpty = [];

    public UniformEditor(UniformDef uniform, ApparelDatabase db, IReadOnlyList<uint>? saved)
    {
        (this.uniform, this.db, this.saved) = (uniform, db, saved);
        var allowed = Allowed().ToList();
        Slots = (uniform.IsHead ? HeadSlots : UniformSlots).Where(s => allowed.Any(i => i.Category == s.Category)).ToList();
        var holdsUnknown = saved?.Any(id => Item(id) is null) == true;
        foreach (var slot in Slots)
        {
            var mine = saved?.Select(Item).FirstOrDefault(i => i?.Category == slot.Category);
            if (mine is not null) selected[slot.Category] = mine.Id;
            else if (holdsUnknown) leaveEmpty.Add(slot.Category);
            else if (!slot.ReadOnly) Pick(slot);
        }
    }

    public IReadOnlyList<Slot> Slots { get; }
    public bool IsCustomized => saved is not null || Edited;
    public bool Edited { get; private set; } // only edited uniforms are written back

    public uint Selected(int category) => selected.GetValueOrDefault(category);

    public IReadOnlyList<ApparelItem> Options(int category)
    {
        var slot = Slots.FirstOrDefault(s => s.Category == category);
        if (slot is null) return [];
        var items = Allowed().Where(i => i.Category == category);
        // With no parent chosen (e.g. a head the file doesn't name) nothing constrains the slot.
        if (slot.ParentCategory is { } p && Item(Selected(p)) is { } parent) items = items.Where(i => parent.Children.Contains(i.Id));
        return items.ToList();
    }

    public void Select(int category, uint id)
    {
        var slot = Slots.FirstOrDefault(s => s.Category == category);
        if (slot is null || slot.ReadOnly || Options(category).All(i => i.Id != id)) return;
        selected[category] = id;
        Edited = true;
        leaveEmpty.Remove(category);
        RepairChildren(category);
    }

    // A child the new parent doesn't allow is re-picked, and that change goes on down the tree.
    void RepairChildren(int category)
    {
        foreach (var child in Slots.Where(s => s.ParentCategory == category))
        {
            if (Options(child.Category).Any(i => i.Id == Selected(child.Category))) continue;
            Pick(child);
            RepairChildren(child.Category);
        }
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
        if (leaveEmpty.Contains(slot.Category)) return;
        var options = Options(slot.Category);
        var pick = (slot.ParentCategory is null ? null : options.FirstOrDefault(i => i.Name.Contains("None", StringComparison.OrdinalIgnoreCase)))
                   ?? options.FirstOrDefault();
        if (pick is null) selected.Remove(slot.Category);
        else selected[slot.Category] = pick.Id;
    }

    IEnumerable<ApparelItem> Allowed() => uniform.Allowed.Select(Item).OfType<ApparelItem>();
    ApparelItem? Item(uint id) => db.Items.GetValueOrDefault(id);
}
