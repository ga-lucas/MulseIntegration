using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Mulse.ServiceDefaults.Security;

/// <summary>Raised when the identity provider cannot be reached or is failing, as opposed to rejecting a request.</summary>
public sealed class IdentityProviderUnavailableException(string message, Exception? innerException = null)
    : HttpRequestException(message, innerException, HttpStatusCode.ServiceUnavailable);

/// <summary>Tells "the identity provider is down" apart from "this token or login is not valid", and answers the former clearly.</summary>
public static class IdentityProviderAvailability
{
    public const string UserMessage = "The sign-in service (identity provider) could not be reached, so your access could not be verified. Nothing was changed. Try again in a moment.";

    public const int RetryAfterSeconds = 5;

    // Thrown by the identity model when it cannot download the provider's discovery document or signing keys.
    private static readonly string[] ConfigurationMarkers = ["IDX20803", "IDX20804", "IDX20807"];

    /// <summary>True when the exception says, by itself, that the provider's metadata could not be obtained.</summary>
    public static bool IsUnavailable(Exception? exception)
    {
        foreach (var current in Chain(exception))
        {
            if (current is IdentityProviderUnavailableException) return true;
            if (ConfigurationMarkers.Any(marker => current.Message.Contains(marker, StringComparison.Ordinal))) return true;
        }

        return false;
    }

    /// <summary>
    /// True for connection, timeout and server-side failures. Only use it where every call involved talks to the
    /// identity provider; elsewhere it would blame the provider for an unrelated network problem.
    /// </summary>
    public static bool IsNetworkFailure(Exception? exception)
    {
        foreach (var current in Chain(exception))
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: null }:
                case HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }:
                case HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests }:
                case SocketException:
                case IOException:
                case TimeoutException:
                    return true;
                case OperationCanceledException { InnerException: TimeoutException }:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Either of the above. For code that only ever deals with the identity provider.</summary>
    public static bool IsProviderFailure(Exception? exception) => IsUnavailable(exception) || IsNetworkFailure(exception);

    public static Task WriteProblemAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
                title: "Identity provider unavailable",
                detail: UserMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable)
            .ExecuteAsync(context);
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        var pending = new Stack<Exception>();
        if (exception is not null) pending.Push(exception);
        var guard = 0;
        while (pending.Count > 0 && guard++ < 32)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
