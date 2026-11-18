using System.Net.Mail;
using System.Net.Sockets;

namespace Mulse.Modules.Abstractions;

/// <summary>
/// A deliver module throws this only when it can prove that no byte of the batch reached the destination
/// (for example the connection was refused, the name did not resolve, or authentication was rejected before any upload).
/// A native Deliver with a retry policy may then try again without risking a duplicate.
/// Any other failure is ambiguous: the destination may already have received the data.
/// </summary>
public sealed class DeliveryNotSentException(string message, Exception? inner = null) : Exception(message, inner);

public static class DeliveryFailureClassifier
{
    /// <summary>
    /// Whether the failure (or one of its inner exceptions) happened while connecting, before any request data could be sent.
    /// Timeouts are deliberately not included: a timeout cannot tell a failed connect from a lost response.
    /// </summary>
    public static bool IsConnectPhaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException socket when socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.HostNotFound
                    or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.TryAgain
                    or SocketError.NoData or SocketError.AddressNotAvailable:
                    return true;
                case HttpRequestException http when http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError:
                    return true;
                case SmtpException { StatusCode: SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst } and not SmtpFailedRecipientException:
                    return true;
            }
        }
        return false;
    }
}
