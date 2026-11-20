using System.Globalization;
using Mulse.Modules.ControlFlow;
using Service.Models;

namespace Service.BizTalkImport.Origin;

/// <summary>
/// Compares a saved imported flow (C), the origin recorded when it was imported (O) and a fresh analysis (N) piece by piece, and applies a chosen subset in place.
/// Pure: reads the config store only through the supplied lookup and never saves anything.
/// </summary>
internal static class BizTalkUpdatePlanner
{
    private const string ScopeNote = "Only native flow nodes, dispatch choices, flow-level settings and stored config values are tracked. Trigger, fetch, parse, deliver and other augment settings are never changed by an update.";

    internal sealed record Analysis(BizTalkUpdatePlan Plan, BizTalkOriginPieces Fresh, BizTalkOriginPieces Current, IReadOnlyDictionary<string, BizTalkUpdatePieceState> States, MigrationOrigin? Origin);

    internal sealed record ConfigWrite(string Name, string Content, bool Overwrite);

    internal sealed record Applied(PipelineDefinition Pipeline, IReadOnlyList<ConfigWrite> ConfigWrites, IReadOnlyList<string> Replaced, BizTalkUpdatePlan Plan);

    public static string? DraftNativeFlowJson(BizTalkDraftFlowResponse draft)
        => draft.Augments.FirstOrDefault(IsNative) is { } step && step.Settings.TryGetValue("flowJson", out var json) ? json : null;

    public static string? SavedNativeFlowJson(PipelineDefinition pipeline)
        => pipeline.Augments.FirstOrDefault(IsNative) is { } step && step.Settings.TryGetValue("flowJson", out var json) ? json : null;

    private static bool IsNative(BizTalkDraftStepResponse step) => string.Equals(step.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase);

    private static bool IsNative(ModuleStepDefinition step) => string.Equals(step.Module, NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase);

    public static Analysis Plan(PipelineDefinition existing, BizTalkFlowCandidateResponse candidate, Func<string, string?> getConfig)
    {
        var migration = existing.Migration
            ?? throw new InvalidOperationException($"Flow '{existing.Id}' was not created by a BizTalk import, so there is no import to update it from.");
        var origin = migration.Origin;
        var fresh = BizTalkOriginPieces.Read(DraftNativeFlowJson(candidate.DraftFlow), candidate.StoredConfigValues);
        var configNames = fresh.Config.Keys
            .Concat(origin?.Pieces.Keys.Where(static key => key.StartsWith(BizTalkOriginPieces.ConfigPrefix, StringComparison.Ordinal)).Select(static key => key[BizTalkOriginPieces.ConfigPrefix.Length..]) ?? [])
            .Distinct(StringComparer.Ordinal);
        var currentConfig = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in configNames)
            if (getConfig(name) is { } value) currentConfig[name] = value;
        var current = BizTalkOriginPieces.Read(SavedNativeFlowJson(existing), currentConfig);
        var states = new Dictionary<string, BizTalkUpdatePieceState>(StringComparer.Ordinal);
        var notes = new List<string> { ScopeNote };

        if (origin is null)
            return new Analysis(new BizTalkUpdatePlan(existing.Id, BizTalkUpdateMode.NoOrigin,
                "This flow was imported before origin tracking existed, so importer output cannot be told apart from your edits. Import into a new disabled flow and compare, or delete and re-import.",
                null, [], new Dictionary<string, int>(), notes), fresh, current, states, null);

        var freshStructure = fresh.Hashes.GetValueOrDefault(BizTalkOriginPieces.StructureKey);
        var currentStructure = current.Hashes.GetValueOrDefault(BizTalkOriginPieces.StructureKey);
        if (!string.Equals(freshStructure, currentStructure, StringComparison.Ordinal))
        {
            var originStructure = origin.Pieces.GetValueOrDefault(BizTalkOriginPieces.StructureKey);
            var importerChanged = !string.Equals(originStructure, freshStructure, StringComparison.Ordinal);
            var userChanged = !string.Equals(originStructure, currentStructure, StringComparison.Ordinal);
            var reason = (importerChanged, userChanged) switch
            {
                (true, false) => "The importer now produces a different node structure (nodes were added, removed or reordered), so the saved flow cannot be patched node by node.",
                (false, true) => "The saved flow's node structure was edited after import, so importer output cannot be patched into it node by node.",
                _ => "Both the importer's node structure and the saved flow's node structure changed since import, so the nodes cannot be patched node by node."
            };
            var state = Classify(originStructure, currentStructure, freshStructure) ?? BizTalkUpdatePieceState.Conflict;
            states[BizTalkOriginPieces.StructureKey] = state;
            notes.Add("Save the new import as a separate disabled flow (supply a new flow id), review the differences, then move over what you need.");
            return new Analysis(new BizTalkUpdatePlan(existing.Id, BizTalkUpdateMode.NewDraft, reason, origin.ImportedUtc,
                [new BizTalkUpdatePiece(BizTalkOriginPieces.StructureKey, "structure", "Node structure", state, false, false)],
                Count(states, 0), notes), fresh, current, states, origin);
        }

        var unchanged = 0;
        var keys = fresh.Hashes.Keys.Concat(origin.Pieces.Keys).Concat(current.Hashes.Keys).Distinct(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key == BizTalkOriginPieces.StructureKey) continue;
            var state = Classify(origin.Pieces.GetValueOrDefault(key), current.Hashes.GetValueOrDefault(key), fresh.Hashes.GetValueOrDefault(key));
            if (state is null) continue;
            if (state == BizTalkUpdatePieceState.Unchanged) unchanged++;
            else states[key] = state.Value;
        }

        var pieces = states
            .OrderBy(static pair => BizTalkOriginPieces.KindOf(pair.Key), StringComparer.Ordinal).ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BizTalkUpdatePiece(pair.Key, BizTalkOriginPieces.KindOf(pair.Key), LabelOf(pair.Key, fresh, current), pair.Value,
                IsSelectable(pair.Value), pair.Value is BizTalkUpdatePieceState.Add or BizTalkUpdatePieceState.UpdateInPlace or BizTalkUpdatePieceState.AlreadyCurrent))
            .ToArray();
        if (states.ContainsValue(BizTalkUpdatePieceState.Removed))
            notes.Add("Pieces the importer no longer produces are listed for information only; an update never deletes anything from the saved flow.");
        var actionable = states.Values.Any(static state => state is BizTalkUpdatePieceState.Add or BizTalkUpdatePieceState.UpdateInPlace or BizTalkUpdatePieceState.Conflict or BizTalkUpdatePieceState.AlreadyCurrent);
        var mode = actionable ? BizTalkUpdateMode.InPlace : BizTalkUpdateMode.NothingToDo;
        var summary = mode == BizTalkUpdateMode.InPlace
            ? "The saved flow can be updated piece by piece. Pieces you edited are never replaced unless you select them."
            : "Nothing new needs importing; the saved flow already matches the importer's output apart from your own edits.";
        return new Analysis(new BizTalkUpdatePlan(existing.Id, mode, summary, origin.ImportedUtc, pieces, Count(states, unchanged), notes), fresh, current, states, origin);
    }

    public static Applied Apply(PipelineDefinition existing, BizTalkFlowCandidateResponse candidate, Func<string, string?> getConfig, IReadOnlyCollection<string> replace, DateTimeOffset now)
    {
        var analysis = Plan(existing, candidate, getConfig);
        if (analysis.Plan.Mode != BizTalkUpdateMode.InPlace || analysis.Origin is null)
            throw new InvalidOperationException($"Flow '{existing.Id}' cannot be updated in place ({analysis.Plan.Mode}): {analysis.Plan.Reason}");
        var keys = replace.Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) throw new ArgumentException("Select at least one piece to replace.", nameof(replace));
        var selectable = analysis.Plan.Pieces.Where(static piece => piece.Selectable).Select(static piece => piece.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = keys.FirstOrDefault(key => !selectable.Contains(key));
        if (unknown is not null) throw new ArgumentException($"Piece '{unknown}' is not an updatable piece of flow '{existing.Id}'. Request a fresh update plan.", nameof(replace));

        var contentKeys = keys.Where(static key => BizTalkOriginPieces.KindOf(key) != "config").ToArray();
        var augments = existing.Augments.ToList();
        if (contentKeys.Length > 0)
        {
            var index = augments.FindIndex(IsNative);
            var savedJson = index >= 0 ? SavedNativeFlowJson(existing) : null;
            if (savedJson is null) throw new InvalidOperationException($"Flow '{existing.Id}' has no native control flow to update.");
            var patched = BizTalkFlowPatcher.Patch(savedJson, analysis.Fresh, contentKeys);
            _ = NativeFlowDefinition.Read(patched, allowBlocked: true);
            var step = augments[index];
            augments[index] = new ModuleStepDefinition
            {
                Module = step.Module,
                Name = step.Name,
                Retry = step.Retry,
                Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase) { ["flowJson"] = patched }
            };
        }

        var configWrites = keys.Where(static key => BizTalkOriginPieces.KindOf(key) == "config")
            .Select(key => new ConfigWrite(key[BizTalkOriginPieces.ConfigPrefix.Length..], analysis.Fresh.Config[key[BizTalkOriginPieces.ConfigPrefix.Length..]],
                analysis.States[key] != BizTalkUpdatePieceState.Add))
            .ToArray();

        var originPieces = new Dictionary<string, string>(analysis.Origin.Pieces, StringComparer.Ordinal);
        foreach (var key in keys.Concat(analysis.States.Where(static pair => pair.Value == BizTalkUpdatePieceState.AlreadyCurrent).Select(static pair => pair.Key)))
            originPieces[key] = analysis.Fresh.Hashes[key];
        if (analysis.Fresh.Hashes.TryGetValue(BizTalkOriginPieces.StructureKey, out var structure))
            originPieces[BizTalkOriginPieces.StructureKey] = structure;

        var report = candidate.DraftFlow.Migration ?? throw new InvalidOperationException("The fresh analysis carries no migration report.");
        var left = analysis.Plan.Pieces.Count(piece => piece.Selectable && !keys.Contains(piece.Key, StringComparer.Ordinal));
        report = report with
        {
            Origin = new MigrationOrigin(originPieces) { ImportedUtc = now },
            Warnings = [.. report.Warnings, $"Updated in place from a re-import on {now.ToString("u", CultureInfo.InvariantCulture)}: {keys.Length} piece(s) replaced, {left} offered piece(s) left as saved. The flow stays disabled and unverified."]
        };
        var pipeline = FlowMappings.CopyPipeline(existing, existing.Id, false, existing.Category, report, augments);
        return new Applied(pipeline, configWrites, keys, analysis.Plan);
    }

    internal static BizTalkUpdatePieceState? Classify(string? origin, string? current, string? fresh)
    {
        if (fresh is null) return origin is null ? null : BizTalkUpdatePieceState.Removed;
        if (string.Equals(origin, fresh, StringComparison.Ordinal))
            return string.Equals(current, fresh, StringComparison.Ordinal) ? BizTalkUpdatePieceState.Unchanged : BizTalkUpdatePieceState.KeptEdit;
        if (string.Equals(current, fresh, StringComparison.Ordinal)) return BizTalkUpdatePieceState.AlreadyCurrent;
        if (origin is null && current is null) return BizTalkUpdatePieceState.Add;
        return string.Equals(current, origin, StringComparison.Ordinal) ? BizTalkUpdatePieceState.UpdateInPlace : BizTalkUpdatePieceState.Conflict;
    }

    private static bool IsSelectable(BizTalkUpdatePieceState state) => state != BizTalkUpdatePieceState.Removed;

    private static string LabelOf(string key, BizTalkOriginPieces fresh, BizTalkOriginPieces current)
    {
        if (key.StartsWith(BizTalkOriginPieces.ConfigPrefix, StringComparison.Ordinal)) return key[BizTalkOriginPieces.ConfigPrefix.Length..];
        var piece = fresh.Content.GetValueOrDefault(key) ?? current.Content.GetValueOrDefault(key);
        if (piece is null) return key;
        return piece.Kind == "choice" ? $"{piece.Value} (in node {piece.NodeId})" : piece.Label;
    }

    private static Dictionary<string, int> Count(IReadOnlyDictionary<string, BizTalkUpdatePieceState> states, int unchanged)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { [nameof(BizTalkUpdatePieceState.Unchanged)] = unchanged };
        foreach (var state in states.Values)
            counts[state.ToString()] = counts.GetValueOrDefault(state.ToString()) + 1;
        return counts;
    }
}
