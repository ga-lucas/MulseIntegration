using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class GitHubCopilotDeviceTests
{
    [Fact]
    public void DeviceIsDefaultAndNeedsNoSecretOrInstallationUrl()
    {
        var options = new GitHubCopilotSignInOptions { ClientId = "publisher-public-id" };
        Assert.True(options.Configured);
        Assert.False(options.UseCallbackOAuth);
        options.ClientSecret = "old-callback-secret";
        Assert.False(options.UseCallbackOAuth);
        options.SignInMode = "Callback";
        Assert.True(options.Configured);
        options.ClientSecret = null;
        Assert.False(options.Configured);
        options.SignInMode = "typo";
        Assert.False(options.Configured);
        Assert.False(new GitHubCopilotSignInOptions().Configured);
    }

    [Fact]
    public async Task BeginExposesOnlyUserCodeAndUsesPublisherClientIdWithoutSecret()
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var prompt = Assert.IsType<GitHubDevicePrompt>(fixture.Device.Get(fixture.Alice));
        Assert.Equal("ABCD-1234", prompt.UserCode);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(15), prompt.ExpiresAt);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(5), prompt.NextPollAt);
        Assert.Null(fixture.Device.Get(fixture.Bob));
        var serialized = JsonSerializer.Serialize(prompt);
        Assert.DoesNotContain("private-device-code", serialized);
        Assert.DoesNotContain("gho_", serialized);
        Assert.Equal(0, fixture.Transport.Polls);
        Assert.Null(fixture.Store.Get(fixture.Alice));
    }

    [Fact]
    public async Task PollHonorsPendingAndSlowDownIntervalsAcrossTabs()
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(0, fixture.Transport.Polls);
        fixture.Clock.Advance(5);
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(1, fixture.Transport.Polls);
        fixture.Clock.Advance(4);
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(1, fixture.Transport.Polls);
        fixture.Clock.Advance(1);
        fixture.Transport.TokenResult = new { error = "slow_down", interval = 12 };
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(12), fixture.Device.Get(fixture.Alice)!.NextPollAt);
        fixture.Clock.Advance(11);
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(2, fixture.Transport.Polls);
        fixture.Clock.Advance(1);
        fixture.Transport.TokenResult = new { error = "authorization_pending" };
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(3, fixture.Transport.Polls);
        Assert.Equal(fixture.Clock.GetUtcNow().AddSeconds(12), fixture.Device.Get(fixture.Alice)!.NextPollAt);
    }

    [Fact]
    public async Task SuccessVerifiesIdentityPersistsEncryptedTokenAndDoesNotAffectOtherUser()
    {
        using var fixture = new Fixture();
        fixture.Connect(fixture.Alice, "old-login", "gho_old");
        fixture.Connect(fixture.Bob, "bob", "gho_bob");
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        Assert.Equal("gho_old", fixture.Store.Get(fixture.Alice)!.AccessToken);
        fixture.Transport.TokenResult = new { access_token = "gho_personal", expires_in = 3600, refresh_token = "not-stored" };
        fixture.Clock.Advance(5);
        Assert.True(await fixture.Device.PollAsync(fixture.Alice, id, default));
        var saved = fixture.Store.Get(fixture.Alice)!;
        Assert.Equal("personal-gh", saved.Login);
        Assert.Equal("gho_personal", saved.AccessToken);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(1), saved.ExpiresAt);
        Assert.Equal("gho_bob", fixture.Store.Get(fixture.Bob)!.AccessToken);
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.DoesNotContain("gho_personal", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(fixture.Directory, fixture.Alice + ".bin"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(1, fixture.Transport.Polls);
        Assert.Equal(1, fixture.Transport.Profiles);
    }

    [Theory]
    [InlineData("access_denied", "declined")]
    [InlineData("expired_token", "expired")]
    [InlineData("device_flow_disabled", "disabled")]
    [InlineData("incorrect_client_credentials", "client ID")]
    public async Task TerminalErrorsAreVisibleAndPreserveExistingConnection(string error, string expected)
    {
        using var fixture = new Fixture();
        fixture.Connect(fixture.Alice, "old", "gho_old");
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        fixture.Transport.TokenResult = new { error, error_description = "private-device-code and secret response" };
        fixture.Clock.Advance(5);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Contains(expected, failure.Message);
        Assert.DoesNotContain("secret response", failure.Message);
        Assert.DoesNotContain("private-device-code", failure.Message);
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Equal("gho_old", fixture.Store.Get(fixture.Alice)!.AccessToken);
        Assert.Equal(0, fixture.Transport.Profiles);
    }

    [Fact]
    public async Task ExpiryAndOwnerMismatchNeverExchangeDeviceCode()
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Bob, id, default));
        fixture.Clock.Advance(901);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Equal(0, fixture.Transport.Polls);
    }

    [Fact]
    public async Task DisconnectWhileDeviceRequestIsInFlightCannotResurrectAttempt()
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.BeforeDevice = async () => { started.SetResult(); await release.Task; };
        var begin = fixture.Device.BeginAsync(fixture.Alice, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Device.Disconnect(fixture.Alice);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => begin);
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Null(fixture.Store.Get(fixture.Alice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrReconnectDuringProfileFetchFencesLateToken(bool reconnect)
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        fixture.Transport.TokenResult = new { access_token = "gho_late" };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.BeforeProfile = async () => { started.SetResult(); await release.Task; };
        fixture.Clock.Advance(5);
        var poll = fixture.Device.PollAsync(fixture.Alice, id, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (reconnect) await fixture.Device.BeginAsync(fixture.Alice, default);
        else fixture.Device.Disconnect(fixture.Alice);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => poll);
        Assert.Null(fixture.Store.Get(fixture.Alice));
        if (reconnect) Assert.NotEqual(id, fixture.Device.Get(fixture.Alice)!.AttemptId);
        else Assert.Null(fixture.Device.Get(fixture.Alice));
    }

    [Fact]
    public async Task SimultaneousPollsOnlyMakeOneTokenRequest()
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.BeforePoll = async () => { started.SetResult(); await release.Task; };
        fixture.Clock.Advance(5);
        var first = fixture.Device.PollAsync(fixture.Alice, id, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Equal(1, fixture.Transport.Polls);
        release.SetResult();
        Assert.False(await first);
    }

    [Fact]
    public async Task NetworkErrorsAreSanitizedAndNotReplayed()
    {
        using var fixture = new Fixture();
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        fixture.Transport.BeforePoll = () => throw new HttpRequestException("private-device-code gh_device_secret");
        fixture.Clock.Advance(5);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Contains("outbound connectivity", error.Message);
        Assert.DoesNotContain("gh_device_secret", error.Message);
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Equal(1, fixture.Transport.Polls);
    }

    [Fact]
    public async Task UnexpectedVerificationAddressAndMalformedTokenFailClosed()
    {
        using var fixture = new Fixture();
        fixture.Transport.VerificationUri = "https://attacker.example/device";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.BeginAsync(fixture.Alice, default));
        Assert.Null(fixture.Device.Get(fixture.Alice));
        fixture.Transport.VerificationUri = GitHubCopilotDeviceAuthorization.VerificationUrl;
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        fixture.Clock.Advance(5);
        fixture.Transport.TokenResult = new { access_token = "bad token" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Device.PollAsync(fixture.Alice, id, default));
        Assert.Null(fixture.Store.Get(fixture.Alice));
        Assert.Equal(0, fixture.Transport.Profiles);
    }

    [Fact]
    public async Task CanceledBeginNeverPublishesLateDeviceCode()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.BeforeDevice = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Device.BeginAsync(fixture.Alice, cancellation.Token));
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Null(fixture.Store.Get(fixture.Alice));
    }

    [Fact]
    public async Task CanceledPollPreservesPreviousConnectionAndCannotPublishLateToken()
    {
        using var fixture = new Fixture();
        fixture.Connect(fixture.Alice, "old-login", "gho_old");
        await fixture.Device.BeginAsync(fixture.Alice, default);
        var id = fixture.Device.Get(fixture.Alice)!.AttemptId;
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.TokenResult = new { access_token = "gho_late" };
        fixture.Transport.BeforeProfile = () => { cancellation.Cancel(); return Task.CompletedTask; };
        fixture.Clock.Advance(5);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Device.PollAsync(fixture.Alice, id, cancellation.Token));
        Assert.Equal("gho_old", fixture.Store.Get(fixture.Alice)!.AccessToken);
        Assert.Null(fixture.Device.Get(fixture.Alice));
        Assert.Equal(1, fixture.Transport.Polls);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mulse-device-test-" + Guid.NewGuid().ToString("N"));
        public GitHubCopilotConnectionStore Store { get; }
        public ManualClock Clock { get; } = new();
        public DeviceTransport Transport { get; } = new();
        public GitHubCopilotDeviceAuthorization Device { get; }
        public string Alice { get; } = Owner("alice");
        public string Bob { get; } = Owner("bob");
        public Fixture()
        {
            Store = new(Directory, new EphemeralDataProtectionProvider());
            Device = new(new HttpClient(Transport), Store, Options.Create(new GitHubCopilotSignInOptions
            {
                ClientId = "publisher-public-id", ClientSecret = "must-never-send"
            }), Clock, NullLogger<GitHubCopilotDeviceAuthorization>.Instance);
        }
        public void Connect(string owner, string login, string token) => Store.Complete(owner, Store.Begin(owner), new(login, token, null, Clock.GetUtcNow()));
        private static string Owner(string subject) => GitHubCopilotConnectionStore.Owner(new(new ClaimsIdentity([new("sub", subject), new("iss", "test-issuer")], "test")));
        public void Dispose() { Device.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class DeviceTransport : HttpMessageHandler
    {
        public object TokenResult { get; set; } = new { error = "authorization_pending" };
        public string VerificationUri { get; set; } = GitHubCopilotDeviceAuthorization.VerificationUrl;
        public int Polls { get; private set; }
        public int Profiles { get; private set; }
        public Func<Task>? BeforeDevice { get; set; }
        public Func<Task>? BeforePoll { get; set; }
        public Func<Task>? BeforeProfile { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object result;
            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
            {
                Profiles++;
                Assert.StartsWith("gho_", request.Headers.Authorization!.Parameter);
                if (BeforeProfile is not null) await BeforeProfile();
                result = new { login = "personal-gh" };
            }
            else
            {
                Assert.Null(request.Headers.Authorization);
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("publisher-public-id", form["client_id"]);
                Assert.False(form.ContainsKey("client_secret"));
                Assert.False(form.ContainsKey("redirect_uri"));
                if (request.RequestUri.AbsoluteUri == "https://github.com/login/device/code")
                {
                    Assert.Equal("read:user", form["scope"]);
                    if (BeforeDevice is not null) await BeforeDevice();
                    result = new { device_code = "private-device-code", user_code = "ABCD-1234", verification_uri = VerificationUri, expires_in = 900, interval = 5 };
                }
                else
                {
                    Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri.AbsoluteUri);
                    Assert.Equal("private-device-code", form["device_code"]);
                    Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
                    Polls++;
                    if (BeforePoll is not null) await BeforePoll();
                    result = TokenResult;
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
