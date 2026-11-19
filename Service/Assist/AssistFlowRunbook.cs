using System.Text;
using System.Text.RegularExpressions;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Service.ConfigValues;
using Service.Models;
using Service.Modules;

namespace Service.Assist;

/// <summary>
/// The flow runbook (R3): purpose, schedule, inputs, outputs, dependencies, failure behaviour and review state of one stored flow, written from its definition.
/// It is deterministic, needs no AI provider and never includes a setting value, config value or secret: only names, module ids, counts and numbers.
/// Purpose, contact and notes are text the person supplies; Mulse does not invent them.
/// </summary>
public sealed partial class AssistFlowRunbook(IModuleCatalog catalog, AssistExplainer explainer, AssistFlowReviewer reviewer)
{
    public const string Disclaimer = "This runbook lists what the flow definition says. It does not show that the flow is correct or that the destinations behave as described; test the flow with sample data.";

    private const int MaxNativeLines = 60;
    private const int MaxPurposeChars = 600;
    private const int MaxContactChars = 300;
    private const int MaxNotesChars = 3000;
    private const string NotProvided = "(not provided yet; add it in the runbook box and generate again)";

    [GeneratedRegex(@"\{\{(?<kind>config|secret):(?<name>[^{}\r\n]{1,200})\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public FlowRunbook Build(PipelineDefinition flow, IReadOnlyDictionary<string, string> configValues, RunbookRequest? request, DateTimeOffset generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(flow);
        configValues ??= new Dictionary<string, string>();
        var descriptors = catalog.GetAll().Select(entry => entry.Descriptor).ToArray();
        var references = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var modulesUsed = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var title = string.IsNullOrWhiteSpace(flow.Name) ? flow.Id : Clean(flow.Name!, 120);

        var sections = new List<RunbookSection>
        {
            Overview(flow, title),
            new("Purpose", UserLines(request?.Purpose, MaxPurposeChars)),
            Inputs(flow, descriptors, references, modulesUsed),
            Processing(flow, descriptors, references, modulesUsed),
            Outputs(flow, descriptors, references, modulesUsed),
            Dependencies(modulesUsed, references, configValues),
            Failures(flow),
            Review(flow, configValues),
            new("Contact", UserLines(request?.Contact, MaxContactChars)),
            new("Notes", UserLines(request?.Notes, MaxNotesChars))
        };

        return new(flow.Id, title, sections, ToMarkdown(title, sections, generatedUtc), Disclaimer);
    }

    // Passing no values keeps every {{config:...}} and {{secret:...}} token as written, so nothing is ever resolved into the runbook.
    private static string KeepReferences(string json) => ConfigValueResolver.SubstituteJson(json, null, _ => { }, sizeLimit: 1_000_000);

    private static RunbookSection Overview(PipelineDefinition flow, string title)
    {
        var lines = new List<string>
        {
            $"Flow: {title} (id '{Clean(flow.Id, 120)}')",
            flow.Enabled ? "State: enabled" : "State: disabled (it will not start until someone enables it)",
            $"Starts: {TriggerText(flow.Trigger)}"
        };
        if (!string.IsNullOrWhiteSpace(flow.Category)) lines.Add($"Category: {Clean(flow.Category!, 80)}");
        if (flow.Migration is not null) lines.Add("Origin: imported from another system and not yet verified. Imported flows stay disabled until they are rebuilt with native steps and saved as a new version.");
        return new("Overview", lines);
    }

    private static string TriggerText(PipelineTriggerOptions trigger) => trigger.Mode switch
    {
        PipelineTriggerMode.Disabled => "Automatic starting is switched off.",
        PipelineTriggerMode.OnDemand => "Only when someone starts it.",
        PipelineTriggerMode.Interval => trigger.Interval is { } interval
            ? $"Automatically every {Duration(interval)}{(trigger.RunOnStartup ? ", and once when the service starts" : "")}."
            : "Automatically, but no interval is set.",
        PipelineTriggerMode.Push => "When an event arrives at a source that supports push (for example a file watcher).",
        _ => "Unknown."
    };

    private RunbookSection Inputs(PipelineDefinition flow, ModuleDescriptor[] descriptors, SortedDictionary<string, SortedSet<string>> references, SortedDictionary<string, string> modulesUsed)
    {
        var lines = new List<string>();
        foreach (var fetch in flow.Fetches)
        {
            var label = StepLabel(fetch.Fetch, $"Source '{Clean(fetch.Id, 80)}'");
            Track(ModuleKind.Fetch, fetch.Fetch.Module, descriptors, modulesUsed);
            lines.Add($"{label}: reads with {ModuleLabel(ModuleKind.Fetch, fetch.Fetch.Module, descriptors)}. {SettingsText(fetch.Fetch, label, references)}");
            if (fetch.InputParseIds.Count > 0) lines.Add($"{label} is keyed off the parsed output of: {string.Join(", ", fetch.InputParseIds.Select(id => Clean(id, 80)))}.");
        }

        foreach (var parse in flow.Parses)
        {
            var label = StepLabel(parse.Parse, $"Parse '{Clean(parse.Id, 80)}'");
            Track(ModuleKind.Parse, parse.Parse.Module, descriptors, modulesUsed);
            lines.Add($"{label}: reads what {(parse.FetchNodeIds.Count > 0 ? string.Join(", ", parse.FetchNodeIds.Select(id => $"'{Clean(id, 80)}'")) : "no source")} fetched, using {ModuleLabel(ModuleKind.Parse, parse.Parse.Module, descriptors)}. {SettingsText(parse.Parse, label, references)}");
        }

        if (lines.Count == 0) lines.Add("The flow has no source steps.");
        return new("Inputs", lines);
    }

    private RunbookSection Processing(PipelineDefinition flow, ModuleDescriptor[] descriptors, SortedDictionary<string, SortedSet<string>> references, SortedDictionary<string, string> modulesUsed)
    {
        var lines = new List<string>();
        for (var index = 0; index < flow.Augments.Count; index++)
        {
            var augment = flow.Augments[index];
            var label = StepLabel(augment, $"Step {index + 1}");
            if (string.Equals(augment.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{label}: a native control-flow block.");
                AddNative(augment, label, descriptors, references, modulesUsed, lines);
                continue;
            }

            Track(ModuleKind.OrchestrationAugment, augment.Module, descriptors, modulesUsed);
            lines.Add($"{label}: {ModuleLabel(ModuleKind.OrchestrationAugment, augment.Module, descriptors)}. {SettingsText(augment, label, references)}");
        }

        if (lines.Count == 0) lines.Add("No processing steps: what is read is passed straight to delivery.");
        return new("Processing", lines);
    }

    private void AddNative(ModuleStepDefinition augment, string label, ModuleDescriptor[] descriptors, SortedDictionary<string, SortedSet<string>> references,
        SortedDictionary<string, string> modulesUsed, List<string> lines)
    {
        var json = augment.Settings.GetValueOrDefault("flowJson", "");
        if (string.IsNullOrWhiteSpace(json))
        {
            lines.Add($"{label}: it has no definition.");
            return;
        }

        try
        {
            var resolved = KeepReferences(json);
            var explanation = explainer.ExplainFlow(resolved);
            foreach (var step in explanation.Steps.Take(MaxNativeLines))
                lines.Add($"{new string(' ', Math.Min(step.Depth, 6) * 2)}- {Clean(step.Title, 120)}: {Clean(step.Summary, 240)}");
            if (explanation.Steps.Count > MaxNativeLines) lines.Add($"...and {explanation.Steps.Count - MaxNativeLines} more native step(s).");
            if (explanation.Durable) lines.Add("The native flow is durable: its state is kept while it waits.");
            foreach (var reference in explanation.ConfigReferences ?? [])
                AddReference(references, reference.Kind, reference.Name, $"{label}, native steps");
            foreach (var node in NativeFlowDefinition.Read(resolved, allowBlocked: true).AllNodes().Where(node => node.Module.Length > 0 && AssistExplainer.ModuleKindOf(node.Kind) is not null))
                Track(AssistExplainer.ModuleKindOf(node.Kind)!.Value, node.Module, descriptors, modulesUsed);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            lines.Add($"{label}: its native definition could not be read, so its steps are not listed.");
        }
    }

    private RunbookSection Outputs(PipelineDefinition flow, ModuleDescriptor[] descriptors, SortedDictionary<string, SortedSet<string>> references, SortedDictionary<string, string> modulesUsed)
    {
        var lines = new List<string>();
        for (var route = 0; route < flow.Deliveries.Count; route++)
        {
            var delivery = flow.Deliveries[route];
            var name = $"Route {route + 1}";
            Track(ModuleKind.Render, delivery.Render.Module, descriptors, modulesUsed);
            lines.Add($"{name}: builds the message with {ModuleLabel(ModuleKind.Render, delivery.Render.Module, descriptors)}. {SettingsText(delivery.Render, $"{name} render", references)}");
            if (!string.IsNullOrWhiteSpace(delivery.AtomicScope)) lines.Add($"{name} is in the atomic scope '{Clean(delivery.AtomicScope!, 80)}': its deliveries succeed or are undone together where an undo step exists.");
            for (var index = 0; index < delivery.Delivers.Count; index++)
            {
                var action = delivery.Delivers[index];
                var label = StepLabel(action.Deliver, $"{name}, destination {index + 1}");
                Track(ModuleKind.Deliver, action.Deliver.Module, descriptors, modulesUsed);
                lines.Add($"{label}: sends with {ModuleLabel(ModuleKind.Deliver, action.Deliver.Module, descriptors)}. {SettingsText(action.Deliver, label, references)}");
                if (action.RequireConfirmation) lines.Add($"{label} waits for a person to confirm before it sends.");
                if (action.Compensation is { } undo)
                {
                    Track(ModuleKind.Deliver, undo.Module, descriptors, modulesUsed);
                    lines.Add($"{label} has an undo (compensation) step: {ModuleLabel(ModuleKind.Deliver, undo.Module, descriptors)}.");
                }
            }
        }

        if (lines.Count == 0) lines.Add("The flow has no delivery routes. Any destinations are inside its native steps (see Processing).");
        return new("Outputs", lines);
    }

    private static RunbookSection Dependencies(SortedDictionary<string, string> modulesUsed, SortedDictionary<string, SortedSet<string>> references, IReadOnlyDictionary<string, string> configValues)
    {
        var lines = new List<string>();
        foreach (var (_, description) in modulesUsed) lines.Add($"Module: {description}");
        foreach (var (key, where) in references)
        {
            var separator = key.IndexOf(':', StringComparison.Ordinal);
            var kind = key[..separator];
            var name = key[(separator + 1)..];
            lines.Add($"{(kind == "secret" ? "Secret" : "Config value")} '{name}': {(configValues.ContainsKey(name) ? "has a stored value" : "has NO stored value yet")}; used by {string.Join("; ", where.Take(5))}{(where.Count > 5 ? $"; and {where.Count - 5} more" : "")}.");
        }

        if (lines.Count == 0) lines.Add("No modules or stored config values or secrets are referenced.");
        return new("Dependencies", lines);
    }

    private RunbookSection Failures(PipelineDefinition flow)
    {
        var lines = new List<string> { $"Flow default retry: {RetryText(flow.Retry)}." };
        for (var route = 0; route < flow.Deliveries.Count; route++)
            for (var index = 0; index < flow.Deliveries[route].Delivers.Count; index++)
            {
                var action = flow.Deliveries[route].Delivers[index];
                var own = action.Deliver.Retry;
                lines.Add($"{StepLabel(action.Deliver, $"Route {route + 1}, destination {index + 1}")}: {(own is null ? $"follows the flow default ({RetryText(flow.Retry)})" : RetryText(own))}.");
            }

        foreach (var augment in flow.Augments.Where(augment => string.Equals(augment.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase)))
            foreach (var node in NativeDeliverNodes(augment))
                lines.Add(node.Retry is { } retry
                    ? $"Native step '{Clean(string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name, 80)}': up to {retry.Attempts} more attempt(s), {Duration(TimeSpan.FromSeconds(retry.IntervalSeconds))} apart, {(retry.RetryAmbiguous ? "including after an ambiguous failure, which could send the data twice" : "only when the module shows nothing was sent")}."
                    : $"Native step '{Clean(string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name, 80)}': no automatic retry.");

        lines.Add("Every run is recorded in History. Open a run and use Explain this run to see what each state means and what to check by hand.");
        return new("Failure behavior", lines);
    }

    private static IEnumerable<NativeFlowNode> NativeDeliverNodes(ModuleStepDefinition augment)
    {
        var json = augment.Settings.GetValueOrDefault("flowJson", "");
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return [.. NativeFlowDefinition.Read(KeepReferences(json), allowBlocked: true).AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Deliver)];
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private RunbookSection Review(PipelineDefinition flow, IReadOnlyDictionary<string, string> configValues)
    {
        var review = reviewer.Review(flow, configValues);
        var lines = new List<string> { $"The pre-enable review found {review.Blockers} blocker(s), {review.Warnings} warning(s) and {review.Notes} note(s)." };
        foreach (var item in review.Items.Where(item => item.Severity is "Blocker" or "Warning").Take(15))
            lines.Add($"{item.Severity}: {Clean(item.Title, 200)}");
        lines.Add("Not checked: " + string.Join(" ", review.NotChecked));
        return new("Before relying on it", lines);
    }

    private static string RetryText(RetryPolicyDefinition? retry)
    {
        if (retry is null || retry.MaxAttempts == 1) return "no automatic retry (one attempt)";
        var attempts = retry.MaxAttempts is { } max ? $"up to {max} attempts" : "no limit on attempts";
        var cap = retry.MaxDelay is { } maxDelay ? $", capped at {Duration(maxDelay)}" : "";
        return $"{attempts}, {Duration(retry.Delay)} apart ({retry.Backoff.ToString().ToLowerInvariant()}{cap})";
    }

    private string SettingsText(ModuleStepDefinition step, string label, SortedDictionary<string, SortedSet<string>> references)
    {
        var keys = new List<string>();
        foreach (var (key, value) in step.Settings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            keys.Add(Clean(key, 60));
            foreach (Match match in ReferencePattern().Matches(value))
                AddReference(references, match.Groups["kind"].Value, match.Groups["name"].Value, label);
        }

        return keys.Count == 0 ? "No settings are filled in." : $"Settings in use: {string.Join(", ", keys)} (values are not included here).";
    }

    private static void AddReference(SortedDictionary<string, SortedSet<string>> references, string kind, string name, string where)
    {
        var key = $"{kind.ToLowerInvariant()}:{name.Trim()}";
        if (!references.TryGetValue(key, out var set)) references[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(where);
    }

    private static void Track(ModuleKind kind, string module, ModuleDescriptor[] descriptors, SortedDictionary<string, string> modulesUsed)
    {
        if (string.IsNullOrWhiteSpace(module)) return;
        modulesUsed[$"{kind}:{module.ToLowerInvariant()}"] = $"{ModuleLabel(kind, module, descriptors)} [{kind}]";
    }

    private static string ModuleLabel(ModuleKind kind, string module, ModuleDescriptor[] descriptors)
    {
        if (string.IsNullOrWhiteSpace(module)) return "(no module chosen)";
        var descriptor = descriptors.FirstOrDefault(candidate => candidate.Kind == kind && string.Equals(candidate.Id, module, StringComparison.OrdinalIgnoreCase));
        return descriptor is null ? $"'{Clean(module, 80)}' (not installed on this server)" : $"{Clean(descriptor.DisplayName, 80)} ('{Clean(module, 80)}')";
    }

    private static string StepLabel(ModuleStepDefinition step, string fallback)
        => string.IsNullOrWhiteSpace(step.Name) ? fallback : $"{Clean(step.Name!, 80)} [{fallback}]";

    private static List<string> UserLines(string? text, int limit)
    {
        var cleaned = Clean(text ?? "", limit, keepNewlines: true);
        if (cleaned.Length == 0) return [NotProvided];
        return [.. cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "0 seconds";
        if (span.TotalDays >= 1 && span.TotalDays % 1 == 0) return Plural(span.TotalDays, "day");
        if (span.TotalHours >= 1 && span.TotalHours % 1 == 0) return Plural(span.TotalHours, "hour");
        if (span.TotalMinutes >= 1 && span.TotalMinutes % 1 == 0) return Plural(span.TotalMinutes, "minute");
        if (span.TotalSeconds % 1 == 0) return Plural(span.TotalSeconds, "second");
        return span.ToString("c");
    }

    private static string Plural(double value, string unit) => $"{value:0} {unit}{(value == 1 ? "" : "s")}";

    private static string Clean(string value, int limit, bool keepNewlines = false)
    {
        var builder = new StringBuilder(Math.Min(value.Length, limit));
        foreach (var character in value)
        {
            if (builder.Length >= limit) { builder.Append("..."); break; }
            if (character == '\n' && keepNewlines) builder.Append('\n');
            else if (character == '\r' && keepNewlines) continue;
            else builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static string ToMarkdown(string title, List<RunbookSection> sections, DateTimeOffset generatedUtc)
    {
        var markdown = new StringBuilder();
        markdown.Append("# Runbook: ").Append(title).Append("\n\n");
        markdown.Append("_Generated ").Append(generatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append(" UTC from the flow definition._\n\n");
        foreach (var section in sections)
        {
            markdown.Append("## ").Append(section.Title).Append("\n\n");
            foreach (var line in section.Lines) markdown.Append(line.StartsWith(' ') || line.StartsWith("- ", StringComparison.Ordinal) ? line : "- " + line).Append('\n');
            markdown.Append('\n');
        }

        markdown.Append("---\n").Append(Disclaimer).Append('\n');
        return markdown.ToString();
    }
}
