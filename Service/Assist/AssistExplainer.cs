using System.Globalization;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Service.Models;
using Service.Modules;

namespace Service.Assist;

/// <summary>
/// Deterministic explanation of a native flow and of one step. Everything here is computed from the flow and the installed module descriptors:
/// it needs no AI provider, sends nothing anywhere, and reports setting values only as "set" or "not set", never the values themselves.
/// </summary>
public sealed class AssistExplainer(IModuleCatalog catalog)
{
    public const string Disclaimer = "These are facts read from the flow definition and the installed modules. They do not show that the flow is correct.";

    public FlowExplanation ExplainFlow(string flowJson)
    {
        var flow = Read(flowJson);
        var modules = Modules();
        var steps = new List<AssistStep>();
        var findings = new List<AssistFinding>();
        var effects = new List<string>();
        Walk(flow.Nodes, 0, modules, steps, findings, effects);

        var all = flow.AllNodes().ToList();
        var counts = all.GroupBy(node => node.Kind.ToString()).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (all.Count > 0 && !all.Any(node => node.Kind == NativeFlowNodeKind.Deliver))
            findings.Add(new("Info", "The flow has no Deliver step, so nothing is sent to a destination.", null));

        return new(flow.Durable, all.Count, counts, steps, effects, findings, Disclaimer, MissingOf(all, modules));
    }

    private static List<AssistMissingModule> MissingOf(IEnumerable<NativeFlowNode> nodes, IReadOnlyList<ModuleDescriptor> modules)
        => [.. nodes
            .Where(node => ModuleKindOf(node.Kind) is not null && node.Module.Length > 0 && !modules.Any(module => string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(node => (Module: node.Module.ToLowerInvariant(), Kind: ModuleKindOf(node.Kind)!.Value))
            .OrderBy(group => group.Key.Module, StringComparer.Ordinal)
            .Select(group => new AssistMissingModule(group.First().Module, group.Key.Kind.ToString(), [.. group.Select(node => node.Id)]))];

    public NodeExplanation ExplainNode(string flowJson, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("A node id is required.");
        var flow = Read(flowJson);
        var node = flow.AllNodes().FirstOrDefault(candidate => string.Equals(candidate.Id, nodeId, StringComparison.Ordinal)) ?? throw new KeyNotFoundException($"The flow has no step '{nodeId}'.");
        var modules = Modules();
        var findings = new List<AssistFinding>();
        var step = Describe(node, 0, modules, findings, new List<string>());
        var kind = NativeNodeCatalog.For(node.Kind);
        var descriptor = FindDescriptor(node, modules);

        var settings = new List<AssistSettingState>();
        if (descriptor is not null)
        {
            foreach (var setting in descriptor.Settings)
                settings.Add(new(setting.Key, setting.Label, setting.Description, setting.IsRequired, StateOf(node, setting)));
            foreach (var key in node.Settings.Keys.Where(key => descriptor.Settings.All(setting => !string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
                settings.Add(new(key, key, "The module does not describe this setting.", false, "set, but not described by the module"));
        }

        var facts = new List<string> { kind.Summary, $"Effect: {kind.Effect}. {kind.SideEffects}" };
        if (node.Children.Count > 0) facts.Add($"It contains {node.Children.Count} step(s) directly.");
        if (node.Otherwise.Count > 0) facts.Add($"Its Otherwise part has {node.Otherwise.Count} step(s).");
        if (node.Conditions.Count > 0 || node.Expression is not null)
            facts.Add($"It decides using {(node.Conditions.Count > 0 ? $"{node.Conditions.Count} condition(s)" : "an expression")}. The text is not repeated here; open the step to read it.");
        if (node.MessageName.Length > 0) facts.Add($"It works with the message named '{node.MessageName}'.");
        if (node.Channel.Length > 0) facts.Add($"Channel '{node.Channel}'" + (node.CorrelationKeys.Count > 0 ? $", matched on {string.Join(", ", node.CorrelationKeys)}." : "."));
        if (node.Kind is NativeFlowNodeKind.ForEach or NativeFlowNodeKind.While) facts.Add($"It repeats at most {node.MaxIterations} time(s).");
        if (flow.Durable && node.Kind is NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive or NativeFlowNodeKind.Listen) facts.Add("The flow is durable, so its state is kept while it waits here.");

        return new(step, kind, descriptor?.Description, settings, facts, findings);
    }

    private static NativeFlowDefinition Read(string flowJson)
    {
        if (string.IsNullOrWhiteSpace(flowJson)) throw new ArgumentException("The native flow definition is required.");
        return NativeFlowDefinition.Read(flowJson, allowBlocked: true);
    }

    private IReadOnlyList<ModuleDescriptor> Modules() => [.. catalog.GetAll().Select(entry => entry.Descriptor)];

    internal static ModuleKind? ModuleKindOf(NativeFlowNodeKind kind) => kind switch
    {
        NativeFlowNodeKind.Augment => ModuleKind.OrchestrationAugment,
        NativeFlowNodeKind.Render => ModuleKind.Render,
        NativeFlowNodeKind.Deliver => ModuleKind.Deliver,
        NativeFlowNodeKind.InvokeTransform => ModuleKind.Transform,
        _ => null
    };

    private static ModuleDescriptor? FindDescriptor(NativeFlowNode node, IReadOnlyList<ModuleDescriptor> modules)
        => ModuleKindOf(node.Kind) is { } kind && node.Module.Length > 0
            ? modules.FirstOrDefault(module => module.Kind == kind && string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase))
            : null;

    private static void Walk(List<NativeFlowNode> nodes, int depth, IReadOnlyList<ModuleDescriptor> modules, List<AssistStep> steps, List<AssistFinding> findings, List<string> effects)
    {
        foreach (var node in nodes)
        {
            var step = Describe(node, depth, modules, findings, effects);
            steps.Add(step);
            Walk(node.Children, depth + 1, modules, steps, findings, effects);
            if (node.Otherwise.Count > 0)
            {
                steps.Add(new(depth + 1, node.Id + ":otherwise", "Otherwise", node.Kind == NativeFlowNodeKind.Try ? "Handler" : "Otherwise", null, null,
                    node.Kind == NativeFlowNodeKind.Try ? "Runs when a step above fails." : "Runs when the conditions above do not hold.", []));
                Walk(node.Otherwise, depth + 2, modules, steps, findings, effects);
            }
        }
    }

    private static AssistStep Describe(NativeFlowNode node, int depth, IReadOnlyList<ModuleDescriptor> modules, List<AssistFinding> findings, List<string> effects)
    {
        var kind = NativeNodeCatalog.For(node.Kind);
        var title = string.IsNullOrWhiteSpace(node.Name) ? kind.Title : node.Name.Trim();
        var notes = new List<string>();
        var descriptor = FindDescriptor(node, modules);
        var moduleKind = ModuleKindOf(node.Kind);

        if (moduleKind is not null && node.Module.Length > 0 && descriptor is null)
        {
            var other = modules.FirstOrDefault(module => string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                notes.Add($"Module '{node.Module}' is installed as a {other.Kind} module, not a {moduleKind}.");
                findings.Add(new("Warning", $"'{title}' needs '{node.Module}' as a {moduleKind} module, but the installed module of that name is a {other.Kind} module.", node.Id));
            }
            else
            {
                notes.Add($"Module '{node.Module}' is not installed.");
                findings.Add(new("Warning", $"'{title}' uses module '{node.Module}', which is not installed here, so the flow cannot run until it is.", node.Id));
            }
        }

        if (descriptor is not null) CheckSettings(node, title, descriptor, notes, findings);

        switch (node.Kind)
        {
            case NativeFlowNodeKind.Blocked:
                notes.Add($"Not translated: {Clip(node.BlockReason)}");
                findings.Add(new("Warning", $"'{title}' is not translated ({Clip(node.BlockReason)}). The flow cannot run until it is replaced.", node.Id));
                break;
            case NativeFlowNodeKind.Deliver:
                effects.Add(DeliverEffect(node, title, descriptor));
                if (node.Retry is null) notes.Add("No automatic retry.");
                else
                {
                    notes.Add(RetryText(node.Retry));
                    if (node.Retry.RetryAmbiguous) findings.Add(new("Warning", $"'{title}' retries even when the destination may already have received the data, which can send it twice.", node.Id));
                }
                if (node.SettingExpressions is { Count: > 0 }) notes.Add($"{node.SettingExpressions.Count} setting(s) are calculated at send time.");
                break;
            case NativeFlowNodeKind.Wait:
                var wait = WaitText(node);
                notes.Add(wait);
                if (node.DurationExpression is null && node.DurationSeconds >= 1) findings.Add(new("Info", $"'{title}' waits {wait.ToLowerInvariant()}, which parks the flow until its next run.", node.Id));
                break;
            case NativeFlowNodeKind.Terminate:
                findings.Add(new("Info", $"'{title}' stops the flow with an error when reached.", node.Id));
                break;
            case NativeFlowNodeKind.Reply:
                effects.Add($"Replies to the caller with message '{node.MessageName}' ('{title}').");
                break;
        }

        return new(depth, node.Id, node.Kind.ToString(), title, node.Module.Length > 0 ? node.Module : null, descriptor?.DisplayName, kind.Summary, notes);
    }

    private static void CheckSettings(NativeFlowNode node, string title, ModuleDescriptor descriptor, List<string> notes, List<AssistFinding> findings)
    {
        foreach (var setting in descriptor.Settings)
        {
            var state = StateOf(node, setting);
            if (setting.IsRequired && state == "not set")
            {
                notes.Add($"Required setting '{setting.Label}' has no value.");
                findings.Add(new("Warning", $"'{title}' has no value for the required setting '{setting.Label}'.", node.Id));
            }
            else if (state == "set to a literal value" && setting.InputKind == ModuleSettingInputKind.Password)
            {
                notes.Add($"'{setting.Label}' holds a typed-in value instead of a secret reference.");
                findings.Add(new("Warning", $"'{title}' keeps '{setting.Label}' as a typed-in value. Store it as a secret and refer to it instead.", node.Id));
            }
        }
    }

    private static string StateOf(NativeFlowNode node, ModuleSettingDescriptor setting)
    {
        if (node.SettingExpressions is { } expressions && expressions.Keys.Any(key => string.Equals(key, setting.Key, StringComparison.OrdinalIgnoreCase))) return "calculated at send time";
        if (!node.Settings.TryGetValue(setting.Key, out var value) || string.IsNullOrWhiteSpace(value))
            return setting.DefaultValue is { Length: > 0 } ? "not set (the module's default applies)" : "not set";
        return AssistRedactor.ValueState(value);
    }

    private static string DeliverEffect(NativeFlowNode node, string title, ModuleDescriptor? descriptor)
    {
        var target = descriptor?.DisplayName ?? node.Module;
        var text = $"Sends data with {target} ('{title}')";
        if (node.MessageName.Length > 0) text += $", message '{node.MessageName}'";
        return text + (node.Retry is null ? ", with no automatic retry." : $", {RetryText(node.Retry).ToLowerInvariant()}");
    }

    private static string RetryText(NativeDeliverRetry retry)
        => string.Create(CultureInfo.InvariantCulture, $"Retries up to {retry.Attempts} more time(s), {retry.IntervalSeconds} second(s) apart; a send of unknown outcome is {(retry.RetryAmbiguous ? "also retried (it can duplicate data)" : "not retried")}.");

    private static string WaitText(NativeFlowNode node)
    {
        if (node.DurationExpression is not null) return "A calculated number of seconds";
        var seconds = node.DurationSeconds;
        var millis = node.DurationMilliseconds;
        return string.Create(CultureInfo.InvariantCulture, $"{(millis > 0 ? $"{seconds}.{millis:000}" : seconds.ToString(CultureInfo.InvariantCulture))} second(s)");
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
