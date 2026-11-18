using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Orchestration;
using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static readonly JsonSerializerOptions NativeJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 128, Converters = { new JsonStringEnumConverter() }
    };

    // Settings mirror the module's documented defaults; the importer cannot know the BizTalk host's zone/culture, so they stay visible and editable.
    private static readonly IReadOnlyDictionary<string, NativeSourceUdf> KnownUdfs = new Dictionary<string, NativeSourceUdf>(StringComparer.Ordinal)
    {
        ["FilenameMaker.MakeFilename"] = new("filename-template-transform", new Dictionary<string, string> { ["timeZone"] = "UTC", ["culture"] = "Invariant" }, 1)
    };
    private static readonly System.Text.RegularExpressions.Regex NsoftwareTransportPattern = new(@"^nsoftware\.S?FTP( \d{4})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool ContainsTransformCall(IEnumerable<NativeFlowNode> nodes)
        => nodes.Any(node => node.Kind == NativeFlowNodeKind.InvokeTransform || ContainsTransformCall(node.Children) || ContainsTransformCall(node.Otherwise));
    private const string DateTimeWarning = "Date and time values are imported as UTC with the invariant culture: DateTime.Now and DateTime.UtcNow both read the UTC clock (the BizTalk host's local time zone and culture are unknown and not applied), DateTime.Parse treats text without an offset as UTC, and the Year/Month/Day/Hour/Minute/Second parts are UTC parts. The default for an uninitialized DateTime is DateTime.MinValue as UTC. Review every date-time value and literal in the editor (the editor shows server-local time with a UTC toggle).";
    private static string UnsupportedVariableHint(string? type) => type switch
    {
        "System.DateTime" or "System.TimeSpan" => " Native DateTime variables cover Now, DateTime.Parse, the six-argument constructor, Year/Month/Day/Hour/Minute/Second, AddDays/AddHours/AddMinutes, Compare, comparison operators and a Delay on later.Subtract(earlier); TimeSpan variables and any other date/time member are not translated. If this computes a batch window (collect until a daily BatchTime or an N:D/H/M interval), the 'durable-batch-augment' module in message/policy mode replaces the whole loop; for a fixed delay use a native Wait.",
        "System.Type" => " A System.Type variable only holds a map type chosen at run time; use 'Derive native map choices' or a map-dispatch step instead of translating it.",
        _ => ""
    };
    private static bool ContainsNode(IEnumerable<NativeFlowNode> nodes, Func<NativeFlowNode, bool> predicate)
        => nodes.Any(node => predicate(node) || ContainsNode(node.Children, predicate) || ContainsNode(node.Otherwise, predicate));
    private static bool IsHl7Multipart(OdxNode type)
    {
        var parts = type.Children.Where(child => child.ShapeType == "PartDeclaration").ToArray();
        return parts.Length == 3 && type.Children.Count == 3
            && parts.Zip([("MSHSegment", "System.Xml.XmlDocument", "False"), ("BodySegments", "System.Xml.XmlDocument", "True"), ("ZSegments", "System.String", "False")])
                .All(pair => pair.First.Name == pair.Second.Item1 && pair.First.Properties.GetValueOrDefault("ClassName") == pair.Second.Item2
                    && pair.First.Properties.GetValueOrDefault("IsBodyPart") == pair.Second.Item3);
    }

    private static BizTalkFlowCandidateResponse AttachNativeFlow(BizTalkFlowCandidateResponse candidate,
        IReadOnlyList<BindingFileAnalysis> bindings, IReadOnlyList<BizTalkRulePolicyResponse> policies, BizTalkSchemaSourceCatalog schemas,
        IReadOnlyDictionary<string, string>? policyTargets, NativeSourceConfigurationCatalog? configuration, string? mapChoices)
    {
        var report = candidate.DraftFlow.Migration;
        var paths = report?.Artifacts.Where(artifact => artifact.Kind == "Orchestration").Select(artifact => artifact.SourcePath).ToArray() ?? [];
        if (report is null || paths.Length != 1) return candidate;
        var diagnostics = report.Diagnostics.ToList();
        try
        {
            var source = OdxProgramReader.ReadFile(paths[0]);
            var flatFile = ReadFlatFileChoices(mapChoices, schemas);
            var services = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "ServiceDeclaration").ToArray();
            if (services.Length != 1) throw new ArgumentException("Native translation requires exactly one source service declaration.");
            var owners = bindings.SelectMany(binding => binding.Services
                .Where(service => service.AssemblyName == candidate.SourceProject && (service.ServiceName == report.SourceOrchestration
                    || service.ServiceName.EndsWith("." + report.SourceOrchestration, StringComparison.Ordinal)))
                .Select(service => (Binding: binding, Service: service))).ToArray();
            var requirements = candidate.DraftFlow.ConfigurationRequirements.ToList();
            var warnings = candidate.DraftFlow.Warnings.ToList();
            var variableDeclarations = services[0].Children.Where(node => node.ShapeType == "VariableDeclaration").ToArray();
            var messageDeclarations = services[0].Children.Where(node => node.ShapeType == "MessageDeclaration").ToArray();
            var documents = variableDeclarations.Concat(messageDeclarations).Where(node => node.Properties.GetValueOrDefault("Type") == "System.Xml.XmlDocument")
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var messages = messageDeclarations.Select(node => node.Name).Concat(documents)
                .Concat(FlattenNativeSource(source.Roots).Where(node => node.ShapeType is "Receive" or "Send").Select(node => node.Properties.GetValueOrDefault("MessageName", "")))
                .Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
            var messageCatalog = NativeSourceMessageCatalog.Read(messageDeclarations, schemas);
            var declarationCounts = FlattenNativeSource(source.Roots).Where(node => node.ShapeType is "MessageDeclaration" or "VariableDeclaration")
                .GroupBy(node => node.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var contextCatalog = NativeSourceContextCatalog.Read(schemas, messageDeclarations.Where(node => declarationCounts[node.Name] == 1)
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal));
            var parents = FlattenNativeSource(source.Roots).SelectMany(parent => parent.Children.Select(child => (child.Id, Parent: parent)))
                .ToDictionary(pair => pair.Id, pair => pair.Parent);
            var elementDeclarations = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "VariableDeclaration")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Single())
                .Where(node => node.Properties.GetValueOrDefault("Type") == "System.Xml.XmlElement"
                    && parents.GetValueOrDefault(node.Id)?.ShapeType is "ServiceDeclaration" or "Scope"
                    && node.Children.Count == 0 && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                    && node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") == "False"
                    && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In").ToArray();
            IReadOnlySet<string> ElementSymbols(OdxNode statement)
            {
                var ancestors = new HashSet<int>();
                var current = statement;
                while (parents.TryGetValue(current.Id, out var parent)) { ancestors.Add(parent.Id); current = parent; }
                return elementDeclarations.Where(declaration => ancestors.Contains(parents[declaration.Id].Id)).Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
            }
            var scopeScalarDeclarations = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "VariableDeclaration" && parents.GetValueOrDefault(node.Id)?.ShapeType == "Scope").ToArray();
            var scalarTypes = variableDeclarations.Concat(scopeScalarDeclarations).Where(node => declarationCounts[node.Name] == 1 && node.Properties.GetValueOrDefault("Type") is "System.String" or "System.Int32" or "System.Boolean" or "System.DateTime")
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Properties["Type"] switch
                { "System.Int32" => NativeScalarType.Int32, "System.Boolean" => NativeScalarType.Boolean, "System.DateTime" => NativeScalarType.DateTime, _ => NativeScalarType.String }, StringComparer.Ordinal);
            var dynamicPorts = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "PortDeclaration" && NativeExpression.ValidName(node.Name)
                    && node.Children.Any(child => child.ShapeType == "PhysicalBindingAttribute" && child.Properties.GetValueOrDefault("IsDynamic") == "True"))
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Key)
                .Where(port => !scalarTypes.ContainsKey(NativeSourceExpressionReader.DynamicPortVariable(port, "Address")) && !scalarTypes.ContainsKey(NativeSourceExpressionReader.DynamicPortVariable(port, "TransportType")))
                .ToHashSet(StringComparer.Ordinal);
            var directBoundPorts = FlattenNativeSource(source.Roots).Where(node => node.ShapeType == "PortDeclaration" && node.Name.Length > 0
                    && node.Children.Any(child => child.ShapeType == "DirectBindingAttribute" && child.Properties.GetValueOrDefault("DirectBindingType") == "MessageBox"))
                .GroupBy(node => node.Name, StringComparer.Ordinal).Where(group => group.Count() == 1).Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            var hiddenDeclarations = new List<NativeFlowNode>();
            void DeclareHidden(string name)
            {
                if (hiddenDeclarations.Any(declaration => declaration.Variable == name)) return;
                hiddenDeclarations.Add(new() { Id = "generated-" + name, Name = name + " (generated)", Kind = NativeFlowNodeKind.DeclareVariable, Variable = name,
                    VariableType = NativeScalarType.String, Expression = new() { Literal = NativeScalar.From("") } });
            }
            foreach (var port in dynamicPorts.Order(StringComparer.Ordinal))
                foreach (var property in new[] { "Address", "TransportType" })
                {
                    var variable = NativeSourceExpressionReader.DynamicPortVariable(port, property);
                    scalarTypes[variable] = NativeScalarType.String; DeclareHidden(variable);
                }
            var transformTypes = variableDeclarations.Where(node => declarationCounts[node.Name] == 1 && NativeExpression.ValidName(node.Name)
                && node.Properties.GetValueOrDefault("Type") == "System.Type" && node.Children.Count == 0
                && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                && node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") == "False"
                && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In").Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var transformMessages = messageDeclarations.Concat(variableDeclarations.Where(node => documents.Contains(node.Name))).Where(node => declarationCounts[node.Name] == 1 && NativeExpression.ValidName(node.Name))
                .Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            var transformCatalog = NativeSourceMessageCatalog.Read(messageDeclarations.Where(node => transformMessages.Contains(node.Name)), schemas);
            var multipartTypes = FlattenNativeSource(source.Roots).Where(shape => shape.ShapeType == "MultipartMessageType" && IsHl7Multipart(shape))
                .Select(shape => (parents.GetValueOrDefault(shape.Id)?.Name is { Length: > 0 } module ? module + "." : "") + shape.Name).ToHashSet(StringComparer.Ordinal);
            var multipartNames = messageDeclarations.Where(node => declarationCounts[node.Name] == 1 && NativeExpression.ValidName(node.Name)
                && multipartTypes.Contains(node.Properties.GetValueOrDefault("Type", ""))).Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
            NativeSourceExpressionReader Assignments(OdxNode node, bool transformsOnly = false) => new(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes,
                transformsOnly ? transformMessages : messages, transformsOnly ? transformCatalog : messageCatalog, documents, ElementSymbols(node), contextCatalog, configuration,
                transformTypes, mapChoices, KnownUdfs, dynamicPorts, multipartNames);
            var erasableTransformTypes = new HashSet<string>(StringComparer.Ordinal);
            if (mapChoices is not null && Mulse.Modules.Mapping.RenderDispatchDefinition.Read(mapChoices, allowEmpty: true).Count > 0)
            {
                foreach (var symbol in transformTypes)
                {
                    var token = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z_0-9])" + System.Text.RegularExpressions.Regex.Escape(symbol) + @"(?![A-Za-z_0-9])",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    var uses = FlattenNativeSource(source.Roots).Where(node => !(node.ShapeType == "VariableDeclaration" && node.Name == symbol))
                        .SelectMany(node => node.Properties.Where(property => token.IsMatch(property.Value)).Select(property => (Node: node, Property: property))).ToArray();
                    try
                    {
                        if (uses.Length == 0 || uses.Any(use => use.Property.Key != "Expression" || use.Node.ShapeType is not ("Expression" or "MessageAssignment" or "VariableAssignment")
                            || use.Node.Children.Count != 0)) continue;
                        foreach (var use in uses) _ = Assignments(use.Node, transformsOnly: true).ReadAssignments("proof");
                        erasableTransformTypes.Add(symbol);
                    }
                    catch (NotSupportedException) { /* The declaration remains an explicit blocker when any use is unproven. */ }
                }
            }
            void Warn(string text) { if (!warnings.Contains(text)) warnings.Add(text); }
            var activeExceptionNames = new HashSet<string>(StringComparer.Ordinal);
            var dateTimeNames = FlattenNativeSource(source.Roots).Where(declaration => declaration.ShapeType == "VariableDeclaration"
                && declaration.Properties.GetValueOrDefault("Type") is "System.DateTime" or "System.TimeSpan").Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
            NativeFlowNode Block(OdxNode node, string reason, List<NativeFlowNode>? children = null)
            {
                // Without a date-time model the reader reports the first unknown member (e.g. 'dtEnd' or 'System.DateTime.Compare') as a missing schema field; name the real cause instead.
                if (reason.Contains("no exact distinguished schema-field declaration", StringComparison.Ordinal))
                {
                    var text = string.Join(" ", node.Properties.Where(property => property.Key is "Expression" or "Timeout").Select(property => property.Value));
                    if (text.Contains("System.DateTime", StringComparison.Ordinal) || text.Contains("System.TimeSpan", StringComparison.Ordinal)
                        || dateTimeNames.Any(name => System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z_0-9.])" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?![A-Za-z_0-9])", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
                        reason = "Statement uses date and time values (System.DateTime/System.TimeSpan)." + UnsupportedVariableHint("System.DateTime");
                }
                diagnostics.Add(new("BT-NATIVE-UNRESOLVED", MigrationDiagnosticSeverity.Blocking, paths[0],
                    $"{node.ShapeType} '{node.Name}' at line {node.SourceLine}: {reason}", "Implement/review the explicit native blocker; no source statement is executed or silently omitted."));
                return new() { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name,
                    Kind = NativeFlowNodeKind.Blocked, BlockReason = reason, Children = children ?? [] };
            }
            NativeFlowNode DynamicSend(OdxNode node, NativeFlowNode translated, string port, string message, SendPortAnalysis send)
            {
                if (send.IsTwoWay) return Block(node, $"Dynamic send port '{send.Name}' is two-way; native request/response over a runtime-selected transport is not inferred.");
                var pipelineNeedsStep = !TransmitPipelineNeedsNoStep(send, Warn);
                var pipeline = pipelineNeedsStep ? BuildTransmitPipelineChain(send, translated.Id, message, Warn, flatFile) : null;
                var chainBlockers = pipeline?.Blockers.ToList() ?? [];
                if (pipeline is not null) message = pipeline.FinalMessage;
                NativeExpression Variable(string name) => new() { Kind = NativeExpressionKind.Variable, Variable = name };
                var addressVariable = NativeSourceExpressionReader.DynamicPortVariable(port, "Address");
                var transportVariable = NativeSourceExpressionReader.DynamicPortVariable(port, "TransportType");
                NativeFlowNode Part(string suffix, string format, string part, string? defaultPort = null)
                {
                    var target = NativeSourceExpressionReader.DynamicPortVariable(port, format == "Mail" ? "MailAddress" : part);
                    DeclareHidden(target);
                    var partSettings = new Dictionary<string, string> { ["format"] = format, ["part"] = part };
                    if (defaultPort is not null) partSettings["defaultPort"] = defaultPort;
                    return new() { Id = translated.Id + suffix, Name = "Address " + part, Kind = NativeFlowNodeKind.InvokeTransform, Module = "address-part-transform",
                        Variable = target, TransformInputs = [Variable(addressVariable)], Settings = partSettings };
                }
                NativeFlowNode Deliver(string suffix, string module, Dictionary<string, string> staticSettings, Dictionary<string, NativeExpression> computed)
                    => new() { Id = translated.Id + suffix, Name = "Deliver (" + module + ")", Kind = NativeFlowNodeKind.Deliver, Module = module, MessageName = message,
                        Settings = new(staticSettings, StringComparer.OrdinalIgnoreCase), SettingExpressions = computed };
                string Requirement(string suffix, string key, string kind, string description)
                    => AddRequirement(requirements, candidate.SuggestedFlowId, $"deliveries[{translated.Id + suffix}].delivers[0].deliver.settings.{key}", kind, description, appliedToDraft: true);
                var allowed = send.DynamicTransportTypes;
                var branches = new List<(string Transport, List<NativeFlowNode> Nodes)>();
                // A MIME wrapper is only expressed for email delivery; every other transport then ends the flow explicitly instead of sending an unwrapped message.
                var mimeSmtpOnly = pipeline is { MimeAttachment: true } && chainBlockers.Count == 0;
                bool Allowed(string transport) => (!mimeSmtpOnly || transport == "SMTP") && (allowed.Count == 0 || allowed.Contains(transport, StringComparer.OrdinalIgnoreCase));
                if (Allowed("FILE"))
                    branches.Add(("FILE", [Part("-file-dir", "Path", "Directory"), Part("-file-name", "Path", "FileName"),
                        Deliver("-file", "file-system-deliver", new() { ["namingMode"] = "Fixed", ["overwriteExisting"] = "false" },
                            new() { ["path"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Directory")), ["fileName"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "FileName")) })]));
                if (Allowed("FTP"))
                    branches.Add(("FTP", [Part("-ftp-host", "Url", "Host"), Part("-ftp-port", "Url", "Port", "21"), Part("-ftp-dir", "Url", "Directory"), Part("-ftp-name", "Url", "FileName"),
                        Deliver("-ftp", "ftp-deliver", new()
                        {
                            ["username"] = Requirement("-ftp", "username", "Secret", $"FTP user name for dynamic send port '{send.Name}'."),
                            ["password"] = Requirement("-ftp", "password", "Secret", $"FTP password for dynamic send port '{send.Name}' (BizTalk never exports passwords)."),
                            ["encryptionMode"] = "Auto", ["passive"] = "true", ["acceptAnyCertificate"] = "false", ["overwrite"] = "false", ["append"] = "false"
                        }, new()
                        {
                            ["host"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Host")), ["port"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Port")),
                            ["remotePath"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "Directory")), ["remoteFileName"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "FileName"))
                        })]));
                if (Allowed("HTTP"))
                    branches.Add(("HTTP", [Deliver("-http", "http-deliver", new() { ["method"] = "POST" }, new() { ["url"] = Variable(addressVariable) })]));
                if (Allowed("SMTP"))
                    branches.Add(("SMTP", [Part("-smtp-to", "Mail", "MailAddress"),
                        Deliver("-smtp", "smtp-email-deliver", new()
                        {
                            ["host"] = Requirement("-smtp", "host", "Config", $"SMTP host for dynamic send port '{send.Name}'."),
                            ["port"] = "25",
                            ["from"] = Requirement("-smtp", "from", "Config", $"SMTP sender address for dynamic send port '{send.Name}'."),
                            ["subject"] = $"Imported BizTalk delivery {send.Name}", ["attachPayload"] = "true",
                            ["attachmentNameMetadataKey"] = "{" + NativeSourceContextCatalog.PlatformKeyNamespace + "MIME}FileName"
                        }, new() { ["to"] = Variable(NativeSourceExpressionReader.DynamicPortVariable(port, "MailAddress")) })]));
                if (branches.Count == 0) return Block(node, mimeSmtpOnly ? $"Dynamic send port '{send.Name}' uses a MIME/S-MIME encoder, which is only translated for SMTP delivery, but the port allows no SMTP handler." : $"Dynamic send port '{send.Name}' allows no transport with a native adapter ({string.Join(", ", allowed)}).");
                var supported = string.Join(", ", branches.Select(branch => branch.Transport));
                List<NativeFlowNode> chain = [new() { Id = translated.Id + "-unsupported", Name = "No native adapter", Kind = NativeFlowNodeKind.Terminate,
                    TerminateMessage = $"Dynamic send port '{port}' has no native adapter for the assigned transport type; native adapters exist for: {supported}." + (mimeSmtpOnly ? " This port's pipeline wraps the message in MIME, which is only translated for SMTP." : "") }];
                foreach (var (transport, nodes) in Enumerable.Reverse(branches))
                    chain = [new() { Id = translated.Id + "-when-" + transport.ToLowerInvariant(), Name = transport, Kind = NativeFlowNodeKind.If,
                        Expression = new() { Kind = NativeExpressionKind.Equal, Operands = [Variable(transportVariable), new() { Literal = NativeScalar.From(transport) }] },
                        Children = nodes, Otherwise = chain }];
                translated.Kind = NativeFlowNodeKind.Sequence; translated.Children = chain;
                Warn($"Dynamic send port '{port}' is translated to an explicit native transport dispatch on the assigned TransportType (exact, case-sensitive FILE/FTP/HTTP/SMTP, limited to the binding's dynamic handlers). Address parsing uses the 'address-part-transform' UDF (Compatibility Pack): FILE splits the last separator, FTP expects ftp://host[:port]/dir/file (default port 21), SMTP strips mailto:. Adapter properties assigned on the outbound message (for example FTP.*, SMTP.* or nsoftware.* values such as TLS mode, subject, sender or host) are NOT translated and stay visible as blockers; the deliver nodes carry reviewed static defaults (FTP encryptionMode=Auto, passive, no overwrite; FILE overwriteExisting=false; SMTP host/from/subject as configuration). A FTP computed remote file name is still expanded as a template by ftp-deliver. Any other or unassigned transport type terminates the flow explicitly.");
                string[] unsupportedAllowed = mimeSmtpOnly ? [] : allowed.Where(transport => !branches.Any(branch => branch.Transport.Equals(transport, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (unsupportedAllowed.Length > 0)
                    Warn($"Dynamic send port '{send.Name}' also allows transports with no native adapter ({string.Join(", ", unsupportedAllowed)}); those values terminate the flow.");
                if (mimeSmtpOnly)
                {
                    if (ApplyMimeAttachment(pipeline!, branches[0].Nodes[^1], Warn) is { } mimeReason) chainBlockers.Add(mimeReason);
                    Warn($"Dynamic send port '{send.Name}' wraps the message in MIME (its pipeline's MIME/S-MIME encoder); only the SMTP branch is translated, and any other assigned transport type ends the flow explicitly rather than sending an unwrapped message. Review which transports BizTalk could really select for this port.");
                }
                if (pipelineNeedsStep)
                {
                    var sendId = translated.Id;
                    translated.Id += "-delivery";
                    if (pipeline is not null && chainBlockers.Count == 0)
                        return new() { Id = sendId, Name = translated.Name, Kind = NativeFlowNodeKind.Sequence, Children = [.. pipeline.Nodes, translated] };
                    var reason = pipeline is null
                        ? $"Send port '{send.Name}' uses transmit pipeline '{send.TransmitPipeline}' whose components are not known (no per-port configuration in the binding and no pipeline file in the import). Add the pipeline project to the import, or configure named-message augment/render nodes on a separate output message and point the dynamic deliveries at that output. No serializer, component equivalence or pass-through is inferred." + PipelineBlockDetail(send)
                        : $"Send port '{send.Name}' uses transmit pipeline '{send.TransmitPipeline}', which has no proven native equivalent for: {string.Join("; ", chainBlockers)}. The translated components are shown as children; configure the remaining step explicitly. No serializer or pass-through is inferred." + PipelineBlockDetail(send);
                    return Block(node, reason, [.. pipeline?.Nodes ?? [], translated]);
                }
                return translated;
            }
            // A correlation set that is only ever initialized by Send shapes of this service and never followed has no native effect; BizTalk's property-promotion requirement is not enforced.
            var sourceShapes = FlattenNativeSource(source.Roots).Where(shape => shape.SourceId.Length > 0).GroupBy(shape => shape.SourceId, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var correlationTypes = FlattenNativeSource(source.Roots).Where(shape => shape.ShapeType == "CorrelationType" && shape.Name.Length > 0).ToArray();
            var inertCorrelations = new Dictionary<int, string>();
            var inertCorrelationRefs = new HashSet<int>();
            // A set initialized by the activating Receive and followed by later Receives maps to native Receive correlation keys, which pin the keys' metadata values from the initial message.
            var followCandidates = new List<(OdxNode Declaration, (string[] Keys, OdxNode Initializer, List<OdxNode> Followers) Follow)>();
            var correlationFailures = new Dictionary<int, string>();
            var followedCorrelations = new Dictionary<int, string>();
            var followedCorrelationRefs = new HashSet<int>();
            var siblingCorrelationTypes = new Lazy<IReadOnlyList<(string Name, OdxNode Type)>>(() => ReadSiblingCorrelationTypes(paths[0]));
            var followerKeys = new Dictionary<string, string[]>(StringComparer.Ordinal);
            (string[] Keys, OdxNode Initializer, List<OdxNode> Followers) ReadFollowedCorrelation(OdxNode declaration)
            {
                var typeName = declaration.Properties.GetValueOrDefault("Type", "");
                var matchingTypes = correlationTypes.Where(type => typeName == type.Name || typeName.EndsWith("." + type.Name, StringComparison.Ordinal)).ToArray();
                if (matchingTypes.Length == 0) matchingTypes = siblingCorrelationTypes.Value.Where(type => type.Name == typeName).Select(type => type.Type).ToArray();
                if (declaration.Name.Length == 0 || declaration.Properties.GetValueOrDefault("ParamDirection", "In") != "In" || matchingTypes.Length != 1)
                    throw new NotSupportedException("Correlation set is a service parameter or its type is not declared exactly once in the supplied source; it needs explicit native channel and correlation-key bindings.");
                OdxNode? initializer = null;
                var followers = new List<OdxNode>();
                foreach (var reference in declaration.Children)
                {
                    if (reference.ShapeType != "StatementRef" || reference.Children.Count != 0
                        || !sourceShapes.TryGetValue(reference.Properties.GetValueOrDefault("Ref", ""), out var target) || target.ShapeType != "Receive")
                        throw new NotSupportedException("Correlation set is initialized by a non-Receive statement or has statements that could not be proven; only Receive-initialized, Receive-followed sets are translated.");
                    var initializes = reference.Properties.GetValueOrDefault("Initializes");
                    if (initializes == "True")
                    {
                        if (initializer is not null) throw new NotSupportedException("Correlation set is initialized by more than one Receive; it needs explicit native correlation-key bindings.");
                        initializer = target;
                    }
                    else if (initializes == "False" && !followers.Contains(target)) followers.Add(target);
                    else throw new NotSupportedException("Correlation set statement initialization or duplication could not be proven.");
                }
                if (initializer is null || followers.Count == 0 || initializer.Properties.GetValueOrDefault("Activate") != "True" || parents.GetValueOrDefault(initializer.Id)?.ShapeType != "ServiceBody"
                    || followers.Any(follower => follower.Properties.GetValueOrDefault("Activate") == "True"))
                    throw new NotSupportedException("A followed correlation set must be initialized by the service's activating Receive and followed only by non-activating Receives; other shapes need explicit native correlation-key bindings.");
                var initialMessage = initializer.Properties.GetValueOrDefault("MessageName", "");
                var keys = new List<string>();
                foreach (var property in matchingTypes[0].Children)
                {
                    if (property.ShapeType != "PropertyRef" || property.Children.Count != 0)
                        throw new NotSupportedException("Correlation type has an unsupported member; only property references are translated.");
                    var key = contextCatalog.Resolve(initialMessage, property.Properties.GetValueOrDefault("Ref", "")).MetadataKey;
                    if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
                }
                if (keys.Count == 0 || keys.Count > 128) throw new NotSupportedException("Correlation type has no property references or too many to pin natively.");
                return ([.. keys], initializer, followers);
            }
            foreach (var declaration in FlattenNativeSource(source.Roots).Where(shape => shape.ShapeType == "CorrelationDeclaration"))
            {
                var typeName = declaration.Properties.GetValueOrDefault("Type", "");
                var senders = new List<string>();
                var inert = declaration.Name.Length > 0 && declaration.Properties.GetValueOrDefault("ParamDirection", "In") == "In"
                    && correlationTypes.Count(type => typeName == type.Name || typeName.EndsWith("." + type.Name, StringComparison.Ordinal)) == 1;
                foreach (var reference in declaration.Children)
                {
                    if (inert && reference.ShapeType == "StatementRef" && reference.Children.Count == 0 && reference.Properties.GetValueOrDefault("Initializes") == "True"
                        && sourceShapes.TryGetValue(reference.Properties.GetValueOrDefault("Ref", ""), out var target) && target.ShapeType == "Send") senders.Add(target.Name);
                    else inert = false;
                }
                if (!inert)
                {
                    try { followCandidates.Add((declaration, ReadFollowedCorrelation(declaration))); }
                    catch (NotSupportedException exception) { correlationFailures[declaration.Id] = exception.Message; }
                    continue;
                }
                inertCorrelations[declaration.Id] = senders.Count == 0 ? "declared, unused" : "initialized by Send " + string.Join(", ", senders.Distinct(StringComparer.Ordinal).Select(name => "'" + name + "'")) + ", never followed";
                foreach (var reference in declaration.Children) inertCorrelationRefs.Add(reference.Id);
            }
            var followerUse = followCandidates.SelectMany(item => item.Follow.Followers.Select(follower => follower.SourceId)).GroupBy(id => id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var (declaration, follow) in followCandidates)
            {
                if (follow.Followers.Any(follower => followerUse[follower.SourceId] > 1))
                {
                    correlationFailures[declaration.Id] = "A following Receive belongs to more than one correlation set; combine them explicitly in native Receive correlation keys.";
                    continue;
                }
                followedCorrelations[declaration.Id] = "initialized by Receive '" + follow.Initializer.Name + "', followed by Receive " + string.Join(", ", follow.Followers.Select(follower => "'" + follower.Name + "'"))
                    + "; native correlation keys: " + string.Join(", ", follow.Keys);
                foreach (var reference in declaration.Children) followedCorrelationRefs.Add(reference.Id);
                foreach (var follower in follow.Followers) followerKeys[follower.SourceId] = follow.Keys;
            }
            NativeFlowNode Convert(OdxNode node)
            {
                if (node.ShapeType == "Decision")
                {
                    try
                    {
                        if (node.Children.Count == 0 || node.Children.Any(branch => branch.ShapeType != "DecisionBranch"))
                            throw new NotSupportedException("Decision requires ordered source branches.");
                        var predicates = node.Children.Select((branch, index) => string.IsNullOrWhiteSpace(branch.Properties.GetValueOrDefault("Expression"))
                            ? index == node.Children.Count - 1 ? null : throw new NotSupportedException("Only the final decision branch may be an else branch.")
                            : new NativeSourceExpressionReader(branch.Properties["Expression"], scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition()).ToArray();
                        List<NativeFlowNode> remaining = [];
                        for (var index = node.Children.Count - 1; index >= 0; index--)
                        {
                            var branch = node.Children[index];
                            var native = new NativeFlowNode { Id = "source-" + branch.Id, Name = branch.Name, Children = branch.Children.Select(Convert).ToList() };
                            if (predicates[index] is { } predicate) { native.Kind = NativeFlowNodeKind.If; native.Expression = predicate; native.Otherwise = remaining; }
                            remaining = [native];
                        }
                        return new() { Id = "source-" + node.Id, Name = node.Name, Children = remaining };
                    }
                    catch (NotSupportedException exception) { return Block(node, exception.Message, node.Children.Select(Convert).ToList()); }
                }
                var selectedVersion = policyTargets?.FirstOrDefault(pair => pair.Key == node.Properties.GetValueOrDefault("PolicyName")).Value;
                if (node.ShapeType == "CallRules" && selectedVersion is not null)
                {
                    try
                    {
                        var policyName = node.Properties.GetValueOrDefault("PolicyName", "");
                        var versions = policies.Where(policy => policy.Name == policyName && $"{policy.MajorVersion}.{policy.MinorVersion}" == selectedVersion).ToArray();
                        if (versions.Length != 1 || versions[0].ProposedPolicyJson is not { } proposal)
                            throw new NotSupportedException("Selected policy target has no unique complete native proposal in the supplied exports.");
                        if (node.Properties.GetValueOrDefault("PolicyVersion", "-1") is { } sourceVersion && sourceVersion != "-1" && sourceVersion != selectedVersion)
                            throw new NotSupportedException("Explicit native policy target conflicts with the source call's pinned version.");
                        if (node.Children.Count != 1 || node.Children[0].ShapeType != "RulesParameterRef" || node.Children[0].Children.Count != 0)
                            throw new NotSupportedException("Native policy call requires exactly one explicit XML message parameter with no nested behavior.");
                        var parameter = node.Children[0];
                        var message = parameter.Properties.GetValueOrDefault("Reference", "");
                        var declaration = messageDeclarations.Where(item => item.Name == message).ToArray();
                        if (declaration.Length != 1 || declarationCounts.GetValueOrDefault(message) != 1
                            || versions[0].FactTypes.Count != 1 || declaration[0].Properties.GetValueOrDefault("Type") != versions[0].FactTypes[0]
                            || parameter.Properties.GetValueOrDefault("Alias") != versions[0].FactTypes[0])
                            throw new NotSupportedException("Policy fact alias requires the exact unshadowed service-message declaration and exported XML fact type.");
                        var policy = XmlRuleRuntime.ReadPolicy(proposal);
                        if (BizTalkRulePolicyAnalyzer.CountPotentialConflicts(policy, default) != 0)
                            throw new NotSupportedException("Selected policy has potentially conflicting matches; native import does not guess BRE agenda/priority conflict resolution.");
                        messageCatalog.ValidatePolicyMessage(message, policy);
                        warnings.Add($"Policy '{policyName}' {selectedVersion}: BizTalk leaves the fact unchanged when no rule matches and carries on. The native node is set to stop instead, so an unknown value is never processed silently; clear 'Stop when no rule matches' on the node to continue like BizTalk.");
                        warnings.Add($"Policy '{policyName}' {selectedVersion} is an explicitly selected native test target, not proof of the deployed version. Only proven conflict-free single-pass XML fact behavior is compiled; full flow activation remains disabled.");
                        return new() { Id = "source-" + node.Id, Name = node.Name, Children = [
                            new() { Id = "source-" + parameter.Id, Name = parameter.Name },
                            new() { Id = "source-" + node.Id + "-rules", Name = policyName + " " + selectedVersion + " (native test target)", Kind = NativeFlowNodeKind.ApplyXmlRules, MessageName = message, PolicyJson = proposal, StopWhenNoRuleMatches = true }
                        ] };
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                    { return Block(node, exception.Message, node.Children.Select(Convert).ToList()); }
                }
                if (node.ShapeType == "Scope")
                {
                    var catches = node.Children.Where(child => child.ShapeType == "Catch").ToArray();
                    var body = node.Children.Where(child => child.ShapeType != "Catch").Select(Convert).ToList();
                    Warn("Source scopes are translated as plain native sequences (or a native Try when the scope has a catch). Scope/transaction timeout, retry, batching, isolation, atomic rollback, synchronization and compensation are not enforced; review each scope's intended failure behavior.");
                    if (catches.Length == 0) return new() { Id = "source-" + node.Id, Name = node.Name, Children = body };
                    var handlerNode = catches[0];
                    var exceptionName = handlerNode.Properties.GetValueOrDefault("ExceptionName", "");
                    var messageVariable = exceptionName + "_Message";
                    var detailsVariable = exceptionName + "_StackTrace";
                    var unsupported = catches.Length != 1 ? "Scope has several catch handlers; native import does not guess handler order or exception-type matching."
                        : handlerNode.Properties.GetValueOrDefault("ExceptionType") != "System.Exception" || handlerNode.Properties.GetValueOrDefault("IsFaultMessage", "False") != "False"
                            ? "Only an untyped System.Exception catch is translated; typed, fault-message and general-exception ordering semantics require an explicit native design."
                        : !NativeExpression.ValidName(exceptionName) || !NativeExpression.ValidName(detailsVariable)
                            ? "Catch exception name is not a valid native variable prefix."
                        : new[] { exceptionName, messageVariable, detailsVariable }.Any(name => scalarTypes.ContainsKey(name) || messages.Contains(name))
                            ? "Catch exception name collides with a declared variable or message."
                        : !activeExceptionNames.Add(exceptionName) ? "Nested catch handlers reuse the active exception name; rename one explicitly." : null;
                    if (unsupported is not null)
                        return Block(node, unsupported, body.Concat(node.Children.Where(child => child.ShapeType == "Catch").Select(Convert)).ToList());
                    List<NativeFlowNode> handler;
                    string[] aliases = [exceptionName + ".Message", messageVariable, exceptionName + ".StackTrace", detailsVariable];
                    foreach (var alias in aliases) scalarTypes[alias] = NativeScalarType.String;
                    try { handler = handlerNode.Children.Select(Convert).ToList(); }
                    finally { foreach (var alias in aliases) scalarTypes.Remove(alias); activeExceptionNames.Remove(exceptionName); }
                    Warn("Native Try handlers run only for faults raised by the guarded body. Terminate, failed Deliver/Augment/Render effects (left ambiguous for reconciliation), persistence faults, and faults crossing a ForEach, Receive or Wait are not caught.");
                    if (handlerNode.Children.Any(child => child.Properties.Values.Any(value => value.Contains(exceptionName + ".StackTrace", StringComparison.Ordinal))))
                        Warn("Source exception StackTrace reads are mapped to the native exception type name and message, not a CLR stack trace. Any output that embeds it (for example a result description) will differ from the legacy text.");
                    return new() { Id = "source-" + node.Id, Name = node.Name, Kind = NativeFlowNodeKind.Try, Children = body,
                        Otherwise = [new() { Id = "source-" + handlerNode.Id, Name = handlerNode.Name, Children = handler }],
                        ExceptionMessageVariable = messageVariable, ExceptionDetailsVariable = detailsVariable };
                }
                var children = node.Children.Where(child => !(node.ShapeType == "Receive" && child.ShapeType == "DNFPredicate")).Select(Convert).ToList();
                var translated = new NativeFlowNode { Id = "source-" + node.Id, Name = node.Name.Length == 0 ? node.ShapeType : node.Name, Children = children };
                switch (node.ShapeType)
                {
                    case "ServiceDeclaration": case "ServiceBody": case "Construct":
                        return translated;
                    case "Module": case "PortType": case "OperationDeclaration": case "PortDeclaration":
                    case "PhysicalBindingAttribute": case "LogicalBindingAttribute": case "PropertyRef":
                    case "CorrelationType": case "PartDeclaration": case "MultipartMessageType":
                    case "DirectBindingAttribute" when node.Properties.GetValueOrDefault("DirectBindingType") == "MessageBox":
                        if (node.ShapeType == "DirectBindingAttribute")
                            Warn("Ports bound directly to the MessageBox (publish/subscribe) are kept as named declarations. A Receive on such a port keeps its channel and translated filter as native conditions, but nothing here publishes or subscribes: the host must deliver messages with the port name as 'channel' metadata and the filter properties as metadata. Outbound direct-bound Sends become flow-link deliveries whose channel you choose.");
                        // Contract/wiring declarations carry no execution semantics; wiring is resolved from bindings (Send) or channel names (Receive).
                        translated.Name = node.ShapeType + ": " + translated.Name;
                        return translated;
                    case "Terminate":
                        if (children.Count != 0) return Block(node, "Terminate contains unsupported nested behavior.", children);
                        var rawTermination = node.Properties.GetValueOrDefault("ErrorMessage", "").Trim();
                        try
                        {
                            var expression = rawTermination.EndsWith(';') ? rawTermination[..^1].TrimEnd() : rawTermination;
                            var value = new NativeSourceExpressionReader(expression, scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            if (value.Kind != NativeExpressionKind.Literal || value.Literal is not { Type: NativeScalarType.String } literal
                                || string.IsNullOrWhiteSpace(literal.Value) || literal.Value.Length > 2000)
                                throw new NotSupportedException("Terminate requires a non-empty literal string of at most 2000 characters; dynamic or missing messages require explicit native configuration.");
                            translated.Kind = NativeFlowNodeKind.Terminate;
                            translated.TerminateMessage = literal.Value;
                            return translated;
                        }
                        catch (Exception exception) when (exception is NotSupportedException or JsonException)
                        {
                            var evidence = rawTermination.Length > 1024 ? rawTermination[..1024] + " [truncated]" : rawTermination;
                            return Block(node, "Terminate message could not be preserved: " + exception.Message + " Source expression: " + evidence, children);
                        }
                    case "Delay":
                        {
                            var rawTimeout = node.Properties.GetValueOrDefault("Timeout", "").Trim();
                            var timeout = rawTimeout.EndsWith(';') ? rawTimeout[..^1].TrimEnd() : rawTimeout;
                            var totalMilliseconds = ReadConstantTimeoutMilliseconds(timeout);
                            if (totalMilliseconds is null && children.Count == 0)
                            {
                                try
                                {
                                    var computed = new NativeSourceExpressionReader(timeout, scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadSubtractDurationSeconds();
                                    translated.Kind = NativeFlowNodeKind.Wait; translated.DurationExpression = computed;
                                    Warn(DateTimeWarning);
                                    Warn("A Delay computed from dateTime.Subtract(dateTime) is imported as a Wait whose duration is evaluated once, in whole seconds truncated toward zero; a negative duration waits 0 seconds. Sub-second parts of the source TimeSpan are dropped.");
                                    return translated;
                                }
                                catch (NotSupportedException) { /* Not the dynamic form; the constant-TimeSpan blocker below states the limit. */ }
                            }
                            if (children.Count != 0 || totalMilliseconds is null or <= 0 or > 31622400000L)
                            {
                                var evidence = rawTimeout.Length > 1024 ? rawTimeout[..1024] + " [truncated]" : rawTimeout;
                                return Block(node, "Source delay timeout is not a supported positive whole-millisecond TimeSpan within the native wait limit, or contains nested behavior; configure it explicitly (source expression: " + evidence + ").", children);
                            }
                            translated.Kind = NativeFlowNodeKind.Wait;
                            translated.DurationSeconds = (int)(totalMilliseconds.Value / 1000); translated.DurationMilliseconds = (int)(totalMilliseconds.Value % 1000);
                            if (translated.DurationSeconds == 0)
                                Warn("A Delay shorter than one second is imported as an in-place sub-second pause (a throttle). A Delay of one second or more parks the instance until the flow's next run, so it is a minimum, not an exact delay.");
                            return translated;
                        }
                    case "MessagePartRef" when parents.GetValueOrDefault(node.Id)?.ShapeType == "Transform":
                        translated.Name = "Map message: " + node.Properties.GetValueOrDefault("MessageRef", node.Name);
                        return translated;
                    case "Transform":
                    {
                        var shape = ReadTransformShape(node, messages, out var shapeProblem);
                        if (shape is null) return Block(node, shapeProblem);
                        if (mapChoices is null || !Mulse.Modules.Mapping.RenderDispatchDefinition.Read(mapChoices, allowEmpty: true).Any(choice => string.Equals(choice.Value, shape.ClassName, StringComparison.Ordinal)))
                            return Block(node, $"Transform shape applies map class '{shape.ClassName}', which has no native renderer choice. Enable 'Derive native map choices' with the map's project in the source paths (see the derived map choices report for why a map could not be converted), or add a choice whose value is exactly this class name. No CLR map is loaded and nothing is guessed.");
                        translated.Kind = NativeFlowNodeKind.Render; translated.Children = [];
                        translated.Module = Mulse.Modules.Mapping.RenderDispatchDefinition.ModuleId;
                        translated.InputMessages = [.. shape.Inputs]; translated.MessageName = shape.Output;
                        translated.SelectionExpression = new() { Literal = NativeScalar.From(shape.ClassName) };
                        Warn("A static Transform shape is imported as a render-dispatch step whose selector is the map class name, using the flow's shared choice table (the converted .btm map). Inputs are read in source order and the output replaces the construct message. The converted map is not a verified equivalent of the deployed BizTalk map: preview it with synthetic inputs against trusted expected output.");
                        return translated;
                    }
                    case "MessageRef" when parents.GetValueOrDefault(node.Id)?.ShapeType is "OperationDeclaration" or "PartDeclaration":
                        translated.Name = "Operation message: " + node.Properties.GetValueOrDefault("Ref", node.Name);
                        return translated;
                    case "MessageRef":
                        if (node.Children.Count == 0 && parents.GetValueOrDefault(node.Id)?.ShapeType == "Construct"
                            && messages.Contains(node.Properties.GetValueOrDefault("Ref", "")))
                        {
                            translated.Name = "Construct message: " + node.Properties["Ref"];
                            return translated;
                        }
                        return Block(node, "Message reference is not an exact declared Construct output; operation/type references require explicit contract translation.", children);
                    case "VariableDeclaration":
                        try
                        {
                            if (elementDeclarations.Any(declaration => declaration.Id == node.Id)) return translated;
                            if (erasableTransformTypes.Contains(node.Name) && variableDeclarations.Any(declaration => declaration.Id == node.Id))
                            {
                                translated.Name = "Native renderer identity binding: " + node.Name;
                                return translated;
                            }
                            if (variableDeclarations.Any(declaration => declaration.Id == node.Id) && documents.Contains(node.Name)
                                && variableDeclarations.Count(declaration => declaration.Name == node.Name) == 1 && node.Children.Count == 0
                                && string.IsNullOrWhiteSpace(node.Properties.GetValueOrDefault("InitialValue"))
                                && node.Properties.GetValueOrDefault("ParamDirection", "In") == "In") return translated;
                            if (!variableDeclarations.Concat(scopeScalarDeclarations).Any(declaration => declaration.Id == node.Id) || !scalarTypes.TryGetValue(node.Name, out var type)
                                || node.Children.Count != 0 || node.Properties.GetValueOrDefault("UseDefaultConstructor", "False") != "False"
                                || node.Properties.GetValueOrDefault("ParamDirection", "In") != "In")
                                throw new NotSupportedException("Only unique service-level or scope-level scalar variables with explicit or language-default initializers are supported; other constructors are not inferred." + UnsupportedVariableHint(node.Properties.GetValueOrDefault("Type")));
                            translated.Kind = NativeFlowNodeKind.DeclareVariable; translated.Variable = node.Name; translated.VariableType = type;
                            if (type == NativeScalarType.DateTime) Warn(DateTimeWarning);
                            var initializer = node.Properties.GetValueOrDefault("InitialValue");
                            translated.Expression = string.IsNullOrWhiteSpace(initializer)
                                ? new() { Literal = type switch { NativeScalarType.Int32 => NativeScalar.From(0), NativeScalarType.Boolean => NativeScalar.From(false),
                                    NativeScalarType.DateTime => NativeScalar.From(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)), _ => NativeScalar.From(null) } }
                                : new NativeSourceExpressionReader(initializer, scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "VariableAssignment":
                    case "Expression":
                    case "MessageAssignment":
                        try
                        {
                            if (children.Count > 0) throw new NotSupportedException("Source statements contain unsupported nested metadata.");
                            var expression = node.Properties.GetValueOrDefault("Expression", "");
                            var hasTransform = expression.Contains("System.Type.GetType", StringComparison.Ordinal) || expression.Contains("transform", StringComparison.Ordinal);
                            translated.Children = Assignments(node, transformsOnly: hasTransform).ReadAssignments(translated.Id).ToList();
                            if (ContainsTransformCall(translated.Children))
                                Warn("Source helper FilenameMaker.MakeFilename is imported as the pure 'filename-template-transform' UDF (Compatibility Pack) with assumed settings timeZone=UTC and culture=Invariant. The BizTalk host's time zone and culture are unknown: review both in the node editor. The transform is not journaled, runs again after a crash before the next checkpoint, and its code version is not pinned.");
                            if (ContainsNode(translated.Children, child => child.Target is { } target && NativeSourceContextCatalog.IsPlatformKey(target.MetadataKey)))
                                Warn("Adapter and HL7 assembler properties written with message-context syntax (BTAHL7Schemas.*, FTP.*, FILE.*, SMTP.*, MIME.FileName, nsoftware.BizTalk.FTP.* and SFTP.*) are stored as importer-defined metadata keys '{" + NativeSourceContextCatalog.PlatformKeyNamespace + "<namespace>}<name>' typed from the assigned value. No native delivery module reads them: BizTalk adapters and the HL7 assembler applied them at send time (host, credentials, remote file/path, TLS mode, subject, delimiters, encoding). Configure the equivalent Deliver and render settings in the editor. Credential values assigned from message fields are copied into message metadata (and checkpoints) like the message itself; literal credentials are blocked.");
                            if (ContainsNode(translated.Children, child => child.Kind == NativeFlowNodeKind.CreateXmlRoot && child.Expression?.Literal?.Value == NativeSourceExpressionReader.MultipartRootName))
                                Warn("HL7vXGeneric multipart messages are imported as one native XML message: an '" + NativeSourceExpressionReader.MultipartRootName + "' root holding the MSHSegment document root followed by the BodySegments document root. The empty ZSegments part is dropped, and part-level context other than MIME.FileName on BodySegments is not translated. The hl7-xml-render module flattens this shape; BTAHL7 assembler behavior is not claimed. A whole-message multipart copy also copies the source message's metadata, whereas BizTalk starts the copy with a new context.");
                            if (ContainsNode(translated.Children, child => child.Id.Contains("-partcontext-", StringComparison.Ordinal)))
                                Warn("MIME.FileName written on the BodySegments part of an HL7 multipart message is stored as message-level metadata '{" + NativeSourceContextCatalog.PlatformKeyNamespace + "MIME}FileName' (assumption: BizTalk's body part and the whole message share that file name when the message is sent). The SMTP deliver's 'attachmentNameMetadataKey' setting names the key it reads; confirm the attachment name of a real BizTalk email. Other part-level context stays blocked.");
                            if (translated.Children.Any(child => child.SelectionExpression is not null))
                                warnings.Add($"Transform '{node.Name}' uses explicitly configured exact native renderer replacements with ordered named arguments; no CLR map is loaded and source parity is not implied. Full flow activation remains disabled.");
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "While": case "Loop":
                        try
                        {
                            translated.Kind = NativeFlowNodeKind.While;
                            translated.Expression = new NativeSourceExpressionReader(node.Properties.GetValueOrDefault("Expression", ""), scalarTypes, messages, messageCatalog, contextCatalog: contextCatalog, configuration: configuration).ReadCondition();
                            warnings.Add($"Source loop '{node.Name}' uses a native safety bound of {translated.MaxIterations} iterations. Review the bound in the visual editor; exceeding it fails explicitly.");
                            return translated;
                        }
                        catch (NotSupportedException exception) { return Block(node, exception.Message, children); }
                    case "MessageDeclaration":
                        if (node.Properties.GetValueOrDefault("InitialValue") is { Length: > 0 }) return Block(node, "Message initialization requires an explicit native payload/fact definition.", children);
                        return translated;
                    case "Receive":
                        if (node.Properties.Any(pair => pair.Key.Contains("Correlation", StringComparison.Ordinal) || pair.Key.Contains("Filter", StringComparison.Ordinal))
                            || children.Count != 0)
                            return Block(node, "Source receive correlation declarations require explicit native channel and promoted-metadata bindings.", children);
                        var message = node.Properties.GetValueOrDefault("MessageName", "");
                        var channel = node.Properties.GetValueOrDefault("PortName", "");
                        if (message.Length == 0 || channel.Length == 0) return Block(node, "Receive has no exact message/port identity.");
                        var filterPredicates = node.Children.Where(child => child.ShapeType == "DNFPredicate").ToArray();
                        if (filterPredicates.Length > 0)
                        {
                            bool readAsGroups;
                            try { translated.Conditions = TranslateReceiveFilter(filterPredicates, message, contextCatalog, out readAsGroups); }
                            catch (NotSupportedException exception) { return Block(node, "Receive filter is not translated: " + exception.Message); }
                            if (readAsGroups)
                                Warn("A Receive filter with mixed AND/OR groupings was read as BizTalk disjunctive normal form: each predicate's Grouping joins it to the next predicate, OR separates groups, AND binds within a group, and the last predicate's Grouping is ignored. Confirm the result against the Receive shape's filter in the BizTalk designer.");
                            Warn("Receive filters become native receive conditions on promoted-metadata keys. Native comparisons are case-insensitive (BizTalk filters are case-sensitive), and Exists requires a non-blank value (BizTalk tests presence). The host must supply the channel and these metadata keys, and unmatched messages are rejected rather than routed elsewhere; review each filter.");
                        }
                        translated.Kind = NativeFlowNodeKind.Receive; translated.MessageName = message; translated.Channel = channel;
                        if (followerKeys.TryGetValue(node.SourceId, out var pinnedKeys))
                        {
                            translated.CorrelationKeys = [.. pinnedKeys];
                            Warn("A Receive that follows a correlation set accepts only messages whose metadata values for the set's promoted-property keys equal the values pinned from the initial (activating) message; mismatches are rejected, not routed elsewhere. BizTalk checks the property promotion at design time; here the host must supply these metadata keys (the XSD property-schema names) on every message, and the promotion itself is not verified.");
                        }
                        return translated;
                    case "Listen":
                        {
                            var heads = children.Select(branch => branch.Kind == NativeFlowNodeKind.Sequence && branch.Children.Count > 0 ? branch.Children[0] : null).ToArray();
                            if (node.Children.Count is < 2 or > 16 || node.Children.Any(branch => branch.ShapeType != "ListenBranch"))
                                return Block(node, "Listen requires 2 to 16 ordered branches, each starting with a Receive or a Delay.", children);
                            if (heads.Any(head => head is null || head.Kind is not (NativeFlowNodeKind.Receive or NativeFlowNodeKind.Wait)) || heads.Count(head => head!.Kind == NativeFlowNodeKind.Wait) > 1 || heads.All(head => head!.Kind == NativeFlowNodeKind.Wait))
                                return Block(node, "Listen branches must each start with a translated Receive or a translated Delay (at most one Delay, which acts as the timeout), with at least one Receive; a branch trigger is blocked or unsupported.", children);
                            translated.Kind = NativeFlowNodeKind.Listen;
                            Warn("A Listen waits for the first matching Receive branch in source order, or runs its Delay branch when that timeout passes; the timeout is fixed when the Listen is entered and survives restarts. A message arriving after the timeout, or one that matches no branch, is rejected rather than queued. BizTalk's simultaneous-arrival tie-breaking is not reproduced.");
                            return translated;
                        }
                    case "ListenBranch":
                        translated.Kind = NativeFlowNodeKind.Sequence;
                        return translated;
                    case "Send":
                        if (children.Count > 0) return Block(node, "Send contains unsupported nested behavior.", children);
                        var outboundMessage = node.Properties.GetValueOrDefault("MessageName", "");
                        if (outboundMessage.Length == 0) return Block(node, "Send has no named message.");
                        if (node.Properties.GetValueOrDefault("OperationMessageName") == "Response")
                        {
                            translated.Kind = NativeFlowNodeKind.Reply; translated.MessageName = outboundMessage; return translated;
                        }
                        var port = node.Properties.GetValueOrDefault("PortName", "");
                        var matches = owners.SelectMany(owner => owner.Service.LogicalPorts.Where(logical => logical.Name == port)
                            .SelectMany(logical => logical.SendPortNames.Select(name => (owner.Binding, Name: name)))).ToArray();
                        if (matches.Length == 0 && directBoundPorts.Contains(port))
                        {
                            // BizTalk publishes to the MessageBox and any subscriber receives it. The native equivalent is a named channel; which flow listens is the user's choice.
                            var linkChannel = AddRequirement(requirements, candidate.SuggestedFlowId, $"deliveries[{translated.Id}].delivers[0].deliver.settings.channel", "Config",
                                $"Flow-link channel for the MessageBox-bound port '{port}'. BizTalk publishes the message and every matching subscription receives it; choose the channel that a flow-link-fetch source in the subscribing flow listens on, or change this node to another deliver module.", appliedToDraft: true);
                            Warn("Sends on ports bound directly to the MessageBox are imported as flow-link deliveries with an unset channel (a placeholder that fails closed until you choose one). BizTalk publish/subscribe delivers to every subscription whose filter matches, and subscribers are found at runtime; a flow-link channel reaches only the flows whose flow-link-fetch source names it, with no filter. Review which BizTalk subscribers exist (receive port filters, send port filters and activating receives) and create one flow-link-fetch source per subscriber flow.");
                            translated.Kind = NativeFlowNodeKind.Deliver; translated.Module = "flow-link-deliver"; translated.MessageName = outboundMessage;
                            translated.Settings = new(StringComparer.OrdinalIgnoreCase) { ["channel"] = linkChannel };
                            return translated;
                        }
                        if (matches.Length != 1) return Block(node, "Outbound logical port has no unique explicit physical binding. Direct subscriptions/dynamic routes are not guessed.");
                        var physical = matches[0].Binding.SendPorts.Where(send => send.Name == matches[0].Name).ToArray();
                        if (physical.Length != 1) return Block(node, "Bound physical send port is missing or ambiguous.");
                        if (physical[0].IsDynamic && dynamicPorts.Contains(port)) return DynamicSend(node, translated, port, outboundMessage, physical[0]);
                        var vendorTransport = NsoftwareTransportPattern.Match(physical[0].TransportType);
                        if (!physical[0].TransportType.Equals("FILE", StringComparison.OrdinalIgnoreCase)
                            && !vendorTransport.Success
                            && !physical[0].TransportType.Equals("HTTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("FTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("SFTP", StringComparison.OrdinalIgnoreCase)
                            && !physical[0].TransportType.Equals("SMTP", StringComparison.OrdinalIgnoreCase))
                            return Block(node, $"Bound transport '{physical[0].TransportType}' requires an explicit native protocol adapter; address/name heuristics are not execution wiring.");
                        var deliverWarnings = new List<string>();
                        var settings = CreateDeliverSettings(physical[0], candidate.SuggestedFlowId, node.Id, requirements, "", includeConfigPlaceholders: true, deliverWarnings);
                        foreach (var deliverWarning in deliverWarnings) Warn(deliverWarning);
                        translated.Kind = NativeFlowNodeKind.Deliver; translated.Module = settings.Module;
                        translated.Settings = new(settings.Settings, StringComparer.OrdinalIgnoreCase); translated.MessageName = outboundMessage;
                        if (physical[0].IsTwoWay) translated.Settings["expectsResponse"] = "true";
                        var pipelineNeedsStep = !TransmitPipelineNeedsNoStep(physical[0], Warn);
                        var chain = pipelineNeedsStep ? BuildTransmitPipelineChain(physical[0], translated.Id, outboundMessage, Warn, flatFile) : null;
                        if (chain is not null) translated.MessageName = chain.FinalMessage;
                        var chainBlockers = chain?.Blockers.ToList() ?? [];
                        if (chain is not null && chainBlockers.Count == 0 && ApplyMimeAttachment(chain, translated, Warn) is { } mimeReason) chainBlockers.Add(mimeReason);
                        if (physical[0].RetryCount > 0)
                        {
                            if (physical[0].RetryCount > NativeDeliverRetry.MaxAttempts || physical[0].RetryIntervalMinutes < 0 || physical[0].RetryIntervalMinutes * 60L > NativeDeliverRetry.MaxIntervalSeconds)
                                return Block(node, $"Send port '{physical[0].Name}' retries {physical[0].RetryCount} time(s) every {physical[0].RetryIntervalMinutes} minute(s), which is outside the native retry limits ({NativeDeliverRetry.MaxAttempts} attempts, {NativeDeliverRetry.MaxIntervalSeconds / 60} minutes apart); configure the retry explicitly.");
                            translated.Retry = new() { Attempts = physical[0].RetryCount, IntervalSeconds = physical[0].RetryIntervalMinutes * 60 };
                            Warn($"Send port '{physical[0].Name}' retries {physical[0].RetryCount} time(s), {physical[0].RetryIntervalMinutes} minute(s) apart. The native Deliver node has an equivalent retry policy, but it repeats a send only when the transport proves nothing was sent (connection refused, name not found, login rejected). BizTalk also retried after a timeout or dropped connection; the native node holds those for reconciliation unless 'also retry ambiguous failures' is turned on (it can send the same data twice). Attempts are not parked exactly: the next attempt runs on the flow's next run after the interval. A BizTalk backup transport, if configured on the port, is not imported.");
                            if (!SupportsNotSentClassification(settings.Module))
                                Warn($"Send port '{physical[0].Name}' uses transport module '{settings.Module}', which cannot prove that a failed send sent nothing, so its retry only runs if 'also retry ambiguous failures' is turned on.");
                        }
                        if (pipelineNeedsStep)
                        {
                            var sendId = translated.Id;
                            translated.Id += "-delivery";
                            if (chain is not null && chainBlockers.Count == 0)
                                return new() { Id = sendId, Name = translated.Name, Kind = NativeFlowNodeKind.Sequence, Children = [.. chain.Nodes, translated] };
                            var reason = chain is null
                                    ? $"Send port '{physical[0].Name}' uses transmit pipeline '{physical[0].TransmitPipeline}' whose components are not known (no per-port configuration in the binding and no pipeline file in the import). Add the pipeline project to the import, or configure named-message augment/render nodes on a separate output message and point the child delivery at that output. No serializer, component equivalence or pass-through is inferred." + PipelineBlockDetail(physical[0])
                                    : chainBlockers.Count > 0
                                        ? $"Send port '{physical[0].Name}' uses transmit pipeline '{physical[0].TransmitPipeline}', which has no proven native equivalent for: {string.Join("; ", chainBlockers)}. The translated components are shown as children; configure the remaining step explicitly. No serializer or pass-through is inferred." + PipelineBlockDetail(physical[0])
                                        : "";
                            return Block(node, reason, [.. chain?.Nodes ?? [], translated]);
                        }
                        return translated;
                    case "CallRules":
                        var name = node.Properties.GetValueOrDefault("PolicyName", "");
                        var versions = policies.Where(policy => policy.Name == name).OrderByDescending(policy => policy.MajorVersion).ThenByDescending(policy => policy.MinorVersion).ToArray();
                        var latest = versions.FirstOrDefault();
                        if (latest?.ProposedPolicyJson is { } proposal)
                            children.Add(new() { Id = translated.Id + "-policy", Name = name + " " + latest.MajorVersion + "." + latest.MinorVersion + " (exported latest; review)",
                                Kind = NativeFlowNodeKind.Augment, Module = "xml-rules-augment", Settings = new() { ["policyJson"] = proposal, ["factMode"] = "Metadata", ["metadataPrefix"] = "rules." } });
                        return Block(node, latest is null ? "Called policy is absent from supplied exports." :
                            "Policy proposal is attached, but CallRules input/output fact bindings and source version resolution require review. Latest exported version is not deployed proof.", children);
                    case "CorrelationDeclaration" when inertCorrelations.TryGetValue(node.Id, out var correlationEvidence):
                        translated.Name = "CorrelationDeclaration (not enforced): " + node.Name + " - " + correlationEvidence;
                        Warn("Correlation sets that are only initialized by a Send and never followed are kept as named evidence; they do not change native behavior. BizTalk's requirement that the sent message carries the set's promoted properties is not enforced, and nothing here subscribes a later Receive. Review any external consumer that relied on these sets.");
                        return translated;
                    case "StatementRef" when inertCorrelationRefs.Contains(node.Id):
                        translated.Name = "StatementRef (not enforced): " + node.Name;
                        return translated;
                    case "CorrelationDeclaration" when followedCorrelations.TryGetValue(node.Id, out var followedEvidence):
                        translated.Name = "CorrelationDeclaration (enforced by Receive correlation keys): " + node.Name + " - " + followedEvidence;
                        return translated;
                    case "StatementRef" when followedCorrelationRefs.Contains(node.Id):
                        translated.Name = "StatementRef (enforced by Receive correlation keys): " + node.Name;
                        return translated;
                    case "CorrelationDeclaration":
                        return Block(node, correlationFailures.GetValueOrDefault(node.Id, "Correlation set is followed by a Receive, is initialized by a non-Send statement, or its type or statements could not be proven; it needs explicit native channel and correlation-key bindings."), children);
                    case "LongRunningTransaction": case "AtomicTransaction": case "TransactionAttribute":
                        if (children.Count != 0) return Block(node, $"{node.ShapeType} contains unsupported nested behavior.", children);
                        var transactionSettings = string.Join("; ", node.Properties.Where(pair => pair.Key is not ("Name" or "Signal" or "ReportToAnalyst")).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
                        translated.Name = node.ShapeType + " (not enforced)" + (transactionSettings.Length == 0 ? "" : ": " + transactionSettings);
                        Warn("Source transaction declarations (atomic/long-running type and transaction attributes such as batch, retry, timeout and isolation) are kept as named evidence only; none is enforced natively. Review whether retry, rollback or timeout behavior must be rebuilt explicitly.");
                        return translated;
                    default:
                        return Block(node, $"Source {node.ShapeType} semantics are not translated by the native importer.", children);
                }
            }
            var definition = new NativeFlowDefinition { Durable = true, Nodes = source.Roots.Select(Convert).ToList() };
            definition.Nodes.InsertRange(0, hiddenDeclarations);
            if (mapChoices is not null && definition.AllNodes().Any(node => node.SelectionExpression is not null))
            {
                definition.RenderChoicesJson = mapChoices;
                warnings.Add("Every dynamic transform in this flow selects from ONE shared choice table (the flow's renderChoicesJson), not its own copy. The table covers every policy map name found for the solution, so a transform can select a map that BizTalk would only ever choose for a different policy field; an unlisted name fails closed. Editing a choice changes every transform in the flow. A map referenced as a config value is shared by every flow that uses it.");
            }
            if (configuration is not null)
                warnings.Add("Explicit host configuration and pure getter source files are native test snapshots, not proof of deployed assembly identity or inherited machine configuration. Referenced getter results are pinned as editable scalar literals; absent keys retain the proven empty-string return. No configuration file or helper assembly is read at execution time.");
            if (JsonSerializer.Serialize(definition, NativeJson).Contains("\"metadataKey\":\"" + NativeSourceContextCatalog.MessageIdKey + "\"", StringComparison.Ordinal))
                warnings.Add("A source read of BTS.MessageID is mapped to the received message's '" + NativeSourceContextCatalog.MessageIdKey + "' metadata (the flow's message-id key, editable under Execution and message settings; if you change that key, update these reads too). BizTalk assigns a new GUID to every message; native flows use the id supplied when the message was received, so its format can differ. A message built inside the flow has no id and the read fails closed.");
            if (definition.AllNodes().Any(node => node.Expression?.RequiresInstance == true || node.NamespaceExpression?.RequiresInstance == true))
                warnings.Add("Source instance identity reads use the stable new native instance GUID, not a legacy BizTalk instance ID. This identity survives waits and restart; review any external identity contract before activation.");
            var json = JsonSerializer.Serialize(definition, NativeJson);
            _ = NativeFlowDefinition.Read(json, allowBlocked: true);
            if (definition.AllNodes().Any(node => node.Expression?.UsesOrdinalSearch == true || node.NamespaceExpression?.UsesOrdinalSearch == true || node.SelectionExpression?.UsesOrdinalSearch == true))
                warnings.Add("Source string IndexOf/LastIndexOf calls default to culture-sensitive comparison in the legacy runtime; native flows search ordinally. Only non-empty printable-ASCII constant search text was imported, and results can differ only for text containing combining or ignorable characters. Review any such inputs.");
            warnings.Add("Native source structure is preserved with explicit blockers; no ODX interpreter or heuristic routing/decision augments execute it. Configure stable message/instance IDs and channel metadata before native activation.");
            warnings.Add("Declared source context properties use exact case-sensitive {namespace}property metadata keys with schema-proven String/Int32/Boolean types. Incoming promotion/context adapters and MessageBox subscriptions are not inferred from body fields; missing keys fail. Review these inlet contracts before activation.");
            return candidate with
            {
                DraftFlow = candidate.DraftFlow with
                {
                    Augments = [new(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = json })],
                    Deliveries = [],
                    ConfigurationRequirements = requirements, Warnings = warnings,
                    Migration = report with { Diagnostics = diagnostics }
                },
                ScaffoldedModules = candidate.ScaffoldedModules.Where(module => !module.ModuleId.StartsWith("odx-translated-", StringComparison.Ordinal))
                    .Concat([new BizTalkScaffoldedModuleResponse(candidate.SuggestedFlowId + "-native.json", candidate.SuggestedFlowId + "-native", "Native control-flow draft", json)]).ToArray()
            };
        }
        catch (Exception exception) when (exception is ArgumentException or System.Xml.XmlException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostics.Add(new("BT-NATIVE-TRANSLATION-FAILED", MigrationDiagnosticSeverity.Blocking, paths[0], "Native draft could not be generated: " + exception.Message,
                "Correct the bounded source/definition; no partial native translation is available."));
            var failed = JsonSerializer.Serialize(new NativeFlowDefinition { Durable = true, Nodes =
                [new() { Id = "translation-failed", Kind = NativeFlowNodeKind.Blocked, BlockReason = "Native source translation failed; inspect migration diagnostics before implementing the flow." }] }, NativeJson);
            return candidate with
            {
                DraftFlow = candidate.DraftFlow with { Augments = [new(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = failed })],
                    Deliveries = [], Migration = report with { Diagnostics = diagnostics } },
                ScaffoldedModules = candidate.ScaffoldedModules.Where(module => !module.ModuleId.StartsWith("odx-translated-", StringComparison.Ordinal)).ToArray()
            };
        }
    }

    // Correlation types may be declared in another orchestration of the same BizTalk project; only the explicitly included XLang items of the one owning .btproj are read.
    private static IReadOnlyList<(string Name, OdxNode Type)> ReadSiblingCorrelationTypes(string orchestrationPath)
    {
        var result = new List<(string, OdxNode)>();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(orchestrationPath))!;
            var owners = new List<List<BizTalkArtifactResponse>>();
            foreach (var projectPath in Directory.EnumerateFiles(directory, "*.btproj", SearchOption.TopDirectoryOnly))
            {
                var artifacts = new List<BizTalkArtifactResponse>();
                AddArtifacts(XDocument.Load(projectPath), directory, directory, "XLang", "Orchestration", artifacts);
                if (artifacts.Any(artifact => string.Equals(artifact.FullPath, Path.GetFullPath(orchestrationPath), StringComparison.OrdinalIgnoreCase))) owners.Add(artifacts);
            }
            if (owners.Count != 1) return result;
            foreach (var artifact in owners[0].Where(artifact => !string.Equals(artifact.FullPath, Path.GetFullPath(orchestrationPath), StringComparison.OrdinalIgnoreCase)).Take(256))
            {
                try
                {
                    foreach (var module in FlattenNativeSource(OdxProgramReader.ReadFile(artifact.FullPath).Roots).Where(shape => shape.ShapeType == "Module" && shape.Name.Length > 0))
                        foreach (var type in module.Children.Where(child => child.ShapeType == "CorrelationType" && child.Name.Length > 0))
                            result.Add((module.Name + "." + type.Name, type));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException or InvalidOperationException) { /* An unreadable sibling cannot prove a type; the set stays blocked. */ }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException) { /* No readable owning project: no sibling evidence. */ }
        return result;
    }

    /// <summary>
    /// True when a send port's transmit pipeline needs no native step: none is set, or it is one of BizTalk's two stock transmit pipelines with no per-port component configuration.
    /// PassThruTransmit does no processing. XMLTransmit is the XML assembler with default settings; its effect on the output text is not reproduced, so it is warned. Any other pipeline stays blocked.
    /// </summary>
    private static bool TransmitPipelineNeedsNoStep(SendPortAnalysis send, Action<string> warn)
    {
        var name = send.TransmitPipeline.Split(',')[0].Trim();
        if (name.Length == 0) return true;
        if (send.Operational.HasSendPipelineConfiguration) return false;
        if (name.Equals("Microsoft.BizTalk.DefaultPipelines.PassThruTransmit", StringComparison.OrdinalIgnoreCase))
        {
            warn("Send ports using the stock PassThruTransmit pipeline (which does no processing) are imported without a pipeline step.");
            return true;
        }
        if (name.Equals("Microsoft.BizTalk.DefaultPipelines.XMLTransmit", StringComparison.OrdinalIgnoreCase))
        {
            warn("Send ports using the stock XMLTransmit pipeline (the XML assembler with default settings, no per-port configuration) are imported without a pipeline step: the message is delivered as the native XML text. The assembler can add an XML declaration or convert the character set; native output is not checked against that, so compare a delivered file with a real BizTalk output (XML declaration, encoding, byte-order mark) before use.");
            return true;
        }
        return false;
    }

    private static string PipelineBlockDetail(SendPortAnalysis send)
        => send.Operational.HasSendPipelineConfiguration ? " The port carries its own pipeline component configuration, so even a stock pipeline is not assumed to be unmodified." : "";

    private static bool SupportsNotSentClassification(string module) =>
        module is "http-deliver" or "ftp-deliver" or "sftp-deliver" or "smtp-email-deliver";

    private static long? ReadConstantTimeoutMilliseconds(string expression)
    {
        if (expression.Length > 4096) return null;
        const System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromMilliseconds(100);
        var ticks = System.Text.RegularExpressions.Regex.Match(expression, @"\A(?:new\s+(?:System\.)?TimeSpan|(?:System\.)?TimeSpan\.FromTicks)\s*\(\s*(?<ticks>[0-9]{1,19})[lL]?\s*\)\z", options, timeout);
        if (ticks.Success)
        {
            if (!long.TryParse(ticks.Groups["ticks"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value % TimeSpan.TicksPerMillisecond != 0) return null;
            return value / TimeSpan.TicksPerMillisecond;
        }
        var ctor = System.Text.RegularExpressions.Regex.Match(expression, @"^new\s+(?:System\.)?TimeSpan\s*\(\s*(?<args>[0-9]{1,9}(?:\s*,\s*[0-9]{1,9}){2,4})\s*\)$", options, timeout);
        long milliseconds;
        if (ctor.Success)
        {
            var args = ctor.Groups["args"].Value.Split(',').Select(value => long.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var offset = args.Length == 3 ? 0 : 1;
            var partial = args.Length == 5 ? args[4] : 0;
            milliseconds = ((offset == 1 ? args[0] * 86400 : 0) + args[offset] * 3600 + args[offset + 1] * 60 + args[offset + 2]) * 1000 + partial;
        }
        else
        {
            var factory = System.Text.RegularExpressions.Regex.Match(expression, @"^(?:System\.)?TimeSpan\.From(Seconds|Minutes|Hours|Days|Milliseconds)\s*\(\s*([0-9]{1,9})\s*\)$", options, timeout);
            if (!factory.Success) return null;
            var number = long.Parse(factory.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            var multiplier = factory.Groups[1].Value switch { "Milliseconds" => 1L, "Minutes" => 60000L, "Hours" => 3600000L, "Days" => 86400000L, _ => 1000L };
            milliseconds = number * multiplier;
        }
        return milliseconds;
    }

    private static IEnumerable<OdxNode> FlattenNativeSource(IEnumerable<OdxNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in FlattenNativeSource(node.Children)) yield return child; }
    }
}
