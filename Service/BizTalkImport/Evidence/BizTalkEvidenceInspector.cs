using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport.Evidence;

/// <summary>
/// Looks at supplied evidence without any AI: what it appears to be, a few structural facts (counts and names, never values) and whether it holds credential-looking values.
/// It also removes credential-looking values from text before any excerpt can be shared. It never decides that a request is satisfied; only a later analysis does that.
/// </summary>
public static partial class BizTalkEvidenceInspector
{
    public const string Removed = "[REMOVED]";

    private const int MaxDecodedBytes = 1_000_000;
    private const int MaxNames = 15;
    private const int MaxZipEntries = 5_000;

    private const string SecretWords = "password|passwd|pwd|secret|token|apikey|accesskey|accountkey|privatekey|credential|connectionstring|sharedaccess";

    // The lookbehind keeps a long run of name characters from being rescanned from every position.
    [GeneratedRegex($@"(?<![\w:.-])(?<name>[\w:.-]*(?:{SecretWords})[\w:.-]*)\s*=\s*(?<q>[""'])(?<v>[^""']*)\k<q>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 2000)]
    private static partial Regex SecretAttribute();

    [GeneratedRegex($@"<(?<name>[\w:.-]*(?:{SecretWords})[\w:.-]*)(?<attrs>\s[^>]*)?>(?<v>[^<]+)</\k<name>>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretElement();

    [GeneratedRegex($@"&lt;(?<name>[\w:.-]*(?:{SecretWords})[\w:.-]*)(?<attrs>(?:\s(?!&gt;).*?)?)&gt;(?<v>(?:(?!&lt;).)+?)&lt;/\k<name>&gt;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EscapedSecretElement();

    [GeneratedRegex($@"(?<pre>\bkey\s*=\s*[""'][^""']*(?:{SecretWords})[^""']*[""']\s+value\s*=\s*)(?<q>[""'])(?<v>[^""']*)\k<q>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValue();

    [GeneratedRegex(@"(?<k>\b(?:password|pwd|user id|uid|shared ?access ?key(?:name)?|accountkey|accesskey)\s*=\s*)(?<v>[^;""'<>&\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"\b(?:Bearer|Basic)\s+(?<v>[A-Za-z0-9._~+/=-]{8,})", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(?<s>\b[a-z][a-z0-9+.-]*://)(?<v>[^/\s:@""'<>]+:[^/\s@""'<>]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"\bnamespace\s+(?<ns>[\w.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(@"\b(?:class|struct)\s+(?<name>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\bpublic\s+static\s+[\w<>\[\],.?\s]+?\s+(?<name>\w+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex StaticMethod();

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SolutionProject();

    /// <summary>Replaces credential-looking values with <see cref="Removed"/> and counts them. References such as {{secret:name}} and already removed values are left alone.</summary>
    public static string Scrub(string text, out int removed)
    {
        try { return ScrubCore(text, out removed); }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: if the text cannot be checked in time, none of it is shared.
            removed = 1;
            return Removed;
        }
    }

    private static string ScrubCore(string text, out int removed)
    {
        var count = 0;
        string Hide(string value) { if (IsOpaque(value)) return value; count++; return Removed; }
        bool IsOpaque(string value) => value.Length == 0 || value == Removed || value.StartsWith("{{", StringComparison.Ordinal);

        text = SecretAttribute().Replace(text, m => $"{m.Groups["name"].Value}={m.Groups["q"].Value}{Hide(m.Groups["v"].Value)}{m.Groups["q"].Value}");
        text = SecretElement().Replace(text, m => $"<{m.Groups["name"].Value}{m.Groups["attrs"].Value}>{Hide(m.Groups["v"].Value)}</{m.Groups["name"].Value}>");
        text = EscapedSecretElement().Replace(text, m => $"&lt;{m.Groups["name"].Value}{m.Groups["attrs"].Value}&gt;{Hide(m.Groups["v"].Value)}&lt;/{m.Groups["name"].Value}&gt;");
        text = SecretKeyValue().Replace(text, m => $"{m.Groups["pre"].Value}{m.Groups["q"].Value}{Hide(m.Groups["v"].Value)}{m.Groups["q"].Value}");
        text = ConnectionStringSecret().Replace(text, m => m.Groups["k"].Value + Hide(m.Groups["v"].Value));
        text = AuthorizationValue().Replace(text, m => m.Value[..m.Value.IndexOf(m.Groups["v"].Value, StringComparison.Ordinal)] + Hide(m.Groups["v"].Value));
        text = PrivateKeyBlock().Replace(text, _ => Hide("key"));
        text = UrlUserInfo().Replace(text, m => m.Groups["s"].Value + Hide(m.Groups["v"].Value) + "@");
        removed = count;
        return text;
    }

    /// <summary>A scrubbed excerpt at most <paramref name="maxChars"/> long, with a note when it was cut.</summary>
    public static string Excerpt(string text, int maxChars)
    {
        var scrubbed = Scrub(text, out _);
        return scrubbed.Length <= maxChars ? scrubbed : $"{scrubbed[..maxChars]}\n[cut: {scrubbed.Length - maxChars} more characters]";
    }

    /// <summary>The text of an item the server may read: pasted text, or an uploaded single file that is text. Null for paths, zips, binary files, real output and anything too large.</summary>
    public static string? ReadableText(BizTalkAssistEvidence item)
    {
        if (item.Kind == BizTalkEvidenceKind.RealOutput) return null;
        if (item.Origin == BizTalkEvidenceOrigin.Text) return item.Text is { Length: <= MaxDecodedBytes } text ? text : null;
        if (item.Origin != BizTalkEvidenceOrigin.Upload || Decode(item) is not { } bytes || IsZip(bytes)) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return null; }
    }

    public static BizTalkEvidenceFinding Inspect(BizTalkAssistEvidence item)
    {
        var name = DisplayName(item);
        var declared = item.Kind.ToString();
        var facts = new List<string>();
        var warnings = new List<string>();

        if (item.Kind == BizTalkEvidenceKind.RealOutput)
        {
            facts.Add($"Recorded as real output ({SizeOf(item)}).");
            warnings.Add("Real output is never read for this check and is never sent to an AI provider.");
            return new(item.Id, name, declared, declared, true, facts, warnings, 0);
        }

        if (item.Origin == BizTalkEvidenceOrigin.Path)
        {
            facts.Add("A path on the server. It is not opened for this check; the importer reads it when you analyze.");
            return new(item.Id, name, declared, "Unknown", true, facts, warnings, 0);
        }

        var bytes = item.Origin == BizTalkEvidenceOrigin.Upload ? Decode(item) : null;
        if (item.Origin == BizTalkEvidenceOrigin.Upload && bytes is null)
        {
            warnings.Add($"The upload is empty, not valid base64, or larger than {MaxDecodedBytes / 1000} KB, so it was not inspected here.");
            return new(item.Id, name, declared, "Unknown", true, facts, warnings, 0);
        }

        if (bytes is not null && IsZip(bytes))
        {
            var detected = InspectZip(bytes, facts, warnings);
            return new(item.Id, name, declared, detected, Matches(item.Kind, detected), facts, warnings, 0);
        }

        var text = ReadableText(item);
        if (text is null)
        {
            warnings.Add(bytes is null ? "The pasted text is empty or larger than the inspection limit." : "The file is not UTF-8 text, so it was not inspected.");
            return new(item.Id, name, declared, "Unknown", true, facts, warnings, 0);
        }

        _ = Scrub(text, out var credentialLike);
        var kind = Describe(text, name, facts, warnings);
        facts.Add($"{text.Split('\n').Length} line(s), {text.Length} character(s).");
        if (!Matches(item.Kind, kind)) warnings.Add($"Supplied as {declared}, but it looks like {kind}.");
        if (credentialLike > 0)
            warnings.Add($"{credentialLike} credential-looking value(s). They are removed from anything sent to an AI provider. Consider removing them from the file before sharing it more widely.");
        return new(item.Id, name, declared, kind, Matches(item.Kind, kind), facts, warnings, credentialLike);
    }

    private static bool Matches(BizTalkEvidenceKind declared, string detected)
        => detected == "Unknown" || string.Equals(declared.ToString(), detected, StringComparison.Ordinal);

    private static string Describe(string text, string name, List<string> facts, List<string> warnings)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<'))
        {
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 5_000_000
                });
                document = XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                warnings.Add($"Not well-formed XML (line {exception.LineNumber}, position {exception.LinePosition}).");
                return "Unknown";
            }

            var root = document.Root!;
            switch (root.Name.LocalName)
            {
                case "BindingInfo":
                    DescribeBindings(root, facts);
                    return "Bindings";
                case "brl":
                    DescribePolicies(root, facts);
                    return "RulePolicy";
                case "configuration":
                    DescribeHostConfig(root, facts);
                    return "HostConfig";
                case "Project" when root.Descendants().Any(e => e.Name.LocalName is "Import" && ((string?)e.Attribute("Project"))?.Contains("BizTalk", StringComparison.OrdinalIgnoreCase) == true):
                    facts.Add("A BizTalk project file.");
                    return "Source";
                default:
                    facts.Add($"XML with root element <{root.Name.LocalName}>.");
                    return "Unknown";
            }
        }

        if (trimmed.StartsWith("Microsoft Visual Studio Solution File", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var projects = SolutionProject().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();
            facts.Add($"A solution with {projects.Length} project(s){Names(projects)}.");
            return "Source";
        }

        if (NamespaceDeclaration().IsMatch(text) || TypeDeclaration().IsMatch(text) && text.Contains("static", StringComparison.Ordinal))
        {
            var types = TypeDeclaration().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();
            var methods = StaticMethod().Matches(text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();
            if (NamespaceDeclaration().Match(text) is { Success: true } ns) facts.Add($"C# in namespace {ns.Groups["ns"].Value}.");
            facts.Add($"{types.Length} type(s){Names(types)}; {methods.Length} public static method(s){Names(methods)}.");
            return "HelperSource";
        }

        facts.Add("Plain text that is not XML, a solution or C# source.");
        return "Unknown";
    }

    private static void DescribeBindings(XElement root, List<string> facts)
    {
        var receive = root.Descendants().Where(e => e.Name.LocalName == "ReceivePort").Select(e => (string?)e.Attribute("Name")).OfType<string>().Distinct().ToArray();
        var send = root.Descendants().Where(e => e.Name.LocalName == "SendPort").Select(e => (string?)e.Attribute("Name")).OfType<string>().Distinct().ToArray();
        var locations = root.Descendants().Count(e => e.Name.LocalName == "ReceiveLocation");
        facts.Add($"A BizTalk binding export: {receive.Length} receive port(s){Names(receive)}, {locations} receive location(s), {send.Length} send port(s){Names(send)}.");
        if ((string?)root.Attribute("BindingStatus") is { Length: > 0 } status)
            facts.Add($"Binding status {status}, {root.Attribute("BoundEndpoints")?.Value ?? "?"} of {root.Attribute("TotalEndpoints")?.Value ?? "?"} endpoint(s) bound.");
    }

    private static void DescribePolicies(XElement root, List<string> facts)
    {
        foreach (var set in root.Elements().Where(e => e.Name.LocalName == "ruleset").Take(MaxNames))
        {
            var version = set.Elements().FirstOrDefault(e => e.Name.LocalName == "version");
            var rules = set.Descendants().Count(e => e.Name.LocalName == "rule");
            facts.Add($"Policy '{(string?)set.Attribute("name")}' version {version?.Attribute("major")?.Value ?? "?"}.{version?.Attribute("minor")?.Value ?? "?"} with {rules} rule(s).");
        }
        if (facts.Count == 0) facts.Add("A business rules export with no policy in it.");
    }

    private static void DescribeHostConfig(XElement root, List<string> facts)
    {
        var keys = root.Descendants().Where(e => e.Name.LocalName == "appSettings").SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "add"))
            .Select(e => (string?)e.Attribute("key")).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        facts.Add($"A .NET configuration file with {keys.Length} appSetting key(s){Names(keys)}. Values are never listed.");
        var redirects = root.Descendants().Count(e => e.Name.LocalName == "dependentAssembly");
        if (redirects > 0) facts.Add($"{redirects} assembly binding redirect(s).");
    }

    private static string InspectZip(byte[] bytes, List<string> facts, List<string> warnings)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxZipEntries)
            {
                warnings.Add($"The zip has more than {MaxZipEntries} entries, so it was not listed.");
                return "Unknown";
            }

            int Count(string extension) => archive.Entries.Count(e => e.FullName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
            var counts = new (string Label, int Count)[]
            {
                (".sln", Count(".sln")), (".btproj", Count(".btproj")), (".odx", Count(".odx")), (".btm", Count(".btm")), (".xsd", Count(".xsd")),
                (".cs", Count(".cs")), (".xml", Count(".xml")), (".config", Count(".config"))
            }.Where(c => c.Count > 0).ToArray();
            facts.Add($"A zip with {archive.Entries.Count} entr(ies): {(counts.Length == 0 ? "no recognised file types" : string.Join(", ", counts.Select(c => $"{c.Count} {c.Label}")))}. Contents are not read.");
            if (archive.Entries.Any(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) warnings.Add("The zip contains another zip. The importer does not open nested archives.");
            return Count(".btproj") + Count(".sln") + Count(".odx") > 0 ? "Source" : "Unknown";
        }
        catch (InvalidDataException)
        {
            warnings.Add("The file starts like a zip but could not be read as one.");
            return "Unknown";
        }
    }

    private static string Names(IReadOnlyCollection<string> values)
        => values.Count == 0 ? "" : $": {string.Join(", ", values.Take(MaxNames))}{(values.Count > MaxNames ? $" and {values.Count - MaxNames} more" : "")}";

    private static string DisplayName(BizTalkAssistEvidence item)
    {
        var name = item.Name;
        if (string.IsNullOrWhiteSpace(name)) return item.Id;
        name = name.Trim();
        // A path names a machine; the file name alone is enough to recognize the evidence.
        var separator = name.LastIndexOfAny(['\\', '/']);
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private static string SizeOf(BizTalkAssistEvidence item)
        => item.Origin == BizTalkEvidenceOrigin.Text ? $"{item.Text?.Length ?? 0} characters" : item.Origin == BizTalkEvidenceOrigin.Upload ? $"{item.ContentBase64?.Length * 3 / 4 ?? 0} bytes" : "a path";

    private static bool IsZip(byte[] bytes) => bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] is 3 or 5;

    private static byte[]? Decode(BizTalkAssistEvidence item)
    {
        if (string.IsNullOrEmpty(item.ContentBase64) || item.ContentBase64.Length > MaxDecodedBytes * 4 / 3 + 8) return null;
        try { return Convert.FromBase64String(item.ContentBase64); }
        catch (FormatException) { return null; }
    }
}
