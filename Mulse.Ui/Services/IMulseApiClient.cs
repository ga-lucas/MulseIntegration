using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public interface IMulseApiClient
{
    Task<IReadOnlyList<ModuleViewModel>> GetModulesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ModulePackageViewModel>> GetModulePackagesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowViewModel>> GetFlowsAsync(CancellationToken cancellationToken);

    Task<FlowViewModel> GetFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowCheckpointViewModel>> GetFlowCheckpointsAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryExecutionViewModel>> GetHistoryExecutionsAsync(string? flowId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support execution history.");

    Task<HistoryExecutionDetailViewModel> GetHistoryExecutionAsync(string executionId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support execution history.");

    Task<IReadOnlyList<HistoryMessageViewModel>> GetHistoryMessagesAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support inbox history.");

    Task<HistoryMessagePayloadViewModel> GetHistoryMessagePayloadAsync(Guid messageId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support message payload inspection.");

    Task ConfirmHistoryDeliveryAsync(string executionId, Guid deliveryId, string confirmation, string evidence, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support delivery confirmation.");

    Task CloseHistoryExecutionAsync(string executionId, string confirmation, string evidence, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support held execution closure.");

    Task<FlowViewModel> CreateFlowAsync(CreateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> UpdateFlowAsync(UpdateFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowDesignAnalysisViewModel> AnalyzeFlowDesignAsync(FlowDesignAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<ParsedDataInspectionViewModel> InspectParsedDataAsync(ParsedDataSampleRequestViewModel request, CancellationToken cancellationToken);

    Task<ParsedDataPreviewViewModel> PreviewParsedDataMappingAsync(ParsedDataPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlMappingPreviewViewModel> PreviewXmlMappingAsync(XmlMappingPreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<NativeFlowTestResponseViewModel> TestNativeFlowAsync(NativeFlowTestRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support isolated native testing.");

    Task<NativeFlowCompareResponseViewModel> CompareNativeFlowAsync(NativeFlowCompareRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support comparing with real output.");

    Task<IReadOnlyList<ComparisonScenarioSummaryViewModel>> ListComparisonScenariosAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support saved comparison scenarios.");

    Task<ComparisonScenarioViewModel> GetComparisonScenarioAsync(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support saved comparison scenarios.");

    Task<ComparisonScenarioSummaryViewModel> SaveComparisonScenarioAsync(SaveComparisonScenarioRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support saved comparison scenarios.");

    Task DeleteComparisonScenarioAsync(string id, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support saved comparison scenarios.");

    Task<NativeFlowCompareResponseViewModel> RunComparisonScenarioAsync(string id, string flowJson, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support saved comparison scenarios.");

    Task<FlowExplanationViewModel> ExplainNativeFlowAsync(string flowJson, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support flow explanations.");

    Task<NodeExplanationViewModel> ExplainNativeNodeAsync(string flowJson, string nodeId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support flow explanations.");

    Task<IReadOnlyList<AssistGlossaryEntryViewModel>> SearchAssistGlossaryAsync(string? search, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support the glossary.");

    Task<ModulePromptViewModel> BuildModulePromptAsync(string flowJson, string moduleId, string? description, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support module prompts.");

    Task<RunExplanationViewModel> ExplainRunAsync(string executionId, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support run explanations.");

    Task<FlowWordingDisclosureViewModel> PreviewFlowWordingAsync(FlowWordingRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI wording of flow explanations.");

    Task<FlowWordingResponseViewModel> FlowWordingAsync(FlowWordingRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI wording of flow explanations.");

    Task<IReadOnlyList<AiActivityEntryViewModel>> GetAiActivityAsync(string? flowId, int take, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support the AI activity list.");

    Task<XmlSchemaTreeViewModel> AnalyzeXmlMappingSchemasAsync(XmlSchemaAnalysisRequestViewModel request, CancellationToken cancellationToken);

    Task<NativeMapConversionViewModel> ImportNativeXmlMappingAsync(XmlMappingImportRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkSolutionAnalysisViewModel> AnalyzeBizTalkSolutionAsync(AnalyzeBizTalkSolutionRequestViewModel request, CancellationToken cancellationToken);

    Task<XmlRulePreviewResultViewModel> PreviewXmlRulesAsync(XmlRulePreviewRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> ImportBizTalkDraftFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkUpdatePlanViewModel> PlanBizTalkFlowUpdateAsync(string flowId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<BizTalkUpdateResultViewModel> UpdateBizTalkFlowAsync(string flowId, UpdateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModulesAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkControlFlowAsync(CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> DownloadBizTalkScaffoldedModuleAsync(string moduleId, CreateImportedBizTalkFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowViewModel> CreateDesignedFlowAsync(CreateDesignedFlowRequestViewModel request, CancellationToken cancellationToken);

    Task<FlowRunViewModel> RunFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowRunViewModel> ReleaseCollectedBatchAsync(string flowId, string instanceId, int expectedPayloadCount, CancellationToken cancellationToken);

    Task<FlowRunViewModel> ReconcileNativeEffectAsync(string flowId, string instanceId, ReconcileNativeEffectRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support native effect reconciliation.");

    Task<FlowViewModel> SetFlowEnabledAsync(string flowId, bool enabled, CancellationToken cancellationToken);

    Task<FlowViewModel> DuplicateFlowAsync(string flowId, string newFlowId, CancellationToken cancellationToken);

    Task<FlowViewModel> SetFlowCategoryAsync(string flowId, string? category, CancellationToken cancellationToken);

    Task DeleteFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfigValueViewModel>> GetConfigValuesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetConfigReferencesAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> GetEncryptionAsync(CancellationToken cancellationToken);

    Task<RuntimeEncryptionViewModel> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken);

    Task<RuntimeStorageViewModel> GetStorageAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage diagnostics.");

    Task<StorageMigrationStatusViewModel> GetStorageMigrationAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage migration.");

    Task<StorageMigrationReportViewModel> RunStorageMigrationAsync(StorageMigrationRequestViewModel request, bool execute, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support storage migration.");

    Task<ProcessingStatusViewModel> GetProcessingAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing controls.");

    Task<ProcessingBacklogViewModel> GetProcessingBacklogAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing backlog diagnostics.");

    Task<ProcessingStatusViewModel> SetMaintenanceAsync(bool requested, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support processing controls.");

    Task<IReadOnlyList<ConfigReferenceUsageViewModel>> GetConfigValueUsagesAsync(CancellationToken cancellationToken);

    Task<ConfigValueViewModel> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken);

    Task DeleteConfigValueAsync(string reference, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> ReloadModulePackageAsync(string packageId, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> SetModulePackageEnabledAsync(string packageId, bool enabled, CancellationToken cancellationToken);

    Task<ModulePackageViewModel> RegisterModulePackageAsync(string id, string assemblyPath, CancellationToken cancellationToken);

    Task<DownloadedFileViewModel> BackupWorkspaceAsync(string password, CancellationToken cancellationToken);

    Task<WorkspaceRestorePreviewViewModel> PreviewWorkspaceRestoreAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken);

    Task<WorkspaceRestorePreviewViewModel> RestoreWorkspaceAsync(RestoreWorkspaceRequestViewModel request, CancellationToken cancellationToken);

    Task<int> DeleteAllFlowsAsync(string confirmation, CancellationToken cancellationToken);

    Task<AiAuthoringSettingsViewModel> GetAiAuthoringSettingsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI authoring.");

    Task<AiAuthoringSettingsViewModel> SetAiAuthoringSettingsAsync(UpdateAiAuthoringSettingsViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI authoring settings.");

    Task<IReadOnlyList<AiAuthoringModelViewModel>> GetCopilotModelsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support Copilot model discovery.");

    Task<AiAuthoringTestViewModel> TestAiAuthoringAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI authoring connection tests.");

    Task<AiAuthoringResponseViewModel> ChatAuthoringAsync(AiAuthoringRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI authoring.");

    Task<ConnectionAnalysisViewModel> AnalyzeConnectionAsync(ConnectionAssistanceRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support connection analysis.");

    Task<ConnectionAssistanceResponseViewModel> AssistConnectionAsync(ConnectionAssistanceRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support connection assistance.");

    Task<BizTalkAssistDisclosureViewModel> PreviewBizTalkAssistAsync(BizTalkAssistRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support BizTalk import assistance.");

    Task<BizTalkAssistResponseViewModel> BizTalkAssistAsync(BizTalkAssistRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support BizTalk import assistance.");

    Task<CompareExplainDisclosureViewModel> PreviewCompareExplainAsync(CompareExplainRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI explanations of comparison differences.");

    Task<CompareExplainResponseViewModel> CompareExplainAsync(CompareExplainRequestViewModel request, CancellationToken cancellationToken)
        => throw new NotSupportedException("This API client does not support AI explanations of comparison differences.");
}
