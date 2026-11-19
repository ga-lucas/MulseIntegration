namespace Service.Models;

/// <summary>The checklist for one imported map. Names and counts only: map paths, script bodies and schema contents are never copied in.</summary>
public sealed record MapReviewMap(
    string Name,
    string Status,
    int DirectLinks,
    int Constants,
    int UnsupportedLinks,
    int Scripts,
    IReadOnlyList<ReviewItem> Items);

/// <summary>The map checklist of one imported flow. A clean checklist does not show that a map produces the right output.</summary>
public sealed record MapReviewReport(
    string FlowId,
    string DisplayName,
    int MapsReviewed,
    int Blockers,
    int Warnings,
    int Notes,
    IReadOnlyList<MapReviewMap> Maps,
    IReadOnlyList<string> Remarks,
    IReadOnlyList<string> Checked,
    IReadOnlyList<string> NotChecked,
    string Disclaimer);
