using System.Text;
using System.Text.RegularExpressions;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// Turns native flows, run records and config references into the structure-only form an AI request may carry.
/// Setting values, config values, secrets, conditions and expressions are never copied. Free text a person typed (names, block reasons, error text)
/// is passed through only when it looks like a plain label or, for the opt-in categories, after pattern-based scrubbing.
/// Scrubbing is best-effort: it cannot recognize a person's name in error text, which is why the payload is always previewed before it is sent.
/// </summary>
public static partial class AssistRedactor
{
    public const string Withheld = "(withheld)";
    public const int MaxBlockReasonChars = 300;
    public const int MaxRunDetailChars = 500;
    public const int MaxRunEvents = 40;

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[\p{L}\p{N}_ .:()\-]{1,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"\{\{(config|secret):([^{}\r\n]{1,200})\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.:\- ]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceNamePattern();

    /// <summary>The structure of a native flow: step kinds, module ids, setting names and states, retries and config references. No values.</summary>
    public static RedactedFlow Flow(string flowJson, IReadOnlyList<ModuleDescriptor> modules, bool includeBlockReasons = false)
    {
        if (string.IsNullOrWhiteSpace(flowJson)) throw new ArgumentException("The native flow definition is required.");
        var flow = NativeFlowDefinition.Read(flowJson, allowBlocked: true);
        var nodes = new List<RedactedNode>();
        var withheldLabels = 0;
        var blockReasons = 0;
        Walk(flow.Nodes, 0);

        var notes = new List<string>();
        if (withheldLabels > 0) notes.Add($"{withheldLabels} name(s) that did not look like a plain label were replaced with {Withheld}.");
        if (blockReasons > 0) notes.Add(includeBlockReasons
            ? $"{blockReasons} block reason(s) are included after credential, address and path scrubbing."
            : $"{blockReasons} block reason(s) were left out.");
        return new(flow.Durable, nodes, References(flow.Nodes), notes);

        void Walk(List<NativeFlowNode> list, int depth)
        {
            foreach (var node in list)
            {
                nodes.Add(Node(node, depth));
                Walk(node.Children, depth + 1);
                Walk(node.Otherwise, depth + 1);
            }
        }

        RedactedNode Node(NativeFlowNode node, int depth)
        {
            string? Text(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (LabelPattern().IsMatch(value.Trim())) return value.Trim();
                withheldLabels++;
                return Withheld;
            }

            string? reason = null;
            if (node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Length > 0)
            {
                blockReasons++;
                if (includeBlockReasons) reason = Scrub(node.BlockReason, MaxBlockReasonChars);
            }

            var keys = node.Settings.Keys.Concat(node.SettingExpressions is null ? [] : node.SettingExpressions.Keys.ToArray()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
            var settings = keys.Select(key => new RedactedSetting(
                IdentifierPattern().IsMatch(key) ? key : Withheld,
                node.SettingExpressions is { } calculated && calculated.Keys.Any(other => string.Equals(other, key, StringComparison.OrdinalIgnoreCase)) ? "calculated at send time"
                    : node.Settings.TryGetValue(key, out var value) ? ValueState(value) : "not set")).ToList();

            return new(
                IdentifierPattern().IsMatch(node.Id) ? node.Id : Withheld, depth, node.Kind.ToString(), Text(node.Name),
                node.Module.Length == 0 ? null : IdentifierPattern().IsMatch(node.Module) ? node.Module : Withheld,
                ModuleStatus(node, modules), Text(node.MessageName), Text(node.Channel), settings,
                node.Retry is null ? null : new(node.Retry.Attempts, node.Retry.IntervalSeconds, node.Retry.RetryAmbiguous),
                reason, node.Conditions.Count > 0 || node.Expression is not null);
        }
    }

    /// <summary>Which config values and secrets a flow refers to, and which steps refer to them. Names only; the values are never read.</summary>
    public static IReadOnlyList<ConfigReference> References(string flowJson)
    {
        if (string.IsNullOrWhiteSpace(flowJson)) throw new ArgumentException("The native flow definition is required.");
        return References(NativeFlowDefinition.Read(flowJson, allowBlocked: true).Nodes);
    }

    internal static IReadOnlyList<ConfigReference> References(List<NativeFlowNode> roots)
    {
        var found = new SortedDictionary<(string Kind, string Name), SortedSet<string>>(Comparer<(string Kind, string Name)>.Create(
            (left, right) => left.Kind != right.Kind ? string.CompareOrdinal(left.Kind, right.Kind) : string.CompareOrdinal(left.Name, right.Name)));
        void Visit(List<NativeFlowNode> list)
        {
            foreach (var node in list)
            {
                foreach (var value in node.Settings.Values)
                    foreach (Match match in ReferencePattern().Matches(value))
                    {
                        var name = match.Groups[2].Value.Trim();
                        var key = (match.Groups[1].Value.ToLowerInvariant(), ReferenceNamePattern().IsMatch(name) ? name : Withheld);
                        if (!found.TryGetValue(key, out var ids)) found[key] = ids = new(StringComparer.Ordinal);
                        ids.Add(IdentifierPattern().IsMatch(node.Id) ? node.Id : Withheld);
                    }
                Visit(node.Children);
                Visit(node.Otherwise);
            }
        }
        Visit(roots);
        return [.. found.Select(pair => new ConfigReference(pair.Key.Kind, pair.Key.Name, [.. pair.Value]))];
    }

    /// <summary>A run in the form an AI request may carry. Stages and states are kept; message content is not part of the input at all, and error text is included only on request and only after scrubbing.</summary>
    public static RedactedRun Run(AssistRunInput run, bool includeErrorText)
    {
        ArgumentNullException.ThrowIfNull(run);
        var events = new List<RedactedRunEvent>();
        var textWithheld = 0;
        foreach (var item in (run.Events ?? []).Take(MaxRunEvents))
        {
            string? detail = null;
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                if (includeErrorText) detail = Scrub(item.Detail, MaxRunDetailChars);
                else textWithheld++;
            }
            events.Add(new(Identifier(item.Stage), Identifier(item.State), detail));
        }

        var omitted = Math.Max(0, (run.Events?.Count ?? 0) - MaxRunEvents);
        var notes = new List<string>();
        if (textWithheld > 0) notes.Add($"{textWithheld} error text(s) were left out.");
        if (includeErrorText) notes.Add("Error text was scrubbed of addresses, paths, identifiers, credentials and long tokens. A name or other personal text inside a message cannot be recognized, so read it first.");
        if (omitted > 0) notes.Add($"{omitted} later event(s) were left out.");
        return new(Identifier(run.State),
            events,
            [.. (run.Deliveries ?? []).Take(MaxRunEvents).Select(delivery => new RedactedRunDelivery(Identifier(delivery.Stage), Identifier(delivery.State)))],
            notes);
    }

    private static string Identifier(string? value) => value is not null && IdentifierPattern().IsMatch(value) ? value : Withheld;

    internal static string ValueState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "not set";
        var trimmed = value.Trim();
        if (trimmed.StartsWith("{{secret:", StringComparison.OrdinalIgnoreCase)) return "refers to a secret";
        if (trimmed.StartsWith("{{config:", StringComparison.OrdinalIgnoreCase)) return "refers to a config value";
        return "set to a literal value";
    }

    private static string? ModuleStatus(NativeFlowNode node, IReadOnlyList<ModuleDescriptor> modules)
    {
        if (AssistExplainer.ModuleKindOf(node.Kind) is not { } kind || node.Module.Length == 0) return null;
        if (modules.Any(module => module.Kind == kind && string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase))) return "installed";
        return modules.Any(module => string.Equals(module.Id, node.Module, StringComparison.OrdinalIgnoreCase)) ? "installed as a different kind" : "not installed";
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*-----[\s\S]*?(?:-----END [A-Z ]*-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PemPattern();

    [GeneratedRegex(@"\b(password|passwd|pwd|secret|token|api[_-]?key|account[_-]?key|access[_-]?key|shared[_-]?access[_-]?signature|sig|authorization|connection[_-]?string)\b\s*[=:]\s*[^\s;,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPairPattern();

    [GeneratedRegex(@"\b(?:Bearer|Basic)\s+[A-Za-z0-9+/=._\-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthSchemePattern();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s'""<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?:\\\\[^\s'""<>|*?]+|[A-Za-z]:\\[^\s'""<>|*?]*)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex(@"(?<![\w/:.])/(?:[\w.\-~]+/)+[\w.\-~]*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();

    // Lowercase dotted names ending in letters look like host names. CLR type names are PascalCase, so exception types survive.
    [GeneratedRegex(@"\b(?:[a-z0-9\-]+\.)+[a-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"\b\d{6,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();

    [GeneratedRegex(@"[A-Za-z0-9+/=_\-]{24,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongTokenPattern();

    /// <summary>Replaces credential pairs, addresses, paths, identifiers and long tokens, removes control characters and clips the result.</summary>
    public static string Scrub(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(Math.Min(text.Length, maxChars * 4));
        foreach (var character in text)
        {
            if (builder.Length >= maxChars * 4) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var result = PemPattern().Replace(builder.ToString(), "[REMOVED]");
        result = AuthSchemePattern().Replace(result, "[REMOVED]");
        result = CredentialPairPattern().Replace(result, match => $"{match.Groups[1].Value}=[REMOVED]");
        result = UrlPattern().Replace(result, "[URL]");
        result = EmailPattern().Replace(result, "[EMAIL]");
        result = GuidPattern().Replace(result, "[ID]");
        result = Ipv4Pattern().Replace(result, "[ADDRESS]");
        result = WindowsPathPattern().Replace(result, "[PATH]");
        result = UnixPathPattern().Replace(result, "[PATH]");
        result = HostPattern().Replace(result, "[HOST]");
        result = LongNumberPattern().Replace(result, "[NUMBER]");
        result = LongTokenPattern().Replace(result, "[TOKEN]");
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result.Length <= maxChars ? result : result[..maxChars] + "...";
    }
}
