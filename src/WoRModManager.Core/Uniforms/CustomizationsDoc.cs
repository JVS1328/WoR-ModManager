using System.Text.Json;
using System.Text.Json.Nodes;

namespace WoRModManager.Core.Uniforms;

// customizations.json as a JSON tree: only the Apparel arrays it's asked to change are touched, everything else is kept.
public sealed class CustomizationsDoc
{
    readonly JsonObject root;
    CustomizationsDoc(JsonObject root) => this.root = root;

    public static CustomizationsDoc Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new JsonException("The uniform file isn't a JSON object."));

    JsonArray Uniforms => root["Uniforms"] as JsonArray ?? (JsonArray)(root["Uniforms"] = new JsonArray());

    JsonObject? Entry(uint key) =>
        Uniforms.OfType<JsonObject>().FirstOrDefault(e => e["key"] is JsonValue v && v.TryGetValue<long>(out var k) && k == key);

    public IReadOnlyList<uint>? Apparel(uint key) =>
        Entry(key)?["value"]?["Apparel"] is JsonArray a ? a.Select(n => (uint)n!.GetValue<long>()).ToList() : null;

    public void SetApparel(uint key, IReadOnlyList<uint> ids)
    {
        var array = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)).ToArray());
        if (Entry(key) is not { } e)
        {
            Uniforms.Add(new JsonObject { ["key"] = key, ["value"] = new JsonObject { ["Apparel"] = array } });
            return;
        }
        if (e["value"] is not JsonObject value) e["value"] = value = new JsonObject();
        value["Apparel"] = array;
    }

    public void Remove(uint key)
    {
        if (Entry(key) is { } e) Uniforms.Remove(e);
    }

    public string ToJson() => root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
