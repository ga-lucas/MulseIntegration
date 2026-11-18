using System.Xml.Linq;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;

namespace Service.Tests;

public sealed class NativeSystemPropertyImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-system-property-" + Guid.NewGuid().ToString("N"));

    private async Task<NativeFlowDefinition> Import(string expression)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Schema Include='Message.xsd'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003">
              <xs:element name="Request"><xs:annotation><xs:appinfo><b:properties><b:property distinguished="true" xpath="/*[local-name()='Request' and namespace-uri()='']/*[local-name()='Value' and namespace-uri()='']"/></b:properties></xs:appinfo></xs:annotation><xs:complexType><xs:sequence><xs:element name="Value" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Flow",
            N("MessageDeclaration", "request", P("Type", "Fixture.Message")), N("MessageDeclaration", "output", P("Type", "Fixture.Message")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("Construct", "construct", N("MessageAssignment", "fields", P("Expression", expression)), N("MessageRef", "result", P("Ref", "output"))),
                N("Send", "reply", P("MessageName", "output"), P("OperationMessageName", "Response")))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var response = await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default);
        var candidate = Assert.Single(response.FlowCandidates);
        Warnings = candidate.DraftFlow.Warnings;
        return NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
    }

    private IReadOnlyList<string> Warnings { get; set; } = [];

    [Fact]
    public async Task MessageIdReadMapsToTheReceivedMessageIdAndFailsClosedWhenAbsent()
    {
        var definition = await Import("output = request; output.Value = System.Convert.ToString(request(BTS.MessageID));");
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var fact = Assert.Single(definition.AllNodes().Where(node => node.Expression is not null).Select(node => node.Expression!)
            .SelectMany(Facts), item => item.MetadataKey == "messageId");
        Assert.Equal(NativeMessageFactKind.Metadata, fact.Kind);
        Assert.Contains(Warnings, warning => warning.Contains("BTS.MessageID", StringComparison.Ordinal) && warning.Contains("fails closed", StringComparison.Ordinal));

        var withId = new IntegrationBatch([new("in.xml", BinaryData.FromString("<Request><Value>v</Value></Request>"), "application/xml", new Dictionary<string, string> { ["messageId"] = "id-1" })]);
        Assert.Equal("id-1", fact.Read(withId).Value);
        var withoutId = new IntegrationBatch([new("in.xml", BinaryData.FromString("<Request><Value>v</Value></Request>"), "application/xml", new Dictionary<string, string>())]);
        Assert.Throws<InvalidOperationException>(() => fact.Read(withoutId));
    }

    [Fact]
    public async Task MessageIdIsReadOnlyAndAssigningItStaysBlocked()
    {
        var definition = await Import("output = request; request(BTS.MessageID) = \"forced\";");
        Assert.Contains(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("BTS.MessageID", StringComparison.Ordinal) && node.BlockReason.Contains("read-only", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings, warning => warning.Contains("mapped to the received message", StringComparison.Ordinal));
    }

    private static IEnumerable<NativeMessageFact> Facts(NativeExpression expression)
    {
        if (expression.Fact is not null) yield return expression.Fact;
        foreach (var operand in expression.Operands)
            foreach (var fact in Facts(operand)) yield return fact;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
