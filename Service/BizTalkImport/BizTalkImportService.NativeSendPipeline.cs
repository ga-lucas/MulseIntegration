using Mulse.Modules.ControlFlow;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    /// <summary>The native nodes for one send port's transmit pipeline, in source order, plus anything that stops the chain from being used.</summary>
    /// <param name="Nodes">Augment/Render nodes that run before the delivery, each reading the previous node's named message.</param>
    /// <param name="FinalMessage">The message the delivery must send.</param>
    /// <param name="Blockers">Components with no proven native equivalent. When any exist the send stays Blocked.</param>
    /// <param name="MimeAttachment">The pipeline wraps the body in a MIME message with no signing or encryption; only an SMTP delivery can express that.</param>
    private sealed record TransmitPipelineChain(List<NativeFlowNode> Nodes, string FinalMessage, List<string> Blockers, bool MimeAttachment);

    /// <summary>The flat-file renderer choices that exist in the flow's shared choice table, and a way to find the message type a named flat-file schema writes.</summary>
    /// <param name="MessageTypes">Message types (<c>namespace#root</c>) that have a flat-file renderer choice.</param>
    /// <param name="MessageTypeOfSchema">Resolves a schema class name (an assembler's DocumentSpecName) to its message type, or null when it is not a supported flat-file schema.</param>
    private sealed record FlatFileChoices(IReadOnlySet<string> MessageTypes, Func<string, string?> MessageTypeOfSchema);

    private static FlatFileChoices? ReadFlatFileChoices(string? mapChoices, BizTalkSchemaSourceCatalog schemas)
    {
        if (mapChoices is null) return null;
        var types = Mulse.Modules.Mapping.RenderDispatchDefinition.Read(mapChoices, allowEmpty: true)
            .Where(choice => choice.Module == "flat-file-schema-render").Select(choice => choice.Value).ToHashSet(StringComparer.Ordinal);
        if (types.Count == 0) return null;
        return new(types, specification =>
        {
            var evidence = schemas.Resolve(specification.Split(',')[0].Trim());
            if (evidence.Status != "SourceCandidate" || evidence.Candidates.Count != 1) return null;
            try
            {
                var documents = schemas.ReadSchemaDocuments(evidence.Candidates[0].SourcePath);
                return Mulse.CompatibilityPack.FlatFileSchema.IsFlatFile(documents[0]) ? Mulse.CompatibilityPack.FlatFileSchema.Compile(documents).MessageType : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
        });
    }

    /// <summary>
    /// Translates the components of a send port's custom transmit pipeline (from the binding, or the design-time pipeline file) into a chain of named
    /// Augment/Render nodes. Only components whose behavior is proven are translated: property setters, the HL7 type caster, the BTAHL7 assembler,
    /// pass-through block-character adders, and a MIME encoder with no signing or encryption. Anything else is listed as a blocker by name.
    /// Returns null when the port's pipeline components are unknown.
    /// </summary>
    private static TransmitPipelineChain? BuildTransmitPipelineChain(SendPortAnalysis send, string nodeId, string inputMessage, Action<string> warn, FlatFileChoices? flatFile = null)
    {
        var components = send.Operational.PipelineComponents;
        if (components.Count == 0) return null;
        var nodes = new List<NativeFlowNode>();
        var blockers = new List<string>();
        var current = inputMessage;
        var mime = false;
        var pipelineName = send.TransmitPipeline.Split(',')[0].Trim();
        string Next() => $"{inputMessage}~{nodeId}~{nodes.Count + 1}";

        void Add(NativeFlowNodeKind kind, string module, string label, Dictionary<string, string> settings)
        {
            var output = Next();
            nodes.Add(new()
            {
                Id = $"{nodeId}-pipe-{nodes.Count + 1}", Name = $"{label} ({send.Name})", Kind = kind, Module = module,
                Settings = new(settings, StringComparer.OrdinalIgnoreCase), InputMessages = [current], MessageName = output
            });
            current = output;
        }

        foreach (var component in components)
        {
            var className = component.ClassName;
            var simpleName = className[(className.LastIndexOf('.') + 1)..];
            if (className.EndsWith("HL7TypeCaster", StringComparison.OrdinalIgnoreCase))
            {
                Add(NativeFlowNodeKind.Augment, "hl7-message-type-augment", "HL7 message type", []);
            }
            else if (className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase))
            {
                var name = component.Properties.GetValueOrDefault("PropertyName", "");
                var ns = component.Properties.GetValueOrDefault("PropertyNamespace", "");
                var value = component.Properties.GetValueOrDefault("PropertyValue", "");
                Add(NativeFlowNodeKind.Augment, "set-context-property-augment", "Set property", new()
                    { ["propertyName"] = name, ["propertyNamespace"] = ns, ["propertyValue"] = value });
                if (name.Length == 0 || ns.Length == 0 || value.Length == 0)
                    warn("A SetProperty pipeline component with an empty name, namespace or value is imported as an editable 'set-context-property-augment' node; like the original it writes nothing until all three are set.");
            }
            else if (PromotionComponents.TryGetValue(simpleName, out var promotion))
            {
                var propertyNamespace = component.Properties.GetValueOrDefault(promotion.NamespaceProperty, "");
                foreach (var (promoted, property) in promotion.Properties)
                    Add(NativeFlowNodeKind.Augment, "set-context-property-augment", "Set " + promoted, new()
                        { ["propertyName"] = promoted, ["propertyNamespace"] = propertyNamespace, ["propertyValue"] = component.Properties.GetValueOrDefault(property, "") });
                warn($"The '{simpleName}' pipeline component is imported as one editable 'set-context-property-augment' node per promoted property; a property with no configured value writes nothing until you enter one (BizTalk's behavior for promoting an empty value was not verified).");
            }
            else if (IsBtaHl7Assembler(className))
            {
                var trailing = component.Properties.GetValueOrDefault("TrailingDelimiterAllowed");
                Add(NativeFlowNodeKind.Render, "hl7-xml-render", "HL7 XML to ER7", new()
                    { ["trimTrailingDelimiters"] = trailing is not null && !IsTrue(trailing) && trailing != "-1" ? "true" : "false" });
                warn("The BTAHL7 assembler is imported as the 'hl7-xml-render' node (HL7 XML to ER7 text). Delimiters come from the message's MSH segment; escape-sequence encoding, segment validation and batch handling are not applied. Compare the output with a real BizTalk-assembled message.");
            }
            else if (className.EndsWith("Hl7AddDelimit", StringComparison.OrdinalIgnoreCase))
            {
                warn("The 'Hl7AddDelimit' (AddBlockCharacters) pipeline component passes the message through unchanged in the source, so it has no node; add MLLP framing in the deliver settings if it is needed.");
            }
            else if (className.Contains("MIME_SMIME", StringComparison.OrdinalIgnoreCase))
            {
                var attachment = IsPipelineTrue(component.Properties.GetValueOrDefault("SendBodyPartAsAttachment"));
                var signature = component.Properties.GetValueOrDefault("SignatureType", "0");
                var encrypt = IsPipelineTrue(component.Properties.GetValueOrDefault("EnableEncryption"));
                if (!attachment) blockers.Add($"MIME/S-MIME encoder with SendBodyPartAsAttachment off (the body would be sent as the MIME message itself)");
                if (signature is not ("0" or "")) blockers.Add($"MIME/S-MIME encoder signing (SignatureType={signature})");
                if (encrypt) blockers.Add("MIME/S-MIME encoder encryption");
                if (attachment && signature is "0" or "" && !encrypt) mime = true;
            }
            else if (className.Contains("FFAsm", StringComparison.OrdinalIgnoreCase))
            {
                var header = component.Properties.GetValueOrDefault("HeaderSpecName", "");
                var trailer = component.Properties.GetValueOrDefault("TrailerSpecName", "");
                var document = component.Properties.GetValueOrDefault("DocumentSpecName", "");
                var charset = component.Properties.GetValueOrDefault("TargetCharset", "");
                var codePage = component.Properties.GetValueOrDefault("TargetCodePage", "0");
                var problem = header.Length > 0 || trailer.Length > 0 ? $"names a header or trailer schema ('{(header.Length > 0 ? header : trailer)}'), which the native flat-file renderer does not write"
                    : charset.Length > 0 && !charset.Replace("-", "").Equals("utf8", StringComparison.OrdinalIgnoreCase) ? $"writes in the '{charset}' character set; the native renderer writes UTF-8 text"
                    : codePage is not ("0" or "65001" or "") ? $"writes in code page {codePage}; the native renderer writes UTF-8 text"
                    : IsPipelineTrue(component.Properties.GetValueOrDefault("PreserveBom")) ? "preserves a byte-order mark, which the native renderer does not write"
                    : flatFile is null ? "has no flat-file schema to render with: no converted map in this import writes a flat-file schema the native renderer supports"
                    : null;
                NativeExpression? selection = null;
                if (problem is null && document.Length > 0)
                {
                    var type = flatFile!.MessageTypeOfSchema(document);
                    if (type is not null && flatFile.MessageTypes.Contains(type)) selection = new() { Kind = NativeExpressionKind.Literal, Literal = NativeScalar.From(type) };
                    else problem = $"names the document schema '{document.Split(',')[0].Trim()}', which is not a flat-file schema the native renderer supports or is not in the flow's choice table";
                }
                else if (problem is null)
                    selection = new() { Kind = NativeExpressionKind.MessageValue, Fact = new() { Kind = NativeMessageFactKind.XmlMessageType, MessageName = current } };
                if (problem is not null)
                    blockers.Add($"flat-file assembler '{simpleName}' ({problem})");
                else
                {
                    var output = Next();
                    nodes.Add(new()
                    {
                        Id = $"{nodeId}-pipe-{nodes.Count + 1}", Name = $"Flat-file assembler ({send.Name})", Kind = NativeFlowNodeKind.Render,
                        Module = Mulse.Modules.Mapping.RenderDispatchDefinition.ModuleId, InputMessages = [current], MessageName = output, SelectionExpression = selection
                    });
                    current = output;
                    warn(document.Length > 0
                        ? $"The flat-file assembler on '{send.Name}' is imported as a render node that writes the message with the flat-file schema '{document.Split(',')[0].Trim()}' from the flow's shared choice table. BizTalk's exact output was not verified; see the flat-file renderer's settings for the assumptions (wrapping, too-long values, hour clock, absent optional fields) and compare the output with a real BizTalk file."
                        : $"The flat-file assembler on '{send.Name}' has no fixed schema, so it is imported as a render node that picks the flat-file schema from the message's type (namespace#root) in the flow's shared choice table, as BizTalk does. A message whose type has no choice stops the flow. BizTalk's exact output was not verified; see the flat-file renderer's settings for the assumptions (wrapping, too-long values, hour clock, absent optional fields) and compare the output with a real BizTalk file.");
                }
            }
            else
            {
                blockers.Add($"component '{className}'");
            }
        }

        var fromFile = !send.Operational.HasSendPipelineConfiguration;
        warn(fromFile
            ? $"Send port pipelines are imported from the design-time pipeline file for '{pipelineName}' because the binding carries no per-port component configuration. The deployed values are not known; compare the binding of the target environment."
            : $"Send port pipeline '{pipelineName}' is imported with the per-port component configuration from the binding.");
        return new(nodes, current, blockers, mime);
    }

    // A binding exports a true Boolean as the COM value -1; a pipeline file writes true.
    private static bool IsPipelineTrue(string? value) => IsTrue(value) || value == "-1";

    /// <summary>Applies the MIME wrapper of a translated pipeline to an SMTP delivery: the body is sent as an attachment. Returns the reason it cannot, or null.</summary>
    private static string? ApplyMimeAttachment(TransmitPipelineChain chain, NativeFlowNode deliver, Action<string> warn)
    {
        if (!chain.MimeAttachment) return null;
        if (deliver.Module != "smtp-email-deliver")
            return "The MIME/S-MIME encoder wraps the message for an email transport, but this send does not use SMTP; no MIME wrapper is applied to other transports.";
        deliver.Settings["attachPayload"] = "true";
        deliver.Settings["usePayloadAsBody"] = "false";
        warn("The MIME/S-MIME encoder (body sent as an attachment, no signing, no encryption) is imported as the SMTP deliver's 'attach payload' setting. The MIME headers BizTalk writes (content type, transfer encoding, attachment file name from the MIME FileName property) are produced by the SMTP library instead; check a delivered email against a real BizTalk email.");
        return null;
    }
}
