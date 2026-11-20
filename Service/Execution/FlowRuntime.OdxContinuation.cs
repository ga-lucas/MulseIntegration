using Mulse.Modules.BuiltIn.Augment;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    private static bool IsOdxStage(PipelineDefinition pipeline, string stageId)
        => stageId.StartsWith("augment:", StringComparison.Ordinal)
            && int.TryParse(stageId.AsSpan("augment:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < pipeline.Augments.Count
            && pipeline.Augments[index].Module.Equals(OdxProgramAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase);

    private FlowOrchestrationCheckpoint? FindDueOdx(IReadOnlyList<FlowOrchestrationCheckpoint> checkpoints)
        => checkpoints.Where(checkpoint => checkpoint.Odx is { RecoveryPending: false, DeadlineUtc: not null }
                && checkpoint.Odx.DeadlineUtc <= timeProvider.GetUtcNow())
            .OrderBy(checkpoint => checkpoint.Odx!.DeadlineUtc).ThenBy(checkpoint => checkpoint.CorrelationKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private async Task<FlowExecutionResult> ResumeOdxCoreAsync(PipelineDefinition pipeline, FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var index = pipeline.Augments.FindIndex(step => step.Module.Equals(OdxProgramAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || checkpoint.Odx is not { } state || state.ModuleId != pipeline.Augments[index].Module || string.IsNullOrWhiteSpace(state.LastExecutionId))
            throw new InvalidOperationException("Due ODX continuation has no compatible program/execution identity. No input was fetched; inspect its pinned definition.");
        var started = timeProvider.GetUtcNow();
        var context = new FlowExecutionContext(pipeline.Id, state.LastExecutionId, started)
        {
            PendingSourceReceipts = await orchestrationStateStore.GetPendingSourceReceiptsAsync(pipeline.Id, cancellationToken).ConfigureAwait(false)
        };
        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, true);
        logger.LogInformation("Resuming due ODX instance {InstanceId} for flow {FlowId} without fetching or replaying upstream steps.", checkpoint.InstanceId, pipeline.Id);
        try
        {
            return await RunWithSourceOutcomesAsync(pipeline, context,
                () => RunAugmentOnwardAsync(pipeline, context, started, index, IntegrationBatch.Empty, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (FlowRetryScheduledSignal signal) { return signal.Result; }
    }
}
