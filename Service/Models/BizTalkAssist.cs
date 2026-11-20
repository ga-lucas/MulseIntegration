using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>The evidence request a user is asking about, as the importer produced it. Treated as untrusted text when it reaches a model.</summary>
public sealed record BizTalkAssistRequestInfo
{
    [Required] public required string Id { get; init; }
    [Required] public required string Title { get; init; }
    public string? Why { get; init; }
    public IReadOnlyList<string>? HowToGet { get; init; }
    public IReadOnlyList<string>? Impact { get; init; }
    public string? Risk { get; init; }
    public IReadOnlyList<string>? Accepts { get; init; }
}

/// <summary>One piece of evidence the user supplied, sent so it can be checked. Content is only used for the local check unless the user opts in to excerpts.</summary>
public sealed record BizTalkAssistEvidence
{
    [Required] public required string Id { get; init; }
    public required BizTalkEvidenceKind Kind { get; init; }
    public required BizTalkEvidenceOrigin Origin { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? ContentBase64 { get; init; }
}

/// <summary><c>Operation</c> is <c>explain</c> (adapt the steps for one request) or <c>review</c> (describe what the supplied evidence is). A request with no <see cref="DisclosureSha256"/> is rejected by the assist endpoint.</summary>
public sealed record BizTalkAssistRequest
{
    [Required] public required string Operation { get; init; }
    public required BizTalkAssistRequestInfo Request { get; init; }
    public string? Question { get; init; }
    public IReadOnlyList<BizTalkAssistEvidence>? Evidence { get; init; }

    /// <summary>Adds scrubbed excerpts of pasted or uploaded text to what is sent. Never applies to real output, paths or zip files.</summary>
    public bool IncludeContent { get; init; }

    /// <summary>The hash from the preview the user approved. The send is refused when what would be sent no longer matches it.</summary>
    public string? DisclosureSha256 { get; init; }
}

/// <summary>What a deterministic check found out about one piece of evidence, without any AI.</summary>
public sealed record BizTalkEvidenceFinding(
    string EvidenceId,
    string Name,
    string DeclaredKind,
    string Detected,
    bool Matches,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Warnings,
    int CredentialLikeValues);

/// <summary>Exactly what would be sent to the AI provider, and what was found locally. Nothing has been sent when this is returned.</summary>
public sealed record BizTalkAssistDisclosure(
    bool AiAvailable,
    string? Unavailable,
    string Provider,
    string Model,
    string Payload,
    string PayloadSha256,
    bool IncludesContent,
    IReadOnlyList<BizTalkEvidenceFinding> Findings,
    IReadOnlyList<string> Notes);

public sealed record BizTalkAssistResponse(string Reply, IReadOnlyList<BizTalkEvidenceFinding> Findings, string Provider, string Model);
