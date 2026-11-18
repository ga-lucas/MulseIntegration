using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Service.BizTalkImport;

/// <summary>
/// Recognizes the BizTalk HL7 v2 (BTAHL7) schema style from schema text alone: segment definitions named by a three-character segment ID whose first field is named
/// <c>ID_1_</c> or <c>ID.2_</c> (MSH). The native HL7 renderer is a plugin module, so the importer only needs this evidence, never the module itself.
/// </summary>
internal static class BizTalkHl7SchemaEvidence
{
    /// <summary>The render-dispatch value (and module setting source) for HL7 messages: the wrapper root the importer builds for HL7 multipart messages.</summary>
    internal const string ChoiceValue = NativeSourceExpressionReader.MultipartRootName;
    internal const string ModuleId = "hl7-schema-render";
    internal const string StoredName = "biztalk-hl7.schemas";

    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly Regex SegmentName = new(@"^([A-Z][A-Z0-9]{2})(?:_.*)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when the first document declares a message element and the set defines at least one HL7 segment.</summary>
    internal static bool IsMessageSchema(IReadOnlyList<string> documents)
    {
        if (documents.Count == 0) return false;
        try
        {
            var loaded = documents.Select(Load).ToArray();
            return loaded[0].Root?.Elements(Xs + "element").Any() == true && loaded.Any(HasSegment);
        }
        catch (Exception exception) when (exception is XmlException or RegexMatchTimeoutException) { return false; }
    }

    /// <summary>True when two documents define the same named type or element in the same namespace with different content; the renderer cannot choose between them.</summary>
    internal static bool Conflicts(IEnumerable<string> documents)
    {
        var seen = new Dictionary<(string Namespace, string Kind, string Name), XElement>();
        foreach (var document in documents.Select(Load))
        {
            var targetNamespace = (string?)document.Root?.Attribute("targetNamespace") ?? string.Empty;
            foreach (var definition in document.Root?.Elements() ?? [])
            {
                if ((string?)definition.Attribute("name") is not { } name) continue;
                var key = (targetNamespace, definition.Name.LocalName, name);
                if (!seen.TryAdd(key, definition) && !XNode.DeepEquals(seen[key], definition)) return true;
            }
        }
        return false;
    }

    private static XDocument Load(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static bool HasSegment(XDocument document) => document.Descendants()
        .Where(element => element.Name == Xs + "complexType" || element.Name == Xs + "element")
        .Any(element =>
        {
            if ((string?)element.Attribute("name") is not { } name || SegmentName.Match(name) is not { Success: true } match) return false;
            var first = element.Descendants(Xs + "element").Select(child => (string?)child.Attribute("name")).FirstOrDefault();
            return first is not null && Regex.IsMatch(first, "^" + match.Groups[1].Value + @"[._]\d+_", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        });
}
