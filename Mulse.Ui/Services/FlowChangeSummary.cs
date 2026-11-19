using System.Text;
using System.Text.Json.Nodes;

namespace Mulse.Ui.Services;

/// <summary>
/// A plain-language list of what a save changed (R2), built from the designer's last saved snapshot and the saved one.
/// It reads structure only: names, modules, triggers and retry numbers. Setting values are never shown, only which
/// settings changed, and canvas positions are ignored. No AI is involved.
/// </summary>
public sealed record FlowChangeSummary(IReadOnlyList<string> Changes, IReadOnlyList<string> Cautions)
{
    public const int MaxLines = 25;

    public bool IsEmpty => Changes.Count == 0 && Cautions.Count == 0;

    public static FlowChangeSummary Build(string beforeHeader, IReadOnlyList<string> beforeNodes, string afterHeader, IReadOnlyList<string> afterNodes)
    {
        var changes = new List<string>();
        var cautions = new List<string>();
        CompareHeader(Parse(beforeHeader), Parse(afterHeader), changes, cautions);
        CompareNodes(beforeNodes.Select(Strip).ToList(), afterNodes.Select(Strip).ToList(), changes, cautions);
        return new FlowChangeSummary(Cap(changes), Cap(cautions));
    }

    private static List<string> Cap(List<string> lines)
        => lines.Count <= MaxLines ? lines : [.. lines.Take(MaxLines), $"...and {lines.Count - MaxLines} more."];

    private static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? new JsonObject();

    private static JsonObject Strip(string json)
    {
        var node = Parse(json);
        node.Remove("x");
        node.Remove("y");
        return node;
    }

    private static void CompareHeader(JsonObject before, JsonObject after, List<string> changes, List<string> cautions)
    {
        if (Text(before, "flowName") != Text(after, "flowName"))
            changes.Add($"Flow name changed from {Quote(Text(before, "flowName"))} to {Quote(Text(after, "flowName"))}.");

        var wasEnabled = Flag(before, "enabled");
        var isEnabled = Flag(after, "enabled");
        if (wasEnabled != isEnabled)
        {
            changes.Add(isEnabled ? "The flow is now enabled." : "The flow is now disabled.");
            if (isEnabled) cautions.Add("The flow is enabled, so it can run on its trigger and send data to its destinations.");
        }

        var oldMode = Text(before, "triggerMode");
        var newMode = Text(after, "triggerMode");
        if (oldMode != newMode)
        {
            changes.Add($"Trigger changed from {oldMode} to {newMode}.");
            if (newMode is "Interval" or "Push") cautions.Add($"The flow now starts by itself ({newMode}) when it is enabled.");
        }

        if (newMode == "Interval" && Text(before, "intervalText") != Text(after, "intervalText"))
            changes.Add($"Interval changed from {Text(before, "intervalText")} to {Text(after, "intervalText")}.");
        if (Flag(before, "runOnStartup") != Flag(after, "runOnStartup"))
            changes.Add(Flag(after, "runOnStartup") ? "Run on startup is now on." : "Run on startup is now off.");

        var oldRetry = Flag(before, "hasDefaultRetry") ? DefaultRetryText(before) : null;
        var newRetry = Flag(after, "hasDefaultRetry") ? DefaultRetryText(after) : null;
        if (oldRetry != newRetry)
        {
            if (newRetry is null)
            {
                changes.Add("The flow's default retry was removed.");
                cautions.Add("Steps without their own retry setting no longer retry after a failure.");
            }
            else
            {
                changes.Add(oldRetry is null ? $"A default retry was added ({newRetry})." : $"The default retry changed from {oldRetry} to {newRetry}.");
            }
        }

        if (Text(before, "category") != Text(after, "category"))
            changes.Add($"Category changed from {Quote(Text(before, "category"))} to {Quote(Text(after, "category"))}.");
    }

    private static string DefaultRetryText(JsonObject header)
        => $"{Number(header, "retryMaxAttempts") ?? "unlimited"} attempts, {Number(header, "retryDelaySeconds") ?? "0"}s delay, {Text(header, "retryBackoff")}";

    private static void CompareNodes(List<JsonObject> before, List<JsonObject> after, List<string> changes, List<string> cautions)
    {
        for (var index = 0; index < before.Count;)
        {
            var match = after.FindIndex(candidate => JsonNode.DeepEquals(before[index], candidate));
            if (match < 0) { index++; continue; }
            before.RemoveAt(index);
            after.RemoveAt(match);
        }

        var pairs = new List<(JsonObject Before, JsonObject After)>();
        Pair(before, after, pairs, static (a, b) => Stage(a) == Stage(b) && Text(a, "name") is { Length: > 0 } name && name == Text(b, "name"));
        Pair(before, after, pairs, static (a, b) => Stage(a) == Stage(b) && Text(a, "graphId") is { Length: > 0 } id && id == Text(b, "graphId"));
        Pair(before, after, pairs, static (a, b) => Stage(a) == Stage(b) && Text(a, "moduleId") is { Length: > 0 } module && module == Text(b, "moduleId"));

        foreach (var removed in before) changes.Add($"Removed {Label(removed)}.");
        foreach (var added in after)
        {
            changes.Add($"Added {Label(added)}.");
            if (Stage(added) == 4) cautions.Add($"A new delivery step was added ({Display(added)}): the flow now sends data to an additional destination.");
        }

        foreach (var (was, now) in pairs) CompareStep(was, now, changes, cautions);
    }

    private static void Pair(List<JsonObject> before, List<JsonObject> after, List<(JsonObject, JsonObject)> pairs, Func<JsonObject, JsonObject, bool> match)
    {
        for (var index = 0; index < before.Count;)
        {
            var found = after.FindIndex(candidate => match(before[index], candidate));
            if (found < 0) { index++; continue; }
            pairs.Add((before[index], after[found]));
            before.RemoveAt(index);
            after.RemoveAt(found);
        }
    }

    private static void CompareStep(JsonObject before, JsonObject after, List<string> changes, List<string> cautions)
    {
        var label = Label(after);
        if (Text(before, "name") != Text(after, "name"))
            changes.Add($"{label}: renamed from {Quote(Text(before, "name"))}.");
        if (Text(before, "moduleId") != Text(after, "moduleId"))
        {
            changes.Add($"{label}: module changed from {Text(before, "moduleId")} to {Text(after, "moduleId")}.");
            cautions.Add($"{Display(after)}: the module changed, so check that its settings still apply.");
        }

        var oldSettings = before["settings"] as JsonObject ?? new JsonObject();
        var newSettings = after["settings"] as JsonObject ?? new JsonObject();
        var added = newSettings.Select(pair => pair.Key).Where(key => !oldSettings.ContainsKey(key)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = oldSettings.Select(pair => pair.Key).Where(key => !newSettings.ContainsKey(key)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var edited = newSettings.Select(pair => pair.Key)
            .Where(key => oldSettings.ContainsKey(key) && oldSettings[key]?.ToJsonString() != newSettings[key]?.ToJsonString())
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count + removed.Count + edited.Count > 0)
        {
            var parts = new List<string>();
            if (added.Count > 0) parts.Add("added " + string.Join(", ", added));
            if (removed.Count > 0) parts.Add("removed " + string.Join(", ", removed));
            if (edited.Count > 0) parts.Add("changed " + string.Join(", ", edited));
            changes.Add($"{label}: settings {string.Join("; ", parts)} (values are not shown).");
        }

        var oldRetry = StepRetryText(before["retry"] as JsonObject);
        var newRetry = StepRetryText(after["retry"] as JsonObject);
        if (oldRetry != newRetry)
        {
            changes.Add(newRetry is null ? $"{label}: its own retry was removed." : $"{label}: retry is now {newRetry}.");
            if (newRetry is null) cautions.Add($"{Display(after)}: its own retry was removed, so it follows the flow default.");
        }

        if (Flag(before, "requireConfirmation") != Flag(after, "requireConfirmation"))
        {
            changes.Add(Flag(after, "requireConfirmation") ? $"{label}: now needs confirmation before it runs." : $"{label}: no longer needs confirmation.");
            if (!Flag(after, "requireConfirmation")) cautions.Add($"{Display(after)}: it no longer waits for confirmation, so it will run without a person approving it.");
        }

        var hadCompensation = before["compensation"] is not null;
        var hasCompensation = after["compensation"] is not null;
        if (hadCompensation != hasCompensation)
        {
            changes.Add(hasCompensation ? $"{label}: an undo (compensation) step was added." : $"{label}: its undo (compensation) step was removed.");
            if (!hasCompensation) cautions.Add($"{Display(after)}: without its undo step, a failure inside an atomic scope cannot reverse this delivery.");
        }
        else if (hasCompensation && !JsonNode.DeepEquals(before["compensation"], after["compensation"]))
        {
            changes.Add($"{label}: its undo (compensation) step changed.");
        }

        if (Text(before, "atomicScope") != Text(after, "atomicScope"))
            changes.Add($"{label}: atomic scope changed from {Quote(Text(before, "atomicScope"))} to {Quote(Text(after, "atomicScope"))}.");
        if (!JsonNode.DeepEquals(before["inputParseIds"], after["inputParseIds"]) || !JsonNode.DeepEquals(before["fetchNodeIds"], after["fetchNodeIds"]))
            changes.Add($"{label}: its connections to other steps changed.");
        if (Number(before, "order") != Number(after, "order"))
            changes.Add($"{label}: its run order changed.");
    }

    private static string? StepRetryText(JsonObject? retry)
        => retry is null ? null : $"{Number(retry, "maxAttempts") ?? "unlimited"} attempts, {Number(retry, "delaySeconds") ?? "0"}s delay, {Text(retry, "backoff")}";

    private static int Stage(JsonObject node) => node["stage"] is JsonValue value && value.TryGetValue<int>(out var stage) ? stage : -1;

    private static string StageName(int stage) => stage switch
    {
        0 => "source fetch",
        1 => "parse",
        2 => "augment",
        3 => "render",
        4 => "delivery",
        _ => "step"
    };

    private static string Display(JsonObject node)
    {
        var display = Text(node, "name");
        if (display.Length == 0) display = Text(node, "graphId");
        if (display.Length == 0) display = Text(node, "moduleId");
        return display.Length == 0 ? "(unnamed)" : Clean(display);
    }

    private static string Label(JsonObject node)
    {
        var module = Text(node, "moduleId");
        return $"{StageName(Stage(node))[..1].ToUpperInvariant()}{StageName(Stage(node))[1..]} step '{Display(node)}'" + (module.Length > 0 && module != Display(node) ? $" ({Clean(module)})" : "");
    }

    private static string Quote(string value) => value.Length == 0 ? "(none)" : $"'{Clean(value)}'";

    private static string Clean(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, 60));
        foreach (var character in value)
        {
            if (builder.Length >= 60) { builder.Append("..."); break; }
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static string Text(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Flag(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? Number(JsonObject node, string key)
        => node[key] is JsonValue value ? value.ToJsonString() : null;
}
