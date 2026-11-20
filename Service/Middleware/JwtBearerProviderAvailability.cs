using Microsoft.AspNetCore.Authentication.JwtBearer;
using Mulse.ServiceDefaults.Security;

namespace Service.Middleware;

/// <summary>
/// When the identity provider cannot be reached, its signing keys cannot be fetched and no token can be checked.
/// That is not the caller's fault, so answer 503 with Retry-After instead of a 500 or a misleading 401.
/// </summary>
public static class JwtBearerProviderAvailability
{
    private const string FlagKey = "Mulse.IdentityProviderUnavailable";

    // While the provider is known to be down, failing tokens are answered at once instead of each waiting on its own probe.
    private const long KnownDownMilliseconds = 5000;

    public static JwtBearerEvents Attach(JwtBearerEvents events)
    {
        long knownDownUntil = 0;

        // The framework swallows a failed metadata download and then reports a bare "signature key not found",
        // so an outage looks like a bad token. Asking the configuration manager again tells the two apart.
        async Task<bool> ProviderIsDownAsync(Microsoft.AspNetCore.Authentication.JwtBearer.AuthenticationFailedContext context)
        {
            if (IdentityProviderAvailability.IsProviderFailure(context.Exception)) return true;
            if (Environment.TickCount64 < Volatile.Read(ref knownDownUntil)) return true;
            var manager = context.Options.ConfigurationManager;
            if (manager is null) return false;
            try
            {
                await manager.GetConfigurationAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
                return false;
            }
            catch (Exception exception) when (IdentityProviderAvailability.IsProviderFailure(exception))
            {
                Volatile.Write(ref knownDownUntil, Environment.TickCount64 + KnownDownMilliseconds);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        var authenticationFailed = events.OnAuthenticationFailed;
        events.OnAuthenticationFailed = async context =>
        {
            if (await ProviderIsDownAsync(context).ConfigureAwait(false))
            {
                context.HttpContext.Items[FlagKey] = true;
                context.HttpContext.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("Service.IdentityProvider")
                    .LogWarning("The identity provider could not be reached, so a bearer token could not be validated.");
                // Handled here so the exception is not rethrown as a 500; the challenge below answers 503.
                context.Fail(context.Exception);
                return;
            }

            await authenticationFailed(context).ConfigureAwait(false);
        };

        var challenge = events.OnChallenge;
        events.OnChallenge = async context =>
        {
            if (context.HttpContext.Items.ContainsKey(FlagKey))
            {
                context.HandleResponse();
                await IdentityProviderAvailability.WriteProblemAsync(context.HttpContext).ConfigureAwait(false);
                return;
            }

            await challenge(context).ConfigureAwait(false);
        };

        return events;
    }
}
