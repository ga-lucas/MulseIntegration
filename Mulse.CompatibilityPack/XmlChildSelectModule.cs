using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Takes one child element of an XML message's root and makes it a message of its own. It splits a multipart message that the importer
/// represents as one wrapper document (the BizTalk multipart-part-to-message copy), for example the header or the body of an HL7 message.
/// </summary>
public sealed class XmlChildSelectModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("partNumber", "Part number", "Which child element of the root to take, counting from 1 in document order. In an imported HL7 multipart message 1 is the MSH document and 2 is the body document.", true, ModuleSettingInputKind.Number, "1"),
        new("expectedRootName", "Expected element name", "Optional local name the selected element must have; a different name stops the flow instead of passing the wrong part on.", false),
        new("metadata", "Message metadata", "Clear starts the new message with no metadata, as BizTalk starts a copied part with a new context (assumed). Keep copies the metadata of the source message.", false, ModuleSettingInputKind.Select, "Clear",
            [new("Clear", "Start with none"), new("Keep", "Copy from the source")])
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "xml-child-select",
        "XML part select",
        ModuleKind.Render,
        "Takes the Nth child element of the root of an XML message and returns it as a standalone XML message.",
        SettingDescriptors,
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var number = ModuleSettings.GetRequired(step.Settings, "partNumber", Descriptor.Id);
        if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index < 1)
            throw new ArgumentException("Setting 'partNumber' must be a whole number of at least 1.");
        var expected = ModuleSettings.GetOptional(step.Settings, "expectedRootName");
        var metadataSetting = ModuleSettings.GetOptional(step.Settings, "metadata");
        var keep = string.IsNullOrWhiteSpace(metadataSetting) ? false
            : string.Equals(metadataSetting.Trim(), "Keep", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(metadataSetting.Trim(), "Clear", StringComparison.OrdinalIgnoreCase) ? false
            : throw new ArgumentException("Setting 'metadata' must be Clear or Keep.");

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(new StringReader(payload.GetText()), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                document = XDocument.Load(reader);
            }
            catch (XmlException exception)
            {
                throw new InvalidOperationException($"XML part select could not read '{payload.Name}' as XML.", exception);
            }

            var children = document.Root!.Elements().ToArray();
            if (index > children.Length)
                throw new InvalidOperationException($"XML part select needs part {index} but '{payload.Name}' has {children.Length} child element(s) under its root.");
            var part = children[index - 1];
            if (!string.IsNullOrWhiteSpace(expected) && part.Name.LocalName != expected.Trim())
                throw new InvalidOperationException($"XML part select expected part {index} of '{payload.Name}' to be '{expected.Trim()}' but found '{part.Name.LocalName}'.");
            var metadata = keep ? new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
            return new IntegrationPayload(payload.Name, BinaryData.FromString(new XElement(part).ToString(SaveOptions.DisableFormatting)), "application/xml", metadata);
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
