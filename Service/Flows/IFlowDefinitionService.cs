using Mulse.Modules;

namespace Service.Flows;

public interface IFlowDefinitionService
{
    IReadOnlyList<PipelineDefinition> GetAll();

    PipelineDefinition GetById(string flowId);

    Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task<PipelineDefinition> UpdateAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task<PipelineDefinition> DuplicateAsync(string flowId, string newFlowId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a server-computed re-import of an imported flow, including its fresh migration report. Unlike <see cref="UpdateAsync"/>, which never lets a client replace the
    /// saved provenance, this is only for the BizTalk update endpoint. The result is always saved disabled and goes through the same validation and module pinning.
    /// </summary>
    Task<PipelineDefinition> ReplaceImportedAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task DeleteAsync(string flowId, CancellationToken cancellationToken);
}
