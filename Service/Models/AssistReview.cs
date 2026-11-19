namespace Service.Models;

/// <summary>One finding of the pre-enable review. Names and counts only: setting values, config values and secrets are never copied in.</summary>
/// <param name="Code">A stable rule code, for example <c>plain-secret</c>.</param>
/// <param name="Severity"><c>Blocker</c> (Mulse will refuse to enable the flow), <c>Warning</c> or <c>Info</c>.</param>
public sealed record ReviewItem(string Code, string Severity, string Title, string Detail, IReadOnlyList<string> Where, string Fix);

/// <summary>The result of the rules that run before a flow is enabled. A clean review does not show that the flow is correct.</summary>
public sealed record FlowReview(
    string FlowId,
    string DisplayName,
    bool Enabled,
    int Blockers,
    int Warnings,
    int Notes,
    IReadOnlyList<ReviewItem> Items,
    IReadOnlyList<string> Checked,
    IReadOnlyList<string> NotChecked,
    string Disclaimer);
