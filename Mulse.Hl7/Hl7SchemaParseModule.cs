using System.Text;
using System.Xml.Linq;
using Mulse.Modules;

namespace Mulse.Hl7;

/// <summary>
/// Parses HL7 v2 ER7 text into XML that follows the BizTalk BTAHL7 schemas: the inverse of <see cref="Hl7SchemaRenderModule"/>. The header (MSH) and the body
/// become two message documents inside an <c>HL7Multipart</c> wrapper, or one document when the body schema itself declares the MSH segment. Segment groups
/// are flat in these schemas, so segments are placed by walking the body schema in order. Every behavior the schemas leave open is a setting.
/// </summary>
public sealed class Hl7SchemaParseModule : IParseModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("schemaText", "HL7 schemas", "The XSD documents of the HL7 messages this step can parse, as a JSON array of XSD texts: every message schema plus the segment and datatype schemas they import. A single XSD is also accepted. Can be a {{config:name}} reference.", true, ModuleSettingInputKind.TextArea),
        new("bodyRoot", "Body schema root", "The root element of the body schema, as 'namespace#name' or just the name. Leave empty to choose by the message type in MSH.9 (and the version in MSH.12 when several schemas match), which assumes the BTAHL7 naming such as DFT_P03_23_GLO_DEF.", false, ModuleSettingInputKind.Text),
        new("headerRoot", "Header schema root", "The root element of the MSH header schema, as 'namespace#name' or just the name. Leave empty to use the one schema whose name starts with MSH_. Not used when the body schema declares the MSH segment itself.", false, ModuleSettingInputKind.Text),
        new("unknownSegments", "Segment not in the schema", "A segment the body schema does not declare (a Z segment, or one out of order): stop with an error (assumed) or leave it out.", false, ModuleSettingInputKind.Select, "Fail",
            [new("Fail", "Stop with an error"), new("Skip", "Leave it out")]),
        new("unknownFields", "Field not in the schema", "A field with a value that the segment schema does not declare: stop with an error (assumed) or leave it out.", false, ModuleSettingInputKind.Select, "Fail",
            [new("Fail", "Stop with an error"), new("Ignore", "Leave it out")]),
        new("escapeSequences", "Escape sequences in values", "Whether \\F\\ \\S\\ \\T\\ \\R\\ \\E\\ in a value are turned back into the delimiter characters (Decode) or kept as written (None, assumed because the imported parser's behavior was not verified).", false, ModuleSettingInputKind.Select, "None",
            [new("None", "Keep as written"), new("Decode", "Decode escape sequences")])
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "hl7-schema-parse",
        "HL7 ER7 to schema XML parse",
        ModuleKind.Parse,
        "Parses pipe-delimited HL7 v2 text into XML that follows the BizTalk BTAHL7 schemas, using the schemas to name fields, components and sub-components. Messages that do not fit the schemas stop with an error naming the segment.",
        SettingDescriptors,
        new ModuleRecommendationProfile([ModuleDataFormat.Text], capabilities: [ModuleCapability.Parsing, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var schemas = Hl7SchemaRenderModule.Schemas(step.Settings.TryGetValue("schemaText", out var schemaText) && !string.IsNullOrWhiteSpace(schemaText)
            ? schemaText : throw new ArgumentException($"Module '{Descriptor.Id}' requires the setting 'schemaText'."));
        var options = ReadOptions(step.Settings);
        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = Parse(payload.Name, payload.Content.ToString(), schemas, options);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase) { ["parsedBy"] = Descriptor.Id, ["parsedFormat"] = "Hl7SchemaXml" };
            return new IntegrationPayload(Path.ChangeExtension(payload.Name, ".xml"), BinaryData.FromString(xml), "application/xml", metadata);
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    internal sealed record Options(string BodyRoot, string HeaderRoot, bool SkipUnknownSegments, bool IgnoreUnknownFields, bool Decode);

    internal static Options ReadOptions(IReadOnlyDictionary<string, string> settings)
    {
        string Text(string key, string fallback) => settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
        string Choice(string key, string fallback, string other)
        {
            var value = Text(key, fallback);
            if (value.Equals(fallback, StringComparison.OrdinalIgnoreCase)) return fallback;
            return value.Equals(other, StringComparison.OrdinalIgnoreCase) ? other : throw new ArgumentException($"Setting '{key}' must be {fallback} or {other}.");
        }
        return new(Text("bodyRoot", ""), Text("headerRoot", ""), Choice("unknownSegments", "Fail", "Skip") == "Skip", Choice("unknownFields", "Fail", "Ignore") == "Ignore", Choice("escapeSequences", "None", "Decode") == "Decode");
    }

    internal static string Parse(string name, string text, Hl7SchemaSet schemas, Options options)
    {
        var lines = text.Trim('\uFEFF', '\u000B', '\u001C', '\r', '\n', ' ').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("MSH", StringComparison.Ordinal) || lines[0].Length < 8)
            throw new InvalidOperationException($"HL7 schema parse found no leading MSH segment with its delimiters in '{name}'.");
        var field = lines[0][3];
        var encoding = lines[0].Substring(4, 4);
        var delimiters = new Delimiters(field, encoding[0], encoding[1], encoding[2], encoding[3], options.Decode);

        var header = lines[0].Split(field);
        var bodyRoot = SelectBodyRoot(schemas, options, header, delimiters, name);
        var bodySchema = schemas.FindRoot(bodyRoot)!;
        var declaresMsh = bodySchema.Children.Any(child => Hl7SchemaSet.SegmentId(child) == "MSH");

        XElement? headerElement = null;
        Hl7SchemaElement? headerDeclared = null;
        if (!declaresMsh)
        {
            var headerRoot = SelectHeaderRoot(schemas, options, name);
            headerElement = new XElement(headerRoot);
            headerDeclared = schemas.FindRoot(headerRoot)!;
        }
        var body = new XElement(bodyRoot);
        var bodyCursor = 0;
        var headerCursor = 0;
        foreach (var line in lines)
        {
            if (line.Length < 3) throw new InvalidOperationException($"HL7 schema parse found a segment shorter than its three-character ID in '{name}'.");
            var id = line[..3];
            var useHeader = id == "MSH" && !declaresMsh;
            var target = useHeader ? headerElement! : body;
            var schema = useHeader ? headerDeclared! : bodySchema;
            ref var cursor = ref (useHeader ? ref headerCursor : ref bodyCursor);
            var declared = FindSegment(schema, id, ref cursor);
            if (declared is null)
            {
                if (options.SkipUnknownSegments) continue;
                throw new InvalidOperationException($"The segment '{id}' is not declared in the HL7 schema '{schema.Name}' at this position (message '{name}'). Set 'Segment not in the schema' to Leave it out to drop such segments.");
            }
            target.Add(BuildSegment(line.Split(field), declared, id, field, delimiters, options, name));
        }

        var output = declaresMsh ? body : new XElement("HL7Multipart", headerElement, body);
        return output.ToString(SaveOptions.DisableFormatting);
    }

    private readonly record struct Delimiters(char Field, char Component, char Repetition, char Escape, char Subcomponent, bool Decode);

    private static XName SelectBodyRoot(Hl7SchemaSet schemas, Options options, string[] header, Delimiters delimiters, string name)
    {
        if (options.BodyRoot.Length > 0) return ResolveRoot(schemas, options.BodyRoot, "bodyRoot");
        var type = header.Length > 8 ? header[8].Split(delimiters.Component) : [];
        var code = type.Length > 0 ? type[0].Trim() : "";
        var trigger = type.Length > 1 ? type[1].Trim() : "";
        var structure = type.Length > 2 ? type[2].Trim() : "";
        var prefixes = new List<string>();
        if (code.Length > 0 && trigger.Length > 0) prefixes.Add($"{code}_{trigger}_");
        if (structure.Length > 0) prefixes.Add(structure + "_");
        if (prefixes.Count == 0) throw new InvalidOperationException($"HL7 schema parse cannot choose a body schema: MSH.9 has no message code and trigger event in '{name}'. Set 'Body schema root'.");
        var candidates = schemas.RootXmlNames.Where(root => prefixes.Any(prefix => root.LocalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count > 1 && header.Length > 11)
        {
            var version = new string(header[11].Split(delimiters.Component)[0].Split('.').Take(2).SelectMany(part => part).Where(char.IsAsciiDigit).ToArray());
            var narrowed = version.Length == 0 ? candidates : candidates.Where(root => root.LocalName.Contains("_" + version + "_", StringComparison.Ordinal)).ToList();
            if (narrowed.Count > 0) candidates = narrowed;
        }
        return candidates.Count == 1 ? candidates[0]
            : throw new InvalidOperationException(candidates.Count == 0
                ? $"HL7 schema parse found no body schema for the message type '{type.FirstOrDefault()}^{trigger}' in '{name}'. Set 'Body schema root'."
                : $"HL7 schema parse found {candidates.Count} body schemas for the message type in '{name}' ({string.Join(", ", candidates.Take(6).Select(root => root.LocalName))}). Set 'Body schema root'.");
    }

    private static XName SelectHeaderRoot(Hl7SchemaSet schemas, Options options, string name)
    {
        if (options.HeaderRoot.Length > 0) return ResolveRoot(schemas, options.HeaderRoot, "headerRoot");
        var candidates = schemas.RootXmlNames.Where(candidate => candidate.LocalName.StartsWith("MSH_", StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0]
            : throw new InvalidOperationException($"HL7 schema parse found {candidates.Count} header schemas whose names start with MSH_ in '{name}'. Set 'Header schema root'.");
    }

    private static XName ResolveRoot(Hl7SchemaSet schemas, string text, string setting)
    {
        var hash = text.LastIndexOf('#');
        var wanted = hash < 0 ? XName.Get(text) : XName.Get(text[(hash + 1)..], text[..hash]);
        var roots = schemas.RootXmlNames.ToList();
        var matches = roots.Where(root => root == wanted).ToList();
        if (matches.Count == 0 && hash < 0) matches = roots.Where(root => root.LocalName == wanted.LocalName).ToList();
        return matches.Count == 1 ? matches[0] : throw new ArgumentException($"Setting '{setting}' names '{text}', which is not a unique root element of the HL7 schemas.");
    }

    // Segments are placed by walking the flat schema in order; a segment that repeats in a later group wraps around to its earlier declaration.
    private static Hl7SchemaElement? FindSegment(Hl7SchemaElement root, string id, ref int cursor)
    {
        for (var index = cursor; index < root.Children.Count; index++)
            if (Hl7SchemaSet.SegmentId(root.Children[index]) == id) { cursor = index; return root.Children[index]; }
        for (var index = 0; index < Math.Min(cursor, root.Children.Count); index++)
            if (Hl7SchemaSet.SegmentId(root.Children[index]) == id) { cursor = index; return root.Children[index]; }
        return null;
    }

    private static XElement BuildSegment(string[] parts, Hl7SchemaElement segment, string id, char fieldSeparator, Delimiters delimiters, Options options, string message)
    {
        var element = new XElement(segment.Name);
        var fieldSchemas = new Dictionary<int, Hl7SchemaElement>();
        foreach (var child in segment.Children)
            if (Hl7SchemaSet.FieldNumber(id, child.Name) is { } declaredNumber) fieldSchemas.TryAdd(declaredNumber, child);

        if (id == "MSH" && fieldSchemas.TryGetValue(1, out var separatorSchema)) element.Add(new XElement(separatorSchema.Name, fieldSeparator.ToString()));
        for (var index = 1; index < parts.Length; index++)
        {
            var value = parts[index];
            if (value.Length == 0) continue;
            var number = id == "MSH" ? index + 1 : index;
            if (!fieldSchemas.TryGetValue(number, out var fieldSchema))
            {
                if (options.IgnoreUnknownFields) continue;
                throw new InvalidOperationException($"Field {number} of segment '{id}' has a value, but the HL7 schema declares no such field (message '{message}'). Set 'Field not in the schema' to Leave it out to drop such fields.");
            }
            if (id == "MSH" && number == 2) { element.Add(new XElement(fieldSchema.Name, value)); continue; }
            var repetitions = value.Split(delimiters.Repetition).Where(repetition => repetition.Length > 0).ToArray();
            if (repetitions.Length > 1 && !fieldSchema.Repeats)
                throw new InvalidOperationException($"Field {number} of segment '{id}' repeats, but the HL7 schema allows it only once (message '{message}').");
            foreach (var repetition in repetitions) element.Add(BuildValue(fieldSchema, repetition, delimiters, delimiters.Component, id, number, message, level: 0));
        }
        return element;
    }

    private static XElement BuildValue(Hl7SchemaElement schema, string value, Delimiters delimiters, char separator, string segment, int field, string message, int level)
    {
        var element = new XElement(schema.Name);
        if (!schema.IsComplex) { element.Value = Decode(value, delimiters); return element; }
        var items = value.Split(separator);
        if (items.Length > schema.Children.Count)
            throw new InvalidOperationException($"'{schema.Name}' in segment '{segment}' has {items.Length} {(level == 0 ? "components" : "sub-components")}, but the HL7 schema declares {schema.Children.Count} (message '{message}').");
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].Length == 0) continue;
            var child = schema.Children[index];
            element.Add(child.IsComplex && level == 0
                ? BuildValue(child, items[index], delimiters, delimiters.Subcomponent, segment, field, message, level + 1)
                : new XElement(child.Name, Decode(items[index], delimiters)));
        }
        return element;
    }

    private static string Decode(string value, Delimiters delimiters)
    {
        if (!delimiters.Decode || value.IndexOf(delimiters.Escape) < 0) return value;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var end = value[index] == delimiters.Escape ? value.IndexOf(delimiters.Escape, index + 1) : -1;
            if (end > index)
            {
                var replacement = end - index == 2
                    ? value[index + 1] switch
                    {
                        'F' => delimiters.Field,
                        'S' => delimiters.Component,
                        'T' => delimiters.Subcomponent,
                        'R' => delimiters.Repetition,
                        'E' => delimiters.Escape,
                        _ => (char?)null
                    }
                    : null;
                if (replacement is { } character) { builder.Append(character); index = end; continue; }
            }
            builder.Append(value[index]);
        }
        return builder.ToString();
    }
}
