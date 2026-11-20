using System.Diagnostics.CodeAnalysis;

namespace Mulse.ServiceDefaults.Security;

public static class CopilotUserCredential
{
    public const string HeaderName = "X-Mulse-Copilot-Token";
    public const int MaxTokenLength = 4096;

    public static bool IsValid([NotNullWhen(true)] string? token)
        => token is { Length: > 0 and <= MaxTokenLength } && !token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
}
