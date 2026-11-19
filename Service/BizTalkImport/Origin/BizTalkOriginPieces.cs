using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Service.BizTalkImport.Origin;

/// <summary>
/// Splits an imported native flow and its stored config values into independently updatable, hashable pieces. The same split is applied to a fresh analysis,
/// to the saved flow, and (as hashes) to the origin recorded at import, so a re-import can tell importer changes from user edits.
/// </summary>
internal sealed class BizTalkOriginPieces
{
    public const string StructureKey = "structure";
    public const string FlowKey = "flow";
    public const string NodePrefix = "node:";
    public const string ChoicePrefix = "choice:";
    public const string SharedChoicePrefix = "shared-choice:";
    public const string ConfigPrefix = "config:";

    private BizTalkOriginPieces(Dictionary<string, string> hashes, Dictionary<string, Piece> content, IReadOnlyDictionary<string, string> config)
    {
        Hashes = hashes;
        Content = content;
        Config = config;
    }

    /// <summary>One content piece of a native flow (never a config piece).</summary>
    internal sealed record Piece(string Kind, string? NodeId, string? Value, JsonNode Body, string Label);

    public IReadOnlyDictionary<string, string> Hashes { get; }

    public IReadOnlyDictionary<string, Piece> Content { get; }

    public IReadOnlyDictionary<string, string> Config { get; }

    public static BizTalkOriginPieces Read(string? flowJson, IReadOnlyDictionary<string, string> config)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var content = new Dictionary<string, Piece>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(flowJson) && TryParseObject(flowJson, out var root))
        {
            var shape = new StringBuilder();
            WalkNodes(root["nodes"] as JsonArray, shape, hashes, content);
            hashes[StructureKey] = HashText(shape.ToString());

            var flow = (JsonObject)root.DeepClone();
            flow.Remove("nodes");
            var shared = flow["renderChoicesJson"];
            flow.Remove("renderChoicesJson");
            hashes[FlowKey] = Hash(flow);
            content[FlowKey] = new Piece("flow", null, null, flow, "Flow settings");
            if (shared is JsonValue sharedValue && sharedValue.TryGetValue<string>(out var sharedText) && TryReadChoices(sharedText, out var sharedEntries))
                foreach (var (value, entry) in sharedEntries)
                {
                    var key = SharedChoicePrefix + value;
                    hashes[key] = Hash(entry);
                    content[key] = new Piece("shared-choice", null, value, entry, value);
                }
        }

        foreach (var (name, text) in config)
            hashes[ConfigPrefix + name] = HashText(text);
        return new BizTalkOriginPieces(hashes, content, config);
    }

    public static string KindOf(string key) => key switch
    {
        StructureKey => "structure",
        FlowKey => "flow",
        _ when key.StartsWith(NodePrefix, StringComparison.Ordinal) => "node",
        _ when key.StartsWith(SharedChoicePrefix, StringComparison.Ordinal) => "shared-choice",
        _ when key.StartsWith(ChoicePrefix, StringComparison.Ordinal) => "choice",
        _ when key.StartsWith(ConfigPrefix, StringComparison.Ordinal) => "config",
        _ => "other"
    };

    public static bool TryParseObject(string json, out JsonObject root)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject parsed)
            {
                root = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        root = null!;
        return false;
    }

    /// <summary>Reads a render-dispatch choices array into value-keyed entries; false when it is not an array of objects with unique string values.</summary>
    public static bool TryReadChoices(string json, out List<(string Value, JsonNode Entry)> entries)
    {
        entries = [];
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                if (item is not JsonObject entry || entry["value"] is not JsonValue valueNode || !valueNode.TryGetValue<string>(out var value) || !seen.Add(value))
                    return false;
                entries.Add((value, entry));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WalkNodes(JsonArray? nodes, StringBuilder shape, Dictionary<string, string> hashes, Dictionary<string, Piece> content)
    {
        shape.Append('[');
        foreach (var item in nodes ?? [])
        {
            if (item is not JsonObject node) continue;
            var id = StringOf(node["id"]);
            var kind = StringOf(node["kind"]);
            shape.Append('(').Append(id.Length).Append(':').Append(id).Append('|').Append(kind);

            var own = (JsonObject)node.DeepClone();
            own.Remove("children");
            own.Remove("otherwise");
            if (own["settings"] is JsonObject settings && settings["casesJson"] is JsonValue casesValue
                && casesValue.TryGetValue<string>(out var casesText) && TryReadChoices(casesText, out var entries))
            {
                settings.Remove("casesJson");
                foreach (var (value, entry) in entries)
                {
                    var key = ChoicePrefix + id + ":" + value;
                    hashes[key] = Hash(entry);
                    content[key] = new Piece("choice", id, value, entry, value);
                }
            }

            var nodeKey = NodePrefix + id;
            hashes[nodeKey] = Hash(own);
            var name = StringOf(own["name"]);
            content[nodeKey] = new Piece("node", id, null, own, name.Length > 0 ? name : id);
            shape.Append('c');
            WalkNodes(node["children"] as JsonArray, shape, hashes, content);
            shape.Append('o');
            WalkNodes(node["otherwise"] as JsonArray, shape, hashes, content);
            shape.Append(')');
        }

        shape.Append(']');
    }

    private static string StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Hash(JsonNode node)
    {
        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return HashText(builder.ToString());
    }

    public static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static void WriteCanonical(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var property in obj.OrderBy(static property => property.Key, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Key)).Append(':');
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    WriteCanonical(array[index], builder);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }
}
