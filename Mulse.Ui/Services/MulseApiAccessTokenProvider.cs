using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.ServiceDefaults.Security;

namespace Mulse.Ui.Services;

public sealed class MulseApiAccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider? timeProvider = null)
{
    // After a failure, calls fail fast for this long so a page that makes many API calls does not
    // hammer an identity provider that is down (or lock a client out with wrong credentials).
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);

    private const string UnavailableMessage = "The sign-in service (identity provider) could not be reached, so the Mulse API could not be called. Nothing was changed. Try again in a moment.";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _refreshAt;
    private DateTimeOffset _validUntil;
    private string? _tokenEndpoint;
    private Exception? _lastFailure;
    private DateTimeOffset _retryNotBefore;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _refreshAt > _clock.GetUtcNow())
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            if (_accessToken is not null && _refreshAt > now)
            {
                return _accessToken;
            }

            if (_lastFailure is not null && _retryNotBefore > now)
            {
                return TokenStillValidOrThrow(now, _lastFailure);
            }

            var authority = configuration["Authentication:Authority"]
                ?? throw new InvalidOperationException("Authentication:Authority is required to acquire a Mulse API access token.");
            var clientId = configuration["MulseApi:ClientId"]
                ?? throw new InvalidOperationException("MulseApi:ClientId is required to acquire a Mulse API access token.");
            var clientSecret = configuration["MulseApi:ClientSecret"]
                ?? throw new InvalidOperationException("MulseApi:ClientSecret is required. Store it in user secrets or a production secret store.");

            try
            {
                var token = await RequestTokenAsync(authority, clientId, clientSecret, cancellationToken).ConfigureAwait(false);
                now = _clock.GetUtcNow();
                _accessToken = token.AccessToken;
                _refreshAt = now.AddSeconds(Math.Max(1, token.ExpiresIn - 30));
                _validUntil = now.AddSeconds(Math.Max(1, token.ExpiresIn - 5));
                _lastFailure = null;
                return _accessToken;
            }
            catch (Exception exception) when (exception is IdentityProviderUnavailableException or CredentialsRejectedException)
            {
                _lastFailure = exception;
                _retryNotBefore = _clock.GetUtcNow().Add(FailureBackoff);
                return TokenStillValidOrThrow(_clock.GetUtcNow(), exception);
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // A refresh is attempted 30 seconds early. If it fails, the old token is still good until it really expires.
    private string TokenStillValidOrThrow(DateTimeOffset now, Exception failure)
    {
        if (_accessToken is not null && _validUntil > now)
        {
            return _accessToken;
        }

        throw failure switch
        {
            IdentityProviderUnavailableException unavailable => new IdentityProviderUnavailableException(unavailable.Message, unavailable.InnerException),
            _ => new CredentialsRejectedException(failure.Message)
        };
    }

    private async Task<TokenResponse> RequestTokenAsync(string authority, string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        try
        {
            _tokenEndpoint ??= await DiscoverTokenEndpointAsync(authority, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials"
                })
            };
            var clientCredentials = $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}";
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(clientCredentials)));

            using var response = await httpClientFactory.CreateClient("oidc-token")
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (IsServerSideFailure(response.StatusCode))
                {
                    throw new IdentityProviderUnavailableException($"{UnavailableMessage} (The identity provider answered HTTP {(int)response.StatusCode}.)");
                }

                throw new CredentialsRejectedException(
                    $"The identity provider rejected the Mulse API client credentials (HTTP {(int)response.StatusCode}). Check MulseApi:ClientId and MulseApi:ClientSecret.");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn <= 0)
            {
                throw new IdentityProviderUnavailableException($"{UnavailableMessage} (The identity provider returned an invalid access-token response.)");
            }

            return token;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is not (IdentityProviderUnavailableException or CredentialsRejectedException)
            && (exception is JsonException || IdentityProviderAvailability.IsProviderFailure(exception)))
        {
            throw new IdentityProviderUnavailableException(UnavailableMessage, exception);
        }
    }

    private async Task<string> DiscoverTokenEndpointAsync(string authority, CancellationToken cancellationToken)
    {
        var discoveryUri = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";
        using var response = await httpClientFactory.CreateClient("oidc-token")
            .GetAsync(discoveryUri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (IsServerSideFailure(response.StatusCode))
            {
                throw new IdentityProviderUnavailableException($"{UnavailableMessage} (The identity provider answered HTTP {(int)response.StatusCode} for its discovery document.)");
            }

            throw new CredentialsRejectedException(
                $"The identity provider answered HTTP {(int)response.StatusCode} for its OpenID Connect discovery document. Check Authentication:Authority.");
        }

        var metadata = await response.Content.ReadFromJsonAsync<OpenIdProviderMetadata>(cancellationToken).ConfigureAwait(false)
            ?? throw new IdentityProviderUnavailableException($"{UnavailableMessage} (The identity provider returned empty OpenID Connect metadata.)");

        if (!Uri.TryCreate(metadata.TokenEndpoint, UriKind.Absolute, out var tokenEndpoint)
            || (tokenEndpoint.Scheme != Uri.UriSchemeHttps && !(environment.IsDevelopment() && tokenEndpoint.IsLoopback)))
        {
            throw new InvalidOperationException("The identity provider token endpoint must use HTTPS outside local development.");
        }

        return tokenEndpoint.AbsoluteUri;
    }

    private static bool IsServerSideFailure(HttpStatusCode status) =>
        status >= HttpStatusCode.InternalServerError || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>The provider answered, but refused the client's credentials or the configured authority; waiting will not fix it.</summary>
    private sealed class CredentialsRejectedException(string message) : InvalidOperationException(message);

    private sealed record OpenIdProviderMetadata([property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
