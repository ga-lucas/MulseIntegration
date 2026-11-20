using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mulse.ServiceDefaults.Security;

namespace Mulse.Ui.Services;

public sealed record GitHubDevicePrompt(string AttemptId, string UserCode, DateTimeOffset ExpiresAt, DateTimeOffset NextPollAt);

public sealed class GitHubCopilotDeviceAuthorization(
    HttpClient http,
    GitHubCopilotConnectionStore connections,
    IOptions<GitHubCopilotSignInOptions> options,
    TimeProvider clock,
    ILogger<GitHubCopilotDeviceAuthorization> logger) : IDisposable
{
    public const string VerificationUrl = "https://github.com/login/device";
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Attempt> _attempts = [];

    private sealed class Attempt(string id, string deviceCode, string userCode, string clientId, DateTimeOffset expiresAt, int interval)
    {
        public string Id { get; } = id;
        public string DeviceCode { get; } = deviceCode;
        public string UserCode { get; } = userCode;
        public string ClientId { get; } = clientId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Interval { get; set; } = interval;
        public DateTimeOffset NextPollAt { get; set; }
        public bool InFlight { get; set; }
        public GitHubDevicePrompt Prompt => new(Id, UserCode, ExpiresAt, NextPollAt);
    }

    public GitHubDevicePrompt? Get(string owner)
    {
        lock (_gate)
        {
            if (!_attempts.TryGetValue(owner, out var attempt)) return null;
            if (attempt.ExpiresAt <= clock.GetUtcNow() || !connections.IsPending(owner, attempt.Id))
            {
                _attempts.Remove(owner);
                return null;
            }
            return attempt.Prompt;
        }
    }

    public async Task BeginAsync(string owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (!settings.Configured || settings.UseCallbackOAuth)
            throw new InvalidOperationException("GitHub device sign-in requires the Mulse app's public client ID and Device sign-in mode.");
        string pending;
        lock (_gate)
        {
            foreach (var expired in _attempts.Where(pair => pair.Value.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                _attempts.Remove(expired);
            if (_attempts.Count >= 256 && !_attempts.ContainsKey(owner))
                throw new InvalidOperationException("Too many GitHub sign-ins are pending. Try again after existing codes expire.");
            pending = connections.Begin(owner);
            _attempts.Remove(owner);
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = settings.ClientId!, ["scope"] = "read:user" })
            };
            using var body = await SendAsync(request, cancellationToken);
            var root = body.RootElement;
            if (Text(root, "verification_uri") != VerificationUrl)
                throw new InvalidOperationException("GitHub returned an unexpected authorization address.");
            var device = Text(root, "device_code");
            var userCode = Text(root, "user_code");
            if (!CopilotUserCredential.IsValid(device) || userCode is not { Length: > 0 and <= 32 }
                || userCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                throw new InvalidOperationException("GitHub returned an invalid device code.");
            var expires = PositiveSeconds(root, "expires_in", 1800);
            var interval = root.TryGetProperty("interval", out _) ? PositiveSeconds(root, "interval", 60) : 5;
            var now = clock.GetUtcNow();
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!connections.IsPending(owner, pending))
                    throw new InvalidOperationException("This GitHub sign-in was canceled or replaced. Start again.");
                if (_attempts.Count >= 256 && !_attempts.ContainsKey(owner))
                    throw new InvalidOperationException("Too many GitHub sign-ins are pending. Try again after existing codes expire.");
                _attempts[owner] = new(pending, device, userCode, settings.ClientId!, now.AddSeconds(expires), interval)
                {
                    NextPollAt = now.AddSeconds(interval)
                };
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            throw Failed(exception);
        }
    }

    // Only the owning authenticated UI circuit can select an attempt; device codes never leave this server.
    public async Task<bool> PollAsync(string owner, string attemptId, CancellationToken cancellationToken)
    {
        Attempt attempt;
        lock (_gate)
        {
            if (Get(owner) is not { } prompt || prompt.AttemptId != attemptId)
                throw new InvalidOperationException("This GitHub sign-in expired, was canceled or was replaced. Start again.");
            attempt = _attempts[owner];
            if (attempt.InFlight || attempt.NextPollAt > clock.GetUtcNow()) return false;
            attempt.InFlight = true;
            attempt.NextPollAt = clock.GetUtcNow().AddSeconds(attempt.Interval);
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = attempt.ClientId, ["device_code"] = attempt.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            using var body = await SendAsync(request, cancellationToken);
            var root = body.RootElement;
            var error = Text(root, "error");
            if (error is "authorization_pending" or "slow_down")
            {
                lock (_gate)
                {
                    if (error == "slow_down")
                    {
                        attempt.Interval += 5;
                        if (root.TryGetProperty("interval", out _))
                            attempt.Interval = Math.Max(attempt.Interval, PositiveSeconds(root, "interval", 1800));
                    }
                    attempt.NextPollAt = clock.GetUtcNow().AddSeconds(attempt.Interval);
                }
                return false;
            }
            if (error is not null)
                throw new InvalidOperationException(error switch
                {
                    "access_denied" => "GitHub authorization was declined. Connect again when ready.",
                    "expired_token" or "token_expired" => "The GitHub code expired. Connect again for a new code.",
                    "device_flow_disabled" => "Device sign-in is disabled for this GitHub app. Ask the Mulse publisher to enable it.",
                    _ => "GitHub device authorization failed. Check the app client ID, account and organization policies, then reconnect."
                });
            var token = Text(root, "access_token");
            if (!CopilotUserCredential.IsValid(token)) throw new InvalidOperationException("GitHub returned an invalid access token.");
            var connection = await ReadConnectionAsync(http, token, ReadExpiry(root, clock.GetUtcNow()), clock.GetUtcNow(), cancellationToken);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_attempts.TryGetValue(owner, out var current) || !ReferenceEquals(current, attempt) || attempt.ExpiresAt <= clock.GetUtcNow())
                    throw new InvalidOperationException("This GitHub sign-in was canceled, expired or replaced. Start again.");
                connections.Complete(owner, attempt.Id, connection);
                _attempts.Remove(owner);
            }
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            lock (_gate)
            {
                if (_attempts.TryGetValue(owner, out var current) && ReferenceEquals(current, attempt)) _attempts.Remove(owner);
            }
            if (cancellationToken.IsCancellationRequested) throw;
            throw Failed(exception);
        }
        finally
        {
            lock (_gate) attempt.InFlight = false;
        }
    }

    public void Disconnect(string owner)
    {
        lock (_gate)
        {
            connections.Disconnect(owner);
            _attempts.Remove(owner);
        }
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("GitHub sign-in could not be completed. Check outbound connectivity, the app configuration and GitHub availability.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private InvalidOperationException Failed(Exception exception)
    {
        logger.LogWarning("GitHub device sign-in failed ({ExceptionType}).", exception.GetType().Name);
        return new InvalidOperationException(exception is InvalidOperationException ? exception.Message
            : "GitHub sign-in could not be completed. Check outbound connectivity and try connecting again.");
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int PositiveSeconds(JsonElement root, string name, int maximum)
    {
        if (!root.TryGetProperty(name, out var property) || !int.TryParse(property.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0 || seconds > maximum)
            throw new InvalidOperationException("GitHub returned an invalid authorization lifetime or polling interval.");
        return seconds;
    }

    internal static DateTimeOffset? ReadExpiry(JsonElement root, DateTimeOffset now)
        => root.TryGetProperty("expires_in", out _) ? now.AddSeconds(PositiveSeconds(root, "expires_in", 31_536_000)) : null;

    internal static async Task<GitHubCopilotConnection> ReadConnectionAsync(HttpClient client, string token, DateTimeOffset? expiresAt,
        DateTimeOffset connectedAt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Mulse-Copilot/1.0");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("GitHub account verification failed. Reconnect and try again.");
        using var profile = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (Text(profile.RootElement, "login") is not { Length: > 0 and <= 100 } login)
            throw new InvalidOperationException("GitHub returned no valid account name.");
        return new(login, token, expiresAt, connectedAt);
    }

    public void Dispose() => http.Dispose();
}
