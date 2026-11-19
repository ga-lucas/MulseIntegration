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
    string Disclaimer);

public sealed record AssistSettingState(string Key, string Label, string Description, bool Required, string State);

public sealed record NodeExplanation(
    AssistStep Step,
    AssistNodeKindInfo KindInfo,
    string? ModuleDescription,
    IReadOnlyList<AssistSettingState> Settings,
    IReadOnlyList<string> Facts,
    IReadOnlyList<AssistFinding> Findings);
