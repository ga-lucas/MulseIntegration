using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.Hl7;

/// <summary>
/// One declared element of an HL7 v2 XML schema (the BizTalk BTAHL7 schema style): its name, whether it has child elements, and the child elements
/// in schema order. Group wrappers made of nested xs:sequence/xs:choice are flattened, so the order is the order of the HL7 positions.
/// </summary>
internal sealed class Hl7SchemaElement
{
    private readonly Dictionary<string, int> _indexByName = new(StringComparer.Ordinal);

    public Hl7SchemaElement(string name, bool repeats, IReadOnlyList<Hl7SchemaElement>? children)
    {
        Name = name; Repeats = repeats; Children = children ?? [];
        IsComplex = children is not null;
        for (var index = 0; index < Children.Count; index++) _indexByName.TryAdd(Children[index].Name, index);
    }

    public string Name { get; }
    public bool Repeats { get; }
    public bool IsComplex { get; }
    public IReadOnlyList<Hl7SchemaElement> Children { get; }

    public int IndexOf(string childName) => _indexByName.TryGetValue(childName, out var index) ? index : -1;
}

/// <summary>
/// A set of XSD documents (root message schemas plus the segment and datatype schemas they import) read only far enough to give the order and nesting of
/// HL7 fields, components and sub-components. Types that are not defined in the set are treated as simple values; a message that then has child elements
/// under such a value is rejected when it is rendered or parsed.
/// </summary>
internal sealed class Hl7SchemaSet
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private const int MaximumDepth = 24;
    private const int MaximumDocuments = 64;
    private const int MaximumDocumentLength = 4_000_000;

    private readonly Dictionary<(string Namespace, string Name), XElement> _complexTypes = new();
    private readonly Dictionary<(string Namespace, string Name), XElement> _simpleTypes = new();
    private readonly Dictionary<(string Namespace, string Name), XElement> _roots = new();
    private readonly Dictionary<XElement, Hl7SchemaElement> _builtTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(string Namespace, string Name), Hl7SchemaElement> _builtRoots = new();

    private Hl7SchemaSet() { }

    public static Hl7SchemaSet Read(IReadOnlyList<string> documents)
    {
        if (documents.Count is 0 or > MaximumDocuments) throw new ArgumentException($"The HL7 schema text must hold 1 to {MaximumDocuments} XSD documents.");
        var set = new Hl7SchemaSet();
        foreach (var text in documents)
        {
            if (text.Length > MaximumDocumentLength) throw new ArgumentException("An HL7 schema document is too large.");
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException exception) { throw new ArgumentException("An HL7 schema document is not well-formed XML: " + exception.Message, exception); }
            var schema = document.Root;
            if (schema is null || schema.Name != Xs + "schema") throw new ArgumentException("An HL7 schema document is not an XSD schema.");
            var targetNamespace = (string?)schema.Attribute("targetNamespace") ?? string.Empty;
            foreach (var child in schema.Elements())
            {
                var name = (string?)child.Attribute("name");
                if (name is null) continue;
                if (child.Name == Xs + "complexType") set.Add(set._complexTypes, (targetNamespace, name), child);
                else if (child.Name == Xs + "simpleType") set.Add(set._simpleTypes, (targetNamespace, name), child);
                else if (child.Name == Xs + "element") set.Add(set._roots, (targetNamespace, name), child);
            }
        }
        return set;
    }

    // The same segment and datatype schemas are imported by many message schemas, so an identical repeat is fine; a different definition is ambiguous.
    private void Add(Dictionary<(string Namespace, string Name), XElement> table, (string Namespace, string Name) key, XElement definition)
    {
        if (table.TryGetValue(key, out var existing))
        {
            if (!XNode.DeepEquals(existing, definition))
                throw new ArgumentException($"The HL7 schemas define '{key.Name}' in namespace '{key.Namespace}' more than once with different content.");
            return;
        }
        table[key] = definition;
    }

    public IReadOnlyList<string> RootNames => _roots.Keys.Select(key => key.Namespace.Length == 0 ? key.Name : key.Namespace + "#" + key.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The declared root elements as XML names, in no particular order.</summary>
    public IEnumerable<XName> RootXmlNames => _roots.Keys.Select(key => XName.Get(key.Name, key.Namespace));

    /// <summary>Finds the declared root element of a message by its XML name. A root whose namespace differs is accepted only when its local name is unique in the set.</summary>
    public Hl7SchemaElement? FindRoot(XName name)
    {
        var key = (name.NamespaceName, name.LocalName);
        if (!_roots.TryGetValue(key, out var declaration))
        {
            var byLocal = _roots.Where(pair => pair.Key.Name == name.LocalName).ToArray();
            if (byLocal.Length != 1) return null;
            key = byLocal[0].Key; declaration = byLocal[0].Value;
        }
        if (_builtRoots.TryGetValue(key, out var built)) return built;
        built = BuildElement(declaration, 0);
        _builtRoots[key] = built;
        return built;
    }

    private Hl7SchemaElement BuildElement(XElement declaration, int depth)
    {
        if (depth > MaximumDepth) throw new ArgumentException("The HL7 schema nests too deeply.");
        var name = (string?)declaration.Attribute("name") ?? throw new ArgumentException("An HL7 schema element has no name (element references are not supported).");
        var maxOccurs = (string?)declaration.Attribute("maxOccurs");
        var repeats = maxOccurs == "unbounded" || int.TryParse(maxOccurs, out var maximum) && maximum > 1;
        var typeName = (string?)declaration.Attribute("type");
        XElement? complex = declaration.Element(Xs + "complexType");
        if (complex is null && typeName is not null)
        {
            var (typeNamespace, localName, builtIn) = ResolveQualified(declaration, typeName);
            if (!builtIn && _complexTypes.TryGetValue((typeNamespace, localName), out var named)) complex = named;
        }
        if (complex is null) return new(name, repeats, null);
        if (_builtTypes.TryGetValue(complex, out var cached) && cached.Name == name && cached.Repeats == repeats) return cached;
        var children = new List<Hl7SchemaElement>();
        Collect(complex, children, depth + 1);
        var element = new Hl7SchemaElement(name, repeats, children);
        if (complex.Attribute("name") is not null) _builtTypes[complex] = element;
        return element;
    }

    private void Collect(XElement container, List<Hl7SchemaElement> children, int depth)
    {
        foreach (var item in container.Elements())
        {
            if (item.Name == Xs + "element") children.Add(BuildElement(item, depth));
            else if (item.Name == Xs + "sequence" || item.Name == Xs + "choice" || item.Name == Xs + "all") Collect(item, children, depth + 1);
            else if (item.Name == Xs + "complexContent" || item.Name == Xs + "group")
                throw new ArgumentException($"The HL7 schema uses '{item.Name.LocalName}', which is not supported; HL7 schemas list their fields in plain sequences.");
        }
    }

    private static (string Namespace, string Name, bool BuiltIn) ResolveQualified(XElement context, string qualified)
    {
        var colon = qualified.IndexOf(':');
        var prefix = colon < 0 ? string.Empty : qualified[..colon];
        var local = colon < 0 ? qualified : qualified[(colon + 1)..];
        var ns = colon < 0 ? context.GetDefaultNamespace().NamespaceName : context.GetNamespaceOfPrefix(prefix)?.NamespaceName ?? string.Empty;
        // An unprefixed name with no default namespace and a prefixed built-in both mean the schema-of-schemas namespace only for the xs prefix.
        return (ns, local, ns == Xs.NamespaceName);
    }

    private static readonly Regex SegmentName = new(@"^([A-Z][A-Z0-9]{2})(?:_|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex FieldName = new(@"^([A-Z][A-Z0-9]{2})[._](\d+)(?:[._]|$)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The three-character segment ID when the element is an HL7 segment: a complex element named after the segment whose first child is field 1 or 2 of that segment (MSH starts at 2 in the 2.X schemas).</summary>
    public static string? SegmentId(Hl7SchemaElement element)
    {
        if (!element.IsComplex || element.Children.Count == 0) return null;
        var segment = SegmentName.Match(element.Name);
        if (!segment.Success) return null;
        var first = FieldName.Match(element.Children[0].Name);
        return first.Success && first.Groups[1].Value == segment.Groups[1].Value ? segment.Groups[1].Value : null;
    }

    /// <summary>The HL7 field number encoded in a field element's name (<c>PID_3_Name</c> or <c>MSH.7_Name</c>), or null when the name has none.</summary>
    public static int? FieldNumber(string segmentId, string fieldName)
    {
        var match = FieldName.Match(fieldName);
        return match.Success && match.Groups[1].Value == segmentId && int.TryParse(match.Groups[2].Value, out var number) && number is > 0 and < 10_000 ? number : null;
    }
}
