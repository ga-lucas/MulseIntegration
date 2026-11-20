using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

/// <summary>Evidence kinds as the service names them. The importer reads the first five; RealOutput is only recorded.</summary>
public static class BizTalkEvidenceKinds
{
    public const string Source = "Source";
    public const string Bindings = "Bindings";
    public const string RulePolicy = "RulePolicy";
    public const string HostConfig = "HostConfig";
    public const string HelperSource = "HelperSource";
    public const string RealOutput = "RealOutput";

    public static IReadOnlyList<string> All { get; } = [Source, Bindings, RulePolicy, HostConfig, HelperSource, RealOutput];

    public static string Describe(string kind) => kind switch
    {
        Source => "BizTalk source (solution, project or folder)",
        Bindings => "Binding export",
        RulePolicy => "Business rule (BRL) export",
        HostConfig => "Host configuration file",
        HelperSource => "Configuration helper source",
        RealOutput => "Real input and output sample",
        _ => kind
    };
}

/// <summary>How evidence reaches the service: a path on the service machine, pasted text, or a browser upload (a zip when several files are expected).</summary>
public static class BizTalkEvidenceOrigins
{
    public const string Path = "Path";
    public const string Text = "Text";
    public const string Upload = "Upload";
}

public sealed record BizTalkEvidenceItemViewModel(
    string Id,
    string Kind,
    string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentBase64 = null);

public sealed record BizTalkEvidenceRequestViewModel(
    string Id,
    string Rule,
    string Title,
    string Why,
    IReadOnlyList<string> HowToGet,
    IReadOnlyList<string> Accepts,
    string Relevance,
    IReadOnlyList<string> Impact,
    string? Risk);

public sealed record BizTalkSummaryRequestViewModel(string Id, string Title);

public sealed record BizTalkAnalysisSummaryViewModel
{
    public IReadOnlyList<BizTalkSummaryRequestViewModel> Requests { get; init; } = [];
    public IReadOnlyDictionary<string, int> OpenBlockersByFlow { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> DerivedChoiceStatuses { get; init; } = new Dictionary<string, string>();
}

public sealed record BizTalkFlowBlockerChangeViewModel(string FlowId, int Before, int After);

public sealed record BizTalkDerivedChoiceChangeViewModel(string Value, string? Before, string? After);

public sealed record BizTalkAnalysisDeltaViewModel(
    IReadOnlyList<BizTalkSummaryRequestViewModel> ClearedRequests,
    IReadOnlyList<BizTalkSummaryRequestViewModel> NewRequests,
    IReadOnlyList<BizTalkSummaryRequestViewModel> StillOpenRequests,
    IReadOnlyList<BizTalkFlowBlockerChangeViewModel> FlowChanges,
    IReadOnlyList<BizTalkDerivedChoiceChangeViewModel> DerivedChoiceChanges);

public sealed record BizTalkUpdatePieceViewModel(string Key, string Kind, string Label, string State, bool Selectable, bool SelectedByDefault);

public sealed record BizTalkUpdatePlanViewModel(
    string FlowId,
    string Mode,
    string Reason,
    DateTimeOffset? ImportedUtc,
    IReadOnlyList<BizTalkUpdatePieceViewModel> Pieces,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<string> Notes);

public sealed record UpdateImportedBizTalkFlowRequestViewModel(CreateImportedBizTalkFlowRequestViewModel Import, IReadOnlyList<string> ReplacePieces);

public sealed record BizTalkUpdateResultViewModel(string Mode, FlowViewModel Flow, IReadOnlyList<string> ReplacedPieces, IReadOnlyList<string> Notes);

public static class BizTalkAssistOperations
{
    public const string Explain = "explain";
    public const string Review = "review";
}

public sealed record BizTalkAssistRequestInfoViewModel(
    string Id,
    string Title,
    string? Why,
    IReadOnlyList<string> HowToGet,
    IReadOnlyList<string> Impact,
    string? Risk,
    IReadOnlyList<string> Accepts);

public sealed record BizTalkAssistEvidenceViewModel(
    string Id,
    string Kind,
    string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentBase64 = null);

public sealed record BizTalkAssistRequestViewModel(
    string Operation,
    BizTalkAssistRequestInfoViewModel Request,
    string? Question,
    IReadOnlyList<BizTalkAssistEvidenceViewModel> Evidence,
    bool IncludeContent,
    string? DisclosureSha256 = null);

/// <summary>What a local check found out about one piece of evidence. Computed on the service without any AI.</summary>
public sealed record BizTalkEvidenceFindingViewModel(
    string EvidenceId,
    string Name,
    string DeclaredKind,
    string Detected,
    bool Matches,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Warnings,
    int CredentialLikeValues);

/// <summary>Exactly what an AI assist would send, and what was found locally. Nothing has been sent when this is returned.</summary>
public sealed record BizTalkAssistDisclosureViewModel(
    bool AiAvailable,
    string? Unavailable,
    string Provider,
    string Model,
    string Payload,
    string PayloadSha256,
    bool IncludesContent,
    IReadOnlyList<BizTalkEvidenceFindingViewModel> Findings,
    IReadOnlyList<string> Notes);

public sealed record BizTalkAssistResponseViewModel(string Reply, IReadOnlyList<BizTalkEvidenceFindingViewModel> Findings, string Provider, string Model);

/// <summary>
/// What the user has agreed to for the current wizard session only. It is never saved or exported.
/// Skipping the prompt covers sends that contain no evidence content; sends with excerpts always ask.
/// </summary>
public sealed class BizTalkAssistConsent
{
    public bool SkipMetadataPrompt { get; set; }
}
