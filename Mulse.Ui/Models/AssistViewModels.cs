namespace Mulse.Ui.Models;

public sealed record AssistNodeKindViewModel(string Kind, string Title, string Summary, string Effect, string SideEffects, List<string> KeySettings);
public sealed record AssistGlossaryEntryViewModel(string Term, string Meaning, string InMulse, List<string> Aliases);
public sealed record AssistStepViewModel(int Depth, string Id, string Kind, string Title, string? Module, string? ModuleName, string Summary, List<string> Notes);
public sealed record AssistFindingViewModel(string Severity, string Text, string? NodeId);
public sealed record FlowExplanationViewModel(bool Durable, int NodeCount, Dictionary<string, int> KindCounts, List<AssistStepViewModel> Steps,
    List<string> Effects, List<AssistFindingViewModel> Findings, string Disclaimer, List<AssistMissingModuleViewModel>? MissingModules = null,
    List<ConfigReferenceViewModel>? ConfigReferences = null);
public sealed record ConfigReferenceViewModel(string Kind, string Name, List<string> NodeIds);
public sealed record FlowWordingRequestViewModel(string FlowJson, string? Question = null, bool IncludeBlockReasons = false, string? FlowId = null, string? DisclosureSha256 = null);
public sealed record FlowWordingDisclosureViewModel(bool AiAvailable, string? Unavailable, string Provider, string Model, string Payload, string PayloadSha256, bool IncludesBlockReasons, IReadOnlyList<string> Notes);
public sealed record FlowWordingResponseViewModel(string Reply, string Provider, string Model);
public sealed record RunWordingRequestViewModel(string? Question = null, bool IncludeErrorText = false, string? DisclosureSha256 = null);
public sealed record RunWordingDisclosureViewModel(bool AiAvailable, string? Unavailable, string Provider, string Model, string Payload, string PayloadSha256, bool IncludesErrorText, IReadOnlyList<string> Notes);
public sealed record AiActivityEntryViewModel(Guid Id, DateTimeOffset At, string? Actor, string Surface, string Action, string Provider, string Model, List<string> Categories,
    string? FlowId, int PayloadChars, string PayloadSha256, string Outcome, string? Detail, bool? ProposalProduced, int? ProposalErrors);
public sealed record AssistMissingModuleViewModel(string Module, string Kind, List<string> NodeIds);
public sealed record ModulePromptViewModel(string Module, string Kind, List<string> NodeIds, List<string> SettingKeys, List<string> UnloadedPackages, string Prompt, string Disclaimer);
public sealed record AssistSettingStateViewModel(string Key, string Label, string Description, bool Required, string State);
public sealed record NodeExplanationViewModel(AssistStepViewModel Step, AssistNodeKindViewModel KindInfo, string? ModuleDescription,
    List<AssistSettingStateViewModel> Settings, List<string> Facts, List<AssistFindingViewModel> Findings);
public sealed record RunDeliveryExplanationViewModel(string Stage, string State, string Meaning, bool NeedsPerson);
public sealed record RunExplanationViewModel(string ExecutionId, string FlowId, string State, string Meaning, bool NeedsPerson, List<string> WhatHappened,
    List<RunDeliveryExplanationViewModel> Deliveries, List<string> WhatToCheck, string Disclaimer);
public sealed record FailurePatternViewModel(string Category, string Stage, int Runs, double Share, List<string> FlowIds, int FlowCount,
    Dictionary<string, int> States, string MostRecentExecutionId, long MostRecentUtcTicks, string WhatToCheck, string SuggestedChange);
public sealed record FailurePatternReportViewModel(int RunsConsidered, int RunsGrouped, List<FailurePatternViewModel> Patterns, string? Headline, List<string> Notes);
public sealed record ReviewItemViewModel(string Code, string Severity, string Title, string Detail, List<string> Where, string Fix);
public sealed record FlowReviewViewModel(string FlowId, string DisplayName, bool Enabled, int Blockers, int Warnings, int Notes, List<ReviewItemViewModel> Items,
    List<string> Checked, List<string> NotChecked, string Disclaimer);
public sealed record RunbookRequestViewModel(string? Purpose, string? Contact, string? Notes);
public sealed record RunbookSectionViewModel(string Title, List<string> Lines);
public sealed record FlowRunbookViewModel(string FlowId, string Title, List<RunbookSectionViewModel> Sections, string Markdown, string Disclaimer);
public sealed record BlockerGroupViewModel(string Code, string Tag, string Severity, string Title, string WhatItMeans, string WhatToDo, int Flows, int Occurrences, int ClearsFlowsAlone, List<string> FlowIds);
public sealed record BlockerTagViewModel(string Tag, string Label, int Groups, int Flows);
public sealed record BlockerTriageViewModel(int ImportedFlows, int FlowsWithBlockers, List<BlockerGroupViewModel> Groups, List<BlockerTagViewModel> Tags, string? Headline, List<string> Notes);
public sealed record ConfigReviewItemViewModel(string Code, string Severity, string Reference, string Title, string Detail, List<string> Related, List<string> FlowIds, string Fix);
public sealed record ConfigReviewViewModel(int StoredValues, int ReferencedNames, int Missing, int Unused, List<ConfigReviewItemViewModel> Items, List<string> Notes);
public sealed record DigestHeldRunViewModel(string ExecutionId, string FlowId, long StartedUtcTicks);
public sealed record DigestSlowFlowViewModel(string FlowId, string DisplayName, int CompletedRuns, double TypicalSeconds, double LatestSeconds);
public sealed record DigestStoppedFlowViewModel(string FlowId, string DisplayName, double IntervalSeconds, long? LastRunUtcTicks, string Reason);
public sealed record OperationalDigestViewModel(long PeriodStartUtcTicks, long PeriodEndUtcTicks, int Hours, int Runs, Dictionary<string, int> RunsByState, double? SuccessRate,
    int FlowsThatRan, List<DigestHeldRunViewModel> HeldRuns, int HeldTotal, List<DigestSlowFlowViewModel> SlowFlows, List<DigestStoppedFlowViewModel> StoppedFlows,
    List<string> Facts, List<string> Notes);
