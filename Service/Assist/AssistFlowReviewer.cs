using System.Text.RegularExpressions;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Service.ConfigValues;
using Service.Flows;
using Service.Models;
using Service.Modules;

namespace Service.Assist;

/// <summary>
/// The pre-enable review (R1): rules, written in code, that look for common mistakes in a stored flow before it is enabled.
/// It is deterministic, needs no AI provider and never returns a setting value, config value or secret: findings carry names, counts and fixes only.
/// </summary>
public sealed partial class AssistFlowReviewer(IModuleCatalog catalog, AssistExplainer explainer)
{
    public const string Disclaimer = "These are rules applied to the flow definition. A review with nothing to report does not show that the flow is correct; test it with sample data before relying on it.";

    private static readonly string[] Checked =
    [
        "The flow is not an unverified import, and every module it uses is installed.",
        "Every {{config:...}} and {{secret:...}} reference has a stored value.",
        "Passwords, tokens and keys are not typed into plain settings.",
        "No setting still holds a placeholder such as TODO, CHANGEME or <value>.",
        "A source and a destination do not point at the same folder, address or queue.",
        "Delivery steps have automatic retries.",
        "Sources that can remove or mark what they read have that turned on.",
        "Native steps are translated and use the settings their modules require."
    ];

    private static readonly string[] NotChecked =
    [
        "Whether the mappings, conditions and messages do what the business needs.",
        "Whether each branch was tried with sample data.",
        "Whether a step was suggested by AI and has not been reviewed yet (this is not tracked yet).",
        "Whether the destination can accept the same data twice."
    ];

    [GeneratedRegex(@"(password|passwd|pwd|secret|token|api[_-]?key|account[_-]?key|access[_-]?key|private[_-]?key|connection[_-]?string|credential)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();

    // A key such as "tokenUrl" or "passwordFile" names where a secret lives, not the secret itself.
    [GeneratedRegex(@"(url|uri|endpoint|path|file|name|type|header|field|expir\w*|ttl|seconds|minutes|enabled|mode|kind|prefix|location|scope)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotSecretSuffixPattern();

    [GeneratedRegex(@"(path|folder|directory|dir|url|uri|endpoint|address|queue|topic|container|share)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationKeyPattern();

    [GeneratedRegex(@"(delete|move|archive|acknowledg|\back\b|cleanup|clean_up|processed|markread|mark_read)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CleanupKeyPattern();

    [GeneratedRegex(@"(^\s*<[^<>\r\n]{1,60}>\s*$)|(\b(TODO|TBD|FIXME|CHANGE_?ME|REPLACE_?ME)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    public FlowReview Review(PipelineDefinition flow, IReadOnlyDictionary<string, string> configValues)
    {
        ArgumentNullException.ThrowIfNull(flow);
        configValues ??= new Dictionary<string, string>();
        var entries = catalog.GetAll();
        var descriptors = entries.Select(entry => entry.Descriptor).ToArray();
        var steps = Steps(flow).ToList();
        var items = new List<ReviewItem>();

        if (flow.Migration is not null)
            items.Add(new("imported-flow", "Blocker", "This flow was imported and has not been verified",
                FlowMigrationReadiness.BlockReason, [], "Review the migration report, rebuild the flow with native steps and save a new version; imported flows stay disabled."));

        var missing = FlowModuleDependencies.FindMissing(flow, entries, configValues);
        if (missing.Count > 0)
            items.Add(new("missing-module", "Blocker", "The flow uses modules that are not installed",
                "Mulse cannot enable or run the flow until these are loaded.", [.. missing],
                "Load the module packages, or choose installed modules. The AI assistance page can write a prompt for a missing module."));

        AddUnresolvedReferences(steps, configValues, items);
        AddPlainSecrets(steps, descriptors, items);
        AddPlaceholders(steps, items);
        AddSourceDestinationLoops(steps, items);
        AddMissingRetries(flow, steps, items);
        AddSourceCleanup(flow, steps, descriptors, items);
        AddNativeFindings(steps, configValues, items);

        if (items.All(item => item.Code != "missing-module") && flow.Deliveries.Count == 0 && !steps.Any(step => step.Role == ReviewRole.Native))
            items.Add(new("no-delivery", "Warning", "The flow delivers nothing", "It has no delivery route, so no data leaves the flow.", [], "Add a delivery route, or confirm that the flow is meant only to read."));

        var ordered = items.OrderBy(item => Rank(item.Severity)).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray();
        return new(flow.Id, string.IsNullOrWhiteSpace(flow.Name) ? flow.Id : flow.Name!, flow.Enabled,
            ordered.Count(item => item.Severity == "Blocker"), ordered.Count(item => item.Severity == "Warning"), ordered.Count(item => item.Severity == "Info"),
            ordered, Checked, NotChecked, Disclaimer);
    }

    private static int Rank(string severity) => severity switch { "Blocker" => 0, "Warning" => 1, _ => 2 };

    private static void AddUnresolvedReferences(List<ReviewStep> steps, IReadOnlyDictionary<string, string> configValues, List<ReviewItem> items)
    {
        var unresolved = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var step in steps)
            foreach (var value in step.Definition.Settings.Values)
                foreach (Match match in ConfigValueResolver.TokenPattern().Matches(value))
                {
                    var reference = match.Groups["reference"].Value;
                    if (configValues.ContainsKey(reference)) continue;
                    if (!unresolved.TryGetValue(reference, out var where)) unresolved[reference] = where = new(StringComparer.Ordinal);
                    where.Add(step.Label);
                }

        foreach (var (reference, where) in unresolved)
            items.Add(new("unresolved-reference", "Blocker", $"'{reference}' has no stored value",
                "The flow refers to a config value or secret that has not been entered, so it cannot be enabled or run.", [.. where], "Enter it on the Config values page."));
    }

    private static void AddPlainSecrets(List<ReviewStep> steps, IReadOnlyList<ModuleDescriptor> descriptors, List<ReviewItem> items)
    {
        foreach (var step in steps)
        {
            var descriptor = step.Kind is { } kind ? descriptors.FirstOrDefault(module => module.Kind == kind && string.Equals(module.Id, step.Definition.Module, StringComparison.OrdinalIgnoreCase)) : null;
            var keys = new List<string>();
            foreach (var (key, value) in step.Definition.Settings)
            {
                if (string.IsNullOrWhiteSpace(value) || ConfigValueResolver.TokenPattern().IsMatch(value)) continue;
                var isPassword = descriptor?.Settings.Any(setting => setting.InputKind == ModuleSettingInputKind.Password && string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase)) == true;
                if (isPassword || (SecretKeyPattern().IsMatch(key) && !NotSecretSuffixPattern().IsMatch(key))) keys.Add(key);
            }

            if (keys.Count > 0)
                items.Add(new("plain-secret", "Warning", $"{step.Label} keeps a credential as typed-in text",
                    $"Setting(s) {string.Join(", ", keys.Order(StringComparer.OrdinalIgnoreCase).Select(key => $"'{key}'"))} hold a literal value, so it is stored and exported with the flow.",
                    [step.Label], "Store the value as a secret on the Config values page and refer to it with {{secret:name}}."));

            if (step.Role != ReviewRole.Native) continue;
            foreach (var node in NativeNodes(step.Definition))
            {
                var nodeDescriptor = AssistExplainer.ModuleKindOf(node.Kind) is { } nodeKind && node.Module.Length > 0
                    ? descriptors.FirstOrDefault(module => module.Kind == nodeKind && string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase)) : null;
                var nodeKeys = node.Settings.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && !ConfigValueResolver.TokenPattern().IsMatch(pair.Value)
                        && (nodeDescriptor?.Settings.Any(setting => setting.InputKind == ModuleSettingInputKind.Password && string.Equals(setting.Key, pair.Key, StringComparison.OrdinalIgnoreCase)) == true
                            || (SecretKeyPattern().IsMatch(pair.Key) && !NotSecretSuffixPattern().IsMatch(pair.Key))))
                    .Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (nodeKeys.Length == 0) continue;
                var where = $"{step.Label}, step '{(string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name)}'";
                items.Add(new("plain-secret", "Warning", $"{where} keeps a credential as typed-in text",
                    $"Setting(s) {string.Join(", ", nodeKeys.Select(key => $"'{key}'"))} hold a literal value, so it is stored and exported with the flow.",
                    [where], "Store the value as a secret on the Config values page and refer to it with {{secret:name}}."));
            }
        }
    }

    private static void AddPlaceholders(List<ReviewStep> steps, List<ReviewItem> items)
    {
        foreach (var step in steps)
        {
            var keys = step.Definition.Settings.Where(pair => PlaceholderPattern().IsMatch(pair.Value)).Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (keys.Length > 0)
                items.Add(new("placeholder-left", "Warning", $"{step.Label} still has a placeholder",
                    $"Setting(s) {string.Join(", ", keys.Select(key => $"'{key}'"))} look like they were never filled in.", [step.Label], "Replace the placeholder with the real value."));
        }
    }

    private static void AddSourceDestinationLoops(List<ReviewStep> steps, List<ReviewItem> items)
    {
        var sources = steps.Where(step => step.Role == ReviewRole.Fetch).ToArray();
        var destinations = steps.Where(step => step.Role == ReviewRole.Deliver).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var destination in destinations)
            {
                var shared = Locations(source).Intersect(Locations(destination), StringComparer.Ordinal).ToArray();
                if (shared.Length == 0 || !seen.Add(source.Label + "|" + destination.Label)) continue;
                var keys = source.Definition.Settings.Where(pair => LocationKeyPattern().IsMatch(pair.Key) && shared.Contains(Normalize(pair.Value))).Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase);
                items.Add(new("source-is-destination", "Warning", "A source and a destination may point at the same place",
                    $"{source.Label} and {destination.Label} use the same value for {string.Join(", ", keys.Select(key => $"'{key}'"))}. The flow could read back what it writes.",
                    [source.Label, destination.Label], "Use different locations, or confirm that reading and writing the same place is intended."));
            }
    }

    private static IEnumerable<string> Locations(ReviewStep step)
        => step.Definition.Settings.Where(pair => LocationKeyPattern().IsMatch(pair.Key)).Select(pair => Normalize(pair.Value)).Where(value => value.Length >= 4).Distinct(StringComparer.Ordinal);

    private static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static void AddMissingRetries(PipelineDefinition flow, List<ReviewStep> steps, List<ReviewItem> items)
    {
        var without = new List<string>();
        var unlimited = new List<string>();
        foreach (var step in steps.Where(step => step.Role == ReviewRole.Deliver))
        {
            var retry = step.Definition.Retry ?? flow.Retry;
            if (retry is null || retry.MaxAttempts == 1) without.Add(step.Label);
            else if (retry.MaxAttempts is null) unlimited.Add(step.Label);
        }

        foreach (var step in steps.Where(step => step.Role == ReviewRole.Native))
            without.AddRange(NativeNodes(step.Definition).Where(node => node.Kind == NativeFlowNodeKind.Deliver && node.Retry is null).Select(node => $"{step.Label}, step '{(string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name)}'"));
        if (without.Count > 0)
            items.Add(new("no-retry", "Warning", $"{without.Count} delivery step(s) have no automatic retry",
                "A short outage at the destination fails the run instead of being retried.", [.. without],
                "Set a retry policy on the flow or on the step. For destinations that cannot take the same data twice, leave retry off and rely on the held-run check."));
        if (unlimited.Count > 0)
            items.Add(new("unlimited-retry", "Info", $"{unlimited.Count} delivery step(s) retry without a limit",
                "A destination that keeps failing is retried forever.", [.. unlimited], "Set a maximum number of attempts."));
    }

    private static void AddSourceCleanup(PipelineDefinition flow, List<ReviewStep> steps, IReadOnlyList<ModuleDescriptor> descriptors, List<ReviewItem> items)
    {
        if (flow.Trigger.Mode is PipelineTriggerMode.OnDemand or PipelineTriggerMode.Disabled) return;
        foreach (var step in steps.Where(step => step.Role == ReviewRole.Fetch))
        {
            var descriptor = descriptors.FirstOrDefault(module => module.Kind == ModuleKind.Fetch && string.Equals(module.Id, step.Definition.Module, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null) continue;
            var options = descriptor.Settings.Where(setting => CleanupKeyPattern().IsMatch(setting.Key)).ToArray();
            if (options.Length == 0) continue;
            var anyOn = options.Any(setting =>
            {
                var value = step.Definition.Settings.TryGetValue(setting.Key, out var set) ? set : setting.DefaultValue;
                return !IsOff(value);
            });
            if (!anyOn)
                items.Add(new("no-source-cleanup", "Info", $"{step.Label} does not mark or remove what it has read",
                    $"The module offers {string.Join(", ", options.Select(setting => $"'{setting.Label}'"))}, but none is on, so the same data may be read again on the next run.",
                    [step.Label], "Turn on the setting that matches how this source should be acknowledged, or confirm that reading again is safe."));
        }
    }

    private static bool IsOff(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is "false" or "0" or "no" or "none" or "off" or "leave" or "keep";

    private void AddNativeFindings(List<ReviewStep> steps, IReadOnlyDictionary<string, string> configValues, List<ReviewItem> items)
    {
        foreach (var step in steps.Where(step => step.Role == ReviewRole.Native))
        {
            var json = step.Definition.Settings.GetValueOrDefault("flowJson", "");
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                var resolved = ConfigValueResolver.SubstituteJson(json, configValues, _ => { }, sizeLimit: 1_000_000);
                var explanation = explainer.ExplainFlow(resolved);
                // Module availability is already reported once for the whole flow, so those findings are not repeated per step.
                foreach (var finding in explanation.Findings.Where(finding => finding.Severity == "Warning" && !finding.Text.Contains("installed", StringComparison.OrdinalIgnoreCase)))
                    items.Add(new("native-step", "Warning", finding.Text, "Found while reading the native flow.", finding.NodeId is null ? [] : [$"Native step '{finding.NodeId}'"],
                        "Open the flow in the designer and fix this step."));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
            {
                items.Add(new("native-unreadable", "Warning", "The native flow could not be read", "Its definition is not valid, so it cannot be checked.", [step.Label], "Open the flow in the designer and save it again."));
            }
        }
    }

    private enum ReviewRole { Other, Fetch, Deliver, Native }

    private sealed record ReviewStep(string Label, ModuleKind? Kind, ModuleStepDefinition Definition, ReviewRole Role);

    private static IEnumerable<ReviewStep> Steps(PipelineDefinition flow)
    {
        foreach (var fetch in flow.Fetches)
            yield return new($"Fetch '{fetch.Id}' ({fetch.Fetch.Module})", ModuleKind.Fetch, fetch.Fetch, ReviewRole.Fetch);
        foreach (var parse in flow.Parses)
            yield return new($"Parse '{parse.Id}' ({parse.Parse.Module})", ModuleKind.Parse, parse.Parse, ReviewRole.Other);

        for (var i = 0; i < flow.Augments.Count; i++)
        {
            var augment = flow.Augments[i];
            var isNative = string.Equals(augment.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase);
            var label = $"Augment {i + 1} ({augment.Module})";
            yield return new(label, ModuleKind.OrchestrationAugment, augment, isNative ? ReviewRole.Native : ReviewRole.Other);
        }

        for (var i = 0; i < flow.Deliveries.Count; i++)
        {
            var route = flow.Deliveries[i];
            yield return new($"Render {i + 1} ({route.Render.Module})", ModuleKind.Render, route.Render, ReviewRole.Other);
            for (var j = 0; j < route.Delivers.Count; j++)
            {
                var action = route.Delivers[j];
                yield return new($"Deliver {i + 1}.{j + 1} ({action.Deliver.Module})", ModuleKind.Deliver, action.Deliver, ReviewRole.Deliver);
                if (action.Compensation is not null)
                    yield return new($"Compensation {i + 1}.{j + 1} ({action.Compensation.Module})", ModuleKind.Deliver, action.Compensation, ReviewRole.Other);
            }
        }
    }

    private static IEnumerable<NativeFlowNode> NativeNodes(ModuleStepDefinition augment)
    {
        var json = augment.Settings.GetValueOrDefault("flowJson", "");
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return [.. NativeFlowDefinition.Read(ConfigValueResolver.SubstituteJson(json, null, _ => { }, sizeLimit: 1_000_000), allowBlocked: true).AllNodes()];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
