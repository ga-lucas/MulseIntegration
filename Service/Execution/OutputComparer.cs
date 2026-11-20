using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Service.Models;

namespace Service.Execution;

/// <summary>
/// Deterministic comparison of an expected (real) output with Mulse's output. XML is compared structurally and each difference is classified;
/// anything else is compared as text. It never decides equivalence: the verdict only says how the two samples differ.
/// </summary>
public static class OutputComparer
{
    public const int MaxDifferences = 200;
    public const int MaxChars = 1_000_000;
    private const int MaxValueChars = 200;

    /// <summary>Every class the comparer can report. Anything else is not a comparer result.</summary>
    public static readonly IReadOnlySet<string> KnownClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "ByteOrderMark", "XmlDeclaration", "Whitespace", "NamespacePrefix", "AttributeOrder", "EmptyElementForm", "Encoding", "ElementOrder",
        "MissingElement", "ExtraElement", "MissingAttribute", "ExtraAttribute", "Structure", "Namespace", "ElementName", "Format",
        "DateTimeFormat", "DateTimeValue", "NumberFormat", "ValueCase", "ValueWhitespace", "Value",
        "LineEndings", "TrailingNewline", "TrailingSpaces", "MissingLine", "ExtraLine"
    };

    private static readonly string[] MomentFormats =
    [
        "yyyyMMdd", "yyyyMMddHHmm", "yyyyMMddHHmmss", "yyyyMMddHHmmss.FFFF", "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy",
        "M/d/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"
    ];

    public static OutputComparison Compare(string expected, string actual)
    {
        if (expected is null || actual is null) throw new ArgumentException("Supply both the expected and the actual output.");
        if (expected.Length > MaxChars || actual.Length > MaxChars) throw new ArgumentException($"Each output can be at most {MaxChars:N0} characters.");
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return new(OutputVerdicts.Identical, [], false);

        var diffs = new Collector();
        var expectedBom = expected.StartsWith('\uFEFF');
        var actualBom = actual.StartsWith('\uFEFF');
        if (expectedBom != actualBom)
            diffs.Add("ByteOrderMark", "/", expectedBom ? "present" : "absent", actualBom ? "present" : "absent", true, "A byte-order mark is present on one side only.");
        var left = expected.TrimStart('\uFEFF');
        var right = actual.TrimStart('\uFEFF');

        var leftXml = TryParse(left, out var leftDoc);
        var rightXml = TryParse(right, out var rightDoc);
        if (leftXml && rightXml) CompareDocuments(leftDoc!, rightDoc!, diffs);
        else if (leftXml != rightXml)
            diffs.Add("Format", "/", leftXml ? "XML" : "not XML", rightXml ? "XML" : "not XML", false, "One output is well-formed XML and the other is not.");
        else CompareText(left, right, diffs);

        var verdict = diffs.Items.All(item => item.Cosmetic) ? OutputVerdicts.FormattingOnly : OutputVerdicts.Different;
        return new(verdict, diffs.Items, diffs.Truncated);
    }

    private sealed class Collector
    {
        public List<OutputDifference> Items { get; } = [];
        public bool Truncated { get; private set; }
        public bool Full => Items.Count >= MaxDifferences;

        public void MarkTruncated() => Truncated = true;

        public void Add(string cls, string path, string? expected, string? actual, bool cosmetic, string explanation)
        {
            if (Full) { Truncated = true; return; }
            Items.Add(new(cls, path, Clip(expected), Clip(actual), cosmetic, explanation));
        }
    }

    private static string? Clip(string? value) => value is { Length: > MaxValueChars } ? value[..MaxValueChars] + "…" : value;

    private static bool TryParse(string text, out XDocument? document)
    {
        document = null;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '<') return false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 5_000_000
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return document.Root is not null;
        }
        catch (XmlException) { return false; }
    }

    private static void CompareDocuments(XDocument expected, XDocument actual, Collector diffs)
    {
        CompareDeclaration(expected.Declaration, actual.Declaration, diffs);
        var left = expected.Root!;
        var right = actual.Root!;
        var rootPath = "/" + left.Name.LocalName;
        if (left.Name != right.Name)
        {
            diffs.Add(left.Name.LocalName == right.Name.LocalName ? "Namespace" : "ElementName", "/", left.Name.ToString(), right.Name.ToString(), false,
                "The root elements differ, so the rest of the document was not compared.");
            return;
        }
        var prefixes = new HashSet<(string, string, string)>();
        CompareElements(left, right, rootPath, diffs, prefixes);
        var leftSpaces = IndentSignature(expected);
        var rightSpaces = IndentSignature(actual);
        if (!leftSpaces.SequenceEqual(rightSpaces, StringComparer.Ordinal))
            diffs.Add("Whitespace", "/", null, null, true, "Indentation or spacing between elements differs.");
    }

    private static List<string> IndentSignature(XDocument document)
        => document.DescendantNodes().OfType<XText>().Where(text => text.Parent is { HasElements: true } && string.IsNullOrWhiteSpace(text.Value)).Select(text => text.Value).ToList();

    private static void CompareDeclaration(XDeclaration? expected, XDeclaration? actual, Collector diffs)
    {
        if (expected is null && actual is null) return;
        if (expected is null || actual is null)
        {
            diffs.Add("XmlDeclaration", "/", expected?.ToString() ?? "absent", actual?.ToString() ?? "absent", true, "The XML declaration is present on one side only.");
            return;
        }
        if (!string.Equals(expected.Encoding, actual.Encoding, StringComparison.OrdinalIgnoreCase))
            diffs.Add("Encoding", "/", expected.Encoding ?? "(none)", actual.Encoding ?? "(none)", false, "The declared encoding differs, so the bytes written may differ.");
        if (!string.Equals(expected.Version, actual.Version, StringComparison.Ordinal) || !string.Equals(expected.Standalone, actual.Standalone, StringComparison.OrdinalIgnoreCase))
            diffs.Add("XmlDeclaration", "/", expected.ToString(), actual.ToString(), true, "The version or standalone setting in the declaration differs.");
    }

    private static string PrefixOf(XElement element)
        => element.GetPrefixOfNamespace(element.Name.Namespace) is { Length: > 0 } prefix ? prefix : "(default)";

    private static void CompareElements(XElement expected, XElement actual, string path, Collector diffs, HashSet<(string, string, string)> prefixes)
    {
        if (diffs.Full) { diffs.MarkTruncated(); return; }
        if (expected.Name.Namespace != XNamespace.None)
        {
            var key = (expected.Name.NamespaceName, PrefixOf(expected), PrefixOf(actual));
            if (key.Item2 != key.Item3 && prefixes.Add(key))
                diffs.Add("NamespacePrefix", path, key.Item2, key.Item3, true, $"Namespace '{key.Item1}' uses a different prefix. The names are the same.");
        }
        CompareAttributes(expected, actual, path, diffs);

        var leftKids = expected.Elements().ToList();
        var rightKids = actual.Elements().ToList();
        if (leftKids.Count == 0 && rightKids.Count == 0)
        {
            if (expected.Value != actual.Value) ClassifyValue(path, expected.Value, actual.Value, diffs);
            else if (expected.IsEmpty != actual.IsEmpty) diffs.Add("EmptyElementForm", path, expected.IsEmpty ? "<a/>" : "<a></a>", actual.IsEmpty ? "<a/>" : "<a></a>", true, "An empty element is written in a different form.");
            return;
        }
        if (leftKids.Count == 0 || rightKids.Count == 0)
        {
            diffs.Add("Structure", path, leftKids.Count == 0 ? "text only" : "child elements", rightKids.Count == 0 ? "text only" : "child elements", false,
                "The element has child elements on one side only.");
            return;
        }
        var leftText = DirectText(expected);
        var rightText = DirectText(actual);
        if (leftText != rightText) ClassifyValue(path + "/text()", leftText, rightText, diffs);

        var leftNames = leftKids.Select(kid => kid.Name).ToList();
        var rightNames = rightKids.Select(kid => kid.Name).ToList();
        var sameMultiset = leftNames.Count == rightNames.Count
            && leftNames.GroupBy(name => name).All(group => rightNames.Count(name => name == group.Key) == group.Count());
        if (sameMultiset && !leftNames.SequenceEqual(rightNames))
            diffs.Add("ElementOrder", path, string.Join(", ", leftNames.Take(8).Select(name => name.LocalName)), string.Join(", ", rightNames.Take(8).Select(name => name.LocalName)), false,
                "The same child elements appear in a different order.");

        var rightByName = rightKids.GroupBy(kid => kid.Name).ToDictionary(group => group.Key, group => group.ToList());
        var seen = new Dictionary<XName, int>();
        foreach (var kid in leftKids)
        {
            var index = seen.GetValueOrDefault(kid.Name);
            seen[kid.Name] = index + 1;
            var total = Math.Max(leftNames.Count(name => name == kid.Name), rightByName.TryGetValue(kid.Name, out var list) ? list.Count : 0);
            var childPath = path + "/" + kid.Name.LocalName + (total > 1 ? $"[{index + 1}]" : "");
            if (rightByName.TryGetValue(kid.Name, out var matches) && index < matches.Count)
                CompareElements(kid, matches[index], childPath, diffs, prefixes);
            else
                diffs.Add("MissingElement", childPath, "present", "absent", false, "The expected output has an element that Mulse's output does not.");
        }
        foreach (var (name, list) in rightByName)
        {
            var expectedCount = leftNames.Count(item => item == name);
            for (var index = expectedCount; index < list.Count; index++)
                diffs.Add("ExtraElement", path + "/" + name.LocalName + (list.Count > 1 ? $"[{index + 1}]" : ""), "absent", "present", false, "Mulse's output has an element that the expected output does not.");
        }
    }

    private static string DirectText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(text => text.Value)).Trim();

    private static void CompareAttributes(XElement expected, XElement actual, string path, Collector diffs)
    {
        var left = expected.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToList();
        var right = actual.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToList();
        var rightByName = right.ToDictionary(attribute => attribute.Name);
        foreach (var attribute in left)
        {
            var attributePath = path + "/@" + attribute.Name.LocalName;
            if (!rightByName.TryGetValue(attribute.Name, out var other)) diffs.Add("MissingAttribute", attributePath, attribute.Value, null, false, "The expected output has an attribute that Mulse's output does not.");
            else if (attribute.Value != other.Value) ClassifyValue(attributePath, attribute.Value, other.Value, diffs);
        }
        var leftNames = left.Select(attribute => attribute.Name).ToHashSet();
        foreach (var attribute in right.Where(attribute => !leftNames.Contains(attribute.Name)))
            diffs.Add("ExtraAttribute", path + "/@" + attribute.Name.LocalName, null, attribute.Value, false, "Mulse's output has an attribute that the expected output does not.");
        if (left.Count == right.Count && left.All(attribute => rightByName.ContainsKey(attribute.Name))
            && !left.Select(attribute => attribute.Name).SequenceEqual(right.Select(attribute => attribute.Name)))
            diffs.Add("AttributeOrder", path, null, null, true, "The same attributes are written in a different order.");
    }

    private static void ClassifyValue(string path, string expected, string actual, Collector diffs)
    {
        if (expected.Trim() == actual.Trim())
            diffs.Add("ValueWhitespace", path, expected, actual, false, "Leading or trailing spaces differ.");
        else if (TryMoment(expected.Trim(), out var left) && TryMoment(actual.Trim(), out var right))
        {
            if (left == right) diffs.Add("DateTimeFormat", path, expected, actual, false, "The same date or time is written in a different format.");
            else diffs.Add("DateTimeValue", path, expected, actual, false, "Both look like dates but differ; this may be the run time or another clock-dependent value.");
        }
        else if (decimal.TryParse(expected.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && decimal.TryParse(actual.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var other) && number == other)
            diffs.Add("NumberFormat", path, expected, actual, false, "The same number is written in a different format.");
        else if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            diffs.Add("ValueCase", path, expected, actual, false, "The values differ only in letter case.");
        else
            diffs.Add("Value", path, expected, actual, false, "The values differ.");
    }

    private static bool TryMoment(string value, out DateTime moment)
        => DateTime.TryParseExact(value, MomentFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out moment);

    private static string LineStyle(string text)
        => text.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : text.Contains('\n') ? "LF" : text.Contains('\r') ? "CR" : "none";

    private static void CompareText(string expected, string actual, Collector diffs)
    {
        var leftStyle = LineStyle(expected);
        var rightStyle = LineStyle(actual);
        if (leftStyle != rightStyle && leftStyle != "none" && rightStyle != "none")
            diffs.Add("LineEndings", "/", leftStyle, rightStyle, false, "The line separator differs. Some consumers (flat files, HL7) require a specific one.");
        var left = Normalize(expected);
        var right = Normalize(actual);
        if (left == right) return;
        if (left.TrimEnd('\n') == right.TrimEnd('\n'))
        {
            diffs.Add("TrailingNewline", "/", left.EndsWith('\n') ? "present" : "absent", right.EndsWith('\n') ? "present" : "absent", false, "A final line break is present on one side only.");
            return;
        }
        var leftLines = left.Split('\n');
        var rightLines = right.Split('\n');
        for (var index = 0; index < Math.Max(leftLines.Length, rightLines.Length); index++)
        {
            var path = "line " + (index + 1);
            if (index >= leftLines.Length) diffs.Add("ExtraLine", path, null, rightLines[index], false, "Mulse's output has a line that the expected output does not.");
            else if (index >= rightLines.Length) diffs.Add("MissingLine", path, leftLines[index], null, false, "The expected output has a line that Mulse's output does not.");
            else if (leftLines[index] != rightLines[index])
            {
                if (leftLines[index].TrimEnd() == rightLines[index].TrimEnd()) diffs.Add("TrailingSpaces", path, leftLines[index], rightLines[index], false, "Spaces at the end of the line differ.");
                else diffs.Add("Value", path, leftLines[index], rightLines[index], false, "The lines differ.");
            }
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
