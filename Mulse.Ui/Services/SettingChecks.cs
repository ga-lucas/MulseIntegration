using System.Text;
using System.Text.RegularExpressions;

namespace Mulse.Ui.Services;

/// <summary>One quiet note about the value typed into a setting. It never contains the value.</summary>
public sealed record SettingFinding(string Code, string Message, bool CanStore);

/// <summary>
/// Deterministic checks run while a setting is edited, before the flow is saved. They need no AI provider, make no calls and never echo the value.
/// A finding is advice: it never blocks typing or saving.
/// </summary>
public static partial class SettingChecks
{
    public const string PlainSecret = "plain-secret";
    public const string Placeholder = "placeholder";
    public const string BrokenReference = "broken-reference";

    [GeneratedRegex(@"\{\{(?:config|secret):[^{}\r\n]+\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(password|passwd|pwd|secret|token|api[_-]?key|account[_-]?key|access[_-]?key|private[_-]?key|connection[_-]?string|credential)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();

    // A key such as "tokenUrl" or "passwordFile" names where a secret lives, not the secret itself.
    [GeneratedRegex(@"(url|uri|endpoint|path|file|name|type|header|field|expir\w*|ttl|seconds|minutes|enabled|mode|kind|prefix|location|scope)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotSecretSuffixPattern();

    [GeneratedRegex(@"(^\s*<[^<>\r\n]{1,60}>\s*$)|(\b(TODO|TBD|FIXME|CHANGE_?ME|REPLACE_?ME)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex StoredNamePattern();

    /// <summary>True when a setting with this key and input kind is meant to hold a credential.</summary>
    public static bool IsCredentialSetting(string? key, string? inputKind)
        => string.Equals(inputKind, "Password", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(key) && SecretKeyPattern().IsMatch(key) && !NotSecretSuffixPattern().IsMatch(key));

    public static IReadOnlyList<SettingFinding> Evaluate(string? key, string? value, string? inputKind)
    {
        if (string.IsNullOrWhiteSpace(value) || inputKind is "Boolean" or "Select") return [];
        var findings = new List<SettingFinding>();
        var hasToken = TokenPattern().IsMatch(value);

        if (!hasToken && IsCredentialSetting(key, inputKind))
            findings.Add(new(PlainSecret, "This looks like a credential typed in as plain text, so it is saved and exported with the flow. Store it as a secret and refer to it instead.", true));

        if (PlaceholderPattern().IsMatch(value))
            findings.Add(new(Placeholder, "This looks like a placeholder that was never filled in.", false));

        // Free-text JSON areas can legitimately contain braces, so only single-line inputs are checked.
        if (inputKind != "TextArea" && TokenPattern().Replace(value, "").Contains("{{", StringComparison.Ordinal))
            findings.Add(new(BrokenReference, "A '{{' here is not a complete {{config:name}} or {{secret:name}} reference, so it will be used as plain text.", false));

        return findings;
    }

    /// <summary>A short, valid stored-value name derived from the setting key, made unique among the existing names.</summary>
    public static string SuggestName(string? key, IEnumerable<string> existing)
    {
        var words = new StringBuilder();
        var previousLower = false;
        foreach (var character in key ?? "")
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (char.IsAsciiLetterUpper(character) && previousLower) words.Append('-');
                words.Append(char.ToLowerInvariant(character));
                previousLower = char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character);
            }
            else if (words.Length > 0 && words[^1] != '-')
            {
                words.Append('-');
                previousLower = false;
            }
        }

        var baseName = words.ToString().Trim('-');
        if (baseName.Length == 0) baseName = "credential";
        if (baseName.Length > 80) baseName = baseName[..80].TrimEnd('-');

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var candidate = baseName;
        for (var number = 2; taken.Contains(candidate); number++) candidate = $"{baseName}-{number}";
        return candidate;
    }

    /// <summary>Null when the name can be used for a new stored value; otherwise a short reason.</summary>
    public static string? ValidateName(string? name, IEnumerable<string> existing)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (!StoredNamePattern().IsMatch(name)) return "Use letters, digits, '.', '_' or '-', starting with a letter or digit (up to 100 characters).";
        return existing.Contains(name, StringComparer.OrdinalIgnoreCase) ? "A stored value with that name already exists. Choose another name so it is not overwritten." : null;
    }

    public static string SecretReference(string name) => $"{{{{secret:{name}}}}}";
}
