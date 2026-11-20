namespace Service.Ai;

public static class AiAuthoringProviders
{
    public const string OpenAI = "OpenAI";
    public const string OpenAICompatible = "OpenAICompatible";
    public const string GitHubCopilot = "GitHubCopilot";

    internal static string? Canonicalize(string? provider)
        => string.Equals(provider?.Trim(), OpenAI, StringComparison.OrdinalIgnoreCase) ? OpenAI
            : string.Equals(provider?.Trim(), OpenAICompatible, StringComparison.OrdinalIgnoreCase) ? OpenAICompatible
            : string.Equals(provider?.Trim(), GitHubCopilot, StringComparison.OrdinalIgnoreCase) ? GitHubCopilot : null;
}

/// <summary>Deployment settings bound from the <c>AiAuthoring</c> configuration section (user secrets/environment recommended for <see cref="ApiKey"/>).</summary>
public sealed class AiAuthoringOptions
{
    public bool Enabled { get; set; }

    /// <summary><c>OpenAI</c>, <c>OpenAICompatible</c> (including Ollama) or <c>GitHubCopilot</c>.</summary>
    public string Provider { get; set; } = AiAuthoringProviders.OpenAI;

    public string Model { get; set; } = "";

    /// <summary>HTTPS endpoint (or http loopback in Development). Required for <c>OpenAICompatible</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>OpenAI or OpenAI-compatible provider API key. GitHub Copilot ignores this setting and requires a per-user connection.</summary>
    public string? ApiKey { get; set; }

    public int MaxOutputTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; } = 90;

    public int MaxMessageChars { get; set; } = 4000;

    public int MaxHistoryMessages { get; set; } = 12;

    public int MaxDraftChars { get; set; } = 400_000;

    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>Directory for the encrypted override file and its data-protection keys. Defaults to <c>Data\ai-authoring</c> under the content root, outside workspace backups.</summary>
    public string? SettingsDirectory { get; set; }

    /// <summary>Directory for the append-only AI activity log. Defaults to <c>Data\ai-activity</c> under the content root.</summary>
    public string? ActivityDirectory { get; set; }
}

internal sealed record AiAuthoringEffectiveSettings(bool Enabled, string Provider, string Model, Uri? Endpoint, int MaxOutputTokens, string? ApiKey, bool EndpointMalformed = false)
{
    public bool CredentialConfigured => !string.IsNullOrEmpty(ApiKey);
}

/// <summary>Raised with a status code and a message that is safe to show to API callers.</summary>
public sealed class AiAuthoringException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
