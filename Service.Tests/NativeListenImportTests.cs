using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeListenImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-listen-import-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private const string BatchKey = "{urn:properties}Batch";
    private const string GroupKey = "{urn:properties}Group";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, string oid, params object[] children)
        => new(Ns + "Element", new XAttribute("Type", kind), new XAttribute("OID", oid), P("Name", name), children);

    private static XElement Receive(string oid, string message, bool activate, string port = "input")
        => N("Receive", "recv-" + oid, oid, P("Activate", activate ? "True" : "False"), P("MessageName", message), P("PortName", port));
    private static XElement Stop(string oid) => N("Terminate", "stop-" + oid, oid, P("ErrorMessage", "\"stop\""));
    private static XElement Delay(string oid, string timeout) => N("Delay", "delay-" + oid, oid, P("Timeout", timeout));
    private static XElement Branch(string oid, params object[] statements) => N("ListenBranch", "branch-" + oid, oid, statements);

    private async Task<NativeFlowDefinition> Import(XElement[] branches, string propertyType = "xs:string", string[]? propertyRefs = null,
        bool initializerActivates = true, bool secondSet = false, string[]? statementRefs = null, bool externalType = false, string externalModule = "Fixture")
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Fixture.btproj"), "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/>" + (externalType ? "<XLang Include='Types.odx'/>" : "") + "<Schema Include='Message.xsd'/>"
            + "<Schema Include='Properties.xsd'><Namespace>Fixture.Properties</Namespace><TypeName>CoreProperties</TypeName></Schema></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Properties.xsd"), $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003" targetNamespace="urn:properties">
              <xs:annotation><xs:appinfo><b:schemaInfo schema_type="property"/></xs:appinfo></xs:annotation>
              <xs:element name="Batch" type="{propertyType}"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid="42e773b8-64c1-4aae-acaf-b3645a04f7d0" propSchFieldBase="MessageContextPropertyBase"/></xs:appinfo></xs:annotation></xs:element>
              <xs:element name="Group" type="xs:string"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid="147d3ed8-a17c-4c12-98eb-3f9f93d9370f" propSchFieldBase="MessageContextPropertyBase"/></xs:appinfo></xs:annotation></xs:element>
            </xs:schema>
            """);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"),
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Request\"><xs:complexType><xs:sequence><xs:element name=\"Value\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:element></xs:schema>");
        var refs = (propertyRefs ?? ["Fixture.Properties.Batch", "Fixture.Properties.Group"]).Select((name, index) => N("PropertyRef", "PropertyRef_" + index, "prop-" + index, P("Ref", name))).ToArray<object>();
        var statements = (statementRefs ?? ["recv-1:True", "recv-2:False"]).Select((item, index) =>
            N("StatementRef", "StatementRef_" + index, "ref-" + index, P("Initializes", item.Split(':')[1]), P("Ref", item.Split(':')[0]))).ToArray<object>();
        var typeElement = N("CorrelationType", "BatchType", "type-1", refs);
        if (externalType)
            await File.WriteAllTextAsync(Path.Combine(_directory, "Types.odx"), "<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", N("Module", externalModule, "module-1", typeElement)));
        var source = new XElement(Ns + "MetaModel",
            externalType ? new XComment("external") : typeElement,
            N("ServiceDeclaration", "Service", "service-1",
                N("MessageDeclaration", "first", "msg-1", P("Type", "Fixture.Message")), N("MessageDeclaration", "next", "msg-2", P("Type", "Fixture.Message")),
                N("CorrelationDeclaration", "BatchSet", "corr-1", P("Type", "Fixture.BatchType"), P("ParamDirection", "In"), statements),
                secondSet ? N("CorrelationDeclaration", "OtherSet", "corr-2", P("Type", "Fixture.BatchType"), P("ParamDirection", "In"),
                    N("StatementRef", "StatementRef_a", "ref-a", P("Initializes", "True"), P("Ref", "recv-1")), N("StatementRef", "StatementRef_b", "ref-b", P("Initializes", "False"), P("Ref", "recv-2"))) : new XComment("none"),
                N("ServiceBody", "Body", "body-1", Receive("recv-1", "first", initializerActivates), N("Listen", "listen", "listen-1", branches.Cast<object>().ToArray()))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = Path.Combine(_directory, "Fixture.btproj"), GenerateNativeFlow = true }, default)).FlowCandidates,
            item => item.DraftFlow.Migration?.SourceOrchestration == "Flow");
        Last = candidate;
        return NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
    }

    private BizTalkFlowCandidateResponse? Last { get; set; }

    private XElement[] ReceiveOrTimeout(string timeout = "new System.TimeSpan(0,0,30)", bool followerActivates = false)
        => [Branch("lb-1", Receive("recv-2", "next", followerActivates), Stop("t-1")), Branch("lb-2", Delay("d-1", timeout))];

    [Fact]
    public async Task ACorrelatedListenWithATimeoutBecomesANativeListenWithPinnedCorrelationKeys()
    {
        var definition = await Import(ReceiveOrTimeout());
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var listen = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Listen);
        Assert.Equal(2, listen.Children.Count);
        Assert.All(listen.Children, branch => Assert.Equal(NativeFlowNodeKind.Sequence, branch.Kind));
        var receive = listen.Children[0].Children[0];
        Assert.Equal(NativeFlowNodeKind.Receive, receive.Kind);
        Assert.Equal("next", receive.MessageName); Assert.Equal("input", receive.Channel);
        Assert.Equal([BatchKey, GroupKey], receive.CorrelationKeys);
        Assert.Equal(NativeFlowNodeKind.Terminate, listen.Children[0].Children[1].Kind);
        var timeout = listen.Children[1].Children[0];
        Assert.Equal(NativeFlowNodeKind.Wait, timeout.Kind); Assert.Equal(30, timeout.DurationSeconds);
        var activating = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Receive && node.MessageName == "first");
        Assert.Empty(activating.CorrelationKeys);
        Assert.Contains(definition.AllNodes(), node => node.Name.StartsWith("CorrelationDeclaration (enforced by Receive correlation keys): BatchSet", StringComparison.Ordinal));
        Assert.Equal(2, definition.AllNodes().Count(node => node.Name.StartsWith("StatementRef (enforced", StringComparison.Ordinal)));
        Assert.Contains(Last!.DraftFlow.Warnings, warning => warning.Contains("pinned from the initial", StringComparison.Ordinal));
        Assert.Contains(Last.DraftFlow.Warnings, warning => warning.Contains("first matching Receive branch", StringComparison.Ordinal));
        Assert.False(Last.DraftFlow.Enabled);
        _ = NativeFlowDefinition.Read(Last.DraftFlow.Augments.Single().Settings["flowJson"]);
    }

    [Fact]
    public async Task AListenWithoutATimeoutIsAcceptedWhenEveryBranchReceives()
    {
        var definition = await Import([Branch("lb-1", Receive("recv-2", "next", false), Stop("t-1")), Branch("lb-2", Receive("recv-3", "next", false), Stop("t-2"))],
            statementRefs: ["recv-1:True", "recv-2:False", "recv-3:False"]);
        var listen = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Listen);
        Assert.All(listen.Children, branch => Assert.Equal(NativeFlowNodeKind.Receive, branch.Children[0].Kind));
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
    }

    [Fact]
    public async Task ACorrelationTypeDeclaredInAnotherOrchestrationOfTheSameProjectIsResolvedByExactName()
    {
        var definition = await Import(ReceiveOrTimeout(), externalType: true);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Equal([BatchKey, GroupKey], Assert.Single(definition.AllNodes(), node => node.MessageName == "next").CorrelationKeys);
    }

    [Fact]
    public async Task AnExternalCorrelationTypeInAnotherModuleIsNotGuessed()
    {
        var definition = await Import(ReceiveOrTimeout(), externalType: true, externalModule: "Other");
        Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name == "BatchSet");
    }

    [Theory]
    [InlineData("dynamic-delay")]
    [InlineData("two-delays")]
    [InlineData("delay-only")]
    [InlineData("single-branch")]
    [InlineData("statement-head")]
    public async Task ListensWithoutProvableBranchTriggersStayBlockedWithTheirBranches(string shape)
    {
        XElement[] branches = shape switch
        {
            "dynamic-delay" => ReceiveOrTimeout("someTimeout"),
            "two-delays" => [Branch("lb-1", Delay("d-0", "new System.TimeSpan(0,0,5)")), Branch("lb-2", Delay("d-1", "new System.TimeSpan(0,0,30)"))],
            "delay-only" => [Branch("lb-1", Delay("d-0", "new System.TimeSpan(0,0,5)")), Branch("lb-2", Stop("t-3"))],
            "single-branch" => [Branch("lb-1", Receive("recv-2", "next", false))],
            _ => [Branch("lb-1", Stop("t-1"), Receive("recv-2", "next", false)), Branch("lb-2", Delay("d-1", "new System.TimeSpan(0,0,30)"))]
        };
        var definition = await Import(branches);
        var listen = Assert.Single(definition.AllNodes(), node => node.Name == "listen");
        Assert.Equal(NativeFlowNodeKind.Blocked, listen.Kind);
        Assert.NotEmpty(listen.Children);
        Assert.False(Last!.DraftFlow.Enabled);
    }

    [Theory]
    [InlineData("decimal-property")]
    [InlineData("unknown-property")]
    [InlineData("not-activating")]
    [InlineData("follower-activates")]
    [InlineData("two-initializers")]
    [InlineData("unresolved-follower")]
    [InlineData("shared-follower")]
    public async Task CorrelationSetsThatCannotBeProvenStayBlockedWithASpecificReason(string problem)
    {
        var definition = problem switch
        {
            "decimal-property" => await Import(ReceiveOrTimeout(), propertyType: "xs:decimal"),
            "unknown-property" => await Import(ReceiveOrTimeout(), propertyRefs: ["Fixture.Properties.Missing"]),
            "not-activating" => await Import(ReceiveOrTimeout(), initializerActivates: false),
            "follower-activates" => await Import(ReceiveOrTimeout(followerActivates: true)),
            "two-initializers" => await Import(ReceiveOrTimeout(), statementRefs: ["recv-1:True", "recv-2:True"]),
            "unresolved-follower" => await Import(ReceiveOrTimeout(), statementRefs: ["recv-1:True", "missing-oid:False"]),
            _ => await Import(ReceiveOrTimeout(), secondSet: true)
        };
        var declaration = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name == "BatchSet");
        Assert.False(string.IsNullOrWhiteSpace(declaration.BlockReason));
        Assert.DoesNotContain("enforced by Receive correlation keys", string.Join("|", definition.AllNodes().Select(node => node.Name)));
        Assert.All(definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Receive && node.MessageName == "next"), receive => Assert.Empty(receive.CorrelationKeys));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
