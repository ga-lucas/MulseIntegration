using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Service.Models;

/// <summary>What a piece of supplied evidence is. The importer reads Source, Bindings, RulePolicy, HostConfig and HelperSource; RealOutput is only recorded; it is compared in the native test panel ("Compare with real output").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BizTalkEvidenceKind>))]
public enum BizTalkEvidenceKind
{
    /// <summary>A BizTalk solution, project, or folder of projects (a file, or a zip of a folder tree).</summary>
    Source,
    /// <summary>One or more exported binding files (a file, or a zip of several).</summary>
    Bindings,
    /// <summary>One or more business rule (BRL) exports (a file, or a zip of several).</summary>
    RulePolicy,
    /// <summary>The host instance's configuration file.</summary>
    HostConfig,
    /// <summary>The C# source of a configuration helper class (a file, or a zip of several).</summary>
    HelperSource,
    /// <summary>A real input and the output the BizTalk server produced for it. Recorded by the client; the importer does not read it yet.</summary>
    RealOutput
}

/// <summary>How an evidence item reaches the server.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BizTalkEvidenceOrigin>))]
public enum BizTalkEvidenceOrigin
{
    /// <summary>A path on the machine that runs the service, used as is.</summary>
    Path,
    /// <summary>Text pasted into the browser, carried in <see cref="BizTalkEvidenceItem.Text"/>.</summary>
    Text,
    /// <summary>A file chosen in the browser, carried in <see cref="BizTalkEvidenceItem.ContentBase64"/>. A zip is extracted into a folder that is deleted after the request.</summary>
    Upload
}

/// <summary>One piece of evidence supplied for an import. Uploaded and pasted content lives only for the duration of one request.</summary>
public sealed record BizTalkEvidenceItem
{
    /// <summary>A stable client-chosen id (letters, digits, dot, dash, underscore; at most 64 characters). A source path can name an uploaded source as <c>evidence:id</c>.</summary>
    [Required]
    public required string Id { get; init; }

    public required BizTalkEvidenceKind Kind { get; init; }
    public required BizTalkEvidenceOrigin Origin { get; init; }

    /// <summary>A display name, and the file name for pasted text or an uploaded single file.</summary>
    public string? Name { get; init; }

    public string? Path { get; init; }
    public string? Text { get; init; }
    public string? ContentBase64 { get; init; }
}

/// <summary>
/// One piece of evidence the importer still needs, with the reason and general steps for getting it. Rules in code create these from analysis results; they are not AI output.
/// </summary>
/// <param name="Id">Stable for the same gap across analyses, so a later analysis can say it was cleared.</param>
/// <param name="Rule">The rule that produced it.</param>
/// <param name="Title">One plain line.</param>
/// <param name="Why">What it unblocks, with counts.</param>
/// <param name="HowToGet">General steps for retrieving it from a BizTalk server or host.</param>
/// <param name="Accepts">Evidence kinds that satisfy it. Empty when the importer reads nothing for it and the gap is closed by editing the imported flow.</param>
/// <param name="Relevance">Certain when the analysis proves the need; Possible when it is a likely cause that the importer cannot confirm.</param>
/// <param name="Impact">Flows, projects, maps or components affected.</param>
/// <param name="Risk">What to leave out of what is shared, or null.</param>
public sealed record BizTalkEvidenceRequest(
    string Id,
    string Rule,
    string Title,
    string Why,
    IReadOnlyList<string> HowToGet,
    IReadOnlyList<BizTalkEvidenceKind> Accepts,
    string Relevance,
    IReadOnlyList<string> Impact,
    string? Risk);

/// <summary>What the browser keeps from one analysis so the next one can report what changed. It holds identifiers, titles, counts and statuses only.</summary>
public sealed record BizTalkAnalysisSummary
{
    public IReadOnlyList<BizTalkSummaryRequest> Requests { get; init; } = [];

    /// <summary>Open blockers by suggested flow id.</summary>
    public IReadOnlyDictionary<string, int> OpenBlockersByFlow { get; init; } = new Dictionary<string, int>();

    /// <summary>Derived map choice status by exact map-name value.</summary>
    public IReadOnlyDictionary<string, string> DerivedChoiceStatuses { get; init; } = new Dictionary<string, string>();
}

public sealed record BizTalkSummaryRequest(string Id, string Title);

public sealed record BizTalkFlowBlockerChange(string FlowId, int Before, int After);

public sealed record BizTalkDerivedChoiceChange(string Value, string? Before, string? After);

/// <summary>What changed between the previous analysis summary and this one. A request is cleared only when this analysis no longer reports it.</summary>
public sealed record BizTalkAnalysisDelta(
    IReadOnlyList<BizTalkSummaryRequest> ClearedRequests,
    IReadOnlyList<BizTalkSummaryRequest> NewRequests,
    IReadOnlyList<BizTalkSummaryRequest> StillOpenRequests,
    IReadOnlyList<BizTalkFlowBlockerChange> FlowChanges,
    IReadOnlyList<BizTalkDerivedChoiceChange> DerivedChoiceChanges);
