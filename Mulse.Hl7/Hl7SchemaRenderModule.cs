using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;

namespace Mulse.Hl7;

/// <summary>
/// Renders HL7 v2 XML that follows the BizTalk BTAHL7 schemas (segment elements such as <c>PID</c> with field elements <c>PID_3_PatientIdentifierList</c>,
/// component elements and sub-component elements named by the datatype schemas) to ER7 text. The schemas are a setting; they give the order of components
/// and sub-components, which the element names alone do not. The input is one message document or an <c>HL7Multipart</c> wrapper whose children are
/// message documents (header part, then body part), rendered in order. Every behavior the schemas leave open is a setting.
/// </summary>
public sealed class Hl7SchemaRenderModule : IRenderModule
{
    internal const string MultipartRootName = "HL7Multipart";
    private const int MaximumCachedSchemas = 32;
    private static readonly ConcurrentDictionary<string, Hl7SchemaSet> Compiled = new(StringComparer.Ordinal);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("schemaText", "HL7 schemas", "The XSD documents of the HL7 messages this step can render, as a JSON array of XSD texts: every message schema plus the segment and datatype schemas they import. A single XSD is also accepted. Each message picks its schema by its root element name. Can be a {{config:name}} reference.", true, ModuleSettingInputKind.TextArea),
        new("trimTrailingDelimiters", "Trim trailing delimiters", "Removes empty trailing fields from each segment. Imported from the BTAHL7 assembler 'TrailingDelimiterAllowed' property (allowed = off).", false, ModuleSettingInputKind.Boolean, "false"),
        new("trimTrailingComponents", "Trim trailing component delimiters", "Removes empty trailing components and sub-components from each field (PID|^^^|). BizTalk's behavior was not verified; off keeps every delimiter.", false, ModuleSettingInputKind.Boolean, "false"),
        new("segmentTerminator", "Segment terminator", "Characters ending each segment. CR is the HL7 standard.", false, ModuleSettingInputKind.Select, "CR",
            [new("CR", "CR (HL7 standard)"), new("LF", "LF"), new("CRLF", "CRLF")]),
        new("escapeSequences", "Escape sequences in values", "Whether a delimiter or escape character inside a value is written as an HL7 escape sequence (Encode: \\F\\ \\S\\ \\T\\ \\R\\ \\E\\) or written as is (None, assumed because the imported assembler's behavior was not verified; a value containing a delimiter then makes the message ambiguous).", false, ModuleSettingInputKind.Select, "None",
            [new("None", "Write as is"), new("Encode", "Encode as escape sequences")]),
        new("fieldSeparator", "Field separator", "Used when the MSH segment has no MSH.1 element. A message that has MSH.1 uses it.", false, ModuleSettingInputKind.Text, "|"),
        new("encodingCharacters", "Encoding characters", "Component, repetition, escape and sub-component characters, used when the MSH segment has no MSH.2 value. A message that has MSH.2 uses it.", false, ModuleSettingInputKind.Text, "^~\\&")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "hl7-schema-render",
        "HL7 schema XML to ER7 render",
        ModuleKind.Render,
        "Renders HL7 v2 XML that follows the BizTalk BTAHL7 schemas into pipe-delimited ER7 text, using the schemas to place fields, components and sub-components. Messages that do not match their schema stop with an error naming the element.",
        SettingDescriptors,
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var schemas = Schemas(step.Settings.TryGetValue("schemaText", out var schemaText) && !string.IsNullOrWhiteSpace(schemaText)
            ? schemaText : throw new ArgumentException($"Module '{Descriptor.Id}' requires the setting 'schemaText'."));
        var options = ReadOptions(step.Settings);
        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = Render(payload.Name, payload.Content.ToString(), schemas, options);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase) { ["renderedBy"] = Descriptor.Id, ["renderedFormat"] = "Hl7" };
            return new IntegrationPayload(Path.ChangeExtension(payload.Name, ".hl7"), BinaryData.FromString(rendered), "text/hl7-v2", metadata);
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    internal sealed record Options(bool TrimFields, bool TrimComponents, string Terminator, bool Escape, char FieldSeparator, string EncodingCharacters);

    internal static Options ReadOptions(IReadOnlyDictionary<string, string> settings)
    {
        string Text(string key, string fallback) => settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        bool Flag(string key) => bool.TryParse(Text(key, "false"), out var value) && value;
        var terminator = Text("segmentTerminator", "CR").ToUpperInvariant() switch
        {
            "CR" => "\r",
            "LF" => "\n",
            "CRLF" => "\r\n",
            var other => throw new ArgumentException($"Unsupported segment terminator '{other}'; use CR, LF or CRLF.")
        };
        var escape = Text("escapeSequences", "None");
        if (!escape.Equals("None", StringComparison.OrdinalIgnoreCase) && !escape.Equals("Encode", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Setting 'escapeSequences' must be None or Encode.");
        var separator = Text("fieldSeparator", "|");
        var encoding = settings.TryGetValue("encodingCharacters", out var encodingText) && !string.IsNullOrEmpty(encodingText) ? encodingText : "^~\\&";
        if (separator.Length != 1) throw new ArgumentException("Setting 'fieldSeparator' must be one character.");
        if (encoding.Length != 4) throw new ArgumentException("Setting 'encodingCharacters' must be four characters.");
        return new(Flag("trimTrailingDelimiters"), Flag("trimTrailingComponents"), terminator, escape.Equals("Encode", StringComparison.OrdinalIgnoreCase), separator[0], encoding);
    }

    internal static Hl7SchemaSet Schemas(string schemaText)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaText)));
        if (Compiled.TryGetValue(key, out var cached)) return cached;
        var set = Hl7SchemaSet.Read(SplitDocuments(schemaText));
        if (Compiled.Count >= MaximumCachedSchemas) Compiled.Clear();
        Compiled[key] = set;
        return set;
    }

    internal static IReadOnlyList<string> SplitDocuments(string schemaText)
    {
        var trimmed = schemaText.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith('[')) return [schemaText];
        try
        {
            var items = JsonSerializer.Deserialize<string[]>(trimmed) ?? [];
            if (items.Length == 0 || items.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("The schema list must hold one or more XSD documents.");
            return items;
        }
        catch (JsonException exception) { throw new ArgumentException("The schema setting is neither one XSD document nor a JSON array of XSD documents.", exception); }
    }

    internal static string Render(string name, string xml, Hl7SchemaSet schemas, Options options)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception) { throw new InvalidOperationException($"HL7 schema render could not read '{name}' as XML.", exception); }

        var root = document.Root!;
        var messages = root.Name.LocalName == MultipartRootName ? root.Elements().ToArray() : [root];
        if (messages.Length == 0) throw new InvalidOperationException($"HL7 schema render found no message in '{name}'.");

        var segments = new List<(XElement Xml, Hl7SchemaElement Schema, string Id)>();
        foreach (var message in messages)
        {
            var schema = schemas.FindRoot(message.Name)
                ?? throw new InvalidOperationException($"HL7 schema render has no schema for the message element '{message.Name}' in '{name}'. Known roots: {string.Join(", ", schemas.RootNames.Take(12))}.");
            CollectSegments(message, schema, segments);
        }
        if (segments.Count == 0 || segments[0].Id != "MSH") throw new InvalidOperationException($"HL7 schema render found no leading MSH segment in '{name}'.");

        var (msh, mshSchema, _) = segments[0];
        var mshFields = FieldElements(msh, mshSchema, "MSH", name);
        var msh1 = mshFields.TryGetValue(1, out var first1) ? first1[0].Xml.Value : null;
        var msh2 = mshFields.TryGetValue(2, out var first2) ? first2[0].Xml.Value : null;
        var field = string.IsNullOrEmpty(msh1) ? options.FieldSeparator : msh1[0];
        var encoding = string.IsNullOrEmpty(msh2) ? options.EncodingCharacters : msh2;
        if (encoding.Length < 4) throw new InvalidOperationException($"HL7 schema render needs four MSH.2 encoding characters in '{name}'.");
        var delimiters = new Delimiters(field, encoding[0], encoding[1], encoding[2], encoding[3], options.Escape);

        var builder = new StringBuilder();
        foreach (var (xmlSegment, schemaSegment, id) in segments)
        {
            var byNumber = FieldElements(xmlSegment, schemaSegment, id, name);
            var first = id == "MSH" ? 2 : 1;
            var count = byNumber.Count == 0 ? 0 : Math.Max(byNumber.Keys.Max() - first + 1, 0);
            var fields = new string[count];
            for (var index = 0; index < count; index++)
            {
                var number = index + first;
                fields[index] = id == "MSH" && number == 2 ? encoding
                    : byNumber.TryGetValue(number, out var list) ? string.Join(delimiters.Repetition, list.Select(item => RenderField(item.Schema, item.Xml, delimiters, options.TrimComponents))) : string.Empty;
            }
            var end = fields.Length;
            if (options.TrimFields) while (end > 0 && fields[end - 1].Length == 0) end--;

            builder.Append(id);
            if (id == "MSH") builder.Append(field);
            for (var index = 0; index < end; index++)
            {
                if (index > 0 || id != "MSH") builder.Append(field);
                builder.Append(fields[index]);
            }
            builder.Append(options.Terminator);
        }
        return builder.ToString();
    }

    private readonly record struct Delimiters(char Field, char Component, char Repetition, char Escape, char Subcomponent, bool Encode);

    private static void CollectSegments(XElement container, Hl7SchemaElement schema, List<(XElement, Hl7SchemaElement, string)> segments)
    {
        foreach (var child in container.Elements())
        {
            var index = schema.IndexOf(child.Name.LocalName);
            if (index < 0) throw new InvalidOperationException($"The element '{child.Name.LocalName}' is not declared under '{schema.Name}' in the HL7 schema.");
            var declared = schema.Children[index];
            var id = Hl7SchemaSet.SegmentId(declared);
            if (id is not null) segments.Add((child, declared, id));
            else if (declared.IsComplex) CollectSegments(child, declared, segments);
            else throw new InvalidOperationException($"The element '{child.Name.LocalName}' under '{schema.Name}' is not a segment or a segment group.");
        }
    }

    private static SortedDictionary<int, List<(XElement Xml, Hl7SchemaElement Schema)>> FieldElements(XElement segment, Hl7SchemaElement schema, string id, string message)
    {
        var fields = new SortedDictionary<int, List<(XElement, Hl7SchemaElement)>>();
        foreach (var child in segment.Elements())
        {
            var index = schema.IndexOf(child.Name.LocalName);
            if (index < 0) throw new InvalidOperationException($"The element '{child.Name.LocalName}' is not declared in segment '{id}' of the HL7 schema (message '{message}').");
            var number = Hl7SchemaSet.FieldNumber(id, child.Name.LocalName)
                ?? throw new InvalidOperationException($"The element '{child.Name.LocalName}' in segment '{id}' has no HL7 field number in its name (message '{message}').");
            if (!fields.TryGetValue(number, out var list)) fields[number] = list = [];
            list.Add((child, schema.Children[index]));
        }
        return fields;
    }

    private static string RenderField(Hl7SchemaElement schema, XElement element, Delimiters delimiters, bool trim)
    {
        if (!schema.IsComplex)
        {
            if (element.HasElements) throw new InvalidOperationException($"The field '{element.Name.LocalName}' has child elements, but its HL7 schema type has none.");
            return Value(element.Value, delimiters);
        }
        if (!element.HasElements) return Value(element.Value, delimiters);
        var parts = RenderChildren(schema, element, delimiters, delimiters.Component, trim, subcomponents: false);
        return parts;
    }

    private static string RenderChildren(Hl7SchemaElement schema, XElement element, Delimiters delimiters, char separator, bool trim, bool subcomponents)
    {
        var values = new SortedDictionary<int, string>();
        foreach (var child in element.Elements())
        {
            var index = schema.IndexOf(child.Name.LocalName);
            if (index < 0) throw new InvalidOperationException($"The element '{child.Name.LocalName}' is not declared under '{schema.Name}' in the HL7 schema.");
            var declared = schema.Children[index];
            string text;
            if (!declared.IsComplex || !child.HasElements)
            {
                if (child.HasElements) throw new InvalidOperationException($"The element '{child.Name.LocalName}' has child elements, but its HL7 schema type has none.");
                text = Value(child.Value, delimiters);
            }
            else if (subcomponents) throw new InvalidOperationException($"The element '{child.Name.LocalName}' nests deeper than an HL7 sub-component.");
            else text = RenderChildren(declared, child, delimiters, delimiters.Subcomponent, trim, subcomponents: true);
            if (!values.TryAdd(index, text)) throw new InvalidOperationException($"The element '{child.Name.LocalName}' appears more than once in '{element.Name.LocalName}'; only a field can repeat.");
        }
        var count = values.Count == 0 ? 0 : values.Keys.Max() + 1;
        var items = Enumerable.Range(0, count).Select(index => values.GetValueOrDefault(index, string.Empty)).ToList();
        if (trim) while (items.Count > 0 && items[^1].Length == 0) items.RemoveAt(items.Count - 1);
        return string.Join(separator, items);
    }

    private static string Value(string text, Delimiters delimiters)
    {
        if (!delimiters.Encode || text.Length == 0) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character == delimiters.Escape) builder.Append(delimiters.Escape).Append('E').Append(delimiters.Escape);
            else if (character == delimiters.Field) builder.Append(delimiters.Escape).Append('F').Append(delimiters.Escape);
            else if (character == delimiters.Component) builder.Append(delimiters.Escape).Append('S').Append(delimiters.Escape);
            else if (character == delimiters.Subcomponent) builder.Append(delimiters.Escape).Append('T').Append(delimiters.Escape);
            else if (character == delimiters.Repetition) builder.Append(delimiters.Escape).Append('R').Append(delimiters.Escape);
            else builder.Append(character);
        }
        return builder.ToString();
    }
}
