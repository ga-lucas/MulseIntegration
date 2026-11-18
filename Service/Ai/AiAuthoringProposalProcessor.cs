using System.Text.Json;
using Mulse.Modules.ControlFlow;

namespace Service.Ai;

internal sealed record AiProposalResult(string? ProposalJson, IReadOnlyList<string> Errors);

/// <summary>
/// Turns a model-produced proposal into a trusted, validated flow document. Never saves or mutates anything:
/// identity/category/migration/disabled state are imposed from the draft, withheld setting values are restored by
/// slot token, protections are checked, and the result is validated against the loaded module catalog.
/// </summary>
internal static class AiAuthoringProposalProcessor
{
    private const int MaxSteps = 200;
    private const int MaxSettingValueChars = 1_000_000;
    private const int MaxProposalChars = 2_000_000;

    public static AiProposalResult Process(JsonElement element, AiFlowDoc draft, AiRedactedDraft redacted, IReadOnlyList<ModuleDescriptor> catalog)
    {
        var errors = new List<string>();
        if (element.ValueKind != JsonValueKind.Object) return Fail("The proposal must be a JSON object.");
        if (element.GetRawText().Length > MaxProposalChars) return Fail("The proposal is too large.");

        AiFlowDoc? proposal;
        try { proposal = element.Deserialize<AiFlowDoc>(AiAuthoringJson.Options); }
        catch (JsonException exception) { return Fail($"The proposal does not match the flow schema{(exception.Path is { Length: > 0 } path ? $" (at {path})" : "")}."); }
        if (proposal is null) return Fail("The proposal must be a JSON object.");
        AiAuthoringRedactor.Normalize(proposal);

        // Server-controlled fields: the model has no authority over identity, provenance, enablement or startup runs.
        proposal.Id = draft.Id ?? "";
        proposal.Category = draft.Category;
        proposal.Migration = draft.Migration;
        proposal.Enabled = false;
        proposal.RunOnStartup = false;
        proposal.Name = proposal.Name is { Length: > 0 } name ? name : draft.Name;
        proposal.MissingModules = null;
        ApplyTrigger(proposal, draft, errors);

        var slots = AiAuthoringRedactor.Slots(proposal);
        if (slots.Count > MaxSteps) return Fail($"The proposal has more than {MaxSteps} steps.");
        MergeWithheld(slots, redacted, errors);
        CheckProtections(proposal, redacted, errors);
        ValidateStructure(proposal, errors);
        if (errors.Count == 0) ValidateAgainstCatalog(proposal, catalog, errors);
        if (errors.Count > 0) return new AiProposalResult(null, errors.Distinct().Take(25).ToArray());

        foreach (var slot in slots) slot.Step.Slot = null;
        var fetches = proposal.Fetches!;
        var parses = proposal.Parses!;
        var routes = proposal.Deliveries!;
        proposal.FetchIds = fetches.Select(static fetch => fetch.Id!).ToList();
        proposal.ParseIds = parses.Select(static parse => parse.Id!).ToList();
        proposal.FetchModules = fetches.Select(static fetch => fetch.Fetch!.Module!).ToList();
        proposal.ParseModules = parses.Select(static parse => parse.Parse!.Module!).ToList();
        proposal.AugmentModules = proposal.Augments!.Select(static step => step.Module!).ToList();
        proposal.RenderModules = routes.Select(static route => route.Render!.Module!).ToList();
        proposal.DeliverModules = routes.SelectMany(static route => route.Delivers!).Select(static action => action.Deliver!.Module!).ToList();
        return new AiProposalResult(JsonSerializer.Serialize(proposal, AiAuthoringJson.Options), []);

        AiProposalResult Fail(string message) => new(null, [message]);
    }

    private static void ApplyTrigger(AiFlowDoc proposal, AiFlowDoc draft, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(proposal.TriggerMode))
        {
            proposal.TriggerMode = draft.TriggerMode;
            proposal.Interval = draft.Interval;
        }
        if (string.IsNullOrWhiteSpace(proposal.TriggerMode)) proposal.TriggerMode = nameof(PipelineTriggerMode.OnDemand);
        var matched = Enum.GetNames<PipelineTriggerMode>().FirstOrDefault(value => string.Equals(value, proposal.TriggerMode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (matched is null)
        {
            errors.Add($"Trigger mode must be one of {string.Join(", ", Enum.GetNames<PipelineTriggerMode>())}.");
            return;
        }
        proposal.TriggerMode = matched;
        if (matched != nameof(PipelineTriggerMode.Interval)) proposal.Interval = null;
        else if (proposal.Interval is not { } interval || interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromDays(366))
            errors.Add("An interval trigger requires an interval between 1 second and 366 days.");
    }

    private static void MergeWithheld(List<AiSlot> slots, AiRedactedDraft redacted, List<string> errors)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            var step = slot.Step;
            var settings = step.Settings!;
            AiOriginalSlot? original = null;
            if (!string.IsNullOrWhiteSpace(step.Slot))
            {
                if (!used.Add(step.Slot)) { errors.Add($"Slot '{step.Slot}' is used by more than one step."); continue; }
                if (redacted.Originals.TryGetValue(step.Slot, out var candidate) && candidate.Kind == slot.Kind
                    && string.Equals(candidate.Module, step.Module, StringComparison.OrdinalIgnoreCase)) original = candidate;
            }
            foreach (var key in settings.Where(static pair => pair.Value.Trim() == AiAuthoringRedactor.Placeholder).Select(static pair => pair.Key).ToArray())
            {
                if (original is not null && original.Withheld.TryGetValue(key, out var value)) settings[key] = value;
                else errors.Add($"Setting '{key}' of module '{step.Module}' uses the withheld placeholder but no matching existing step could restore it; keep the step's slot token and module, or provide a new value.");
            }
            if (original is null) continue;
            foreach (var (key, value) in original.Withheld)
                if (!settings.ContainsKey(key)) settings[key] = value;
        }
    }

    private static void CheckProtections(AiFlowDoc proposal, AiRedactedDraft redacted, List<string> errors)
    {
        foreach (var (token, protection) in redacted.Protections)
        {
            if (!protection.RequireConfirmation && !protection.HasCompensation && protection.AtomicScope is null) continue;
            var module = redacted.Originals[token].Module;
            var matches = proposal.Deliveries!.SelectMany(static route => route.Delivers!.Select(action => (route, action)))
                .Where(item => string.Equals(item.action.Deliver!.Slot, token, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !string.Equals(matches[0].action.Deliver!.Module?.Trim(), module, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The proposal would remove, duplicate or recreate the protected deliver '{module}' without its slot token; edit that delivery manually.");
                continue;
            }
            var (route, action) = matches[0];
            if (protection.RequireConfirmation && !action.RequireConfirmation)
                errors.Add($"The proposal would turn off required confirmation for deliver '{module}'; edit that protection manually.");
            if (protection.HasCompensation && action.Compensation is null)
                errors.Add($"The proposal would remove the compensation action of deliver '{module}'; edit that protection manually.");
            if (protection.AtomicScope is not null && !string.Equals(route.AtomicScope?.Trim(), protection.AtomicScope.Trim(), StringComparison.OrdinalIgnoreCase))
                errors.Add($"The proposal would change the atomic scope of the route delivering '{module}'; edit that protection manually.");
        }
    }

    private static void ValidateStructure(AiFlowDoc proposal, List<string> errors)
    {
        if (errors.Count > 0) return;
        if (proposal.Name?.Length > 200) errors.Add("Flow name must be at most 200 characters.");
        var fetches = proposal.Fetches!;
        var parses = proposal.Parses!;
        var augments = proposal.Augments!;
        var routes = proposal.Deliveries!;
        if (fetches.Count is 0 or > 32 || parses.Count is 0 or > 32) errors.Add("A flow needs 1-32 fetch nodes and 1-32 parse nodes.");
        if (augments.Count > 64 || routes.Count > 32 || routes.Any(static route => route.Delivers!.Count is 0 or > 16))
            errors.Add("A flow allows at most 64 augments and 32 delivery routes, each with 1-16 deliver actions.");
        if (routes.Count == 0 && !augments.Any(static step => string.Equals(step.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase)))
            errors.Add("A flow needs at least one delivery route or a native control-flow block.");
        foreach (var slot in AiAuthoringRedactor.Slots(proposal))
        {
            var step = slot.Step;
            if (string.IsNullOrWhiteSpace(step.Module) || step.Module.Length > 256) errors.Add("Every step needs a module id of at most 256 characters.");
            if (step.Name?.Length > 200) errors.Add("Step names must be at most 200 characters.");
            if (step.Settings!.Count > 128 || step.Settings.Any(static pair => pair.Key.Length is 0 or > 128 || pair.Value.Length > MaxSettingValueChars))
                errors.Add($"Step '{step.Module}' has too many settings or an oversized key/value.");
            ValidateRetry(step.Retry, errors);
        }
        ValidateRetry(proposal.Retry, errors);
        if (fetches.Any(static fetch => fetch.Id!.Length > 128) || parses.Any(static parse => parse.Id!.Length > 128)
            || routes.Any(static route => route.AtomicScope?.Length > 128))
            errors.Add("Node ids and atomic scope names must be at most 128 characters.");
    }

    private static void ValidateRetry(AiRetryDoc? retry, List<string> errors)
    {
        if (retry is null) return;
        if (retry.MaxAttempts is < 1 or > 100 || retry.DelaySeconds is < 0 or > 86400 || retry.MaxDelaySeconds is < 0 or > 86400
            || !string.IsNullOrWhiteSpace(retry.Backoff) && !Enum.TryParse<RetryBackoffKind>(retry.Backoff, true, out _) || retry.Backoff is { } backoff && int.TryParse(backoff, out _))
            errors.Add("Retry policies need 1-100 attempts, delays of 0-86400 seconds and a Fixed, Linear or Exponential backoff.");
    }

    private static void ValidateAgainstCatalog(AiFlowDoc proposal, IReadOnlyList<ModuleDescriptor> catalog, List<string> errors)
    {
        foreach (var slot in AiAuthoringRedactor.Slots(proposal))
            if (!catalog.Any(item => item.Kind == slot.Kind && string.Equals(item.Id, slot.Step.Module, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"Unknown {slot.Kind} module '{slot.Step.Module}'; only loaded modules may be used.");
        if (errors.Count > 0) return;

        PipelineDefinition pipeline;
        try { pipeline = ToPipeline(proposal); }
        catch (ArgumentException exception) { errors.Add(exception.Message); return; }

        try { _ = FlowRuntime.OrderSourceGraph(pipeline, message => new ArgumentException(message)); }
        catch (ArgumentException exception) { errors.Add(exception.Message); return; }

        foreach (var parse in pipeline.Parses.Select(static parse => parse.Parse).Concat(pipeline.Augments).Concat(pipeline.Deliveries.Select(static route => route.Render))
            .Concat(pipeline.Deliveries.SelectMany(static route => route.Delivers.Select(static action => action.Deliver))))
        {
            if (!parse.Settings.TryGetValue("conditionJson", out var conditions) || string.IsNullOrWhiteSpace(conditions)) continue;
            try { _ = DecisionRuntime.ParseConditions(conditions, parse.Module); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
            { errors.Add($"Step '{parse.Module}' has invalid conditionJson."); }
        }

        foreach (var augment in pipeline.Augments)
        {
            if (string.Equals(augment.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                try { _ = NativeFlowDefinition.Read(augment.Settings.GetValueOrDefault("flowJson", ""), allowBlocked: true); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or JsonException)
                { errors.Add("The native control-flow block has an invalid flowJson definition."); }
            }
            if (string.Equals(augment.Module, "parsed-data-map-augment", StringComparison.OrdinalIgnoreCase)
                && augment.Settings.GetValueOrDefault("sourceId") is { Length: > 0 } sourceId
                && !pipeline.Parses.Any(parse => string.Equals(parse.Id, sourceId, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"The parsed-data map references missing source '{sourceId}'.");
        }
        if (errors.Count > 0) return;

        try { DurableBatchSafety.Validate(pipeline); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        { errors.Add(exception.Message); return; }

        // Dependencies named inside native/mapping JSON are checked when they can be resolved without reading config values.
        try
        {
            foreach (var (id, kind) in FlowModuleDependencies.References(pipeline, null).ToList())
                if (!catalog.Any(item => item.Kind == kind && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"The flow depends on unknown {kind} module '{id}'.");
        }
        catch (ArgumentException)
        {
            // Needs a config value that is intentionally not read here; the save path re-validates.
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or JsonException)
        {
            errors.Add("The flow's embedded module dependencies could not be validated.");
        }
    }

    internal static PipelineDefinition ToPipeline(AiFlowDoc doc)
    {
        static ModuleStepDefinition Step(AiStepDoc step) => new()
        {
            Module = step.Module!,
            Name = step.Name,
            Settings = new Dictionary<string, string>(step.Settings!, StringComparer.OrdinalIgnoreCase),
            Retry = Retry(step.Retry)
        };
        return new PipelineDefinition
        {
            Id = string.IsNullOrWhiteSpace(doc.Id) ? "draft" : doc.Id,
            Name = doc.Name,
            Enabled = false,
            Trigger = new PipelineTriggerOptions
            {
                Mode = Enum.Parse<PipelineTriggerMode>(doc.TriggerMode ?? nameof(PipelineTriggerMode.OnDemand), true),
                Interval = doc.Interval
            },
            Fetches = doc.Fetches!.Select(fetch => new FetchNodeDefinition { Id = fetch.Id!, Fetch = Step(fetch.Fetch!), InputParseIds = [.. fetch.InputParseIds!] }).ToList(),
            Parses = doc.Parses!.Select(parse => new ParseNodeDefinition { Id = parse.Id!, Parse = Step(parse.Parse!), FetchNodeIds = [.. parse.FetchNodeIds!] }).ToList(),
            Augments = doc.Augments!.Select(Step).ToList(),
            Deliveries = doc.Deliveries!.Select(route => new DeliveryRouteDefinition
            {
                Render = Step(route.Render!),
                AtomicScope = route.AtomicScope,
                Delivers = route.Delivers!.Select(action => new DeliveryActionDefinition
                {
                    Deliver = Step(action.Deliver!),
                    Compensation = action.Compensation is null ? null : Step(action.Compensation),
                    RequireConfirmation = action.RequireConfirmation
                }).ToList()
            }).ToList(),
            Retry = Retry(doc.Retry)
        };
    }

    private static RetryPolicyDefinition? Retry(AiRetryDoc? retry) => retry is null ? null : new RetryPolicyDefinition
    {
        MaxAttempts = retry.MaxAttempts,
        Delay = TimeSpan.FromSeconds(retry.DelaySeconds),
        Backoff = Enum.TryParse<RetryBackoffKind>(retry.Backoff, true, out var kind) ? kind : RetryBackoffKind.Fixed,
        MaxDelay = retry.MaxDelaySeconds is { } max ? TimeSpan.FromSeconds(max) : null
    };
}
