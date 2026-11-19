namespace Service.Models;

/// <summary>Text a person adds to a runbook. Mulse cannot know these, so they are never guessed.</summary>
public sealed record RunbookRequest(string? Purpose = null, string? Contact = null, string? Notes = null);

public sealed record RunbookSection(string Title, IReadOnlyList<string> Lines);

/// <summary>A runbook for one flow, built from its definition. Facts only; setting values, config values and secrets are never included.</summary>
public sealed record FlowRunbook(string FlowId, string Title, IReadOnlyList<RunbookSection> Sections, string Markdown, string Disclaimer);
