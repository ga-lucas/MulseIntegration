namespace Service.Models;

/// <summary>What a kind of native node does, in plain words. Written once and shared by the explain feature and the AI prompt catalog.</summary>
public sealed record AssistNodeKindInfo(string Kind, string Title, string Summary, string Effect, string SideEffects, IReadOnlyList<string> KeySettings);

public sealed record AssistGlossaryEntry(string Term, string Meaning, string InMulse, IReadOnlyList<string> Aliases);

public sealed record AssistContextPartInfo(string Name, string Category, int BudgetChars);

public sealed record AssistActionInfo(string Id, string Title, bool Built, bool NeedsAi);

public sealed record AssistSurfaceInfo(string Id, string Title, IReadOnlyList<AssistContextPartInfo> Context, IReadOnlyList<AssistActionInfo> Actions);

public sealed record AssistCategoryInfo(string Category, string Description, bool CanBeSent);

public sealed record AssistSurfacesResponse(IReadOnlyList<AssistCategoryInfo> Categories, IReadOnlyList<AssistSurfaceInfo> Surfaces);

public sealed record ExplainFlowRequest(string FlowJson);

public sealed record ExplainNodeRequest(string FlowJson, string NodeId);

public sealed record AssistStep(int Depth, string Id, string Kind, string Title, string? Module, string? ModuleName, string Summary, IReadOnlyList<string> Notes);

public sealed record AssistFinding(string Severity, string Text, string? NodeId);

public sealed record FlowExplanation(
    bool Durable,
    int NodeCount,
    IReadOnlyDictionary<string, int> KindCounts,
    IReadOnlyList<AssistStep> Steps,
    IReadOnlyList<string> Effects,
    IReadOnlyList<AssistFinding> Findings,
    string Disclaimer,
    IReadOnlyList<AssistMissingModule>? MissingModules = null,
    IReadOnlyList<ConfigReference>? ConfigReferences = null);

/// <summary>A module the flow uses that is not installed on this server, with the steps that use it.</summary>
public sealed record AssistMissingModule(string Module, string Kind, IReadOnlyList<string> NodeIds);

public sealed record ModulePromptRequest(string FlowJson, string ModuleId, string? Description);

public sealed record ModulePromptResponse(
    string Module,
    string Kind,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> SettingKeys,
    IReadOnlyList<string> UnloadedPackages,
    string Prompt,
    string Disclaimer);

public sealed record AssistSettingState(string Key, string Label, string Description, bool Required, string State);

public sealed record NodeExplanation(
    AssistStep Step,
    AssistNodeKindInfo KindInfo,
    string? ModuleDescription,
    IReadOnlyList<AssistSettingState> Settings,
    IReadOnlyList<string> Facts,
    IReadOnlyList<AssistFinding> Findings);

public sealed record RedactedSetting(string Key, string State);

public sealed record RedactedRetry(int Attempts, int IntervalSeconds, bool RetryAmbiguous);

/// <summary>One step of a native flow as an AI request may carry it: no setting values, conditions or expressions.</summary>
public sealed record RedactedNode(
    string Id,
    int Depth,
    string Kind,
    string? Label,
    string? Module,
    string? ModuleStatus,
    string? Message,
    string? Channel,
    IReadOnlyList<RedactedSetting> Settings,
    RedactedRetry? Retry,
    string? BlockReason,
    bool HasConditions);

/// <summary>A config value or secret a flow refers to. The name only; the value is never read.</summary>
public sealed record ConfigReference(string Kind, string Name, IReadOnlyList<string> NodeIds);

public sealed record RedactedFlow(bool Durable, IReadOnlyList<RedactedNode> Nodes, IReadOnlyList<ConfigReference> References, IReadOnlyList<string> Notes);

public sealed record AssistRunEventInput(string Stage, string State, string? Detail);

public sealed record AssistRunDeliveryInput(string Stage, string State);

public sealed record AssistRunInput(string State, IReadOnlyList<AssistRunEventInput>? Events, IReadOnlyList<AssistRunDeliveryInput>? Deliveries);

public sealed record RedactedRunEvent(string Stage, string State, string? Detail);

public sealed record RedactedRunDelivery(string Stage, string State);

public sealed record RedactedRun(string State, IReadOnlyList<RedactedRunEvent> Events, IReadOnlyList<RedactedRunDelivery> Deliveries, IReadOnlyList<string> Notes);
/// <summary>Ask for the explanation of a flow in plain words. The server rebuilds the redacted payload and refuses to send unless its hash matches what the user approved.</summary>
public sealed record FlowWordingRequest(string FlowJson, string? Question = null, bool IncludeBlockReasons = false, string? FlowId = null, string? DisclosureSha256 = null);

/// <summary>Exactly what a wording request would send. Nothing has been sent when this is returned.</summary>
public sealed record FlowWordingDisclosure(
    bool AiAvailable,
    string? Unavailable,
    string Provider,
    string Model,
    string Payload,
    string PayloadSha256,
    bool IncludesBlockReasons,
    IReadOnlyList<string> Notes);

public sealed record FlowWordingResponse(string Reply, string Provider, string Model);