using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Mapping;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeSharedChoiceImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-shared-choices-" + Guid.NewGuid().ToString("N"));

    private static string Choices(params string[] values)
        => JsonSerializer.Serialize(values.Select(value => new RenderDispatchCase { Value = value, Module = "xslt-render", Settings = new() { ["xsltText"] = "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='1.0'/>" } }), JsonSerializerOptions.Web);

    private async Task<BizTalkFlowCandidateResponse> Import(string? choices, int transforms)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Shared.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string type, string name, params object?[] children) => new(ns + "Element", new XAttribute("Type", type), P("Name", name), children);
        var outputs = Enumerable.Range(0, transforms).Select(index => "out" + index).ToArray();
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Shared",
            N("VariableDeclaration", "tMap", P("Type", "System.Type")),
            N("MessageDeclaration", "input", P("Type", "System.Xml.XmlDocument")),
            outputs.Select(name => N("MessageDeclaration", name, P("Type", "System.Xml.XmlDocument"))),
            N("ServiceBody", "Body",
                N("Receive", "receive", P("MessageName", "input"), P("PortName", "input")),
                outputs.Select((name, index) => N("Construct", "build" + index,
                    N("MessageAssignment", "assign" + index, P("Expression", $"tMap = System.Type.GetType(\"Example.Map{index % 2}\"); transform ({name}) = tMap(input);")),
                    N("MessageRef", "ref" + index, P("Ref", name)))),
                N("Send", "reply", P("MessageName", outputs.LastOrDefault() ?? "input"), P("OperationMessageName", "Response")))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var response = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true, NativeMapChoicesJson = choices }, default);
        return Assert.Single(response.FlowCandidates);
    }

    [Fact]
    public async Task DynamicTransformsShareOneFlowTableInsteadOfCopyingItIntoEveryNode()
    {
        var choices = Choices("Example.Map0", "Example.Map1");
        var candidate = await Import(choices, 6);
        var json = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        var definition = NativeFlowDefinition.Read(json, allowBlocked: true);
        var transforms = definition.AllNodes().Where(node => node.SelectionExpression is not null).ToArray();
        Assert.Equal(6, transforms.Length);
        Assert.All(transforms, node => Assert.Empty(node.Settings));
        Assert.Equal(choices, definition.RenderChoicesJson);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(json, "XSL/Transform"));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("ONE shared choice table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlowWithoutDynamicTransformsCarriesNoSharedTable()
    {
        var candidate = await Import(Choices("Example.Map0"), 0);
        Assert.DoesNotContain("renderChoicesJson", candidate.DraftFlow.Augments.Single().Settings["flowJson"]);
    }

    [Fact]
    public async Task EditorShowsTheSharedTableOnceAndRoundTripsItWithoutLoss()
    {
        var choices = Choices("Example.Map0", "Example.Map1");
        var candidate = await Import(choices, 3);
        var json = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        var model = JsonSerializer.Deserialize<Mulse.Ui.Models.NativeFlowEditorModel>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(choices, model.RenderChoicesJson);
        var saved = JsonSerializer.Serialize(model, JsonSerializerOptions.Web);
        Assert.Equal(choices, NativeFlowDefinition.Read(saved, allowBlocked: true).RenderChoicesJson);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
        Assert.Contains("Shared renderer choices", html);
        var hints = System.Text.RegularExpressions.Regex.Count(html, "shared renderer choices\\. Edit them");
        Assert.True(hints == 3, $"hints={hints}");

        var minimal = JsonSerializer.Serialize(new NativeFlowDefinition
        {
            Durable = true, RenderChoicesJson = choices,
            Nodes = [new() { Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = "source", Expression = new() { Literal = NativeScalar.From("<s/>") } },
                new() { Kind = NativeFlowNodeKind.Render, Module = "render-dispatch", InputMessages = ["source"], MessageName = "out", SelectionExpression = new() { Literal = NativeScalar.From("Example.Map0") } }]
        }, JsonSerializerOptions.Web);
        var modules = new Mulse.Ui.Models.ModuleViewModel[] { new("render-dispatch", "Render dispatch", "Render", "Synthetic", [], [], [], []) };
        var valid = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = minimal, ["Modules"] = modules }))).ToHtmlString());
        Assert.DoesNotContain("Some nodes are incomplete", valid);
        var withoutTable = minimal.Replace("\"renderChoicesJson\":" + JsonSerializer.Serialize(choices, JsonSerializerOptions.Web) + ",", "").Replace(",\"renderChoicesJson\":" + JsonSerializer.Serialize(choices, JsonSerializerOptions.Web), "");
        Assert.DoesNotContain("renderChoicesJson", withoutTable);
        var invalid = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = withoutTable, ["Modules"] = modules }))).ToHtmlString());
        Assert.Contains("Some nodes are incomplete", invalid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
