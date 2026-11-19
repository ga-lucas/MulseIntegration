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
