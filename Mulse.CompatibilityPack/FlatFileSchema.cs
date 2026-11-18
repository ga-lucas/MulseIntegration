using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.CompatibilityPack;

/// <summary>How the renderer behaves where the schema does not say. Every value is a module setting.</summary>
public sealed record FlatFileRenderOptions
{
    /// <summary>Wrap every non-empty value that has a wrap character (Always), or only values that contain a delimiter, line break or the wrap character (WhenNeeded).</summary>
    public bool WrapAlways { get; init; } = true;
    /// <summary>A value longer than its positional field is cut to fit instead of failing.</summary>
    public bool TruncateTooLong { get; init; }
    /// <summary>A delimited value that contains its record's delimiter, and has no wrap character, is written as is instead of failing.</summary>
    public bool WriteDelimiterInValue { get; init; }
    /// <summary>A lower-case <c>hh</c> in a date/time format prints a 12-hour clock (the .NET meaning) instead of 24 hours.</summary>
    public bool TwelveHourClock { get; init; }
    /// <summary>An optional record or field that is absent still takes its place (its delimiter is written) instead of being skipped.</summary>
    public bool KeepAbsentPlaceholders { get; init; } = true;
}

/// <summary>
/// A strict, schema-driven subset of the BizTalk flat-file assembler. <see cref="Compile"/> reads a flat-file XSD (with its imports) and throws
/// <see cref="NotSupportedException"/> naming the first feature it cannot reproduce; <see cref="Render"/> turns an XML instance into delimited or
/// positional text. Nothing is guessed: unsupported schema features and instances that do not fit the schema fail instead of producing text.
/// </summary>
public sealed class FlatFileSchema
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly HashSet<string> SchemaInfoKnown = new(StringComparer.Ordinal)
    {
        "standard", "root_reference", "default_pad_char", "pad_char_type", "default_wrap_char", "wrap_char_type", "count_positions_by_byte", "codepage",
        "parser_optimization", "lookahead_depth", "suppress_empty_nodes", "generate_empty_nodes", "allow_early_termination", "early_terminate_optional_fields",
        "allow_message_breakup_of_infix_root", "compile_parse_tables"
    };
    private static readonly HashSet<string> RecordInfoKnown = new(StringComparer.Ordinal)
    {
        "structure", "child_delimiter", "child_delimiter_type", "child_order", "preserve_delimiter_for_empty_data", "repeating_delimiter", "repeating_delimiter_type",
        "suppress_trailing_delimiters", "tag_name", "sequence_number", "rootTypeName"
    };
    private static readonly HashSet<string> FieldInfoKnown = new(StringComparer.Ordinal)
    {
        "justification", "min_length_with_pad", "pad_char", "pad_char_type", "pos_length", "pos_offset", "sequence_number", "wrap_char", "wrap_char_type", "datetime_format"
    };

    private abstract class Part
    {
        public required XName Name { get; init; }
        public int Min { get; init; } = 1;
        public int Max { get; init; } = 1;
        public int Sequence { get; init; } = -1;
    }

    private sealed class Field : Part
    {
        public string Type { get; init; } = "xs:string";
        public bool Left { get; init; } = true;
        public int MinLength { get; init; }
        public string? Pad { get; init; }
        public bool NoPad { get; init; }
        public int Length { get; init; }
        public string? Wrap { get; init; }
        public string? DateTimeFormat { get; init; }
        public bool IsAttribute { get; set; }
    }

    private sealed class Group : Part
    {
        public List<Part> Children { get; } = [];
    }

    private sealed class Record : Part
    {
        public bool Positional { get; init; }
        public string Delimiter { get; init; } = "";
        public string Order { get; init; } = "infix";
        public string? RepeatDelimiter { get; init; }
        public bool SuppressTrailing { get; init; }
        public string? Tag { get; init; }
        public List<Part> Children { get; } = [];
    }

    private readonly Record _root;
    public XName RootName => _root.Name;

    /// <summary>The BizTalk message type of an instance of this schema: <c>namespace#root</c>.</summary>
    public string MessageType => MessageTypeOf(_root.Name);

    private FlatFileSchema(Record root) => _root = root;

    public static string MessageTypeOf(XName name) => name.NamespaceName.Length == 0 ? name.LocalName : name.NamespaceName + "#" + name.LocalName;

    /// <summary>True when the document is annotated as a BizTalk flat-file schema (<c>standard="Flat File"</c>).</summary>
    public static bool IsFlatFile(string xsdText)
    {
        try
        {
            var document = Load(xsdText);
            return document.Root?.Descendants().Any(static e => e.Name.LocalName == "schemaInfo" && (string?)e.Attribute("standard") == "Flat File") == true;
        }
        catch (XmlException) { return false; }
    }

    /// <summary>Parses a schema setting: one XSD document, or a JSON array of XSD documents whose first item is the root schema.</summary>
    public static IReadOnlyList<string> SplitDocuments(string schemaText)
    {
        var trimmed = schemaText.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith('[')) return [schemaText];
        try
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<string[]>(trimmed) ?? [];
            if (items.Length == 0 || items.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("The schema list must hold one or more XSD documents.");
            return items;
        }
        catch (System.Text.Json.JsonException exception) { throw new ArgumentException("The schema setting is neither one XSD document nor a JSON array of XSD documents.", exception); }
    }

    private static XDocument Load(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static FlatFileSchema Compile(IReadOnlyList<string> documents)
    {
        if (documents.Count == 0) throw new ArgumentException("A flat-file schema needs at least one XSD document.");
        var schemas = new List<XElement>();
        foreach (var text in documents)
        {
            XDocument document;
            try { document = Load(text); }
            catch (XmlException exception) { throw new ArgumentException("A flat-file schema document is not well-formed XML: " + exception.Message, exception); }
            if (document.Root is not { } root || root.Name != Xs + "schema") throw new ArgumentException("A flat-file schema document must have an xs:schema root.");
            schemas.Add(root);
        }
        return new Compiler(schemas).Compile();
    }

    private sealed class Compiler(List<XElement> schemas)
    {
        private readonly Dictionary<XName, XElement> _elements = [];
        private readonly Dictionary<XName, XElement> _types = [];
        private string _defaultPad = " ";
        private bool _defaultPadNone;
        private int _depth;

        public FlatFileSchema Compile()
        {
            foreach (var schema in schemas)
            {
                var target = (string?)schema.Attribute("targetNamespace") ?? "";
                foreach (var child in schema.Elements())
                {
                    var name = (string?)child.Attribute("name");
                    if (name is null) continue;
                    if (child.Name == Xs + "element") _elements.TryAdd(XName.Get(name, target), child);
                    else if (child.Name == Xs + "complexType") _types.TryAdd(XName.Get(name, target), child);
                    else if (child.Name == Xs + "simpleType") throw new NotSupportedException($"named simple type '{name}'");
                    else if (child.Name == Xs + "group" || child.Name == Xs + "attributeGroup") throw new NotSupportedException($"{child.Name.LocalName} '{name}'");
                }
                if (schema.Elements(Xs + "include").Any() || schema.Elements(Xs + "redefine").Any()) throw new NotSupportedException("xs:include and xs:redefine");
            }
            var first = schemas[0];
            var infos = first.Descendants().Where(static e => e.Name.LocalName == "schemaInfo" && e.Attribute("standard") is not null).ToArray();
            if (infos.Length != 1 || (string?)infos[0].Attribute("standard") != "Flat File") throw new NotSupportedException("a schema that is not annotated as 'Flat File'");
            var info = infos[0];
            foreach (var attribute in info.Attributes().Where(static a => !a.IsNamespaceDeclaration))
                if (!SchemaInfoKnown.Contains(attribute.Name.LocalName)) throw new NotSupportedException($"schemaInfo '{attribute.Name.LocalName}'");
            if (Bool(info, "count_positions_by_byte")) throw new NotSupportedException("schemaInfo count_positions_by_byte=true");
            if ((string?)info.Attribute("codepage") is { Length: > 0 } codepage && codepage != "65001") throw new NotSupportedException($"schemaInfo codepage {codepage} (only UTF-8, 65001, text is produced)");
            _defaultPadNone = (string?)info.Attribute("pad_char_type") == "none";
            _defaultPad = PadValue((string?)info.Attribute("default_pad_char"), (string?)info.Attribute("pad_char_type"), " ")!;
            var rootReference = (string?)info.Attribute("root_reference") ?? throw new NotSupportedException("a schema with no root_reference");
            var target0 = (string?)first.Attribute("targetNamespace") ?? "";
            var rootName = XName.Get(rootReference, target0);
            if (!_elements.TryGetValue(rootName, out var rootElement)) throw new NotSupportedException($"root_reference '{rootReference}' is not a global element of the first schema");
            var built = Build(rootElement, rootElement, XName.Get(rootReference, target0), global: true);
            if (built is not Record record) throw new NotSupportedException($"root '{rootReference}' is a field, not a record");
            return new FlatFileSchema(record);
        }

        private static bool Bool(XElement element, string name) => string.Equals((string?)element.Attribute(name), "true", StringComparison.OrdinalIgnoreCase);

        private static XElement? Annotation(XElement element, string kind)
            => element.Element(Xs + "annotation")?.Elements(Xs + "appinfo").Elements().FirstOrDefault(e => e.Name.LocalName == kind);

        private Part Build(XElement use, XElement definition, XName name, bool global)
        {
            if (++_depth > 64) throw new NotSupportedException("a schema nested more than 64 levels (or a recursive type)");
            try
            {
                var min = (string?)use.Attribute("minOccurs") is { } minText ? int.Parse(minText, CultureInfo.InvariantCulture) : 1;
                var maxText = (string?)use.Attribute("maxOccurs") ?? "1";
                var max = maxText == "unbounded" ? int.MaxValue : int.Parse(maxText, CultureInfo.InvariantCulture);
                var recordInfo = Annotation(use, "recordInfo") ?? Annotation(definition, "recordInfo");
                var fieldInfo = Annotation(use, "fieldInfo") ?? Annotation(definition, "fieldInfo");
                var sequenceSource = recordInfo ?? fieldInfo;
                var sequence = sequenceSource?.Attribute("sequence_number") is { } sequenceAttribute ? int.Parse(sequenceAttribute.Value, CultureInfo.InvariantCulture) : -1;
                if (definition.Attribute("substitutionGroup") is not null || definition.Attribute("abstract") is not null)
                    throw new NotSupportedException($"element '{name.LocalName}' with substitutionGroup or abstract");
                var inline = definition.Element(Xs + "complexType");
                var typeName = (string?)definition.Attribute("type");
                XElement? complex = inline;
                if (complex is null && typeName is not null && !typeName.StartsWith("xs:", StringComparison.Ordinal) && !typeName.StartsWith("xsd:", StringComparison.Ordinal))
                {
                    var qualified = (typeName.Contains(':') ? definition.GetNamespaceOfPrefix(typeName[..typeName.IndexOf(':')]) : definition.GetDefaultNamespace()) ?? XNamespace.None;
                    var local = typeName.Contains(':') ? typeName[(typeName.IndexOf(':') + 1)..] : typeName;
                    var typeKey = XName.Get(local, qualified.NamespaceName.Length == 0 ? (string?)definition.Ancestors(Xs + "schema").First().Attribute("targetNamespace") ?? "" : qualified.NamespaceName);
                    complex = _types.TryGetValue(typeKey, out var found) ? found : throw new NotSupportedException($"element '{name.LocalName}' of unresolved type '{typeName}'");
                }
                if (complex is null)
                {
                    if (recordInfo is not null && fieldInfo is null) throw new NotSupportedException($"element '{name.LocalName}' is a simple element annotated as a record");
                    return Field(use, min, max, sequence, name, typeName ?? "xs:string", fieldInfo);
                }
                if (recordInfo is null) throw new NotSupportedException($"record '{name.LocalName}' has no recordInfo annotation");
                return Record(complex, min, max, sequence, name, recordInfo);
            }
            finally { _depth--; }
        }

        private Part Field(XElement use, int min, int max, int sequence, XName name, string type, XElement? info)
        {
            if (info is not null)
                foreach (var attribute in info.Attributes().Where(static a => !a.IsNamespaceDeclaration))
                    if (!FieldInfoKnown.Contains(attribute.Name.LocalName)) throw new NotSupportedException($"fieldInfo '{attribute.Name.LocalName}' on '{name.LocalName}'");
            var padType = (string?)info?.Attribute("pad_char_type");
            var padText = (string?)info?.Attribute("pad_char");
            var noPad = padType == "none" || (padType is null or "default") && _defaultPadNone;
            var pad = padType is null or "default" or "none" ? _defaultPad : PadValue(padText, padType, _defaultPad);
            var wrapText = (string?)info?.Attribute("wrap_char");
            var wrapType = (string?)info?.Attribute("wrap_char_type");
            var format = (string?)info?.Attribute("datetime_format");
            if (format is not null)
            {
                if (!type.EndsWith(":date", StringComparison.Ordinal) && !type.EndsWith(":dateTime", StringComparison.Ordinal) && !type.EndsWith(":time", StringComparison.Ordinal))
                    throw new NotSupportedException($"datetime_format on '{name.LocalName}', which is not an xs:date, xs:dateTime or xs:time field");
                ValidateFormat(format, name.LocalName);
            }
            var positionLength = info?.Attribute("pos_length") is { } lengthAttribute ? int.Parse(lengthAttribute.Value, CultureInfo.InvariantCulture) : 0;
            if (info?.Attribute("pos_offset") is { } offset && offset.Value != "0") throw new NotSupportedException($"pos_offset {offset.Value} on '{name.LocalName}'");
            var justification = (string?)info?.Attribute("justification") ?? "left";
            if (justification is not ("left" or "right")) throw new NotSupportedException($"justification '{justification}' on '{name.LocalName}'");
            return new Field
            {
                Name = name, Min = min, Max = max, Sequence = sequence, Type = type, Left = justification == "left",
                MinLength = info?.Attribute("min_length_with_pad") is { } minLength ? int.Parse(minLength.Value, CultureInfo.InvariantCulture) : 0,
                Pad = pad, NoPad = noPad, Length = positionLength,
                Wrap = string.IsNullOrEmpty(wrapText) ? null : PadValue(wrapText, wrapType, "\""),
                DateTimeFormat = format
            };
        }

        private Part Record(XElement complex, int min, int max, int sequence, XName name, XElement info)
        {
            foreach (var attribute in info.Attributes().Where(static a => !a.IsNamespaceDeclaration))
                if (!RecordInfoKnown.Contains(attribute.Name.LocalName)) throw new NotSupportedException($"recordInfo '{attribute.Name.LocalName}' on '{name.LocalName}'");
            var structure = (string?)info.Attribute("structure");
            if (structure is not ("delimited" or "positional")) throw new NotSupportedException($"record '{name.LocalName}' structure '{structure}'");
            if (info.Attribute("preserve_delimiter_for_empty_data") is { } preserve && !string.Equals(preserve.Value, "true", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"preserve_delimiter_for_empty_data=false on '{name.LocalName}'");
            var order = (string?)info.Attribute("child_order") ?? "default";
            order = order == "default" ? "infix" : order;
            if (order is not ("infix" or "postfix" or "prefix")) throw new NotSupportedException($"child_order '{order}' on '{name.LocalName}'");
            var tag = (string?)info.Attribute("tag_name");
            var delimiter = Delimiter((string?)info.Attribute("child_delimiter"), (string?)info.Attribute("child_delimiter_type"), name.LocalName) ?? "";
            var repeat = Delimiter((string?)info.Attribute("repeating_delimiter"), (string?)info.Attribute("repeating_delimiter_type"), name.LocalName);
            if (structure == "delimited" && delimiter.Length == 0 && (complex.Element(Xs + "sequence")?.Elements(Xs + "element").Count() ?? 0) > 1)
                throw new NotSupportedException($"delimited record '{name.LocalName}' has no child delimiter");
            var record = new Record
            {
                Name = name, Min = min, Max = max, Sequence = sequence, Positional = structure == "positional", Delimiter = delimiter, Order = order,
                RepeatDelimiter = repeat, SuppressTrailing = Bool(info, "suppress_trailing_delimiters"), Tag = string.IsNullOrEmpty(tag) ? null : tag
            };
            var content = complex.Elements().Where(static e => e.Name != Xs + "annotation").ToArray();
            var attributes = content.Where(static e => e.Name == Xs + "attribute").ToArray();
            var structural = content.Where(static e => e.Name != Xs + "attribute").ToArray();
            var lastAttribute = int.MinValue;
            if (attributes.Length > 0)
            {
                foreach (var attribute in attributes)
                {
                    var attributeName = (string?)attribute.Attribute("name") ?? throw new NotSupportedException($"an attribute with no name in record '{name.LocalName}'");
                    if ((string?)attribute.Attribute("form") == "qualified" || attribute.Attribute("ref") is not null || attribute.Attribute("fixed") is not null || attribute.Attribute("default") is not null)
                        throw new NotSupportedException($"attribute '{attributeName}' with form, ref, fixed or default in record '{name.LocalName}'");
                    var attributeInfo = Annotation(attribute, "fieldInfo") ?? throw new NotSupportedException($"attribute '{attributeName}' in record '{name.LocalName}' has no fieldInfo annotation");
                    var attributeSequence = attributeInfo.Attribute("sequence_number") is { } number ? int.Parse(number.Value, CultureInfo.InvariantCulture) : -1;
                    if (attributeSequence >= 0 && attributeSequence <= lastAttribute) throw new NotSupportedException($"record '{name.LocalName}' lists attribute '{attributeName}' out of sequence_number order");
                    if (attributeSequence >= 0) lastAttribute = attributeSequence;
                    var built = (Field)Field(attribute, (string?)attribute.Attribute("use") == "required" ? 1 : 0, 1, attributeSequence, XName.Get(attributeName), (string?)attribute.Attribute("type") ?? "xs:string", attributeInfo);
                    built.IsAttribute = true;
                    record.Children.Add(built);
                }
            }
            if (structural.Length == 0) return record;
            if (structural.Length != 1 || structural[0].Name != Xs + "sequence")
                throw new NotSupportedException($"record '{name.LocalName}' content '{structural[0].Name.LocalName}' (only a single xs:sequence is supported)");
            var elementForm = (string?)complex.Ancestors(Xs + "schema").First().Attribute("elementFormDefault") == "qualified";
            var targetNamespace = (string?)complex.Ancestors(Xs + "schema").First().Attribute("targetNamespace") ?? "";
            var top = structural[0];
            if (top.Attribute("minOccurs") is not null || top.Attribute("maxOccurs") is not null)
            {
                var wrapper = Group(top, name.LocalName, elementForm, targetNamespace);
                record.Children.Add(wrapper);
            }
            else AddChildren(top, record.Children, name.LocalName, elementForm, targetNamespace, lastAttribute);
            return record;
        }

        private Group Group(XElement sequence, string owner, bool elementForm, string targetNamespace)
        {
            var info = Annotation(sequence, "groupInfo");
            if (info is not null && info.Attributes().Any(static a => !a.IsNamespaceDeclaration && a.Name.LocalName != "sequence_number"))
                throw new NotSupportedException($"groupInfo '{info.Attributes().First(static a => !a.IsNamespaceDeclaration && a.Name.LocalName != "sequence_number").Name.LocalName}' in '{owner}'");
            var min = (string?)sequence.Attribute("minOccurs") is { } minText ? int.Parse(minText, CultureInfo.InvariantCulture) : 1;
            var maxText = (string?)sequence.Attribute("maxOccurs") ?? "1";
            var group = new Group
            {
                Name = XName.Get("group"), Min = min, Max = maxText == "unbounded" ? int.MaxValue : int.Parse(maxText, CultureInfo.InvariantCulture),
                Sequence = info?.Attribute("sequence_number") is { } number ? int.Parse(number.Value, CultureInfo.InvariantCulture) : -1
            };
            AddChildren(sequence, group.Children, owner, elementForm, targetNamespace);
            if (group.Children.Count == 0) throw new NotSupportedException($"an empty xs:sequence inside record '{owner}'");
            return group;
        }

        private void AddChildren(XElement sequence, List<Part> children, string owner, bool elementForm, string targetNamespace, int lastSequence = int.MinValue)
        {
            foreach (var child in sequence.Elements())
            {
                if (child.Name == Xs + "annotation") continue;
                if (child.Name == Xs + "sequence")
                {
                    var nested = Group(child, owner, elementForm, targetNamespace);
                    children.Add(nested);
                    if (nested.Sequence >= 0)
                    {
                        if (nested.Sequence <= lastSequence) throw new NotSupportedException($"record '{owner}' lists a sequence group out of sequence_number order");
                        lastSequence = nested.Sequence;
                    }
                    continue;
                }
                if (child.Name != Xs + "element") throw new NotSupportedException($"{child.Name.LocalName} inside record '{owner}'");
                if ((string?)child.Attribute("ref") is { } reference)
                {
                    var prefix = reference.Contains(':') ? reference[..reference.IndexOf(':')] : "";
                    var space = (prefix.Length == 0 ? child.GetDefaultNamespace() : child.GetNamespaceOfPrefix(prefix)) ?? XNamespace.None;
                    var key = XName.Get(reference[(reference.IndexOf(':') + 1)..], space.NamespaceName);
                    if (!_elements.TryGetValue(key, out var global)) throw new NotSupportedException($"element reference '{reference}' that is not in the supplied schemas");
                    children.Add(Build(child, global, key, global: true));
                }
                else
                {
                    var childName = (string?)child.Attribute("name") ?? throw new NotSupportedException("an element with no name or ref");
                    var qualified = (string?)child.Attribute("form") is { } form ? form == "qualified" : elementForm;
                    children.Add(Build(child, child, XName.Get(childName, qualified ? targetNamespace : ""), global: false));
                }
                var added = children[^1];
                if (added.Sequence >= 0)
                {
                    if (added.Sequence <= lastSequence) throw new NotSupportedException($"record '{owner}' lists '{added.Name.LocalName}' out of sequence_number order");
                    lastSequence = added.Sequence;
                }
            }
        }

        private static string? Delimiter(string? value, string? type, string owner)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (type == "hex")
            {
                var text = new StringBuilder();
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || !int.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 255)
                        throw new NotSupportedException($"hex delimiter '{value}' on '{owner}'");
                    text.Append((char)code);
                }
                return text.ToString();
            }
            if (type is not (null or "char")) throw new NotSupportedException($"delimiter type '{type}' on '{owner}'");
            return value;
        }

        private static string? PadValue(string? value, string? type, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            if (type == "hex")
                return int.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) && code is >= 0 and <= 255
                    ? ((char)code).ToString() : throw new NotSupportedException($"hex character '{value}'");
            return value.Length == 1 ? value : throw new NotSupportedException($"character '{value}' with more than one character");
        }

        private static void ValidateFormat(string format, string owner)
        {
            for (var index = 0; index < format.Length;)
            {
                var letter = format[index];
                var run = 1;
                while (index + run < format.Length && format[index + run] == letter) run++;
                var ok = !char.IsLetter(letter) || letter switch { 'y' => run is 2 or 4, 'M' or 'd' or 'H' or 'h' or 'm' or 's' => run == 2, 'f' => run is >= 1 and <= 7, _ => false };
                if (!ok) throw new NotSupportedException($"datetime_format '{format}' on '{owner}' (the part '{new string(letter, run)}' is not one of yyyy, yy, MM, dd, HH, hh, mm, ss, f..)");
                index += run;
            }
        }
    }

    public string Render(string xml, FlatFileRenderOptions? options = null)
    {
        options ??= new();
        XDocument document;
        try { document = Load(xml); }
        catch (XmlException exception) { throw new InvalidOperationException("The message is not well-formed XML: " + exception.Message, exception); }
        var root = document.Root!;
        if (root.Name != _root.Name)
            throw new InvalidOperationException($"This schema describes '{MessageTypeOf(_root.Name)}' but the message is '{MessageTypeOf(root.Name)}'.");
        return RenderRecord(_root, root, options, []);
    }

    private static string RenderRecord(Record record, XElement element, FlatFileRenderOptions options, IReadOnlyList<string> outerDelimiters)
    {
        var inner = record.Positional ? outerDelimiters : [.. outerDelimiters, .. (record.Delimiter.Length == 0 ? [] : new[] { record.Delimiter })];
        if (record.RepeatDelimiter is { Length: > 0 } repeatDelimiter) inner = [.. inner, repeatDelimiter];
        var items = new List<string>();
        if (record.Tag is not null) items.Add(record.Tag);
        var actual = element.Elements().ToArray();
        var position = 0;
        RenderParts(record.Children, record, element, actual, ref position, items, options, inner);
        if (position < actual.Length)
            throw new InvalidOperationException($"Element '{actual[position].Name.LocalName}' is not allowed here in '{element.Name.LocalName}' (unknown or out of order).");
        if (record.Positional) return string.Concat(items);
        if (record.SuppressTrailing) while (items.Count > 0 && items[^1].Length == 0 && !(record.Tag is not null && items.Count == 1)) items.RemoveAt(items.Count - 1);
        return record.Order switch
        {
            "postfix" => string.Concat(items.Select(item => item + record.Delimiter)),
            "prefix" when record.Tag is not null => items[0] + string.Concat(items.Skip(1).Select(item => record.Delimiter + item)),
            "prefix" => string.Concat(items.Select(item => record.Delimiter + item)),
            _ => string.Join(record.Delimiter, items)
        };
    }

    private static bool Starts(Part part, XName name) => part switch
    {
        Group group => group.Children.Any(child => Starts(child, name)),
        _ => part.Name == name
    };

    private static void RenderParts(IReadOnlyList<Part> parts, Record record, XElement element, XElement[] actual, ref int position, List<string> items, FlatFileRenderOptions options, IReadOnlyList<string> inner)
    {
        foreach (var part in parts)
        {
            if (part is Group group)
            {
                var cycles = 0;
                while (cycles < group.Max && position < actual.Length && Starts(group, actual[position].Name))
                {
                    var before = position;
                    RenderParts(group.Children, record, element, actual, ref position, items, options, inner);
                    if (position == before) break;
                    cycles++;
                }
                if (cycles < group.Min)
                {
                    var none = 0;
                    RenderParts(group.Children, record, element, [], ref none, items, options, inner);
                }
                continue;
            }
            var matched = new List<XElement>();
            if (part is Field { IsAttribute: true } attributeField)
            {
                var present = element.Attribute(attributeField.Name.LocalName);
                if (present is null && attributeField.Min > 0) throw new InvalidOperationException($"Required attribute '{attributeField.Name.LocalName}' is missing in '{element.Name.LocalName}'.");
                if (present is not null) items.Add(RenderField(attributeField, present.Value, options, inner, record.Positional));
                else if (options.KeepAbsentPlaceholders) items.Add(RenderField(attributeField, "", options, inner, record.Positional));
                continue;
            }
            while (position < actual.Length && actual[position].Name == part.Name) matched.Add(actual[position++]);
            if (matched.Count < part.Min)
                throw new InvalidOperationException($"Required element '{part.Name.LocalName}' is missing or out of order in '{element.Name.LocalName}'."
                    + (position < actual.Length && actual[position].Name.LocalName == part.Name.LocalName ? $" The next element is in namespace '{actual[position].Name.NamespaceName}' but the schema expects '{part.Name.NamespaceName}'." : ""));
            if (matched.Count > part.Max) throw new InvalidOperationException($"Element '{part.Name.LocalName}' occurs {matched.Count} times in '{element.Name.LocalName}'; the schema allows {part.Max}.");
            if (matched.Count == 0)
            {
                if (options.KeepAbsentPlaceholders || part.Min > 0) items.Add(part is Field absent ? RenderField(absent, "", options, inner, record.Positional) : "");
                continue;
            }
            if (part is Field field)
                foreach (var occurrence in matched) items.Add(RenderField(field, FieldText(occurrence), options, inner, record.Positional));
            else
            {
                var child = (Record)part;
                var rendered = matched.Select(occurrence => RenderRecord(child, occurrence, options, inner)).ToArray();
                if (child.RepeatDelimiter is { Length: > 0 } between) items.Add(string.Join(between, rendered));
                else items.AddRange(rendered);
            }
        }
    }

    private static string FieldText(XElement element)
    {
        if (element.HasElements) throw new InvalidOperationException($"Field '{element.Name.LocalName}' contains child elements.");
        if (string.Equals((string?)element.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance")), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Field '{element.Name.LocalName}' is xsi:nil; how BizTalk writes a nil value is not reproduced, so nothing was written.");
        return element.Value;
    }

    private static string RenderField(Field field, string value, FlatFileRenderOptions options, IReadOnlyList<string> delimiters, bool positional)
    {
        if (field.DateTimeFormat is not null && value.Length != 0) value = FormatDateTime(value, field.DateTimeFormat, field.Name.LocalName, options.TwelveHourClock);
        if (positional)
        {
            if (field.Length <= 0) throw new InvalidOperationException($"Positional field '{field.Name.LocalName}' has no pos_length.");
            if (value.Length > field.Length)
            {
                if (!options.TruncateTooLong) throw new InvalidOperationException($"Value of '{field.Name.LocalName}' is {value.Length} characters; the position holds {field.Length}. Turn on 'truncate too-long values' to cut it.");
                value = field.Left ? value[..field.Length] : value[^field.Length..];
            }
            if (value.Length < field.Length)
            {
                if (field.NoPad) throw new InvalidOperationException($"Field '{field.Name.LocalName}' has no pad character, so a {value.Length}-character value cannot fill {field.Length} positions.");
                value = Pad(value, field.Length, field);
            }
            return value;
        }
        if (field.MinLength > value.Length && !field.NoPad) value = Pad(value, field.MinLength, field);
        if (value.Length == 0) return value;
        var needsWrap = delimiters.Any(delimiter => value.Contains(delimiter, StringComparison.Ordinal)) || value.Contains('\r') || value.Contains('\n');
        if (field.Wrap is { } wrap)
        {
            if (value.Contains(wrap, StringComparison.Ordinal))
                throw new InvalidOperationException($"Value of '{field.Name.LocalName}' contains its wrap character; BizTalk's escaping of it is not reproduced, so nothing was written.");
            return options.WrapAlways || needsWrap ? wrap + value + wrap : value;
        }
        if (needsWrap && !options.WriteDelimiterInValue)
            throw new InvalidOperationException($"Value of '{field.Name.LocalName}' contains a delimiter or line break and the field has no wrap character. Turn on 'write delimiters inside values' to write it anyway.");
        return value;
    }

    private static string Pad(string value, int length, Field field)
    {
        var pad = field.Pad ?? " ";
        var fill = new string(pad[0], length - value.Length);
        return field.Left ? value + fill : fill + value;
    }

    private static string FormatDateTime(string value, string format, string owner, bool twelveHour)
    {
        DateTime moment;
        try { moment = XmlConvert.ToDateTime(value.Trim(), XmlDateTimeSerializationMode.RoundtripKind); }
        catch (FormatException exception) { throw new InvalidOperationException($"Value of '{owner}' is not a valid XML date or time.", exception); }
        var text = new StringBuilder();
        for (var index = 0; index < format.Length;)
        {
            var letter = format[index];
            var run = 1;
            while (index + run < format.Length && format[index + run] == letter) run++;
            text.Append(letter switch
            {
                'y' when run == 4 => moment.Year.ToString("0000", CultureInfo.InvariantCulture),
                'y' => (moment.Year % 100).ToString("00", CultureInfo.InvariantCulture),
                'M' => moment.Month.ToString("00", CultureInfo.InvariantCulture),
                'd' => moment.Day.ToString("00", CultureInfo.InvariantCulture),
                'H' => moment.Hour.ToString("00", CultureInfo.InvariantCulture),
                'h' => (twelveHour ? (moment.Hour % 12 == 0 ? 12 : moment.Hour % 12) : moment.Hour).ToString("00", CultureInfo.InvariantCulture),
                'm' => moment.Minute.ToString("00", CultureInfo.InvariantCulture),
                's' => moment.Second.ToString("00", CultureInfo.InvariantCulture),
                'f' => (moment.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture)[..run],
                _ => new string(letter, run)
            });
            index += run;
        }
        return text.ToString();
    }
}
