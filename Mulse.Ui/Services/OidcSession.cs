using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Mulse.ServiceDefaults.Security;

namespace Mulse.Ui.Services;

public static class OidcSession
{
    public static void Configure(OpenIdConnectOptions options)
    {
        options.SaveTokens = true;
        options.BackchannelTimeout = TimeSpan.FromSeconds(15);
        options.Events.OnRemoteFailure = context =>
        {
            // Signing in needs the provider twice (the browser comes back, then the code is redeemed); either can fail.
            if (!IdentityProviderAvailability.IsProviderFailure(context.Failure)) return Task.CompletedTask;
            IdentityProviderUnavailablePage.LogUnavailable(context.HttpContext, context.Failure!);
            context.HandleResponse();
            return IdentityProviderUnavailablePage.WriteAsync(context.HttpContext);
        };
        options.Events.OnTicketReceived = context =>
        {
            var properties = context.Properties ??= new AuthenticationProperties();
            // Logout needs the ID token; downstream API access uses a separate service identity.
            var idToken = properties.GetTokenValue("id_token");
            properties.StoreTokens(string.IsNullOrEmpty(idToken)
                ? []
                : [new AuthenticationToken { Name = "id_token", Value = idToken }]);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // Older sessions have no saved ID token. Keycloak can identify the client instead.
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Ends the local session first, then asks the provider to end its own. If the provider is down the user is
    /// still signed out of Mulse and sent home, instead of getting an error page while still signed in.
    /// </summary>
    public static async Task SignOutAsync(HttpContext context)
    {
        var properties = new AuthenticationProperties { RedirectUri = "/" };
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme, properties).ConfigureAwait(false);
        try
        {
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, properties).ConfigureAwait(false);
        }
        catch (Exception exception) when (!context.Response.HasStarted && IdentityProviderAvailability.IsProviderFailure(exception))
        {
            IdentityProviderUnavailablePage.LogUnavailable(context, exception);
            context.Response.Redirect("/");
        }
    }
}
