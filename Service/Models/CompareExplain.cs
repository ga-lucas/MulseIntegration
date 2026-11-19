using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>One difference as the AI would see it: its class, where it is and whether it is cosmetic. Never the values.</summary>
public sealed record CompareExplainDifference(string Class, string Path, bool Cosmetic);

public sealed record CompareExplainSample(string Verdict, List<CompareExplainDifference> Differences, bool Truncated = false);

/// <summary>A request to explain difference classes. Sample names, expected and actual values are not part of it and are never sent.</summary>
public sealed record CompareExplainRequest
{
    [Required] public required List<CompareExplainSample> Samples { get; init; }
    public string? Question { get; init; }

    /// <summary>The hash from the preview the user approved. The send is refused when what would be sent no longer matches it.</summary>
    public string? DisclosureSha256 { get; init; }
}

/// <summary>Exactly what would be sent to the AI provider. Nothing has been sent when this is returned.</summary>
public sealed record CompareExplainDisclosure(bool AiAvailable, string? Unavailable, string Provider, string Model, string Payload, string PayloadSha256, IReadOnlyList<string> Notes);

public sealed record CompareExplainResponse(string Reply, string Provider, string Model);
