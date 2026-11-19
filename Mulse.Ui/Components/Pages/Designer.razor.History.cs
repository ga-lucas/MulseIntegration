using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// Edit history for the flow designer: undo/redo, "unsaved changes", reset to the last loaded/saved state, and reload.
//
// The designer's draft is spread across many fields, and settings editors (including the native control-flow, mapping
// and join editors) mutate it in place, so there is no single change hook. Instead the draft is captured as a snapshot
// after every UI event the page can observe and recorded when it differs from the previous one:
//   - events handled by this component, including EventCallbacks child editors raise to it (IHandleEvent below);
//   - bubbled change/click events from anywhere in the designer surface (child-internal edits);
//   - the JS-invoked canvas callbacks (node drags and toolbox drops).
// A snapshot never includes the viewport, selection, panels, sample data or anything that is not saved with the flow.
public partial class Designer : IHandleEvent
{
    private const long HistoryMaxSize = 50_000_000;

    private EditHistory<DraftSnapshot>? _history;
    private bool _observerOnly;
    private bool _confirmReload;
    private object? _migrationJsonSource;
    private string? _migrationJson;

    private bool CanUndoEdit => _history is { CanUndo: true } && !_isSaving && !_isLoading;

    private bool CanRedoEdit => _history is { CanRedo: true } && !_isSaving && !_isLoading;

    private bool HasUnsavedChanges => _history?.IsDirty == true;

    private string UndoTitle => _history is { CanUndo: true } history ? $"Undo the last change ({history.UndoCount} available). Ctrl+Z" : "Nothing to undo";

    private string RedoTitle => _history is { CanRedo: true } history ? $"Redo the change you undid ({history.RedoCount} available). Ctrl+Y" : "Nothing to redo";

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        _observerOnly = false;
        var task = callback.InvokeAsync(arg);
        var pending = task.Status is not (TaskStatus.RanToCompletion or TaskStatus.Canceled);
        if (pending)
        {
            StateHasChanged();
            return RecordAfterAsync(task, arg);
        }

        var recorded = RecordChange(arg);
        if (!_observerOnly || recorded)
        {
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    private async Task RecordAfterAsync(Task task, object? arg)
    {
        try
        {
            await task;
        }
        catch
        {
            if (task.IsCanceled)
            {
                return;
            }

            throw;
        }

        RecordChange(arg);
        StateHasChanged();
    }

    /// <summary>Bubbled change/click events from the designer surface: records edits made inside child editors.</summary>
    private void ObserveSurfaceEvent() => _observerOnly = true;

    private void ResetHistory()
    {
        _confirmReload = false;
        var initial = CaptureDraft(null);
        if (_history is null)
        {
            _history = new EditHistory<DraftSnapshot>(initial, measure: static snapshot => snapshot.Size, maxSize: HistoryMaxSize);
        }
        else
        {
            _history.Reinitialize(initial);
        }
    }

    private bool RecordChange(object? arg = null)
    {
        if (_history is null || _isLoading)
        {
            return false;
        }

        var next = CaptureDraft(_history.Current);
        return _history.Record(next, arg is ChangeEventArgs ? DescribeEdit(_history.Current, next) : null);
    }

    private void MarkDraftSaved()
    {
        RecordChange();
        _history?.MarkSaved();
    }

    private void UndoEdit()
    {
        if (_history is null || _isSaving || _isLoading) return;
        _confirmReload = false;
        RecordChange();
        if (_history.Undo(out var snapshot))
        {
            RestoreDraft(snapshot);
            _errorMessage = null;
            _statusMessage = "Undid the last change. Nothing was saved.";
        }
    }

    private void RedoEdit()
    {
        if (_history is null || _isSaving || _isLoading) return;
        _confirmReload = false;
        RecordChange();
        if (_history.Redo(out var snapshot))
        {
            RestoreDraft(snapshot);
            _errorMessage = null;
            _statusMessage = "Redid the change. Nothing was saved.";
        }
    }

    /// <summary>Returns the draft to how it was when loaded or last saved. This is an ordinary edit, so Undo brings the changes back.</summary>
    private void ResetEdits()
    {
        if (_history is null || _isSaving || _isLoading) return;
        _confirmReload = false;
        RecordChange();
        if (_history.RevertToBaseline(out var snapshot))
        {
            RestoreDraft(snapshot);
            _errorMessage = null;
            _statusMessage = IsExistingFlow
                ? "Reset to the last loaded or saved state. Undo brings your changes back."
                : "Reset to the starting state. Undo brings your changes back.";
        }
    }

    private async Task RequestReloadAsync()
    {
        if (!IsExistingFlow || _isSaving || _isLoading) return;
        RecordChange();
        if (HasUnsavedChanges)
        {
            _confirmReload = true;
            return;
        }

        await ReloadSavedAsync();
    }

    private void CancelReload() => _confirmReload = false;

    /// <summary>Discards the draft and its history and loads the flow as it is saved on the server. A failed load keeps the draft.</summary>
    private async Task ReloadSavedAsync()
    {
        _confirmReload = false;
        if (!IsExistingFlow) return;
        _errorMessage = null;
        try
        {
            var flow = await ApiClient.GetFlowAsync(FlowId!, CancellationToken.None);
            BuildFromFlow(flow);
            _seedSource = SeedSource.ExistingFlow;
            UpdateUnresolvedConfigCount();
            ResetHistory();
            _statusMessage = "Reloaded the saved flow. Unsaved changes and the undo history were discarded.";
        }
        catch (Exception exception)
        {
            _errorMessage = $"Unable to reload flow '{FlowId}': {exception.Message} Your draft was kept.";
        }
    }

    [JSInvokable]
    public Task OnHistoryShortcut(string action)
    {
        if (action == "undo") UndoEdit();
        else if (action == "redo") RedoEdit();
        StateHasChanged();
        return Task.CompletedTask;
    }

    private DraftSnapshot CaptureDraft(DraftSnapshot? previous)
    {
        var header = JsonSerializer.Serialize(new DraftHeader
        {
            FlowId = _flowId,
            FlowName = _flowName,
            Enabled = _enabled,
            TriggerMode = _triggerMode,
            IntervalText = _intervalText,
            RunOnStartup = _runOnStartup,
            HasDefaultRetry = _hasDefaultRetry,
            RetryMaxAttempts = _retryMaxAttempts,
            RetryDelaySeconds = _retryDelaySeconds,
            RetryBackoff = _retryBackoff,
            RetryMaxDelaySeconds = _retryMaxDelaySeconds,
            Category = _authoringCategory
        }, JsonOptions);

        string? migrationJson = null;
        if (_migration is not null)
        {
            if (!ReferenceEquals(_migrationJsonSource, _migration))
            {
                _migrationJson = JsonSerializer.Serialize(_migration, JsonOptions);
                _migrationJsonSource = _migration;
            }

            migrationJson = _migrationJson;
        }

        var nodes = new string[_nodes.Count];
        for (var index = 0; index < nodes.Length; index++)
        {
            var json = JsonSerializer.Serialize(DraftNode.From(_nodes[index]), JsonOptions);
            nodes[index] = previous is not null && index < previous.Nodes.Length && string.Equals(previous.Nodes[index], json, StringComparison.Ordinal)
                ? previous.Nodes[index]
                : json;
        }

        return new DraftSnapshot(header, migrationJson, nodes);
    }

    private void RestoreDraft(DraftSnapshot snapshot)
    {
        var header = JsonSerializer.Deserialize<DraftHeader>(snapshot.Header, JsonOptions)!;
        var selectedIndex = IndexOfNode(_selectedNodeId);
        var augmentIndex = IndexOfNode(_selectedAugmentNodeId);
        var mappingIndex = IndexOfNode(_selectedMappingNodeId);

        _nodes.Clear();
        _joinEditors.Clear();
        foreach (var json in snapshot.Nodes)
        {
            _nodes.Add(JsonSerializer.Deserialize<DraftNode>(json, JsonOptions)!.ToNode());
        }

        _selectedNodeId = NodeIdAt(selectedIndex);
        _selectedAugmentNodeId = NodeIdAt(augmentIndex);
        _selectedMappingNodeId = NodeIdAt(mappingIndex);
        _connectionFromId = null;

        _flowId = header.FlowId;
        _flowName = header.FlowName;
        _enabled = header.Enabled;
        _triggerMode = header.TriggerMode;
        _intervalText = header.IntervalText;
        _runOnStartup = header.RunOnStartup;
        _hasDefaultRetry = header.HasDefaultRetry;
        _retryMaxAttempts = header.RetryMaxAttempts;
        _retryDelaySeconds = header.RetryDelaySeconds;
        _retryBackoff = header.RetryBackoff;
        _retryMaxDelaySeconds = header.RetryMaxDelaySeconds;
        _authoringCategory = header.Category;

        _migration = snapshot.MigrationJson is null
            ? null
            : JsonSerializer.Deserialize<FlowMigrationReportViewModel>(snapshot.MigrationJson, JsonOptions);
        _migrationJsonSource = _migration;
        _migrationJson = snapshot.MigrationJson;

        // History replaces the one-step AI undo: the proposal itself is an ordinary history entry.
        _assistantUndoDraft = _assistantAppliedDraft = null;
        UpdateUnresolvedConfigCount();
    }

    private int IndexOfNode(string? id)
        => id is null ? -1 : _nodes.FindIndex(node => string.Equals(node.Id, id, StringComparison.Ordinal));

    private string? NodeIdAt(int index) => index >= 0 && index < _nodes.Count ? _nodes[index].Id : null;

    /// <summary>
    /// Names what a single edit touched so consecutive typing in one field is one undo step: the one node that
    /// changed, or the flow settings. Anything broader returns null and is never coalesced.
    /// </summary>
    private static string? DescribeEdit(DraftSnapshot before, DraftSnapshot after)
    {
        if (before.Nodes.Length != after.Nodes.Length || !string.Equals(before.MigrationJson, after.MigrationJson, StringComparison.Ordinal))
        {
            return null;
        }

        var changed = -1;
        for (var index = 0; index < before.Nodes.Length; index++)
        {
            if (string.Equals(before.Nodes[index], after.Nodes[index], StringComparison.Ordinal))
            {
                continue;
            }

            if (changed >= 0)
            {
                return null;
            }

            changed = index;
        }

        return changed >= 0
            ? string.Equals(before.Header, after.Header, StringComparison.Ordinal) ? $"node:{changed}" : null
            : "flow";
    }

    private sealed record DraftSnapshot(string Header, string? MigrationJson, string[] Nodes)
    {
        public long Size => Header.Length + (MigrationJson?.Length ?? 0) + Nodes.Sum(static node => (long)node.Length);

        public bool Equals(DraftSnapshot? other)
            => other is not null
                && string.Equals(Header, other.Header, StringComparison.Ordinal)
                && string.Equals(MigrationJson, other.MigrationJson, StringComparison.Ordinal)
                && Nodes.AsSpan().SequenceEqual(other.Nodes);

        public override int GetHashCode() => HashCode.Combine(Header, MigrationJson, Nodes.Length);
    }

    private sealed class DraftHeader
    {
        public string FlowId { get; init; } = string.Empty;

        public string FlowName { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        public string TriggerMode { get; init; } = "OnDemand";

        public string IntervalText { get; init; } = "00:05:00";

        public bool RunOnStartup { get; init; }

        public bool HasDefaultRetry { get; init; }

        public int? RetryMaxAttempts { get; init; }

        public double RetryDelaySeconds { get; init; }

        public string RetryBackoff { get; init; } = "Fixed";

        public double? RetryMaxDelaySeconds { get; init; }

        public string? Category { get; init; }
    }

    private sealed class DraftNode
    {
        public NodeStage Stage { get; init; }

        public string ModuleId { get; init; } = string.Empty;

        public string? Name { get; init; }

        public Dictionary<string, string> Settings { get; init; } = [];

        public int Order { get; init; }

        public string RouteId { get; init; } = string.Empty;

        public string GraphId { get; init; } = string.Empty;

        public List<string> InputParseIds { get; init; } = [];

        public List<string> FetchNodeIds { get; init; } = [];

        public string? AtomicScope { get; init; }

        public RetryPolicyRequestViewModel? Retry { get; init; }

        public FlowStepRequestViewModel? Compensation { get; init; }

        public bool RequireConfirmation { get; init; }

        public double X { get; init; }

        public double Y { get; init; }

        public static DraftNode From(FlowNode node) => new()
        {
            Stage = node.Stage,
            ModuleId = node.ModuleId,
            Name = node.Name,
            Settings = node.Settings,
            Order = node.Order,
            RouteId = node.RouteId,
            GraphId = node.GraphId,
            InputParseIds = node.InputParseIds,
            FetchNodeIds = node.FetchNodeIds,
            AtomicScope = node.AtomicScope,
            Retry = node.Retry,
            Compensation = node.Compensation,
            RequireConfirmation = node.RequireConfirmation,
            X = node.X,
            Y = node.Y
        };

        public FlowNode ToNode()
        {
            var node = new FlowNode
            {
                Stage = Stage,
                ModuleId = ModuleId,
                Name = Name,
                Settings = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase),
                Order = Order,
                RouteId = RouteId,
                GraphId = GraphId,
                AtomicScope = AtomicScope,
                Retry = Retry,
                Compensation = Compensation is null
                    ? null
                    : Compensation with { Settings = new Dictionary<string, string>(Compensation.Settings, StringComparer.OrdinalIgnoreCase) },
                RequireConfirmation = RequireConfirmation,
                X = X,
                Y = Y
            };
            node.InputParseIds.AddRange(InputParseIds);
            node.FetchNodeIds.AddRange(FetchNodeIds);
            return node;
        }
    }
}
