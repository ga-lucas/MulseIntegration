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

    /// <summary>
    /// Translates the components of a send port's custom transmit pipeline (from the binding, or the design-time pipeline file) into a chain of named
    /// Augment/Render nodes. Only components whose behavior is proven are translated: property setters, the HL7 type caster, the BTAHL7 assembler,
    /// pass-through block-character adders, and a MIME encoder with no signing or encryption. Anything else is listed as a blocker by name.
    /// Returns null when the port's pipeline components are unknown.
    /// </summary>
    private static TransmitPipelineChain? BuildTransmitPipelineChain(SendPortAnalysis send, string nodeId, string inputMessage, Action<string> warn)
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
            else
            {
                blockers.Add(className.Contains("FFAsm", StringComparison.OrdinalIgnoreCase)
                    ? $"flat-file assembler '{simpleName}' (renders the message as flat-file text from a flat-file schema; no native flat-file schema renderer is wired for this send)"
                    : $"component '{className}'");
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
