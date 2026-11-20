using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.ServiceDefaults.Security;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Ai;

namespace Service.Tests;

public sealed class GitHubCopilotSignInTests
{
    private static ClaimsPrincipal User(string subject, string issuer = "https://identity.example", string role = "admin")
        => new(new ClaimsIdentity([new("sub", subject), new("iss", issuer), new(ClaimTypes.Role, role)], "test"));

    [Fact]
    public void ConnectionsAreEncryptedAndBoundToIssuerAndSubject()
    {
        using var fixture = new StoreFixture();
        var alice = GitHubCopilotConnectionStore.Owner(User("alice"));
        var bob = GitHubCopilotConnectionStore.Owner(User("bob"));
        var otherIssuer = GitHubCopilotConnectionStore.Owner(User("alice", "https://other.example"));
        Assert.NotEqual(alice, bob);
        Assert.NotEqual(alice, otherIssuer);
        fixture.Connect(alice, "alice-gh", "gho_alice-private");
        Assert.Null(fixture.Store.Get(bob));
        Assert.Null(fixture.Store.Get(otherIssuer));
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Directory, alice + ".bin"));
        Assert.DoesNotContain("gho_alice-private", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("alice-gh", System.Text.Encoding.UTF8.GetString(bytes));
        var reloaded = new GitHubCopilotConnectionStore(fixture.Directory, fixture.Protection);
        Assert.Equal("gho_alice-private", reloaded.Get(alice)!.AccessToken);
        File.Copy(Path.Combine(fixture.Directory, alice + ".bin"), Path.Combine(fixture.Directory, bob + ".bin"));
        Assert.Throws<InvalidOperationException>(() => reloaded.Get(bob));
        Assert.Throws<ArgumentException>(() => fixture.Store.Get("..\\outside"));
        Assert.Throws<InvalidOperationException>(() => GitHubCopilotConnectionStore.Owner(new ClaimsPrincipal()));
    }

    [Fact]
    public void DisconnectAndNewConnectInvalidateOldCallbacksWithoutAffectingOtherUsers()
    {
        using var fixture = new StoreFixture();
        var alice = GitHubCopilotConnectionStore.Owner(User("alice"));
        var bob = GitHubCopilotConnectionStore.Owner(User("bob"));
        fixture.Connect(bob, "bob-gh", "gho_bob");
        var first = fixture.Store.Begin(alice);
        var second = fixture.Store.Begin(alice);
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Complete(alice, first, Connection("old", "gho_old")));
        fixture.Store.Complete(alice, second, Connection("new", "gho_new"));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Complete(alice, second, Connection("replay", "gho_replay")));
        var pending = fixture.Store.Begin(alice);
        fixture.Store.Disconnect(alice);
        Assert.Null(fixture.Store.Get(alice));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Complete(alice, pending, Connection("late", "gho_late")));
        Assert.Equal("bob-gh", fixture.Store.Get(bob)!.Login);
    }

    [Fact]
    public async Task CredentialAccessorTracksUserChangesExpiryAndDisconnect()
    {
        using var fixture = new StoreFixture();
        var alice = GitHubCopilotConnectionStore.Owner(User("alice"));
        var bob = GitHubCopilotConnectionStore.Owner(User("bob"));
        fixture.Connect(alice, "alice-gh", "gho_alice");
        fixture.Connect(bob, "bob-gh", "gho_bob");
        var authentication = new TestAuthentication(User("alice"));
        using var credential = new UserCopilotCredential(authentication, fixture.Store);
        Assert.Equal("gho_alice", await credential.GetTokenAsync(default));
        authentication.Change(User("bob"));
        Assert.Equal("gho_bob", await credential.GetTokenAsync(default));
        fixture.Store.Disconnect(bob);
        Assert.Null(await credential.GetTokenAsync(default));
        fixture.Store.Complete(bob, fixture.Store.Begin(bob), Connection("expired", "gho_expired") with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credential.GetTokenAsync(default));
        Assert.Contains("expired", error.Message);
        authentication.Change(new ClaimsPrincipal());
        await Assert.ThrowsAsync<InvalidOperationException>(() => credential.GetTokenAsync(default));
    }

    [Fact]
    public async Task AuthoringTransportNeverSharesOrSerializesTokensAndStopsAfterDisconnect()
    {
        using var fixture = new StoreFixture();
        var owner = GitHubCopilotConnectionStore.Owner(User("alice"));
        fixture.Connect(owner, "alice-gh", "gho_alice");
        var authentication = new TestAuthentication(User("alice"));
        using var credential = new UserCopilotCredential(authentication, fixture.Store);
        var transport = new ApiTransport();
        using var http = new HttpClient(transport) { BaseAddress = new("https://mulse.example") };
        var api = new MulseApiClient(http, copilotCredential: credential);
        await api.ChatAuthoringAsync(new("hi", "{}", []), default);
        Assert.Equal(["gho_alice"], transport.Tokens);
        Assert.DoesNotContain("gho_alice", Assert.Single(transport.Bodies));
        Assert.False(http.DefaultRequestHeaders.Contains(CopilotUserCredential.HeaderName));
        fixture.Store.Disconnect(owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.TestAiAuthoringAsync(default));
        Assert.Single(transport.Tokens);
        authentication.Change(User("bob"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.ChatAuthoringAsync(new("hi", "{}", []), default));
        Assert.Single(transport.Tokens);
    }

    [Fact]
    public async Task NonCopilotRequestsDoNotReadOrForwardGitHubCredentialsAndRemotePlaintextIsRejected()
    {
        using var fixture = new StoreFixture();
        var owner = GitHubCopilotConnectionStore.Owner(User("alice"));
        fixture.Store.Complete(owner, fixture.Store.Begin(owner), Connection("alice", "gho_expired") with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        using var credential = new UserCopilotCredential(new TestAuthentication(User("alice")), fixture.Store);
        var transport = new ApiTransport { Provider = "OpenAI" };
        using var http = new HttpClient(transport) { BaseAddress = new("https://mulse.example") };
        await new MulseApiClient(http, copilotCredential: credential).TestAiAuthoringAsync(default);
        Assert.Null(Assert.Single(transport.Tokens));
        fixture.Connect(owner, "alice", "gho_active");
        transport.Provider = "GitHubCopilot";
        using var plaintext = new HttpClient(transport, disposeHandler: false) { BaseAddress = new("http://remote-mulse.example") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MulseApiClient(plaintext, copilotCredential: credential).TestAiAuthoringAsync(default));
        Assert.Single(transport.Tokens);
    }

    [Fact]
    public async Task ModelDiscoveryUsesLatestUserWithoutSavedCopilotProviderAndFailsAfterDisconnect()
    {
        using var fixture = new StoreFixture();
        var alice = GitHubCopilotConnectionStore.Owner(User("alice"));
        fixture.Connect(alice, "alice", "gho_alice");
        fixture.Connect(GitHubCopilotConnectionStore.Owner(User("bob")), "bob", "gho_bob");
        var authentication = new TestAuthentication(User("alice"));
        using var credential = new UserCopilotCredential(authentication, fixture.Store);
        var transport = new ApiTransport { Provider = "OpenAI" };
        using var http = new HttpClient(transport) { BaseAddress = new("https://mulse.example") };
        var api = new MulseApiClient(http, copilotCredential: credential);
        Assert.Equal("test-model", Assert.Single(await api.GetCopilotModelsAsync(default)).Id);
        authentication.Change(User("bob"));
        await api.GetCopilotModelsAsync(default);
        Assert.Equal(["gho_alice", "gho_bob"], transport.Tokens);
        Assert.Equal(0, transport.SettingsReads);
        Assert.Empty(transport.Bodies);
        Assert.False(http.DefaultRequestHeaders.Contains(CopilotUserCredential.HeaderName));
        fixture.Store.Disconnect(alice);
        authentication.Change(User("alice"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetCopilotModelsAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MulseApiClient(http).GetCopilotModelsAsync(default));
        Assert.Equal(2, transport.Tokens.Count);
    }

    [Fact]
    public async Task NamedAuthoringDestinationIsCheckedBeforeForwardingPersonalCredentials()
    {
        using var fixture = new StoreFixture();
        fixture.Connect(GitHubCopilotConnectionStore.Owner(User("alice")), "alice", "gho_alice");
        using var credential = new UserCopilotCredential(new TestAuthentication(User("alice")), fixture.Store);
        var transport = new ApiTransport();
        using var settings = new HttpClient(transport) { BaseAddress = new("https://mulse.example") };
        var api = new MulseApiClient(settings, new NamedClientFactory(transport), credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.TestAiAuthoringAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetCopilotModelsAsync(default));
        Assert.Empty(transport.Tokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthMutationsRequireMulseAdminAndValidAntiforgery(bool device)
    {
        await using var host = await OAuthHost.Start(device);
        using var browser = new Browser(host.Address);
        using var anonymous = await browser.Send(HttpMethod.Post, "/auth/github-copilot/connect");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await browser.Login("viewer");
        using var forbidden = await browser.Send(HttpMethod.Post, "/auth/github-copilot/connect", await browser.Get("/test/antiforgery"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await browser.Login("alice");
        var owner = GitHubCopilotConnectionStore.Owner(User("alice"));
        host.Store.Complete(owner, host.Store.Begin(owner), Connection("alice-gh", "gho_alice"));
        using var connect = await browser.Send(HttpMethod.Post, "/auth/github-copilot/connect");
        using var disconnect = await browser.Send(HttpMethod.Post, "/auth/github-copilot/disconnect", "forged");
        Assert.Equal(HttpStatusCode.BadRequest, connect.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, disconnect.StatusCode);
        Assert.Equal("gho_alice", host.Store.Get(owner)!.AccessToken);
        Assert.Equal(0, host.Backchannel.TokenExchanges);
    }

    [Fact]
    public async Task AccountPageShowsOnlyCurrentUserMetadataAndOffersUnreadableStoreRecovery()
    {
        using var fixture = new StoreFixture();
        var alice = GitHubCopilotConnectionStore.Owner(User("alice"));
        fixture.Connect(alice, "alice-gh", "gho_private-alice");
        fixture.Connect(GitHubCopilotConnectionStore.Owner(User("bob")), "bob-gh", "gho_private-bob");
        var html = await RenderAccount(fixture.Store, User("alice"));
        Assert.Contains("alice-gh", html);
        Assert.DoesNotContain("bob-gh", html);
        Assert.DoesNotContain("gho_private", html);
        Assert.Contains("Reconnect GitHub", html);
        Assert.Contains("action=\"/auth/github-copilot/disconnect\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        var unconnected = await RenderAccount(fixture.Store, User("carol"));
        Assert.Contains("No GitHub account is connected", unconnected);
        Assert.DoesNotContain("alice-gh", unconnected);
        File.WriteAllText(Path.Combine(fixture.Directory, alice + ".bin"), "not encrypted data");
        var unreadable = await RenderAccount(fixture.Store, User("alice"));
        Assert.Contains("Unable to load your GitHub connection", unreadable);
        Assert.Contains("action=\"/auth/github-copilot/disconnect\"", unreadable);
        Assert.DoesNotContain("Loading your connection", unreadable);
        Assert.DoesNotContain("No GitHub account is connected", unreadable);
    }

    [Fact]
    public async Task DeviceConnectStaysOnPrivateUiAndRegistersNoCallbackHandler()
    {
        await using var host = await OAuthHost.Start(device: true);
        using var browser = new Browser(host.Address);
        await browser.Login("alice");
        var redirect = await browser.Connect();
        Assert.Equal("/github-copilot?github=device", redirect.OriginalString);
        Assert.DoesNotContain("device-secret", redirect.OriginalString);
        var owner = GitHubCopilotConnectionStore.Owner(User("alice"));
        Assert.Equal("TEST-CODE", host.Device.Get(owner)!.UserCode);
        Assert.Null(host.Device.Get(GitHubCopilotConnectionStore.Owner(User("bob"))));
        Assert.False(host.HasCallbackHandler);
        Assert.Equal("alice", await browser.Get("/test/subject"));
        var html = await RenderAccount(host.Store, User("alice"), host.Device);
        Assert.Contains("TEST-CODE", html);
        Assert.Contains("https://github.com/login/device", html);
        Assert.Contains("checks automatically", html);
        Assert.DoesNotContain("device-secret", html);
        await browser.Disconnect();
        Assert.Null(host.Device.Get(owner));
        Assert.Null(host.Store.Get(owner));
        Assert.Equal(0, host.Backchannel.TokenExchanges);
    }

    [Fact]
    public async Task OAuthHandshakeLinksPersonalOrOrgBackedAccountWithoutReplacingMulseLogin()
    {
        await using var host = await OAuthHost.Start();
        using var browser = new Browser(host.Address);
        await browser.Login("alice");
        var challenge = await browser.Connect();
        Assert.Equal("github.com", challenge.Host);
        var query = QueryHelpers.ParseQuery(challenge.Query);
        Assert.Equal("test-client-id", query["client_id"]);
        Assert.Contains("read:user", query["scope"].ToString());
        Assert.False(query["scope"].ToString().Contains("repo", StringComparison.Ordinal));
        Assert.NotEmpty(query["code_challenge"].ToString());
        using var callback = await browser.Callback(challenge, "personal");
        Assert.Equal("/github-copilot?github=connected", callback.Headers.Location!.OriginalString);
        var saved = host.Store.Get(GitHubCopilotConnectionStore.Owner(User("alice")));
        Assert.Equal("personal-gh", saved!.Login);
        Assert.Equal("gho_personal-private", saved.AccessToken);
        Assert.NotNull(saved.ExpiresAt);
        Assert.Equal("alice", await browser.Get("/test/subject"));
        Assert.Equal(1, host.Backchannel.TokenExchanges);
        Assert.Equal(1, host.Backchannel.Profiles);
        Assert.Null(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("bob"))));
        Assert.DoesNotContain("gho_", string.Join(";", browser.Cookies.Values));

        await browser.Login("bob");
        challenge = await browser.Connect();
        using var org = await browser.Callback(challenge, "organization");
        Assert.Equal("/github-copilot?github=connected", org.Headers.Location!.OriginalString);
        Assert.Equal("organization-gh", host.Store.Get(GitHubCopilotConnectionStore.Owner(User("bob")))!.Login);
        Assert.Equal("personal-gh", host.Store.Get(GitHubCopilotConnectionStore.Owner(User("alice")))!.Login);
        await browser.Disconnect();
        Assert.Null(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("bob"))));
        Assert.NotNull(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("alice"))));
    }

    [Fact]
    public async Task OAuthRejectsSwitchedMulseUserCanceledAttemptAndMissingCorrelation()
    {
        await using var host = await OAuthHost.Start();
        using var browser = new Browser(host.Address);
        await browser.Login("alice");
        var challenge = await browser.Connect();
        await browser.Login("bob");
        using var switched = await browser.Callback(challenge, "personal");
        Assert.Equal("/github-copilot?github=failed", switched.Headers.Location!.OriginalString);
        Assert.Null(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("alice"))));
        Assert.Null(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("bob"))));
        Assert.Equal(0, host.Backchannel.Profiles);

        challenge = await browser.Connect();
        await browser.Disconnect();
        using var canceled = await browser.Callback(challenge, "personal");
        Assert.Equal("/github-copilot?github=failed", canceled.Headers.Location!.OriginalString);
        Assert.Null(host.Store.Get(GitHubCopilotConnectionStore.Owner(User("bob"))));
        using var replayed = await browser.Callback(challenge, "personal");
        Assert.Equal("/github-copilot?github=failed", replayed.Headers.Location!.OriginalString);
        Assert.Equal("bob", await browser.Get("/test/subject"));
    }

    private static GitHubCopilotConnection Connection(string login, string token) => new(login, token, null, DateTimeOffset.UtcNow);

    private static async Task<string> RenderAccount(GitHubCopilotConnectionStore store, ClaimsPrincipal user, GitHubCopilotDeviceAuthorization? device = null)
    {
        var collection = new ServiceCollection().AddLogging().AddSingleton(store)
            .AddSingleton(Options.Create(new GitHubCopilotSignInOptions { ClientId = "id", ClientSecret = "secret", SignInMode = "Callback" }))
            .AddSingleton<AntiforgeryStateProvider, TestAntiforgery>();
        collection.AddAuthorizationBuilder().AddPolicy("MulseAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("admin"));
        if (device is not null) collection.AddSingleton(device);
        else collection.AddSingleton(provider => new GitHubCopilotDeviceAuthorization(new HttpClient(), store,
            provider.GetRequiredService<IOptions<GitHubCopilotSignInOptions>>(), TimeProvider.System,
            provider.GetRequiredService<ILogger<GitHubCopilotDeviceAuthorization>>()));
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CascadingValue<Task<AuthenticationState>>.Value)] = Task.FromResult(new AuthenticationState(user)),
                [nameof(CascadingValue<Task<AuthenticationState>>.ChildContent)] = (RenderFragment)(builder =>
                {
                    builder.OpenComponent<GitHubCopilotAccount>(0); builder.CloseComponent();
                })
            });
            return (await renderer.RenderComponentAsync<CascadingValue<Task<AuthenticationState>>>(parameters)).ToHtmlString();
        });
    }

    private sealed class TestAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("test-csrf", "__RequestVerificationToken");
    }

    private sealed class NamedClientFactory(HttpMessageHandler transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("mulse-ai-authoring", name);
            return new(transport, disposeHandler: false) { BaseAddress = new("http://remote-mulse.example") };
        }
    }

    private sealed class TestAuthentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = user;
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
        public void Change(ClaimsPrincipal next) { _user = next; NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(next))); }
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mulse-github-test-" + Guid.NewGuid().ToString("N"));
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public GitHubCopilotConnectionStore Store { get; }
        public StoreFixture() => Store = new(Directory, Protection);
        public void Connect(string owner, string login, string token) => Store.Complete(owner, Store.Begin(owner), Connection(login, token));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class ApiTransport : HttpMessageHandler
    {
        public string Provider { get; set; } = "GitHubCopilot";
        public int SettingsReads { get; private set; }
        public List<string?> Tokens { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/authoring/settings")
            {
                SettingsReads++;
                Assert.False(request.Headers.Contains(CopilotUserCredential.HeaderName));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AiAuthoringSettingsViewModel(true, Provider, "test", "", false, 1024)) };
            }
            Tokens.Add(request.Headers.TryGetValues(CopilotUserCredential.HeaderName, out var values) ? values.Single() : null);
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri.AbsolutePath == "/api/authoring/copilot/models")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new AiAuthoringModelViewModel("test-model", "Test") }) };
            return new(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)
                ? JsonContent.Create(new AiAuthoringTestViewModel("ok")) : JsonContent.Create(new AiAuthoringResponseViewModel("ok", null, [])) };
        }
    }

    private sealed class GitHubBackchannel : HttpMessageHandler
    {
        public int TokenExchanges { get; private set; }
        public int Profiles { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri == "https://github.com/login/device/code")
            {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("test-client-id", form["client_id"]);
                Assert.False(form.ContainsKey("client_secret"));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    device_code = "device-secret", user_code = "TEST-CODE", verification_uri = "https://github.com/login/device", expires_in = 900, interval = 5
                }) };
            }
            if (request.RequestUri.AbsoluteUri == "https://github.com/login/oauth/access_token")
            {
                TokenExchanges++;
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("test-client-secret", form["client_secret"]);
                Assert.NotEmpty(form["code_verifier"].ToString());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "gho_" + form["code"] + "-private", token_type = "bearer", expires_in = 3600 }) };
            }
            Assert.Equal("https://api.github.com/user", request.RequestUri.AbsoluteUri);
            Profiles++;
            var code = request.Headers.Authorization!.Parameter!.Replace("gho_", "", StringComparison.Ordinal).Replace("-private", "", StringComparison.Ordinal);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { login = code + "-gh", id = 123 }) };
        }
    }

    private sealed class OAuthHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _directory;
        public GitHubBackchannel Backchannel { get; }
        public Uri Address { get; }
        public GitHubCopilotConnectionStore Store => _app.Services.GetRequiredService<GitHubCopilotConnectionStore>();
        public GitHubCopilotDeviceAuthorization Device => _app.Services.GetRequiredService<GitHubCopilotDeviceAuthorization>();
        public bool HasCallbackHandler => _app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync(GitHubCopilotSignIn.Scheme).GetAwaiter().GetResult() is not null;
        private OAuthHost(WebApplication app, string directory, GitHubBackchannel backchannel)
        { _app = app; _directory = directory; Backchannel = backchannel; Address = new Uri(app.Urls.Single()); }
        public static async Task<OAuthHost> Start(bool device = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "mulse-oauth-test-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHubCopilot:ClientId"] = "test-client-id", ["GitHubCopilot:ClientSecret"] = "test-client-secret",
                ["GitHubCopilot:SignInMode"] = device ? "Device" : "Callback", ["GitHubCopilot:SettingsDirectory"] = directory
            });
            builder.Services.AddDataProtection();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddAntiforgery();
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            {
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
            builder.Services.AddAuthorizationBuilder().AddPolicy("MulseAdmin", policy => policy.RequireAuthenticatedUser().RequireRole("admin"));
            builder.Services.AddGitHubCopilotSignIn(builder.Configuration, builder.Environment);
            var backchannel = new GitHubBackchannel();
            if (device) builder.Services.AddSingleton(provider => new GitHubCopilotDeviceAuthorization(new HttpClient(backchannel),
                provider.GetRequiredService<GitHubCopilotConnectionStore>(), provider.GetRequiredService<IOptions<GitHubCopilotSignInOptions>>(),
                TimeProvider.System, provider.GetRequiredService<ILogger<GitHubCopilotDeviceAuthorization>>()));
            builder.Services.PostConfigure<OAuthOptions>(GitHubCopilotSignIn.Scheme, options => options.Backchannel = new HttpClient(backchannel));
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization();
            app.MapGet("/test/login/{subject}", async (HttpContext context, string subject) => await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, User(subject, role: subject == "viewer" ? "viewer" : "admin")));
            app.MapGet("/test/subject", (HttpContext context) => context.User.FindFirstValue("sub") ?? "anonymous");
            app.MapGet("/test/antiforgery", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken!);
            app.MapGitHubCopilotSignIn();
            await app.StartAsync();
            return new(app, directory, backchannel);
        }
        public async ValueTask DisposeAsync()
        {
            await _app.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class Browser(Uri address) : IDisposable
    {
        public Dictionary<string, string> Cookies { get; } = [];
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address };
        public void Dispose() => _http.Dispose();
        public async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? antiforgery = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(pair => pair.Key + "=" + pair.Value)));
            if (antiforgery is not null) request.Headers.Add("RequestVerificationToken", antiforgery);
            var response = await _http.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values)
                {
                    var pair = value.Split(';', 2)[0].Split('=', 2);
                    if (pair[1].Length == 0) Cookies.Remove(pair[0]);
                    else Cookies[pair[0]] = pair[1];
                }
            return response;
        }
        public async Task<string> Get(string path) { using var response = await Send(HttpMethod.Get, path); return await response.Content.ReadAsStringAsync(); }
        public async Task Login(string user) { using var response = await Send(HttpMethod.Get, "/test/login/" + user); response.EnsureSuccessStatusCode(); }
        public async Task<Uri> Connect()
        {
            var token = await Get("/test/antiforgery");
            using var response = await Send(HttpMethod.Post, "/auth/github-copilot/connect", token);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return response.Headers.Location!;
        }
        public Task<HttpResponseMessage> Callback(Uri challenge, string code)
        {
            var state = QueryHelpers.ParseQuery(challenge.Query)["state"].ToString();
            return Send(HttpMethod.Get, "/signin-github-copilot?code=" + code + "&state=" + Uri.EscapeDataString(state));
        }
        public async Task Disconnect()
        {
            var token = await Get("/test/antiforgery");
            using var response = await Send(HttpMethod.Post, "/auth/github-copilot/disconnect", token);
            Assert.Equal("/github-copilot?github=disconnected", response.Headers.Location!.OriginalString);
        }
    }
}
