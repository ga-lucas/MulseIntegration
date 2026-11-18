using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.Mapping;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.BuiltIn.Render;
using Service.Ai;
using Service.Models;

namespace Service.Tests;

public sealed class ConnectionAssistanceTests
{
    private const string Mapping = """{"version":1,"recordPath":"","fields":[{"sourcePath":"/order","targetPath":"/invoice","kind":"Field","missing":"Error"}]}""";
    private const string Response = """{"reply":"Copy order to invoice.","proposal":{{MAPPING}}}""";
    private static ConnectionSample Sample(string name = "normal", string input = "{\"order\":\"private-order-1\"}", string expected = "[{\"invoice\":\"private-order-1\"}]")
        => new(name, input, expected);

    private static string Draft() => JsonSerializer.Serialize(new
    {
        id = "orders", category = "customer", name = "Orders", enabled = true, runOnStartup = true, triggerMode = "OnDemand",
        fetches = new[] { new { id = "f1", fetch = new { module = "fetch", settings = new { path = @"C:\private\orders", token = "private-token" } }, inputParseIds = Array.Empty<string>() } },
        parses = new[] { new { id = "p1", parse = new { module = "parse", settings = new { } }, fetchNodeIds = new[] { "f1" } } },
        augments = Array.Empty<object>(),
        deliveries = new[] { new { render = new { module = "json-render", settings = new { indented = "true" } },
            delivers = new[] { new { deliver = new { module = "deliver", settings = new { endpoint = "https://private.example", apiKey = "private-key" } }, requireConfirmation = true,
                compensation = new { module = "deliver", settings = new { undo = "private-undo" } } } }, atomicScope = "scope" } }
    });

    private static ConnectionAssistanceRequest Request(params ConnectionSample[] samples)
        => new(Draft(), "p1", 0, "Copy order to invoice", samples.Length == 0 ? [Sample()] : samples);

    private static (AiAuthoringService Service, ScriptedChatClient Client, ScriptedFactory Factory) Create(string? response = null)
    {
        var settings = new AiAuthoringSettingsService(Options.Create(new AiAuthoringOptions { Enabled = true, Model = "test", ApiKey = "provider-private-key" }),
            new MemoryAiSettingsStore(), new TestHost("Production"));
        var client = new ScriptedChatClient(_ => response ?? Response.Replace("{{MAPPING}}", Mapping));
        var factory = new ScriptedFactory(client);
        var catalog = new ReadOnlyCatalog([
            new("fetch", "Fetch", ModuleKind.Fetch, "test"), new("parse", "Parse", ModuleKind.Parse, "test"),
            new("json-render", "JSON", ModuleKind.Render, "test"), new("deliver", "Deliver", ModuleKind.Deliver, "test"),
            new("parsed-data-map-augment", "Map", ModuleKind.OrchestrationAugment, "test",
                [new("mappingJson", "Map", "test", true), new("sourceId", "Source", "test", false), new("inputFormat", "Format", "test", false)])
        ]);
        return (new(settings, catalog, factory, NullLogger<AiAuthoringService>.Instance), client, factory);
    }

    [Fact]
    public void AnalysisCombinesOptionalNullableAndArrayShapesWithoutValuesOrProviderCalls()
    {
        var (service, _, factory) = Create();
        var result = service.AnalyzeConnection(Request(
            Sample("private-scenario-name", """{"order":"private-value","customer":null,"lines":[]}"""),
            Sample("second", """{"order":"another-private-value","extra":true,"customer":"x","lines":[{"sku":"private-sku","qty":2}]}""")));
        Assert.Empty(result.Errors);
        Assert.True(result.InputShape.Single(field => field.Path == "/extra").Optional);
        var customer = result.InputShape.Single(field => field.Path == "/customer");
        Assert.True(customer.Nullable);
        Assert.Equal(["string"], customer.Types);
        Assert.Equal(["number"], result.InputShape.Single(field => field.Path == "/lines/*/qty").Types);
        Assert.DoesNotContain("private-value", result.DisclosureJson);
        Assert.DoesNotContain("private-sku", result.DisclosureJson);
        Assert.DoesNotContain("private-scenario-name", result.DisclosureJson);
        Assert.Equal(0, factory.Created);
    }

    [Theory]
    [InlineData("{\"order\":null}", "only null")]
    [InlineData("{\"order\":[]}", "array item shape")]
    [InlineData("[]", "populated record")]
    public void AmbiguousSamplesProduceActionableBlockingDiagnostics(string input, string diagnostic)
    {
        var analysis = ConnectionSampleAnalysis.Analyze([Sample(input: input)]);
        Assert.Contains(analysis.Errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConflictingTypesBlockBeforeProviderCall()
    {
        var (service, _, factory) = Create();
        var result = await service.AssistConnectionAsync(Request(Sample("one"), Sample("two", "{\"order\":42}")), default);
        Assert.Null(result.ProposalJson);
        Assert.Empty(result.Previews);
        Assert.Contains(result.Errors, error => error.Contains("conflicting types", StringComparison.Ordinal));
        Assert.Equal(0, factory.Created);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualProviderContextIncludesOnlyIndividuallyApprovedSampleValues(bool inputConsent, bool outputConsent)
    {
        var (service, client, _) = Create();
        var sample = Sample(input: "{\"order\":\"input-private\"}", expected: "[{\"invoice\":\"output-private\"}]") with
        { ShareInputValues = inputConsent, ShareExpectedValues = outputConsent };
        await service.AssistConnectionAsync(Request(sample), default);
        var sent = string.Join("\n", client.Received.Select(message => message.Text));
        Assert.Equal(inputConsent, sent.Contains("input-private", StringComparison.Ordinal));
        Assert.Equal(outputConsent, sent.Contains("output-private", StringComparison.Ordinal));
        foreach (var secret in new[] { "private-token", "private.example", "private-key", "private-undo", "provider-private-key", "C:\\private" })
            Assert.DoesNotContain(secret, sent);
        Assert.Null(client.Options!.Tools);
    }

    [Fact]
    public async Task EveryScenarioIsComparedAndAnyDifferenceBlocksApply()
    {
        var (service, _, _) = Create();
        var result = await service.AssistConnectionAsync(Request(Sample("matches"), Sample("different", expected: "[{\"invoice\":\"wrong\"}]")), default);
        Assert.Equal(2, result.Previews.Count);
        Assert.True(result.Previews[0].Matches);
        Assert.False(result.Previews[1].Matches);
        Assert.NotNull(result.Previews[1].ActualJson);
        Assert.NotEmpty(result.Errors);
        Assert.Null(result.ProposalJson);
        Assert.NotNull(result.MappingJson);
    }

    [Fact]
    public async Task MissingFieldFailureDoesNotSkipOtherScenarios()
    {
        var (service, _, _) = Create();
        var result = await service.AssistConnectionAsync(Request(Sample("missing", "{}"), Sample("valid")), default);
        Assert.Equal(2, result.Previews.Count);
        Assert.False(result.Previews[0].Matches);
        Assert.Null(result.Previews[0].ActualJson);
        Assert.NotNull(result.Previews[0].Error);
        Assert.True(result.Previews[1].Matches);
        Assert.Null(result.ProposalJson);
    }

    [Fact]
    public async Task SuccessfulProposalOnlyAddsMappingAndImposesDisabledState()
    {
        var (service, _, _) = Create();
        var result = await service.AssistConnectionAsync(Request(Sample("first"), Sample("second", "{\"order\":\"second\"}", "[{\"invoice\":\"second\"}]")), default);
        Assert.Empty(result.Errors);
        Assert.All(result.Previews, preview => Assert.True(preview.Matches));
        var original = JsonNode.Parse(Draft())!;
        var proposal = JsonNode.Parse(Assert.IsType<string>(result.ProposalJson))!;
        foreach (var property in new[] { "id", "name", "category", "fetches", "parses", "deliveries", "triggerMode" })
            Assert.True(JsonNode.DeepEquals(original[property], proposal[property]), property);
        Assert.False((bool)proposal["enabled"]!);
        Assert.False((bool)proposal["runOnStartup"]!);
        var augment = Assert.Single(proposal["augments"]!.AsArray());
        Assert.Equal("parsed-data-map-augment", (string?)augment!["module"]);
        Assert.Equal("p1", (string?)augment["settings"]!["sourceId"]);
        Assert.Equal("Json", (string?)augment["settings"]!["inputFormat"]);
        var map = ParsedDataMapping.ReadMap((string)augment["settings"]!["mappingJson"]!);
        Assert.Equal("/order", Assert.Single(map.Fields).SourcePath);
        var pipeline = AiAuthoringProposalProcessor.ToPipeline(proposal.Deserialize<AiFlowDoc>(AiAuthoringJson.Options)!);
        var mapped = await new ParsedDataMapAugmentModule().AugmentAsync(ParsedDataMappingTests.Context,
            new IntegrationBatch([ParsedDataMappingTests.Payload(Sample().InputJson, "p1")]), pipeline.Augments[0], default);
        var rendered = await new JsonRenderModule().RenderAsync(ParsedDataMappingTests.Context, mapped, pipeline.Deliveries[0].Render, default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(result.Previews[0].ActualJson!), JsonNode.Parse(Assert.Single(rendered.Payloads).GetText())));
        Assert.DoesNotContain("private-order-1", result.MappingJson);
    }

    [Fact]
    public async Task NestedObjectAndArrayTemplatesMatchAllScenarioShapes()
    {
        const string mapping = """{"fields":[{"kind":"Object","sourcePath":"/customer","targetPath":"/buyer","fields":[{"sourcePath":"/id","targetPath":"/code"}]},{"kind":"Array","sourcePath":"/lines","targetPath":"/items","fields":[{"sourcePath":"/sku","targetPath":"/product"}]}]}""";
        var (service, _, _) = Create(Response.Replace("{{MAPPING}}", mapping));
        var result = await service.AssistConnectionAsync(Request(
            Sample("populated", """{"customer":{"id":1},"lines":[{"sku":"a"},{"sku":"b"}]}""", """[{"buyer":{"code":1},"items":[{"product":"a"},{"product":"b"}]}]"""),
            Sample("empty", """{"customer":{"id":2},"lines":[]}""", """[{"buyer":{"code":2},"items":[]}]""")), default);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Previews.Count);
        Assert.All(result.Previews, preview => Assert.True(preview.Matches));
        Assert.NotNull(result.ProposalJson);
    }

    [Fact]
    public async Task AmplifiedPreviewOutputIsExplicitlyBlockedAndNotReturned()
    {
        var mapping = JsonSerializer.Serialize(new ParsedDataMap
        {
            Fields = Enumerable.Range(0, 150).Select(i => new ParsedDataFieldMap { SourcePath = "", TargetPath = "/copy" + i }).ToList()
        }, ParsedDataMapping.JsonOptions);
        var (service, _, _) = Create(Response.Replace("{{MAPPING}}", mapping));
        var result = await service.AssistConnectionAsync(Request(Sample(input: JsonSerializer.Serialize(new { value = new string('x', 1024) }))), default);
        var preview = Assert.Single(result.Previews);
        Assert.False(preview.Matches);
        Assert.Null(preview.ActualJson);
        Assert.Contains("131072", preview.Error);
        Assert.Null(result.ProposalJson);
    }

    [Fact]
    public void ShapesEscapeJsonPointersAndBoundPathAndValueCounts()
    {
        var analysis = ConnectionSampleAnalysis.Analyze([Sample(input: """{"a/b":{"~key":"x"}}""")]);
        Assert.Contains(analysis.InputShape, field => field.Path == "/a~1b/~0key");
        var tooManyFields = JsonSerializer.Serialize(Enumerable.Range(0, 256).ToDictionary(i => "f" + i, _ => 1));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample(input: tooManyFields)]));
        var manyValues = "{\"order\":[" + string.Join(',', Enumerable.Repeat("0", 12_000)) + "]}";
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample("one", manyValues), Sample("two", manyValues)]));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("route")]
    [InlineData("renderer")]
    [InlineData("augment")]
    [InlineData("extra-route")]
    public async Task UnsupportedScopeIsRejectedBeforeProviderCall(string invalid)
    {
        var (service, _, factory) = Create();
        var request = Request();
        var doc = JsonNode.Parse(request.DraftJson)!;
        if (invalid == "source") request = request with { SourceId = "other" };
        if (invalid == "route") request = request with { RouteIndex = 99 };
        if (invalid == "renderer") doc["deliveries"]![0]!["render"]!["module"] = "xml-render";
        if (invalid == "augment") doc["augments"]!.AsArray().Add(new JsonObject { ["module"] = "parsed-data-map-augment" });
        if (invalid == "extra-route") doc["deliveries"]!.AsArray().Add(doc["deliveries"]![0]!.DeepClone());
        request = request with { DraftJson = doc.ToJsonString() };
        await Assert.ThrowsAsync<ArgumentException>(() => service.AssistConnectionAsync(request, default));
        Assert.Equal(0, factory.Created);
    }

    [Theory]
    [InlineData("{\"reply\":\"bad\",\"proposal\":{\"fields\":null}}")]
    [InlineData("{\"reply\":\"bad\",\"proposal\":{\"fields\":[{\"kind\":\"Constant\",\"targetPath\":\"/x\",\"constantJson\":\"1\"}]}}")]
    [InlineData("{\"reply\":\"bad\",\"proposal\":{\"fields\":[{\"kind\":\"Transform\",\"targetPath\":\"/x\",\"transformModule\":\"arbitrary-plugin\"}]}}")]
    [InlineData("{\"reply\":\"bad\",\"proposal\":{\"fields\":[{\"targetPath\":\"/x\",\"sourceScope\":\"Root\"}]}}")]
    [InlineData("{\"reply\":\"bad\",\"proposal\":{\"deliveries\":[]}}")]
    public async Task ModelCannotEscapeMappingScopeOrExecutePlugins(string response)
    {
        var (service, _, _) = Create(response);
        var result = await service.AssistConnectionAsync(Request(), default);
        Assert.Null(result.ProposalJson);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.Previews);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void InvalidSampleDataIsRejected(string input)
        => Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample(input: input)]));

    [Fact]
    public void SampleBoundsAndUniqueNamesAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([]));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze(Enumerable.Range(0, 9).Select(i => Sample(i.ToString())).ToArray()));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample("same"), Sample(" SAME ")]));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample(input: new string('x', 32_769))]));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample(input: new string('[', 17) + "0" + new string(']', 17))]));
        Assert.Throws<ArgumentException>(() => ConnectionSampleAnalysis.Analyze([Sample(expected: "{}") ]));
    }
}
