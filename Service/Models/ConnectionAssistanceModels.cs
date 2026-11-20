namespace Service.Models;

public sealed record ConnectionSample(string Name, string InputJson, string ExpectedJson,
    bool ShareInputValues = false, bool ShareExpectedValues = false);

public sealed record ConnectionAssistanceRequest(string DraftJson, string SourceId, int RouteIndex,
    string Instructions, IReadOnlyList<ConnectionSample> Samples);

public sealed record ConnectionShapeField(string Path, IReadOnlyList<string> Types, bool Optional, bool Nullable);
public sealed record ConnectionAnalysis(IReadOnlyList<ConnectionShapeField> InputShape,
    IReadOnlyList<ConnectionShapeField> OutputShape, IReadOnlyList<string> Errors, string DisclosureJson);
public sealed record ConnectionPreview(string Name, bool Matches, string? ActualJson, string? Error);
public sealed record ConnectionAssistanceResponse(string Reply, string? MappingJson, string? ProposalJson,
    IReadOnlyList<string> Errors, IReadOnlyList<ConnectionPreview> Previews);
