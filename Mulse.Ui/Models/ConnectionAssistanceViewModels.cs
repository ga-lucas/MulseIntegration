namespace Mulse.Ui.Models;

public sealed record ConnectionSampleViewModel(string Name, string InputJson, string ExpectedJson,
    bool ShareInputValues = false, bool ShareExpectedValues = false);
public sealed record ConnectionAssistanceRequestViewModel(string DraftJson, string SourceId, int RouteIndex,
    string Instructions, IReadOnlyList<ConnectionSampleViewModel> Samples);
public sealed record ConnectionShapeFieldViewModel(string Path, IReadOnlyList<string> Types, bool Optional, bool Nullable);
public sealed record ConnectionAnalysisViewModel(IReadOnlyList<ConnectionShapeFieldViewModel> InputShape,
    IReadOnlyList<ConnectionShapeFieldViewModel> OutputShape, IReadOnlyList<string> Errors, string DisclosureJson);
public sealed record ConnectionPreviewViewModel(string Name, bool Matches, string? ActualJson, string? Error);
public sealed record ConnectionAssistanceResponseViewModel(string Reply, string? MappingJson, string? ProposalJson,
    IReadOnlyList<string> Errors, IReadOnlyList<ConnectionPreviewViewModel> Previews);
