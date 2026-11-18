using System.Text.Json.Nodes;

namespace Mulse.Ui.Services;

public sealed record AuthoringChange(string Path, string Before, string After);

public static class AuthoringProposalReview
{
    public static IReadOnlyList<AuthoringChange> Compare(string beforeJson, string afterJson)
    {
        var changes = new List<AuthoringChange>();
        CompareNodes(JsonNode.Parse(beforeJson), JsonNode.Parse(afterJson), "flow", false, changes);
        return changes;
    }

    private static void CompareNodes(JsonNode? before, JsonNode? after, string path, bool setting, List<AuthoringChange> changes)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject || after is JsonObject)
        {
            var left = before as JsonObject;
            var right = after as JsonObject;
            foreach (var key in (left?.Select(pair => pair.Key) ?? []).Union(right?.Select(pair => pair.Key) ?? []).Order(StringComparer.Ordinal))
                CompareNodes(left?[key], right?[key], path + "." + key, setting || key == "settings", changes);
        }
        else if (before is JsonArray || after is JsonArray)
        {
            var left = before as JsonArray;
            var right = after as JsonArray;
            for (var index = 0; index < Math.Max(left?.Count ?? 0, right?.Count ?? 0); index++)
                CompareNodes(index < (left?.Count ?? 0) ? left![index] : null,
                    index < (right?.Count ?? 0) ? right![index] : null, $"{path}[{index}]", setting, changes);
        }
        else
        {
            changes.Add(new(path, Display(before, setting), Display(after, setting)));
        }
    }

    private static string Display(JsonNode? value, bool setting)
    {
        if (value is null) return "(not set)";
        if (setting) return "(setting value hidden; review in Properties after applying)";
        var text = value.ToString();
        return text.Length <= 160 ? text : text[..160] + "...";
    }
}
