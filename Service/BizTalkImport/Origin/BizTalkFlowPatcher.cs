using System.Text.Json.Nodes;

namespace Service.BizTalkImport.Origin;

/// <summary>Applies selected pieces of a fresh analysis onto a saved native flow, leaving every other node, choice and setting exactly as the user left it.</summary>
internal static class BizTalkFlowPatcher
{
    public static string Patch(string existingFlowJson, BizTalkOriginPieces fresh, IEnumerable<string> keys)
    {
        if (!BizTalkOriginPieces.TryParseObject(existingFlowJson, out var root))
            throw new InvalidOperationException("The saved native flow is not a JSON object, so it cannot be updated in place.");
        var nodes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        Index(root["nodes"] as JsonArray, nodes);

        foreach (var key in keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!fresh.Content.TryGetValue(key, out var piece))
                throw new InvalidOperationException($"Piece '{key}' is not part of the fresh analysis.");
            switch (piece.Kind)
            {
                case "flow":
                    ReplaceFlow(root, (JsonObject)piece.Body);
                    break;
                case "node":
                    ReplaceNode(Node(nodes, piece), (JsonObject)piece.Body);
                    break;
                case "choice":
                    var owner = Node(nodes, piece);
                    if (owner["settings"] is not JsonObject settings) owner["settings"] = settings = new JsonObject();
                    ReplaceChoice(settings, "casesJson", piece);
                    break;
                case "shared-choice":
                    ReplaceChoice(root, "renderChoicesJson", piece);
                    break;
                default:
                    throw new InvalidOperationException($"Piece '{key}' cannot be applied to a flow.");
            }
        }

        return root.ToJsonString();
    }

    private static JsonObject Node(Dictionary<string, JsonObject> nodes, BizTalkOriginPieces.Piece piece)
        => nodes.TryGetValue(piece.NodeId ?? string.Empty, out var node)
            ? node
            : throw new InvalidOperationException($"Node '{piece.NodeId}' is missing from the saved flow.");

    private static void Index(JsonArray? array, Dictionary<string, JsonObject> nodes)
    {
        foreach (var item in array ?? [])
        {
            if (item is not JsonObject node) continue;
            if (node["id"] is JsonValue id && id.TryGetValue<string>(out var text)) nodes.TryAdd(text, node);
            Index(node["children"] as JsonArray, nodes);
            Index(node["otherwise"] as JsonArray, nodes);
        }
    }

    private static void ReplaceFlow(JsonObject root, JsonObject fresh)
    {
        foreach (var name in root.Select(static property => property.Key).Where(name => name is not ("nodes" or "renderChoicesJson") && !fresh.ContainsKey(name)).ToArray())
            root.Remove(name);
        foreach (var (name, value) in fresh)
            root[name] = value?.DeepClone();
    }

    private static void ReplaceNode(JsonObject target, JsonObject fresh)
    {
        var children = target["children"]?.DeepClone();
        var otherwise = target["otherwise"]?.DeepClone();
        var cases = target["settings"] is JsonObject oldSettings ? oldSettings["casesJson"]?.DeepClone() : null;
        target.Clear();
        foreach (var (name, value) in fresh)
            target[name] = value?.DeepClone();
        if (children is not null) target["children"] = children;
        if (otherwise is not null) target["otherwise"] = otherwise;
        if (cases is not null)
        {
            if (target["settings"] is not JsonObject settings) target["settings"] = settings = new JsonObject();
            settings["casesJson"] = cases;
        }
    }

    private static void ReplaceChoice(JsonObject owner, string property, BizTalkOriginPieces.Piece piece)
    {
        var array = new JsonArray();
        if (owner[property] is JsonValue current && current.TryGetValue<string>(out var text))
            array = JsonNode.Parse(text) as JsonArray ?? throw new InvalidOperationException($"The saved choices for '{piece.Value}' are not a JSON array, so the choice cannot be updated in place.");
        var replacement = piece.Body.DeepClone();
        var index = -1;
        for (var position = 0; position < array.Count; position++)
            if (array[position] is JsonObject entry && entry["value"] is JsonValue value && value.TryGetValue<string>(out var existing) && string.Equals(existing, piece.Value, StringComparison.Ordinal))
                index = position;
        if (index >= 0) array[index] = replacement;
        else array.Add(replacement);
        owner[property] = array.ToJsonString();
    }
}
