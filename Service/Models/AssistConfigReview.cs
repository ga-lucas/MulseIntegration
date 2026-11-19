namespace Service.Models;

/// <summary>One finding about the stored config values and secrets, by name. It never carries a value.</summary>
public sealed record ConfigReviewItem(
    string Code,
    string Severity,
    string Reference,
    string Title,
    string Detail,
    IReadOnlyList<string> Related,
    IReadOnlyList<string> FlowIds,
    string Fix);

public sealed record ConfigReviewReport(
    int StoredValues,
    int ReferencedNames,
    int Missing,
    int Unused,
    IReadOnlyList<ConfigReviewItem> Items,
    IReadOnlyList<string> Notes);
