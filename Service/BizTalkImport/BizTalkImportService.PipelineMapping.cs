using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static bool IsBtaHl7Assembler(string className)
        => className.Contains("BTAHL7", StringComparison.OrdinalIgnoreCase) && className.Contains("Asm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When a send port's binding carries no SendPipelineData, falls back to the design-time component properties of the
    /// referenced custom pipeline (.btp) so the same component-to-step mapping applies. Values are design-time defaults.
    /// </summary>
    private static BindingFileAnalysis ApplyPipelineFileDefaults(BindingFileAnalysis binding, IEnumerable<BizTalkArtifactResponse> artifacts, List<string> draftWarnings)
    {
        var pipelineFiles = artifacts
            .Where(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static artifact => artifact.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pipelineFiles.Length == 0)
        {
            return binding;
        }

        var receivePorts = binding.ReceivePorts.Select(port => port with
        {
            ReceiveLocations = port.ReceiveLocations.Select(location =>
            {
                if (location.Operational.PipelineComponents.Count > 0 || location.ReceivePipeline.Length == 0
                    || FindPipelineFile(pipelineFiles, location.ReceivePipeline) is not { } file
                    || ReadPipelineFileComponents(file.FullPath) is not { Count: > 0 } components)
                {
                    return location;
                }

                draftWarnings.Add($"Receive location '{location.Name}' has no pipeline configuration in the binding; component properties were read from the design-time pipeline file '{Path.GetFileName(file.FullPath)}'. Deployed per-location overrides are not known - compare with the deployed binding.");
                return location with { Operational = location.Operational with { PipelineComponents = components } };
            }).ToArray()
        }).ToArray();

        var ports = binding.SendPorts.Select(port =>
        {
            if (port.Operational.PipelineComponents.Count > 0 || port.TransmitPipeline.Length == 0)
            {
                return port;
            }

            var file = FindPipelineFile(pipelineFiles, port.TransmitPipeline);
            if (file is null)
            {
                return port;
            }

            var components = ReadPipelineFileComponents(file.FullPath);
            if (components.Count == 0)
            {
                return port;
            }

            draftWarnings.Add($"Send port '{port.Name}' has no pipeline configuration in the binding; component properties were read from the design-time pipeline file '{Path.GetFileName(file.FullPath)}'. Deployed per-port overrides are not known - compare with the deployed binding.");
            return port with { Operational = port.Operational with { PipelineComponents = components } };
        }).ToArray();

        return binding with { ReceivePorts = receivePorts, SendPorts = ports };
    }

    private static BizTalkArtifactResponse? FindPipelineFile(IEnumerable<BizTalkArtifactResponse> pipelineFiles, string pipelineTypeName)
    {
        var typeName = pipelineTypeName.Split(',')[0].Trim();
        var simpleName = typeName[(typeName.LastIndexOf('.') + 1)..];
        return pipelineFiles.FirstOrDefault(artifact => string.Equals(Path.GetFileNameWithoutExtension(artifact.FullPath), simpleName, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<PipelineComponentConfig> ReadPipelineFileComponents(string path)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(path, System.Xml.Linq.LoadOptions.None);
            return document.Descendants()
                .Where(static element => element.Name.LocalName == "Component" && element.Elements().Any(static child => child.Name.LocalName == "Name"))
                .Select(static component => new PipelineComponentConfig(
                    component.Elements().First(static child => child.Name.LocalName == "Name").Value.Split(',')[0].Trim(),
                    component.Elements().Where(static element => element.Name.LocalName == "Properties").Elements()
                        .Where(static property => property.Name.LocalName == "Property" && property.Attribute("Name") is not null && !SecretPropertyPattern.IsMatch(property.Attribute("Name")!.Value))
                        .GroupBy(static property => property.Attribute("Name")!.Value, StringComparer.Ordinal)
                        .ToDictionary(static group => group.Key, static group => (group.First().Elements().FirstOrDefault(static child => child.Name.LocalName == "Value")?.Value ?? group.First().Value).Trim(), StringComparer.Ordinal)))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns the pipeline components configured on each send port into draft augment steps with their property values
    /// pre-filled. Only components with a verified native equivalent are mapped (HL7TypeCaster from the Mulse.Hl7 package
    /// and SetProperty from the Compatibility Pack); everything else gets an explicit warning so nothing is silently dropped.
    /// </summary>
    private static IReadOnlyList<BizTalkDraftStepResponse> CreatePipelineComponentAugments(
        IReadOnlyList<BindingFileAnalysis> relatedBindings,
        List<string> draftWarnings)
    {
        var steps = new List<BizTalkDraftStepResponse>();
        var sendPorts = ConsolidateSendPorts(relatedBindings.SelectMany(static binding => binding.SendPorts).ToArray(), []);
        var perPort = new List<List<BizTalkDraftStepResponse>>(sendPorts.Count);

        foreach (var port in sendPorts)
        {
            var portSteps = new List<BizTalkDraftStepResponse>();
            perPort.Add(portSteps);
            var assemblerSeen = false;
            foreach (var component in port.Operational.PipelineComponents)
            {
                var className = component.ClassName;
                if (assemblerSeen && (className.EndsWith("HL7TypeCaster", StringComparison.OrdinalIgnoreCase) || className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase)))
                    draftWarnings.Add($"Send port '{port.Name}' has '{className}' after the HL7 assembler, but draft augments run before rendering. Source order is not preserved; review and implement the pipeline explicitly.");
                if (className.EndsWith("HL7TypeCaster", StringComparison.OrdinalIgnoreCase))
                {
                    portSteps.Add(new BizTalkDraftStepResponse("hl7-message-type-augment", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                }
                else if (className.EndsWith("PS.SetProperty", StringComparison.OrdinalIgnoreCase) || className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase))
                {
                    portSteps.Add(CreateSetPropertyStep(component, $"Send port '{port.Name}'", draftWarnings));
                }
                else if (TryCreatePromotionSteps(component, $"Send port '{port.Name}'", portSteps, draftWarnings))
                {
                    // handled
                }
                else if (className.EndsWith("Hl7AddDelimit", StringComparison.OrdinalIgnoreCase))
                {
                    draftWarnings.Add($"Send port '{port.Name}' uses '{className}' (AddBlockCharacters). Its source Execute passes the message through unchanged (the block start/end characters are never applied), so no step is generated; add MLLP framing in the deliver settings if it is needed.");
                }
                else if (className.Contains("MIME_SMIME", StringComparison.OrdinalIgnoreCase))
                {
                    var detail = string.Join(", ", component.Properties.Select(static pair => $"{pair.Key}={pair.Value}"));
                    draftWarnings.Add($"Send port '{port.Name}' uses the MIME/S-MIME encoder{(detail.Length > 0 ? $" ({detail})" : string.Empty)}. No native MIME/S-MIME module exists: attach the body through the SMTP deliver settings and decide separately how signing/encryption is handled.");
                }
                else if (IsBtaHl7Assembler(className))
                {
                    assemblerSeen = true;
                    draftWarnings.Add($"Send port '{port.Name}' uses '{className}'; the assembler is mapped to the 'hl7-xml-render' step (trailing-delimiter setting imported). Verify delimiters, escape sequences and segment validation against synthetic messages.");
                }
                else
                {
                    draftWarnings.Add($"Send port '{port.Name}' uses unsupported pipeline component '{className}'; its behavior has not been translated. Review the source configuration and implement an explicit native replacement.");
                }
            }
        }

            var receiveSteps = CreateReceivePipelineSteps(relatedBindings, draftWarnings);
            steps.AddRange(receiveSteps);
            var identical = perPort.Count <= 1 || perPort.Skip(1).All(port => SameSteps(perPort[0], port));
            for (var index = 0; index < perPort.Count; index++)
            {
                if (identical && index > 0)
                {
                    break;
                }

                foreach (var step in perPort[index])
                {
                    if (identical)
                    {
                        steps.Add(step);
                        continue;
                    }

                    var scoped = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
                    {
                        ["conditionJson"] = $$"""[{"source":"Metadata","path":"targetRoute","operator":"Equals","value":"route-{{index + 1}}"}]"""
                    };
                    steps.Add(step with { Settings = scoped });
                }
            }

            if (steps.Count > receiveSteps.Count)
            {
                draftWarnings.Add($"{steps.Count - receiveSteps.Count} pipeline-component augment step(s) were generated from send-port pipeline configuration. " +
                    (identical
                        ? "The mapped augment steps are identical across send ports, so they run for the whole flow. Unsupported components and renderer placement still require review."
                        : "Mapped send-port augment steps differ, so each step is scoped to its delivery route with a targetRoute condition (route-N, in send-port order); populate the 'targetRoute' metadata for each payload before enabling."));
            }

            return steps;

        static bool SameSteps(List<BizTalkDraftStepResponse> first, List<BizTalkDraftStepResponse> second)
            => first.Count == second.Count && first.Zip(second).All(pair =>
                pair.First.Module == pair.Second.Module && pair.First.Settings.Count == pair.Second.Settings.Count
                && pair.First.Settings.All(setting => pair.Second.Settings.TryGetValue(setting.Key, out var value) && value == setting.Value));
    }

    private sealed record PromotionComponent(string NamespaceProperty, (string Promoted, string Property)[] Properties);

    // Promoting property-setter components, verified from their Execute methods: each promotes fixed names in the namespace
    // held by one of the component's own properties. Names that differ from the component property are listed explicitly.
    private static readonly IReadOnlyDictionary<string, PromotionComponent> PromotionComponents = new Dictionary<string, PromotionComponent>(StringComparer.OrdinalIgnoreCase)
    {
        ["ConsumptionPropertySetter"] = new("PropertyNamespace", [("OrganizationId", "OrganizationId"), ("OrganizationName", "OrganizationName"), ("CommonConsumptionMap", "CommonConsumptionMap"), ("FailureNotification", "FailureNotification")]),
        ["SetAndPromoteItemSyncProperties"] = new("PropertyNamespace", [("OrganizationId", "OrganizationId"), ("OrganizationName", "OrganizationName"), ("ReadyForSynch", "ReadyForSync"), ("CommonItemImportMap", "CommonItemImportMap"), ("FailureNotification", "FailureNotification"), ("SuccessNotification", "SuccessNotification"), ("ImportThrottle", "ImportThrottle"), ("ImportBatchSize", "ImportBatchSize")]),
        ["MapNamePromotions"] = new("CustomPropertySchema_NameSpace", [("HeaderMapName", "HeaderMapName"), ("BodyMapName", "BodyMapName"), ("TrailerMapName", "TrailerMapName"), ("PreProcessor_MapName", "PreProcessor_MapName"), ("InterfaceName", "InterfaceName"), ("CustomerName", "CustomerName"), ("DestinationMessageType", "DestinationMessageType"), ("PreProcessing_Required", "PreProcessing_Required"), ("IsHL7", "IsHL7"), ("IsFlatFile", "IsFlatFile"), ("IsXml", "IsXml"), ("IsEDIData", "IsEDI")]),
        ["HL7Promotions"] = new("hl7_propertyNameSpace", [("hl7_MsgType", "hl7_MsgType"), ("hl7_Msh2", "hl7_Msh2"), ("hl7_Msh3_1", "hl7_Msh3_1"), ("hl7_Msh5_1", "hl7_Msh5_1"), ("hl7_MsgClass", "hl7_MsgClass"), ("hl7_IsBatch", "hl7_IsBatch"), ("hl7_IsParseError", "hl7_IsParseError"), ("hl7_IsZpartPresent", "hl7_IsZpartPresent"), ("hl7_IsSegmentDelimitedToChar", "hl7_IsSegmentDelimitedToChar"), ("hl7_XProperty1", "hl7_XProperty1"), ("hl7_XProperty2", "hl7_XProperty2"), ("hl7_XProperty3", "hl7_XProperty3")])
    };

    private static BizTalkDraftStepResponse CreateSetPropertyStep(PipelineComponentConfig component, string owner, List<string> warnings)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["propertyName"] = component.Properties.GetValueOrDefault("PropertyName", string.Empty),
            ["propertyNamespace"] = component.Properties.GetValueOrDefault("PropertyNamespace", string.Empty),
            ["propertyValue"] = component.Properties.GetValueOrDefault("PropertyValue", string.Empty)
        };
        if (settings.Values.Any(static value => value.Length == 0))
        {
            warnings.Add($"{owner} has a SetProperty pipeline component with an empty name, namespace or value; like the original it writes nothing until all three are configured on the 'set-context-property-augment' step.");
        }

        return new BizTalkDraftStepResponse("set-context-property-augment", settings);
    }

    /// <summary>
    /// Maps a promoting property-setter component to one editable 'set-context-property-augment' step per promoted property.
    /// Properties with no configured value still get a step (a no-op until filled in) so every value is editable in the UI.
    /// </summary>
    private static bool TryCreatePromotionSteps(PipelineComponentConfig component, string owner, List<BizTalkDraftStepResponse> steps, List<string> warnings)
    {
        var simpleName = component.ClassName[(component.ClassName.LastIndexOf('.') + 1)..];
        if (!PromotionComponents.TryGetValue(simpleName, out var definition))
        {
            return false;
        }

        var propertyNamespace = component.Properties.GetValueOrDefault(definition.NamespaceProperty, string.Empty);
        var empty = new List<string>();
        foreach (var (promoted, property) in definition.Properties)
        {
            var value = component.Properties.GetValueOrDefault(property, string.Empty);
            if (value.Length == 0)
            {
                empty.Add(promoted);
            }

            steps.Add(new BizTalkDraftStepResponse("set-context-property-augment", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["propertyName"] = promoted,
                ["propertyNamespace"] = propertyNamespace,
                ["propertyValue"] = value
            }));
        }

        warnings.Add($"{owner} uses '{simpleName}', which promotes {definition.Properties.Length} context properties; one 'set-context-property-augment' step was drafted per property." +
            (propertyNamespace.Length == 0 ? $" The component's '{definition.NamespaceProperty}' value is empty, so set the namespace on each step." : string.Empty) +
            (empty.Count > 0 ? $" No value is configured for: {string.Join(", ", empty)}. Those steps write nothing until a value is entered (BizTalk's behavior for promoting an empty value was not verified)." : string.Empty));
        return true;
    }

    /// <summary>
    /// Maps recognized components of receive-location pipelines to flow-wide augment steps. Standard Microsoft components are
    /// left to the parse step; unrecognized custom components are reported. Locations that differ are reported, not merged.
    /// </summary>
    private static List<BizTalkDraftStepResponse> CreateReceivePipelineSteps(IReadOnlyList<BindingFileAnalysis> relatedBindings, List<string> draftWarnings)
    {
        var locations = relatedBindings.SelectMany(static binding => binding.ReceivePorts).SelectMany(static port => port.ReceiveLocations)
            .GroupBy(static location => location.Name, StringComparer.OrdinalIgnoreCase).Select(static group => group.First())
            .Where(static location => location.Operational.PipelineComponents.Count > 0)
            .ToArray();
        var result = new List<BizTalkDraftStepResponse>();
        List<BizTalkDraftStepResponse>? firstSteps = null;
        string? firstName = null;
        foreach (var location in locations)
        {
            var owner = $"Receive location '{location.Name}'";
            var steps = new List<BizTalkDraftStepResponse>();
            var warnings = new List<string>();
            foreach (var component in location.Operational.PipelineComponents)
            {
                var className = component.ClassName;
                if (className.EndsWith("PS.SetProperty", StringComparison.OrdinalIgnoreCase) || className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(CreateSetPropertyStep(component, owner, warnings));
                }
                else if (TryCreatePromotionSteps(component, owner, steps, warnings))
                {
                    // handled
                }
                else if (className.EndsWith("Hl7AddDelimit", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{owner} uses '{className}' (AddBlockCharacters), which passes the message through unchanged in the source; no step is generated.");
                }
                else if (!className.StartsWith("Microsoft.BizTalk.Component", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"{owner} uses unsupported pipeline component '{className}'; its behavior has not been translated. Review the source configuration and implement an explicit native replacement.");
                }
            }

            if (firstSteps is null)
            {
                firstSteps = steps;
                firstName = location.Name;
                result.AddRange(steps);
                draftWarnings.AddRange(warnings);
                if (steps.Count > 0)
                {
                    draftWarnings.Add($"{steps.Count} pipeline-component augment step(s) were generated from the receive pipeline of '{location.Name}'. They run for the whole flow; review values and ordering against the source pipeline.");
                }
            }
            else if (!SameStepLists(firstSteps, steps))
            {
                draftWarnings.Add($"{owner} has receive pipeline configuration that differs from '{firstName}'. Only '{firstName}' was generated; configure this location's values separately (for example with a condition on the step).");
            }
        }

        return result;
    }

    private static bool SameStepLists(List<BizTalkDraftStepResponse> first, List<BizTalkDraftStepResponse> second)
        => first.Count == second.Count && first.Zip(second).All(pair =>
            pair.First.Module == pair.Second.Module && pair.First.Settings.Count == pair.Second.Settings.Count
            && pair.First.Settings.All(setting => pair.Second.Settings.TryGetValue(setting.Key, out var value) && value == setting.Value));
}
