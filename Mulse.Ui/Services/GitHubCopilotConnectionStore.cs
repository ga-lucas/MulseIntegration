using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Mulse.Ui.Services;

public sealed record GitHubCopilotConnection(string Login, string AccessToken, DateTimeOffset? ExpiresAt, DateTimeOffset ConnectedAt);
internal sealed record GitHubCopilotConnectionEntry(string? PendingId, GitHubCopilotConnection? Connection);

public sealed class GitHubCopilotConnectionStore
{
    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly Lock _gate = new();

    public GitHubCopilotConnectionStore(string directory, IDataProtectionProvider protection)
    {
        _directory = Path.GetFullPath(directory);
        _protector = protection.CreateProtector("Mulse.GitHubCopilot.UserConnections.v1");
    }

    public static string Owner(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) throw new InvalidOperationException("Sign in to Mulse before connecting GitHub.");
        var subject = user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The Mulse identity has no stable subject identifier.");
        var issuer = user.FindFirst("iss")?.Value ?? subject.Issuer;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject.Value))
            throw new InvalidOperationException("The Mulse identity has no stable issuer and subject identifier.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { issuer, subject.Value }))));
    }

    public GitHubCopilotConnection? Get(string owner)
    {
        lock (_gate) return Read(owner)?.Connection;
    }

    public string Begin(string owner)
    {
        lock (_gate)
        {
            var pending = Guid.NewGuid().ToString("N");
            Write(owner, new(pending, Read(owner)?.Connection));
            return pending;
        }
    }

    public void Complete(string owner, string pending, GitHubCopilotConnection connection)
    {
        lock (_gate)
        {
            if (Read(owner)?.PendingId != pending)
                throw new InvalidOperationException("This GitHub connection attempt expired or was disconnected. Start again.");
            Write(owner, new(null, connection));
        }
    }

    public bool IsPending(string owner, string pending)
    {
        lock (_gate) return Read(owner)?.PendingId == pending;
    }

    public void Disconnect(string owner)
    {
        lock (_gate) File.Delete(FilePath(owner));
    }

    private string FilePath(string owner)
    {
        if (owner.Length != 64 || !owner.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid connection owner.", nameof(owner));
        return Path.Combine(_directory, owner + ".bin");
    }

    private GitHubCopilotConnectionEntry? Read(string owner)
    {
        var path = FilePath(owner);
        if (!File.Exists(path)) return null;
        try
        {
            var protector = _protector.CreateProtector(owner);
            return JsonSerializer.Deserialize<GitHubCopilotConnectionEntry>(protector.Unprotect(File.ReadAllBytes(path)))
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Your saved GitHub connection could not be read. Ask the administrator to check connection storage and data-protection keys.", exception);
        }
    }

    private void Write(string owner, GitHubCopilotConnectionEntry entry)
    {
        var path = FilePath(owner);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = path + ".tmp";
        try
        {
            var bytes = _protector.CreateProtector(owner).Protect(JsonSerializer.SerializeToUtf8Bytes(entry));
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
