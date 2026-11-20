using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mulse.ServiceDefaults.Security;
using Mulse.Ui.Services;
using Service.Middleware;

namespace Service.Tests;

public sealed class IdentityProviderAvailabilityTests
{
    [Fact]
    public void Classifier_RecognizesIdentityModelMetadataFailures()
    {
        var wrapped = new InvalidOperationException("IDX20803: Unable to obtain configuration from: 'https://idp/.well-known/openid-configuration'.");

        Assert.True(IdentityProviderAvailability.IsUnavailable(wrapped));
        Assert.True(IdentityProviderAvailability.IsProviderFailure(wrapped));
        Assert.True(IdentityProviderAvailability.IsUnavailable(new IdentityProviderUnavailableException("down")));
    }

    [Fact]
    public void Classifier_FindsNetworkFailuresNestedInOtherExceptions()
    {
        var refused = new InvalidOperationException("outer", new IOException("io", new HttpRequestException("refused")));
        var timedOut = new TaskCanceledException("timeout", new TimeoutException());

        Assert.True(IdentityProviderAvailability.IsNetworkFailure(refused));
        Assert.True(IdentityProviderAvailability.IsNetworkFailure(timedOut));
        Assert.True(IdentityProviderAvailability.IsNetworkFailure(new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway)));
        Assert.True(IdentityProviderAvailability.IsNetworkFailure(new AggregateException(new SocketException())));
    }

    [Fact]
    public void Classifier_DoesNotBlameTheProviderForOrdinaryProblems()
    {
        Assert.False(IdentityProviderAvailability.IsProviderFailure(new ArgumentException("bad token")));
        Assert.False(IdentityProviderAvailability.IsProviderFailure(new OperationCanceledException()));
        Assert.False(IdentityProviderAvailability.IsProviderFailure(new HttpRequestException("not found", null, HttpStatusCode.NotFound)));
        Assert.False(IdentityProviderAvailability.IsProviderFailure(null));
        // A plain connection error is only the provider's fault where every call involved is a provider call.
        Assert.False(IdentityProviderAvailability.IsUnavailable(new HttpRequestException("api refused")));
    }

    [Fact]
    public async Task TokenProvider_WhenProviderIsDown_ThrowsAFriendlyErrorAndFailsFastDuringBackoff()
    {
        var harness = new TokenHarness(_ => throw new HttpRequestException("Connection refused to https://idp.test"));

        var first = await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));
        var second = await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));

        Assert.Contains("could not be reached", first.Message);
        Assert.Contains("Nothing was changed", first.Message);
        Assert.DoesNotContain(TokenHarness.Secret, first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, harness.Calls);

        harness.Clock.Advance(MulseApiAccessTokenProvider.FailureBackoff + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));
        Assert.Equal(2, harness.Calls);
    }

    [Fact]
    public async Task TokenProvider_RecoversOnceTheProviderIsBack()
    {
        var down = true;
        var harness = new TokenHarness(request =>
        {
            if (down) throw new HttpRequestException("refused");
            return TokenHarness.Success(request, "recovered-token", 300);
        });

        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));
        down = false;
        harness.Clock.Advance(MulseApiAccessTokenProvider.FailureBackoff + TimeSpan.FromSeconds(1));

        Assert.Equal("recovered-token", await harness.Provider.GetAccessTokenAsync(default));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TokenProvider_TreatsServerSideStatusesAsUnavailable(HttpStatusCode status)
    {
        var harness = new TokenHarness(request => request.Method == HttpMethod.Get
            ? TokenHarness.Discovery()
            : new HttpResponseMessage(status));

        var exception = await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));

        Assert.Contains($"HTTP {(int)status}", exception.Message);
    }

    [Fact]
    public async Task TokenProvider_TreatsATimeoutAsUnavailable()
    {
        var harness = new TokenHarness(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task TokenProvider_ExplainsRejectedCredentialsWithoutBlamingAvailability()
    {
        var harness = new TokenHarness(request => request.Method == HttpMethod.Get
            ? TokenHarness.Discovery()
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => harness.Provider.GetAccessTokenAsync(default));

        Assert.IsNotType<IdentityProviderUnavailableException>(exception);
        Assert.Contains("rejected the Mulse API client credentials (HTTP 401)", exception.Message);
        Assert.Contains("MulseApi:ClientSecret", exception.Message);
        Assert.DoesNotContain(TokenHarness.Secret, exception.Message);

        // Wrong credentials must not be retried on every request.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => harness.Provider.GetAccessTokenAsync(default));
        Assert.Equal(2, harness.Calls);
    }

    [Fact]
    public async Task TokenProvider_KeepsUsingTheCachedTokenWhileARefreshFails()
    {
        var down = false;
        var harness = new TokenHarness(request =>
        {
            if (down) throw new HttpRequestException("refused");
            return TokenHarness.Success(request, "cached-token", 60);
        });
        Assert.Equal("cached-token", await harness.Provider.GetAccessTokenAsync(default));

        down = true;
        harness.Clock.Advance(TimeSpan.FromSeconds(35));
        Assert.Equal("cached-token", await harness.Provider.GetAccessTokenAsync(default));

        harness.Clock.Advance(TimeSpan.FromSeconds(21));
        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(() => harness.Provider.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task TokenProvider_DoesNotHideACallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = new TokenHarness(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Provider.GetAccessTokenAsync(cancellation.Token));
    }

    [Fact]
    public async Task Api_AnswersUnavailableWhenTheProviderCannotBeReached()
    {
        var authority = UnreachableAuthority();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = "mulse-api";
                options.RequireHttpsMetadata = false;
                options.BackchannelTimeout = TimeSpan.FromSeconds(5);
                options.Events = JwtBearerProviderAvailability.Attach(new JwtBearerEvents());
            });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/x", () => "ok");
        await app.StartAsync();
        try
        {
            using var client = ClientFor(app);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/x");
            request.Headers.Authorization = new("Bearer", WellFormedToken());
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("5", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("Identity provider unavailable", body);
            Assert.DoesNotContain("127.0.0.1", body);
            Assert.DoesNotContain("IDX", body);

            // With no token there is nothing to verify, so it is still an ordinary 401.
            using var anonymous = await client.GetAsync("/api/x");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Ui_ShowsAFriendlyPageWhenSignInCannotReachTheProvider()
    {
        await using var app = await StartOidcAppAsync();
        try
        {
            using var client = ClientFor(app);

            using var page = new HttpRequestMessage(HttpMethod.Get, "/");
            page.Headers.Accept.ParseAdd("text/html");
            using var htmlResponse = await client.SendAsync(page);
            var html = await htmlResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, htmlResponse.StatusCode);
            Assert.Equal("text/html", htmlResponse.Content.Headers.ContentType!.MediaType);
            Assert.True(htmlResponse.Headers.Contains("Retry-After"));
            Assert.Contains("Sign-in is temporarily unavailable", html);
            Assert.Contains("href=\"/\"", html);
            Assert.DoesNotContain("127.0.0.1", html);
            Assert.DoesNotContain("IDX", html);

            using var api = new HttpRequestMessage(HttpMethod.Get, "/");
            api.Headers.Accept.ParseAdd("application/json");
            using var jsonResponse = await client.SendAsync(api);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, jsonResponse.StatusCode);
            Assert.Equal("application/problem+json", jsonResponse.Content.Headers.ContentType!.MediaType);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Ui_SignOutStillEndsTheLocalSessionWhenTheProviderIsDown()
    {
        await using var app = await StartOidcAppAsync();
        try
        {
            using var client = ClientFor(app);

            using var signin = await client.GetAsync("/test-signin");
            Assert.Equal(HttpStatusCode.OK, signin.StatusCode);
            using var logout = await client.PostAsync("/test-signout", null);

            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
            Assert.Equal("/", logout.Headers.Location!.OriginalString);
            Assert.Contains(logout.Headers.GetValues("Set-Cookie"), cookie =>
                cookie.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal) && cookie.Contains("1970"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Ui_RemoteFailureFromAnUnreachableProviderIsAnsweredFriendly()
    {
        var options = new OpenIdConnectOptions();
        OidcSession.Configure(options);
        var scheme = new AuthenticationScheme("oidc", null, typeof(OpenIdConnectHandler));
        using var body = new MemoryStream();

        var unreachable = NewHttpContext(body);
        var down = new RemoteFailureContext(unreachable, scheme, options, new HttpRequestException("refused"));
        await options.Events.RemoteFailure(down);

        Assert.True(down.Result?.Handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unreachable.Response.StatusCode);

        var denied = new RemoteFailureContext(NewHttpContext(new MemoryStream()), scheme, options, new Exception("access_denied"));
        await options.Events.RemoteFailure(denied);

        Assert.Null(denied.Result);
    }

    private static string WellFormedToken()
    {
        // Signed with a throwaway key; the provider is never reachable, so the signature is never checked.
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]);
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "http://idp.test/realms/mulse",
            Audience = "mulse-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256)
        });
    }

    private static DefaultHttpContext NewHttpContext(Stream body)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Headers.Accept = "text/html";
        context.Response.Body = body;
        return context;
    }

    private static string UnreachableAuthority()
    {
        // A port that was free a moment ago and has nothing listening: the connection is refused straight away.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/realms/mulse";
    }

    private static HttpClient ClientFor(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    }

    private static async Task<WebApplication> StartOidcAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = UnreachableAuthority();
                options.RequireHttpsMetadata = false;
                options.ClientId = "mulse-ui";
                options.ClientSecret = TokenHarness.Secret;
                options.ResponseType = "code";
                OidcSession.Configure(options);
            });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseIdentityProviderUnavailablePage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/", () => "Protected workspace").RequireAuthorization();
        app.MapGet("/test-signin", async (HttpContext context) =>
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], "test"))));
        app.MapPost("/test-signout", (HttpContext context) => OidcSession.SignOutAsync(context));
        await app.StartAsync();
        return app;
    }

    private sealed class TokenHarness
    {
        public const string Secret = "s3cret-value-for-tests";

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public TokenHarness(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = request =>
            {
                Calls++;
                return respond(request);
            };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Authority"] = "https://idp.test/realms/mulse",
                ["MulseApi:ClientId"] = "mulse-ui-service",
                ["MulseApi:ClientSecret"] = Secret
            }).Build();
            Provider = new MulseApiAccessTokenProvider(new Factory(this), configuration, new TestEnvironment(), Clock);
        }

        public int Calls { get; private set; }

        public ManualClock Clock { get; } = new();

        public MulseApiAccessTokenProvider Provider { get; }

        public static HttpResponseMessage Discovery() =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(new { token_endpoint = "https://idp.test/token" }) };

        public static HttpResponseMessage Success(HttpRequestMessage request, string token, int expiresIn) =>
            request.Method == HttpMethod.Get
                ? Discovery()
                : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = token, expires_in = expiresIn }) };

        private sealed class Factory(TokenHarness owner) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new Handler(owner._respond), disposeHandler: true);
        }

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Mulse.Ui.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
