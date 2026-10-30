using System.Text.Json.Nodes;
using WoRModManager.Core.Tests.TestUtil;
using WoRModManager.Core.Uniforms;

namespace WoRModManager.Core.Tests;

public class UniformEditorTests : IDisposable
{
    const uint Key = 10, JacketA = 100, Shirt1 = 101, JacketB = 102, ShirtNone = 103, Shirt2 = 104, Shoe = 110, Trouser = 111, Odd = 120;
    const uint Head1 = 200, Hair1 = 201, Hair2 = 202, Head2 = 203;

    readonly TempGame _game = new();
    readonly ApparelDatabase _db;

    public UniformEditorTests()
    {
        _game.WritePak("GameData.pak", ApparelBins.Pak(
            ApparelBins.Uniforms(
                (Key, "4th Pennsylvania - Private", 1, [JacketA, Shirt1, JacketB, ShirtNone, Shirt2, Shoe, Trouser, Odd]),
                (ApparelDatabase.HumanHeadKey, "Head", ApparelDatabase.HumanHeadKey, [Head1, Hair1, Hair2, Head2])),
            ApparelBins.Apparel(
                (JacketA, "US_JacketA", 0, [Shirt1]), (JacketB, "US_JacketB", 0, [ShirtNone, Shirt2]),
                (Shirt1, "US_Shirt1", 1, []), (ShirtNone, "US_ShirtNone", 1, []), (Shirt2, "US_Shirt2", 1, []),
                (Shoe, "US_Shoe", 3, [Trouser]), (Trouser, "US_Trouser", 2, []), (Odd, "US_Odd", 20, []),
                (Head1, "Head_John", 15, []), (Head2, "Head_Bill", 15, []), (Hair1, "Hair_Short", 17, []), (Hair2, "Hair_Long", 17, []))));
        _db = ApparelDatabase.Load(_game.Paths.Assets)!;
    }

    public void Dispose() => _game.Dispose();

    UniformEditor Editor(uint key, params uint[]? saved) => new(_db.Uniforms.Single(u => u.Key == key), _db, saved);

    const string Live = """
        {
        	"Uniforms": [
        		{ "key": 392959916, "value": { "Apparel": [ 170363493, 3878937953 ] } },
        		{ "key": 2774717941, "value": { "Apparel": [ 3695474445, 2958941383 ] } }
        	],
        	"HeadParameters": { "HeadType": "John", "Age": 0.120000, "SeasonalGear": true, "HorseCoat": "Chestnut" },
        	"Foo": 1
        }
        """;

    [Fact] public void Doc_RoundTrip_PreservesUnknownMembers()
    {
        var doc = CustomizationsDoc.Parse(Live);
        Assert.Equal([170363493u, 3878937953u], doc.Apparel(392959916));
        doc.SetApparel(392959916, [1]);
        var back = JsonNode.Parse(doc.ToJson())!;
        Assert.Equal("John", (string)back["HeadParameters"]!["HeadType"]!);
        Assert.Equal(1, (int)back["Foo"]!);
        var again = CustomizationsDoc.Parse(doc.ToJson());
        Assert.Equal([1u], again.Apparel(392959916));
        Assert.Equal([3695474445u, 2958941383u], again.Apparel(2774717941));
    }

    [Fact] public void Doc_SetApparel_AppendsNewKey()
    {
        var doc = CustomizationsDoc.Parse("""{"Uniforms":[]}""");
        Assert.Null(doc.Apparel(4000000000));
        doc.SetApparel(4000000000, [4000000001]);
        Assert.Equal([4000000001u], CustomizationsDoc.Parse(doc.ToJson()).Apparel(4000000000));
    }

    [Fact] public void Doc_MissingUniformsArray_IsCreatedOnSet()
    {
        var doc = CustomizationsDoc.Parse("{}");
        doc.SetApparel(5, [6]);
        Assert.Equal([6u], CustomizationsDoc.Parse(doc.ToJson()).Apparel(5));
    }

    [Fact] public void Doc_Remove_DropsEntry()
    {
        var doc = CustomizationsDoc.Parse(Live);
        doc.Remove(392959916);
        var again = CustomizationsDoc.Parse(doc.ToJson());
        Assert.Null(again.Apparel(392959916));
        Assert.NotNull(again.Apparel(2774717941));
    }

    [Fact] public void Options_FilteredByParentChildren()
    {
        var e = Editor(Key, JacketA, Shirt1);
        Assert.Equal([Shirt1], e.Options(1).Select(i => i.Id));
        Assert.Equal([JacketA, JacketB], e.Options(0).Select(i => i.Id));
        Assert.Equal([0, 1, 3, 2], e.Slots.Select(s => s.Category)); // no belt/hat items: those slots are hidden
    }

    [Fact] public void Select_RepairsInvalidChild_PreferringNone()
    {
        var e = Editor(Key, JacketA, Shirt1);
        e.Select(0, JacketB);
        Assert.Equal(JacketB, e.Selected(0));
        Assert.Equal(ShirtNone, e.Selected(1));
    }

    [Fact] public void Select_IgnoresIdsThatArentOptions()
    {
        var e = Editor(Key, JacketA, Shirt1);
        e.Select(1, Shirt2); // Jacket A doesn't allow Shirt2
        Assert.Equal(Shirt1, e.Selected(1));
    }

    [Fact] public void Edit_PreservesUnknownIds()
    {
        var e = Editor(Key, JacketA, 999999, Odd);
        e.Select(0, JacketB);
        Assert.Equal([JacketB, 999999u, Odd, ShirtNone, Shoe, Trouser], e.ToApparel());
    }

    [Fact] public void HeadSlot_IsReadOnly()
    {
        var e = Editor(ApparelDatabase.HumanHeadKey, Head1, Hair1);
        Assert.Equal([15, 17], e.Slots.Select(s => s.Category));
        Assert.True(e.Slots[0].ReadOnly);
        e.Select(15, Head2);
        Assert.Equal(Head1, e.Selected(15));
        e.Select(17, Hair2);
        Assert.Equal([Head1, Hair2], e.ToApparel());
    }

    [Fact] public void HeadSlot_NotSaved_IsNotWritten()
    {
        var e = Editor(ApparelDatabase.HumanHeadKey, Hair1);
        e.Select(17, Hair2);
        Assert.Equal([Hair2], e.ToApparel());
    }

    [Fact] public void NotCustomized_UntilSelect()
    {
        var e = Editor(Key, null);
        Assert.False(e.IsCustomized);
        Assert.Equal(JacketA, e.Selected(0));
        e.Select(0, JacketB);
        Assert.True(e.IsCustomized);
        Assert.True(Editor(Key, JacketA).IsCustomized);
    }
}
