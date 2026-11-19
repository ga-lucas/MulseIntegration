namespace Service.Models;

/// <summary>Effective AI authoring settings. Never contains the API key; only whether one is available.</summary>
public sealed record AiAuthoringSettingsResponse(
    bool Enabled,
    string Provider,
    string Model,
    string? Endpoint,
    bool CredentialConfigured,
    int MaxOutputTokens,
    string? Notice = null);

/// <summary>Replaces the stored AI authoring overrides. <see cref="ApiKey"/> is write-only; omit it to keep the stored key.</summary>
public sealed record AiAuthoringSettingsRequest(
    bool Enabled,
    string Provider,
    string Model,
    string? Endpoint,
    int MaxOutputTokens,
    string? ApiKey = null,
    bool ClearApiKey = false);

/// <summary>A model returned by the requesting user's authenticated Copilot catalog.</summary>
public sealed record AiAuthoringModelResponse(string Id, string Name);

/// <summary>Outcome of a provider connectivity test.</summary>
public sealed record AiAuthoringTestResponse(string Message);

/// <summary>One prior conversation turn. Only roles <c>user</c> and <c>assistant</c> are accepted.</summary>
public sealed record AiAuthoringChatMessage(string Role, string Text);

/// <summary>
/// A chat turn. <see cref="DraftJson"/> is the current unsaved flow in the UI <c>FlowViewModel</c> JSON shape.
/// </summary>
public sealed record AiAuthoringChatRequest(
    string Message,
    string DraftJson,
    IReadOnlyList<AiAuthoringChatMessage>? History = null,
    bool IncludeEnvironmentSettings = false);

/// <summary>
/// The assistant reply. <see cref="ProposalJson"/> is a complete validated <c>FlowViewModel</c> JSON document (disabled,
/// original id/category/migration preserved) or null when no valid proposal was produced.
/// </summary>
public sealed record AiAuthoringChatResponse(
    string Reply,
    string? ProposalJson,
    IReadOnlyList<string> ValidationErrors);
