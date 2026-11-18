using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeSendPortPipelineAndDirectBindingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-send-pipeline-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), P("Name", name), children);

    private const string Stock = "Microsoft.BizTalk.DefaultPipelines.";

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string? sendPort, bool directBound = false)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"),
            N("PortDeclaration", "outPort", directBound ? N("DirectBindingAttribute", "direct", P("DirectBindingType", "MessageBox")) : N("PhysicalBindingAttribute", "binding")),
            N("ServiceBody", "Body",
                N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("Send", "Out", P("MessageName", "request"), P("PortName", "outPort")))));
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var request = new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true };
        if (sendPort is not null)
        {
            var binding = Path.Combine(_directory, "bindings.xml");
            await File.WriteAllTextAsync(binding, $"""
                <BindingInfo><ModuleRefCollection><ModuleRef Name="Fixture"><Services><Service Name="Fixture.Flow"><Ports>
                <Port Name="input"><ReceivePortRef Name="Input"/></Port><Port Name="outPort"><SendPortRef Name="Output"/></Port>
                </Ports></Service></Services></ModuleRef></ModuleRefCollection>
                <ReceivePortCollection><ReceivePort Name="Input"><ReceiveLocations><ReceiveLocation Name="Source"><Address>C:\synthetic\input</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort></ReceivePortCollection>
                <SendPortCollection>{sendPort}</SendPortCollection></BindingInfo>
                """);
            request = request with { AdditionalBindingPaths = [binding] };
        }
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(request, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    private static string SendPort(string pipeline, int retry = 0, string pipelineData = "")
        => $"<SendPort Name=\"Output\"><TransmitPipeline Name=\"{pipeline}\"/>{(pipelineData.Length == 0 ? "" : $"<SendPipelineData>{System.Security.SecurityElement.Escape(pipelineData)}</SendPipelineData>")}"
            + $"<PrimaryTransport><Address>C:\\synthetic\\out\\%MessageID%.xml</Address><TransportType Name=\"FILE\"/><RetryCount>{retry}</RetryCount><RetryInterval>5</RetryInterval></PrimaryTransport></SendPort>";

    [Theory]
    [InlineData("PassThruTransmit", "PassThruTransmit")]
    [InlineData("XMLTransmit", "XMLTransmit")]
    public async Task StockTransmitPipelinesNeedNoStepAndAreWarned(string pipeline, string warningText)
    {
        var (candidate, definition) = await Import(SendPort(Stock + pipeline));
        Assert.True(definition.AllNodes().All(node => node.Kind != NativeFlowNodeKind.Blocked), string.Join(" | ", definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Blocked).Select(node => node.BlockReason)));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains(warningText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomAndOtherTransmitPipelinesStayBlockedWithTheNameShown()
    {
        var (_, custom) = await Import(SendPort("Example.Pipelines.SendFlatFile"));
        var blocked = Assert.Single(custom.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("Example.Pipelines.SendFlatFile", blocked.BlockReason!, StringComparison.Ordinal);
        var (_, edi) = await Import(SendPort("Microsoft.BizTalk.Edi.DefaultPipelines.EdiSend"));
        Assert.Contains(edi.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
    }

    [Fact]
    public async Task StockPipelineWithPerPortComponentConfigurationStaysBlocked()
    {
        var (_, definition) = await Import(SendPort(Stock + "XMLTransmit", pipelineData: "<Root><Stage CategoryId=\"x\"><Components><Component Name=\"Example.Component\"><Properties><Setting vt=\"8\">1</Setting></Properties></Component></Components></Stage></Root>"));
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("own pipeline component configuration", blocked.BlockReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryStaysBlockedEvenWithAStockPipeline()
    {
        var (_, definition) = await Import(SendPort(Stock + "PassThruTransmit", retry: 3));
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("retry", blocked.BlockReason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transmit pipeline", blocked.BlockReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageBoxBoundSendBecomesFlowLinkDeliverWithAnUnsetChannel()
    {
        var (candidate, definition) = await Import(null, directBound: true);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var deliver = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver && node.Module == "flow-link-deliver");
        Assert.Equal("request", deliver.MessageName);
        Assert.StartsWith("{{config:", deliver.Settings["channel"], StringComparison.Ordinal);
        Assert.Contains(candidate.DraftFlow.ConfigurationRequirements, requirement => requirement.SettingPath.EndsWith("settings.channel", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("publish/subscribe", StringComparison.Ordinal) && warning.Contains("fails closed", StringComparison.Ordinal));
        Assert.False(candidate.DraftFlow.Enabled);
    }

    [Fact]
    public async Task UnboundSendThatIsNotMessageBoxBoundStaysBlocked()
    {
        var (_, definition) = await Import(null);
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("no unique explicit physical binding", blocked.BlockReason!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
