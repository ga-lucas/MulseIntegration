using Microsoft.Extensions.Options;
using Service.Models;

namespace Service.Ai;

internal sealed class AiAuthoringSettingsService(IOptions<AiAuthoringOptions> options, IAiAuthoringSettingsStore store, IHostEnvironment environment)
{
    public AiAuthoringOptions Options => options.Value;

    public AiAuthoringSettingsResponse Get()
    {
        var effective = Resolve();
        return new AiAuthoringSettingsResponse(effective.Enabled, effective.Provider, effective.Model, effective.Endpoint?.AbsoluteUri,
            effective.CredentialConfigured, effective.MaxOutputTokens);
    }

    public AiAuthoringSettingsResponse Update(AiAuthoringSettingsRequest request)
    {
        if (request is null) throw new ArgumentException("A settings body is required.");
        if (request.ClearApiKey && !string.IsNullOrEmpty(request.ApiKey))
            throw new ArgumentException("Provide either an API key or clearApiKey, not both.");
        var provider = AiAuthoringProviders.Canonicalize(request.Provider)
            ?? throw new ArgumentException($"Provider must be '{AiAuthoringProviders.OpenAI}', '{AiAuthoringProviders.OpenAICompatible}' or '{AiAuthoringProviders.GitHubCopilot}'.");
        var model = (request.Model ?? "").Trim();
        if (model.Length > 128 || model.Any(char.IsControl)) throw new ArgumentException("Model must be at most 128 characters without control characters.");
        if (request.MaxOutputTokens is < 256 or > 16384) throw new ArgumentException("Max output tokens must be between 256 and 16384.");
        var endpoint = ValidateEndpoint(request.Endpoint, provider);
        var apiKey = request.ApiKey?.Trim();
        if (provider == AiAuthoringProviders.GitHubCopilot && apiKey is not null)
            throw new ArgumentException("Connect GitHub per user in the UI. GitHub tokens cannot be saved as a shared Service credential.");
        if (apiKey is not null && (apiKey.Length is 0 or > 512 || apiKey.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))))
            throw new ArgumentException("API key must be 1-512 characters without whitespace.");

        var existing = store.Load();
        // A stored key is only kept for the same provider and endpoint; otherwise it must be re-entered so it is never sent to a new destination.
        var sameBinding = existing is not null && existing.Provider == provider && Normalize(provider, existing.Endpoint) == Normalize(provider, endpoint?.AbsoluteUri);
        var storedKey = request.ClearApiKey || provider == AiAuthoringProviders.GitHubCopilot ? null : apiKey ?? (sameBinding ? existing!.ApiKey : null);
        var suppressed = request.ClearApiKey || string.IsNullOrEmpty(apiKey) && sameBinding && existing!.KeySuppressed;
        var deploymentKey = !suppressed && string.IsNullOrEmpty(apiKey) && DeploymentKeyApplies(provider, endpoint?.AbsoluteUri) ? options.Value.ApiKey : null;
        if (request.Enabled)
        {
            if (model.Length == 0) throw new ArgumentException("A model is required to enable AI authoring.");
            if (provider != AiAuthoringProviders.GitHubCopilot && string.IsNullOrEmpty(storedKey) && string.IsNullOrEmpty(deploymentKey) && !KeylessAllowed(provider, endpoint))
                throw new ArgumentException("An API key is required to enable AI authoring; stored and deployment keys are not reused when the provider or endpoint changes (keyless access is only allowed for loopback OpenAI-compatible endpoints).");
        }

        store.Save(new AiAuthoringOverrides(request.Enabled, provider, model, endpoint?.AbsoluteUri, request.MaxOutputTokens, storedKey, suppressed));
        return Get();
    }

    internal AiAuthoringEffectiveSettings Resolve()
    {
        var deployment = options.Value;
        var overrides = store.Load();
        var rawProvider = (overrides?.Provider ?? deployment.Provider)?.Trim() ?? "";
        var provider = AiAuthoringProviders.Canonicalize(rawProvider) ?? rawProvider;
        var endpointText = overrides is null ? deployment.Endpoint : overrides.Endpoint;
        var key = overrides?.ApiKey;
        if (string.IsNullOrEmpty(key) && overrides?.KeySuppressed != true && DeploymentKeyApplies(provider, endpointText)) key = deployment.ApiKey;
        if (provider == AiAuthoringProviders.GitHubCopilot) key = null;
        Uri? endpoint = Uri.TryCreate(endpointText?.Trim(), UriKind.Absolute, out var parsed) ? parsed : null;
        var malformed = endpoint is null && !string.IsNullOrWhiteSpace(endpointText);
        return new AiAuthoringEffectiveSettings(overrides?.Enabled ?? deployment.Enabled, provider,
            (overrides?.Model ?? deployment.Model ?? "").Trim(), endpoint,
            Math.Clamp(overrides?.MaxOutputTokens ?? deployment.MaxOutputTokens, 256, 16384), string.IsNullOrWhiteSpace(key) ? null : key.Trim(), malformed);
    }

    /// <summary>Returns a safe error when the effective settings cannot be used to call a provider, otherwise null.</summary>
    internal string? Validate(AiAuthoringEffectiveSettings settings)
    {
        if (settings.Model.Length == 0) return "AI authoring has no model configured.";
        if (settings.EndpointMalformed) return "AI authoring has a malformed endpoint configured.";
        if (AiAuthoringProviders.Canonicalize(settings.Provider) is null) return "AI authoring has an unsupported provider.";
        if (settings.Endpoint is null && settings.Provider == AiAuthoringProviders.OpenAICompatible) return "OpenAICompatible requires an endpoint.";
        try { _ = ValidateEndpoint(settings.Endpoint?.AbsoluteUri, settings.Provider); }
        catch (ArgumentException exception) { return exception.Message; }
        if (!settings.CredentialConfigured && !KeylessAllowed(settings.Provider, settings.Endpoint)) return "AI authoring has no credential configured.";
        return null;
    }

    private Uri? ValidateEndpoint(string? endpoint, string provider)
    {
        if (provider == AiAuthoringProviders.GitHubCopilot)
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("GitHub Copilot uses the official SDK and does not accept a custom API endpoint.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (provider == AiAuthoringProviders.OpenAICompatible) throw new ArgumentException("OpenAICompatible requires an endpoint.");
            return null;
        }
        if (endpoint.Length > 2048 || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Endpoint must be an absolute URL of at most 2048 characters.");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Endpoint must not contain credentials, a query string or a fragment.");
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && environment.IsDevelopment() && uri.IsLoopback) return uri;
        throw new ArgumentException("Endpoint must use HTTPS (plain HTTP is only allowed for loopback hosts in Development).");
    }

    private bool DeploymentKeyApplies(string provider, string? endpoint)
    {
        var deployment = options.Value;
        return string.Equals(AiAuthoringProviders.Canonicalize(deployment.Provider), provider, StringComparison.Ordinal)
            && string.Equals(Normalize(provider, deployment.Endpoint), Normalize(provider, endpoint), StringComparison.OrdinalIgnoreCase);
    }

    // An unset OpenAI endpoint means the official API, so it binds identically to the explicit official URL.
    private static string Normalize(string provider, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) && provider == AiAuthoringProviders.OpenAI) endpoint = OfficialOpenAiEndpoint;
        return Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : endpoint?.Trim() ?? "";
    }

    private const string OfficialOpenAiEndpoint = "https://api.openai.com/v1";

    private static bool KeylessAllowed(string provider, Uri? endpoint)
        => provider == AiAuthoringProviders.OpenAICompatible && endpoint is { IsLoopback: true };
}
