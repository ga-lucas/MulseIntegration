using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeSendPipelineChainTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-send-chain-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), P("Name", name), children);

    private static string Component(string name, params (string Key, string Value)[] properties)
        => $"<Component><Name>{name}</Name><Properties>{string.Concat(properties.Select(property => $"<Property Name=\"{property.Key}\"><Value>{property.Value}</Value></Property>"))}</Properties></Component>";

    private static string Pipeline(params string[] components)
        => $"<Document><Stages><Stage CategoryId=\"9d0e4101-4cce-4536-83fa-4a5040674ad6\"><Components>{string.Concat(components)}</Components></Stage></Stages></Document>";

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string pipelineFile, string transport = "FILE", int retry = 0, string? pipelineData = null)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"),
            N("PortDeclaration", "outPort", N("PhysicalBindingAttribute", "binding")),
            N("ServiceBody", "Body",
                N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("Send", "Out", P("MessageName", "request"), P("PortName", "outPort")))));
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Pipeline Include='SendX.btp'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        await File.WriteAllTextAsync(Path.Combine(_directory, "SendX.btp"), pipelineFile);
        var address = transport == "SMTP" ? "mailto:synthetic@example.com" : "C:\\synthetic\\out\\%MessageID%.xml";
        var data = pipelineData is null ? "" : $"<SendPipelineData>{System.Security.SecurityElement.Escape(pipelineData)}</SendPipelineData>";
        var binding = Path.Combine(_directory, "bindings.xml");
        await File.WriteAllTextAsync(binding, $"""
            <BindingInfo><ModuleRefCollection><ModuleRef Name="Fixture"><Services><Service Name="Fixture.Flow"><Ports>
            <Port Name="input"><ReceivePortRef Name="Input"/></Port><Port Name="outPort"><SendPortRef Name="Output"/></Port>
            </Ports></Service></Services></ModuleRef></ModuleRefCollection>
            <ReceivePortCollection><ReceivePort Name="Input"><ReceiveLocations><ReceiveLocation Name="Source"><Address>C:\synthetic\input</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort></ReceivePortCollection>
            <SendPortCollection><SendPort Name="Output"><TransmitPipeline Name="Example.Pipelines.SendX"/>{data}
            <PrimaryTransport><Address>{address}</Address><TransportType Name="{transport}"/><RetryCount>{retry}</RetryCount><RetryInterval>5</RetryInterval></PrimaryTransport></SendPort></SendPortCollection></BindingInfo>
            """);
        var request = new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true, AdditionalBindingPaths = [binding] };
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(request, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    private static string Blockers(NativeFlowDefinition definition)
        => string.Join(" | ", definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Blocked).Select(node => node.BlockReason));

    [Fact]
    public async Task PropertySetterAndHl7ComponentsBecomeAnOrderedNamedChainBeforeTheDelivery()
    {
        var (candidate, definition) = await Import(Pipeline(
            Component("Example.CPC.PS.SetProperty", ("PropertyName", "FileName"), ("PropertyNamespace", "urn:mime"), ("PropertyValue", "a.hl7")),
            Component("Example.CPC.HL7TypeCaster"),
            Component("Microsoft.Solutions.BTAHL7.Pipelines.HL72fAsm", ("TrailingDelimiterAllowed", "false"))));
        Assert.True(string.IsNullOrEmpty(Blockers(definition)), Blockers(definition));
        var steps = definition.AllNodes().Where(node => node.Id.Contains("-pipe-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["set-context-property-augment", "hl7-message-type-augment", "hl7-xml-render"], steps.Select(step => step.Module));
        Assert.Equal([NativeFlowNodeKind.Augment, NativeFlowNodeKind.Augment, NativeFlowNodeKind.Render], steps.Select(step => step.Kind));
        Assert.Equal("request", steps[0].InputMessages!.Single());
        Assert.Equal(steps[0].MessageName, steps[1].InputMessages!.Single());
        Assert.Equal(steps[1].MessageName, steps[2].InputMessages!.Single());
        Assert.Equal("a.hl7", steps[0].Settings["propertyValue"]);
        Assert.Equal("true", steps[2].Settings["trimTrailingDelimiters"]);
        var deliver = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver);
        Assert.Equal(steps[2].MessageName, deliver.MessageName);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("design-time pipeline file", StringComparison.Ordinal));
        Assert.False(candidate.DraftFlow.Enabled);
    }

    [Fact]
    public async Task PerPortBindingConfigurationWinsOverTheDesignTimeFile()
    {
        var (candidate, definition) = await Import(Pipeline(Component("Example.CPC.PS.SetProperty", ("PropertyName", "FileName"), ("PropertyNamespace", "urn:mime"), ("PropertyValue", "design.txt"))),
            pipelineData: "<Root><Stages><Stage><Components><Component Name=\"Example.CPC.PS.SetProperty\"><Properties><PropertyName vt=\"8\">FileName</PropertyName><PropertyNamespace vt=\"8\">urn:mime</PropertyNamespace><PropertyValue vt=\"8\">deployed.txt</PropertyValue></Properties></Component></Components></Stage></Stages></Root>");
        Assert.True(string.IsNullOrEmpty(Blockers(definition)), Blockers(definition));
        Assert.Equal("deployed.txt", Assert.Single(definition.AllNodes(), node => node.Module == "set-context-property-augment").Settings["propertyValue"]);
        Assert.DoesNotContain(candidate.DraftFlow.Warnings, warning => warning.Contains("design-time pipeline file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropertiesTheBindingDoesNotSetKeepTheDesignTimeValues()
    {
        var (_, definition) = await Import(Pipeline(Component("Microsoft.BizTalk.Component.MIME_SMIME_Encoder", ("SendBodyPartAsAttachment", "true"), ("SignatureType", "0"), ("EnableEncryption", "false"))),
            transport: "SMTP",
            pipelineData: "<Root><Stages><Stage><Components><Component Name=\"Microsoft.BizTalk.Component.MIME_SMIME_Encoder\"><Properties /></Component></Components></Stage></Stages></Root>");
        Assert.True(string.IsNullOrEmpty(Blockers(definition)), Blockers(definition));
        Assert.Equal("true", Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver).Settings["attachPayload"]);
    }

    [Fact]
    public async Task EmptySetPropertyStaysAnEditableNoOpNodeWithAWarning()
    {
        var (candidate, definition) = await Import(Pipeline(Component("Example.CPC.PS.SetProperty", ("PropertyName", ""), ("PropertyNamespace", ""), ("PropertyValue", ""))));
        Assert.True(string.IsNullOrEmpty(Blockers(definition)), Blockers(definition));
        Assert.Single(definition.AllNodes(), node => node.Module == "set-context-property-augment");
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("writes nothing until all three are set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownComponentKeepsTheSendBlockedAndNamesItWhileShowingTheTranslatedParts()
    {
        var (_, definition) = await Import(Pipeline(
            Component("Example.CPC.HL7TypeCaster"),
            Component("Example.Custom.Compressor")));
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("Example.Custom.Compressor", blocked.BlockReason!, StringComparison.Ordinal);
        Assert.Contains(blocked.Children, child => child.Module == "hl7-message-type-augment");
        Assert.Contains(blocked.Children, child => child.Kind == NativeFlowNodeKind.Deliver);
    }

    [Fact]
    public async Task FlatFileAssemblerIsNamedAsTheBlockerWhileOtherComponentsAreTranslated()
    {
        var (_, definition) = await Import(Pipeline(
            Component("Example.CPC.PS.SetProperty", ("PropertyName", "A"), ("PropertyNamespace", "urn:x"), ("PropertyValue", "1")),
            Component("Microsoft.BizTalk.Component.FFAsmComp", ("DocumentSpecName", ""))));
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("flat-file assembler", blocked.BlockReason!, StringComparison.Ordinal);
        Assert.Contains(blocked.Children, child => child.Module == "set-context-property-augment");
    }

    [Fact]
    public async Task MimeEncoderWithoutSigningOrEncryptionBecomesSmtpAttachment()
    {
        var (candidate, definition) = await Import(Pipeline(Component("Microsoft.BizTalk.Component.MIME_SMIME_Encoder",
            ("SendBodyPartAsAttachment", "true"), ("SignatureType", "0"), ("EnableEncryption", "false"))), transport: "SMTP");
        Assert.True(string.IsNullOrEmpty(Blockers(definition)), Blockers(definition));
        var deliver = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver);
        Assert.Equal("smtp-email-deliver", deliver.Module);
        Assert.Equal("true", deliver.Settings["attachPayload"]);
        Assert.Equal("false", deliver.Settings["usePayloadAsBody"]);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("MIME headers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SignatureType", "1")]
    [InlineData("EnableEncryption", "true")]
    [InlineData("SendBodyPartAsAttachment", "false")]
    public async Task MimeEncoderWithSigningEncryptionOrBodyAsMessageStaysBlocked(string key, string value)
    {
        var properties = new Dictionary<string, string> { ["SendBodyPartAsAttachment"] = "true", ["SignatureType"] = "0", ["EnableEncryption"] = "false", [key] = value };
        var (_, definition) = await Import(Pipeline(Component("Microsoft.BizTalk.Component.MIME_SMIME_Encoder", properties.Select(pair => (pair.Key, pair.Value)).ToArray())), transport: "SMTP");
        Assert.Contains("MIME/S-MIME encoder", Blockers(definition), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MimeEncoderOnANonSmtpTransportStaysBlocked()
    {
        var (_, definition) = await Import(Pipeline(Component("Microsoft.BizTalk.Component.MIME_SMIME_Encoder", ("SendBodyPartAsAttachment", "true"), ("SignatureType", "0"), ("EnableEncryption", "false"))));
        Assert.Contains("does not use SMTP", Blockers(definition), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryKeepsATranslatedPipelineBlocked()
    {
        var (_, definition) = await Import(Pipeline(Component("Example.CPC.HL7TypeCaster")), retry: 2);
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("retry", blocked.BlockReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no proven native equivalent", blocked.BlockReason!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
