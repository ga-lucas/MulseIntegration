using System.Security;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkReceivePipelineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-receive-pipeline-" + Guid.NewGuid().ToString("N"));

    private static string Component(string name, params (string Key, string Value)[] properties)
        => $"<Component Name=\"{name}\"><Properties>{string.Concat(properties.Select(static p => $"<{p.Key} vt=\"8\">{p.Value}</{p.Key}>"))}</Properties></Component>";

    private static string PipelineData(params string[] components)
        => SecurityElement.Escape("<Root><Stages><Stage><Components>" + string.Concat(components) + "</Components></Stage></Stages></Root>")!;

    private static string Location(string name, string? pipelineData, string pipelineName = "Example.Pipelines.ReceiveThing, Example.Pipelines, Version=1.0.0.0")
        => $"<ReceiveLocation Name=\"{name}\"><Address>C:\\in\\{name}\\*.xml</Address><TransportType Name=\"FILE\"/>"
            + $"<ReceivePipeline Name=\"{pipelineName}\"/>"
            + (pipelineData is null ? string.Empty : $"<ReceivePipelineData>{pipelineData}</ReceivePipelineData>")
            + "<TransportTypeData>&lt;CustomProps&gt;&lt;FileMask&gt;*.xml&lt;/FileMask&gt;&lt;/CustomProps&gt;</TransportTypeData></ReceiveLocation>";

    private static string Binding(string locations, string sendPorts = "")
        => "<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\"><ReceivePortRef Name=\"InPort\"/></Port></Ports></Service></Services></ModuleRef></ModuleRefCollection>"
            + $"<ReceivePortCollection><ReceivePort Name=\"InPort\"><ReceiveLocations>{locations}</ReceiveLocations></ReceivePort></ReceivePortCollection><SendPortCollection>{sendPorts}</SendPortCollection></BindingInfo>";

    private async Task<BizTalkFlowCandidateResponse> AnalyzeAsync(string bindingXml, string? pipelineFile = null)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Billing.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Example.Billing</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/>"
            + (pipelineFile is null ? string.Empty : "<Pipeline Include='ReceiveThing.btp'/>") + "</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='Send'/></om:MetaModel>");
        if (pipelineFile is not null)
        {
            File.WriteAllText(Path.Combine(_directory, "ReceiveThing.btp"), pipelineFile);
        }

        var binding = Path.Combine(_directory, "bindings.xml");
        File.WriteAllText(binding, bindingXml);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalBindingPaths = [binding] }, default);
        return result.FlowCandidates.First();
    }

    private static IReadOnlyList<BizTalkDraftStepResponse> SetSteps(BizTalkFlowCandidateResponse candidate)
        => candidate.DraftFlow.Augments.Where(static step => step.Module == "set-context-property-augment").ToArray();

    [Fact]
    public async Task ConsumptionSetter_BecomesOneEditableStepPerPromotedProperty()
    {
        var data = PipelineData(Component("Example.CPC.PS.Consumption.ConsumptionPropertySetter",
            ("PropertyNamespace", "urn:example:consumption"), ("OrganizationId", "42"), ("OrganizationName", "Example Org"), ("FailureNotification", "ops@example.com")));
        var candidate = await AnalyzeAsync(Binding(Location("In", data)));

        var steps = SetSteps(candidate);
        Assert.Equal(["OrganizationId", "OrganizationName", "CommonConsumptionMap", "FailureNotification"], steps.Select(static step => step.Settings["propertyName"]));
        Assert.All(steps, static step => Assert.Equal("urn:example:consumption", step.Settings["propertyNamespace"]));
        Assert.Equal("42", steps[0].Settings["propertyValue"]);
        Assert.Equal(string.Empty, steps[2].Settings["propertyValue"]);
        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.Contains("ConsumptionPropertySetter", StringComparison.Ordinal) && warning.Contains("No value is configured for: CommonConsumptionMap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PromotedNamesThatDifferFromTheComponentProperty_UseThePromotedName()
    {
        var data = PipelineData(
            Component("Example.CPC.PS.ItemSynch.SetAndPromoteItemSyncProperties", ("PropertyNamespace", "urn:example:items"), ("ReadyForSync", "true"), ("ImportThrottle", "5")),
            Component("Example.Core.Custom.CPC.PS.MNP.MapNamePromotions", ("CustomPropertySchema_NameSpace", "urn:example:custom"), ("IsEDI", "true"), ("BodyMapName", "Example.Maps.Body")));
        var candidate = await AnalyzeAsync(Binding(Location("In", data)));

        var steps = SetSteps(candidate);
        var ready = Assert.Single(steps, static step => step.Settings["propertyName"] == "ReadyForSynch");
        Assert.Equal("true", ready.Settings["propertyValue"]);
        Assert.Equal("urn:example:items", ready.Settings["propertyNamespace"]);
        Assert.Equal("5", Assert.Single(steps, static step => step.Settings["propertyName"] == "ImportThrottle").Settings["propertyValue"]);
        var edi = Assert.Single(steps, static step => step.Settings["propertyName"] == "IsEDIData");
        Assert.Equal("true", edi.Settings["propertyValue"]);
        Assert.Equal("urn:example:custom", edi.Settings["propertyNamespace"]);
        Assert.Equal("Example.Maps.Body", Assert.Single(steps, static step => step.Settings["propertyName"] == "BodyMapName").Settings["propertyValue"]);
        Assert.DoesNotContain(steps, static step => step.Settings["propertyName"] is "IsEDI" or "ReadyForSync");
    }

    [Fact]
    public async Task Hl7Promotions_AreDraftedInTheHl7Namespace()
    {
        var data = PipelineData(Component("Example.Core.Custom.CPC.PS.HL7Promotions", ("hl7_propertyNameSpace", "urn:example:hl7"), ("hl7_MsgType", "ADT^A01"), ("hl7_IsBatch", "false")));
        var candidate = await AnalyzeAsync(Binding(Location("In", data)));

        var steps = SetSteps(candidate);
        Assert.Equal(12, steps.Count);
        Assert.Equal("ADT^A01", Assert.Single(steps, static step => step.Settings["propertyName"] == "hl7_MsgType").Settings["propertyValue"]);
        Assert.All(steps, static step => Assert.Equal("urn:example:hl7", step.Settings["propertyNamespace"]));
    }

    [Fact]
    public async Task MissingBindingConfiguration_FallsBackToTheDesignTimePipelineFile()
    {
        const string pipeline = "<Document><Stages><Stage><Components>"
            + "<Component><Name>Example.CPC.PS.Consumption.ConsumptionPropertySetter</Name><Properties>"
            + "<Property Name=\"PropertyNamespace\"><Value>urn:example:design</Value></Property><Property Name=\"OrganizationId\"><Value>7</Value></Property>"
            + "<Property Name=\"OrganizationName\" /></Properties></Component></Components></Stage></Stages></Document>";
        var candidate = await AnalyzeAsync(Binding(Location("In", null)), pipeline);

        var org = Assert.Single(SetSteps(candidate), static step => step.Settings["propertyName"] == "OrganizationId");
        Assert.Equal("7", org.Settings["propertyValue"]);
        Assert.Equal("urn:example:design", org.Settings["propertyNamespace"]);
        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.Contains("design-time pipeline file 'ReceiveThing.btp'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocationsWithDifferentConfiguration_AreReportedAndOnlyTheFirstIsGenerated()
    {
        string Data(string org) => PipelineData(Component("Example.CPC.PS.Consumption.ConsumptionPropertySetter", ("PropertyNamespace", "urn:x"), ("OrganizationId", org)));
        var candidate = await AnalyzeAsync(Binding(Location("InA", Data("1")) + Location("InB", Data("2")) + Location("InC", Data("1"))));

        Assert.Equal("1", Assert.Single(SetSteps(candidate), static step => step.Settings["propertyName"] == "OrganizationId").Settings["propertyValue"]);
        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.Contains("'InB'", StringComparison.Ordinal) && warning.Contains("differs from 'InA'", StringComparison.Ordinal));
        Assert.DoesNotContain(candidate.DraftFlow.Warnings, static warning => warning.Contains("'InC'", StringComparison.Ordinal) && warning.Contains("differs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StandardMicrosoftComponentsAreSilent_ButUnknownCustomComponentsAreReported()
    {
        var data = PipelineData(Component("Microsoft.BizTalk.Component.XmlDasmComp"), Component("Example.Custom.MysteryComponent"));
        var candidate = await AnalyzeAsync(Binding(Location("In", data)));

        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.Contains("MysteryComponent", StringComparison.Ordinal) && warning.Contains("not been translated", StringComparison.Ordinal));
        Assert.DoesNotContain(candidate.DraftFlow.Warnings, static warning => warning.Contains("XmlDasmComp", StringComparison.Ordinal));
        Assert.Empty(SetSteps(candidate));
    }

    [Fact]
    public async Task AddBlockCharacters_IsReportedAsNoEffect_OnSendAndReceiveSides()
    {
        var data = PipelineData(Component("Example.CPC.AddBlockCharacters.Hl7AddDelimit", ("BlockStartCharacter", "11")));
        var send = $"<SendPort Name=\"Out\"><PrimaryTransport><Address>C:\\out</Address><TransportType Name=\"FILE\"/><TransportTypeData>&lt;CustomProps&gt;&lt;FileName&gt;x.hl7&lt;/FileName&gt;&lt;/CustomProps&gt;</TransportTypeData></PrimaryTransport><SendPipelineData>{data}</SendPipelineData></SendPort>";
        var candidate = await AnalyzeAsync(Binding(Location("In", data), send).Replace("<Port Name=\"p\">", "<Port Name=\"p\"><SendPortRef Name=\"Out\"/>", StringComparison.Ordinal));

        Assert.DoesNotContain(candidate.DraftFlow.Warnings, static warning => warning.Contains("unsupported pipeline component", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.StartsWith("Send port 'Out'", StringComparison.Ordinal) && warning.Contains("passes the message through unchanged", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, static warning => warning.StartsWith("Receive location 'In'", StringComparison.Ordinal) && warning.Contains("passes the message through unchanged", StringComparison.Ordinal));
        Assert.Empty(SetSteps(candidate));
    }

    [Fact]
    public async Task SendPortPromotionComponents_AreDraftedToo()
    {
        var data = PipelineData(Component("Example.CPC.PS.MNP.MapNamePromotions", ("CustomPropertySchema_NameSpace", "urn:x"), ("IsEDI", "false")));
        var send = $"<SendPort Name=\"Out\"><PrimaryTransport><Address>C:\\out</Address><TransportType Name=\"FILE\"/><TransportTypeData>&lt;CustomProps&gt;&lt;FileName&gt;x.xml&lt;/FileName&gt;&lt;/CustomProps&gt;</TransportTypeData></PrimaryTransport><SendPipelineData>{data}</SendPipelineData></SendPort>";
        var candidate = await AnalyzeAsync(Binding(Location("In", null), send).Replace("<Port Name=\"p\">", "<Port Name=\"p\"><SendPortRef Name=\"Out\"/>", StringComparison.Ordinal));

        Assert.Equal("false", Assert.Single(SetSteps(candidate), static step => step.Settings["propertyName"] == "IsEDIData").Settings["propertyValue"]);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
