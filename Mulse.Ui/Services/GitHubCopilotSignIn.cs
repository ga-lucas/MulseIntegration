using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Mulse.ServiceDefaults.Security;

namespace Mulse.Ui.Services;

public sealed class GitHubCopilotSignInOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string SignInMode { get; set; } = "Device";
    public string? SettingsDirectory { get; set; }
    public bool UseCallbackOAuth => string.Equals(SignInMode, "Callback", StringComparison.OrdinalIgnoreCase);
    public bool Configured => !string.IsNullOrWhiteSpace(ClientId)
        && (UseCallbackOAuth ? !string.IsNullOrWhiteSpace(ClientSecret) : string.Equals(SignInMode, "Device", StringComparison.OrdinalIgnoreCase));
}

public interface IUserCopilotCredential
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class UserCopilotCredential : IUserCopilotCredential, IDisposable
{
    private readonly AuthenticationStateProvider _authentication;
    private readonly GitHubCopilotConnectionStore _connections;
    private Task<AuthenticationState> _state;

    public UserCopilotCredential(AuthenticationStateProvider authentication, GitHubCopilotConnectionStore connections)
    {
        _authentication = authentication;
        _connections = connections;
        _authentication.AuthenticationStateChanged += StateChanged;
        _state = authentication.GetAuthenticationStateAsync();
    }

    private void StateChanged(Task<AuthenticationState> state) => _state = state;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var state = await _state.WaitAsync(cancellationToken);
        var owner = GitHubCopilotConnectionStore.Owner(state.User);
        var connection = _connections.Get(owner);
        if (connection?.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Your GitHub connection expired. Reconnect under Configuration > AI assistance.");
        return connection?.AccessToken;
    }

    public void Dispose() => _authentication.AuthenticationStateChanged -= StateChanged;
}

public static class GitHubCopilotSignIn
{
    public const string Scheme = "GitHubCopilotConnection";
    private const string OwnerKey = "mulse-owner";
    private const string PendingKey = "mulse-pending";

    public static IServiceCollection AddGitHubCopilotSignIn(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("GitHubCopilot");
        var settings = section.Get<GitHubCopilotSignInOptions>() ?? new();
        services.AddOptions<GitHubCopilotSignInOptions>().Bind(section)
            .Validate(value => string.Equals(value.SignInMode, "Device", StringComparison.OrdinalIgnoreCase) || value.UseCallbackOAuth,
                "GitHubCopilot:SignInMode must be Device or Callback.")
            .ValidateOnStart();
        var directory = Path.GetFullPath(settings.SettingsDirectory ?? Path.Combine("Data", "github-copilot"), environment.ContentRootPath);
        services.AddSingleton(provider => new GitHubCopilotConnectionStore(directory, provider.GetRequiredService<IDataProtectionProvider>()));
        services.AddScoped<IUserCopilotCredential, UserCopilotCredential>();
        services.AddSingleton(provider => new GitHubCopilotDeviceAuthorization(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024
            }, provider.GetRequiredService<GitHubCopilotConnectionStore>(), provider.GetRequiredService<IOptions<GitHubCopilotSignInOptions>>(),
            TimeProvider.System, provider.GetRequiredService<ILogger<GitHubCopilotDeviceAuthorization>>()));
        if (!settings.Configured || !settings.UseCallbackOAuth) return services;

        services.AddAuthentication().AddOAuth(Scheme, options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.ClientId = settings.ClientId!;
            options.ClientSecret = settings.ClientSecret!;
            options.CallbackPath = "/signin-github-copilot";
            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.Scope.Add("read:user");
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Backchannel = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024
            };
            options.Events.OnCreatingTicket = CompleteAsync;
            options.Events.OnTicketReceived = context =>
            {
                // Linking must never replace the existing Mulse authentication cookie with a GitHub identity.
                context.HandleResponse();
                context.Response.Redirect("/github-copilot?github=connected");
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Scheme)
                    .LogWarning("GitHub account linking failed ({ExceptionType}).", context.Failure?.GetType().Name ?? "Unknown");
                context.HandleResponse();
                context.Response.Redirect("/github-copilot?github=failed");
                return Task.CompletedTask;
            };
        });
        return services;
    }

    private static async Task CompleteAsync(OAuthCreatingTicketContext context)
    {
        var current = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!current.Succeeded || current.Principal is null
            || !(await authorization.AuthorizeAsync(current.Principal, null, "MulseAdmin")).Succeeded)
            throw new InvalidOperationException("Sign in to Mulse again before linking GitHub.");
        var owner = GitHubCopilotConnectionStore.Owner(current.Principal);
        if (!context.Properties.Items.TryGetValue(OwnerKey, out var expectedOwner) || owner != expectedOwner
            || !context.Properties.Items.TryGetValue(PendingKey, out var pending) || string.IsNullOrEmpty(pending))
            throw new InvalidOperationException("The Mulse user changed during GitHub authorization. Start again.");
        if (!CopilotUserCredential.IsValid(context.AccessToken)) throw new InvalidOperationException("GitHub returned an invalid access token.");

        var now = DateTimeOffset.UtcNow;
        var expires = context.TokenResponse.Response is { } tokens ? GitHubCopilotDeviceAuthorization.ReadExpiry(tokens.RootElement, now) : null;
        var connection = await GitHubCopilotDeviceAuthorization.ReadConnectionAsync(context.Backchannel, context.AccessToken, expires, now, context.HttpContext.RequestAborted);
        context.HttpContext.RequestServices.GetRequiredService<GitHubCopilotConnectionStore>().Complete(owner, pending, connection);
    }

    public static WebApplication MapGitHubCopilotSignIn(this WebApplication app)
    {
        app.MapPost("/auth/github-copilot/connect", async (HttpContext context, IAntiforgery antiforgery,
            IOptions<GitHubCopilotSignInOptions> options, GitHubCopilotConnectionStore connections, GitHubCopilotDeviceAuthorization device) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await ValidateAntiforgeryAsync(context, antiforgery)) return Results.BadRequest("Invalid anti-forgery token. Reload the page and try again.");
            if (!options.Value.Configured) return Results.LocalRedirect("/github-copilot?github=unconfigured");
            var owner = GitHubCopilotConnectionStore.Owner(context.User);
            if (!options.Value.UseCallbackOAuth)
            {
                try
                {
                    await device.BeginAsync(owner, context.RequestAborted);
                    return Results.LocalRedirect("/github-copilot?github=device");
                }
                catch (InvalidOperationException exception)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Scheme)
                        .LogWarning("GitHub device sign-in could not start ({ExceptionType}).", exception.GetType().Name);
                    return Results.LocalRedirect("/github-copilot?github=failed");
                }
            }
            var properties = new AuthenticationProperties { RedirectUri = "/github-copilot" };
            properties.Items[OwnerKey] = owner;
            properties.Items[PendingKey] = connections.Begin(owner);
            return Results.Challenge(properties, [Scheme]);
        }).RequireAuthorization("MulseAdmin");
        app.MapPost("/auth/github-copilot/disconnect", async (HttpContext context, IAntiforgery antiforgery, GitHubCopilotDeviceAuthorization device) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await ValidateAntiforgeryAsync(context, antiforgery)) return Results.BadRequest("Invalid anti-forgery token. Reload the page and try again.");
            device.Disconnect(GitHubCopilotConnectionStore.Owner(context.User));
            return Results.LocalRedirect("/github-copilot?github=disconnected");
        }).RequireAuthorization("MulseAdmin");
        return app;
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(Scheme)
                .LogWarning("GitHub account linking rejected an invalid anti-forgery token.");
            return false;
        }
    }
}
