using System.Text.Json;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

public partial class Designer
{
    private bool _showAssistant;
    private string? _assistantUndoDraft;
    private string? _assistantAppliedDraft;
    private string? _authoringCategory;

    private async Task<string> CaptureAuthoringDraftAsync()
    {
        string? snapshot = null;
        await InvokeAsync(() => snapshot = CaptureAuthoringDraft());
        return snapshot ?? throw new InvalidOperationException("Unable to capture the current draft.");
    }

    private string CaptureAuthoringDraft()
    {
        var id = IsExistingFlow ? FlowId! : _flowId.Trim();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Enter a flow ID before starting the assistant.");
        TimeSpan? interval = null;
        if (_triggerMode == "Interval")
        {
            if (!TimeSpan.TryParse(_intervalText, out var parsed) || parsed <= TimeSpan.Zero)
                throw new InvalidOperationException("Correct the trigger interval before asking for a proposal.");
            interval = parsed;
        }
        if (Routes.Any(route => route.Render is null))
            throw new InvalidOperationException("Complete or remove disconnected delivery routes before asking for a proposal.");
        var draft = new FlowViewModel(id, _enabled, _triggerMode, interval, _runOnStartup,
            [], [], [], [], [], [], [],
            FetchNodes.Select(node => new FlowFetchNodeViewModel(node.GraphId, AuthoringStep(node), node.InputParseIds.ToArray())).ToArray(),
            ParseNodes.Select(node => new FlowParseNodeViewModel(node.GraphId, AuthoringStep(node), node.FetchNodeIds.ToArray())).ToArray(),
            AugmentNodes.Select(AuthoringStep).ToArray(),
            Routes.Select(route => new DeliveryRouteViewModel(AuthoringStep(route.Render!),
                route.Delivers.Select(node => new DeliveryActionViewModel(AuthoringStep(node),
                    node.Compensation is null ? null : new FlowStepViewModel(node.Compensation.Module,
                        node.Compensation.Settings, AuthoringRetry(node.Compensation.Retry), node.Compensation.Name), node.RequireConfirmation)).ToArray(),
                NormalizeAtomicScope(route.Render!.AtomicScope))).ToArray(),
            _hasDefaultRetry ? new(_retryMaxAttempts, _retryDelaySeconds, _retryBackoff, _retryMaxDelaySeconds) : null,
            Category: _authoringCategory, Name: _flowName, Migration: _migration);
        return JsonSerializer.Serialize(draft, JsonOptions);
    }

    private static FlowStepViewModel AuthoringStep(FlowNode node)
        => new(node.ModuleId, new Dictionary<string, string>(node.Settings, StringComparer.OrdinalIgnoreCase),
            AuthoringRetry(node.Retry), node.Name);

    private static RetryPolicyViewModel? AuthoringRetry(RetryPolicyRequestViewModel? retry)
        => retry is null ? null : new(retry.MaxAttempts, retry.DelaySeconds, retry.Backoff, retry.MaxDelaySeconds);

    private void ApplyAuthoringProposal(FlowAuthoringProposal proposal)
    {
        var current = CaptureAuthoringDraft();
        if (current != proposal.BaseDraftJson)
            throw new InvalidOperationException("The draft changed after this proposal was requested. Ask for a new proposal; your current edits were not replaced.");
        var next = JsonSerializer.Deserialize<FlowViewModel>(proposal.ProposalJson, JsonOptions)
            ?? throw new InvalidOperationException("The assistant returned an empty proposal.");
        var baseline = JsonSerializer.Deserialize<FlowViewModel>(current, JsonOptions)!;
        if (next.Id != baseline.Id || next.Category != baseline.Category
            || JsonSerializer.Serialize(next.Migration, JsonOptions) != JsonSerializer.Serialize(baseline.Migration, JsonOptions))
            throw new InvalidOperationException("A proposal cannot change flow identity, customer category or migration provenance.");
        if (next.Fetches is null || next.Parses is null || next.Augments is null || next.Deliveries is null)
            throw new InvalidOperationException("The proposal is missing required flow collections.");
        if (next.Fetches.Any(fetch => fetch is null || string.IsNullOrWhiteSpace(fetch.Id) || !CompleteStep(fetch.Fetch) || fetch.InputParseIds is null)
            || next.Parses.Any(parse => parse is null || string.IsNullOrWhiteSpace(parse.Id) || !CompleteStep(parse.Parse) || parse.FetchNodeIds is null)
            || next.Augments.Any(step => !CompleteStep(step))
            || next.Deliveries.Any(route => route is null || !CompleteStep(route.Render) || route.Delivers is null
                || route.Delivers.Any(action => action is null || !CompleteStep(action.Deliver)
                    || action.Compensation is not null && !CompleteStep(action.Compensation))))
            throw new InvalidOperationException("The proposal contains incomplete steps. The current draft was not replaced.");
        if (next.TriggerMode == "Interval" && next.Interval is not { Ticks: > 0 })
            throw new InvalidOperationException("The proposal has an invalid trigger interval.");
        BuildFromFlow(next with { Enabled = false, RunOnStartup = false });
        _assistantUndoDraft = current;
        _assistantAppliedDraft = CaptureAuthoringDraft();
        _statusMessage = "AI proposal applied to the unsaved canvas. The draft is disabled. Review settings before saving; no flow was saved or executed.";
        _errorMessage = null;
        SetMappingWorkspace(false);
        UpdateUnresolvedConfigCount();
    }

    private static bool CompleteStep(FlowStepViewModel? step)
        => step is not null && !string.IsNullOrWhiteSpace(step.Module) && step.Settings is not null
            && step.Settings.Values.All(value => value is not null)
            && step.Settings.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == step.Settings.Count;

    private void UndoAuthoringProposal()
    {
        if (_assistantUndoDraft is null) return;
        if (_assistantAppliedDraft != CaptureAuthoringDraft())
        {
            _errorMessage = "The draft changed after applying the proposal. Undo was blocked to preserve your newer edits.";
            return;
        }
        var original = JsonSerializer.Deserialize<FlowViewModel>(_assistantUndoDraft, JsonOptions)
            ?? throw new InvalidOperationException("The original draft is unavailable.");
        BuildFromFlow(original);
        _assistantUndoDraft = _assistantAppliedDraft = null;
        UpdateUnresolvedConfigCount();
        _statusMessage = "Restored the original unsaved draft. Nothing was saved or executed.";
    }
}
