using Microsoft.Extensions.Primitives;
using Mulse.ServiceDefaults.Security;

namespace Mulse.Ui.Services;

/// <summary>
/// Answers "the sign-in service is unreachable" with a plain 503 page instead of the generic error page.
/// It is self-contained on purpose: it must work when sign-in is the thing that is broken.
/// </summary>
public static class IdentityProviderUnavailablePage
{
    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Sign-in unavailable - Mulse</title>
        <style>
        body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f5f6f8;color:#1f2933}
        main{max-width:32rem;padding:2rem;background:#fff;border-radius:.5rem;box-shadow:0 1px 4px rgba(0,0,0,.15)}
        h1{font-size:1.3rem;margin-top:0}
        a{display:inline-block;margin-top:.5rem;padding:.5rem 1rem;border-radius:.25rem;background:#2563eb;color:#fff;text-decoration:none}
        </style>
        </head>
        <body>
        <main>
        <h1>Sign-in is temporarily unavailable</h1>
        <p>Mulse could not reach the sign-in service (identity provider), so it cannot confirm who you are right now.</p>
        <p>Nothing was changed, and running flows are not affected. Please try again in a moment. If this keeps happening, contact your administrator.</p>
        <a href="/">Try again</a>
        </main>
        </body>
        </html>
        """;

    public static IApplicationBuilder UseIdentityProviderUnavailablePage(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Exception exception) when (!context.Response.HasStarted && IdentityProviderAvailability.IsUnavailable(exception))
            {
                LogUnavailable(context, exception);
                context.Response.Clear();
                await WriteAsync(context).ConfigureAwait(false);
            }
        });

    public static void LogUnavailable(HttpContext context, Exception exception) =>
        context.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Mulse.Ui.IdentityProvider")
            .LogWarning("The identity provider could not be reached: {Reason}", exception.GetBaseException().Message);

    public static Task WriteAsync(HttpContext context)
    {
        if (!AcceptsHtml(context.Request))
        {
            return IdentityProviderAvailability.WriteProblemAsync(context);
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = IdentityProviderAvailability.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(Html);
    }

    private static bool AcceptsHtml(HttpRequest request) =>
        request.Headers.Accept.Any(value => value is not null && value.Contains("text/html", StringComparison.OrdinalIgnoreCase));
}
