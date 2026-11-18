using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Mapping;
using Service.BizTalkImport;
using Service.ConfigValues;
using Service.Models;

namespace Service.Tests;

public sealed class StoredMapChoiceTests
{
    private static readonly FlowExecutionContext Context = new("stored", "test", DateTimeOffset.UtcNow);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Resolver_KeepsStoredReferencesInDispatchChoicesAndStillReportsMissingOnes()
    {
        var cases = JsonSerializer.Serialize(new[]
        {
            new RenderDispatchCase { Value = "a", Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = "{{config:biztalk-map.a}}" } },
            new RenderDispatchCase { Value = "b", Module = "xslt-render", Settings = new() { ["xsltText"] = "{{config:biztalk-map.b}}" } }
        }, Web);
        var missing = new List<string>();

        var kept = ConfigValueResolver.SubstituteJson(cases, new Dictionary<string, string> { ["biztalk-map.a"] = "BIG-A" }, missing.Add, deferStored: true);

        Assert.Equal(["biztalk-map.b"], missing);
        Assert.Contains("{{config:biztalk-map.a}}", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("BIG-A", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void LazyResolution_ExpandsOnlyStoredDefinitionKeysAndFailsClosedWhenMissing()
    {
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{{config:biztalk-map.a}}", ["other"] = "{{config:biztalk-map.a}}" };

        Assert.True(ConfigValueResolver.HasStoredDefinitionReference(settings));
        Assert.False(ConfigValueResolver.HasStoredDefinitionReference(new Dictionary<string, string> { ["mappingJson"] = "{ \"inline\": true }", ["other"] = "{{config:x}}" }));
        Assert.False(ConfigValueResolver.HasStoredDefinitionReference(new Dictionary<string, string> { ["xsltText"] = "prefix {{config:x}}" }));

        var resolved = ConfigValueResolver.ResolveStoredDefinitions(settings, reference => reference == "biztalk-map.a" ? "BIG" : null);
        Assert.Equal("BIG", resolved["mappingJson"]);
        Assert.Equal("{{config:biztalk-map.a}}", resolved["other"]);
        Assert.Throws<InvalidOperationException>(() => ConfigValueResolver.ResolveStoredDefinitions(settings, _ => null));
        Assert.Throws<ArgumentException>(() => ConfigValueResolver.ResolveStoredDefinitions(settings, _ => new string('x', 1_000_001)));
    }

    [Fact]
    public async Task Invoker_LoadsOnlyTheSelectedStoredMapAtRenderTime_AndFailsClosedWithoutAValue()
    {
        var seen = new List<string>();
        var catalog = new FakeExecutableModuleCatalog().AddRender(new DelegateRenderModule("xml-mapping-render", (_, batch, step, _) =>
        {
            seen.Add(step.Settings["mappingJson"]);
            return Task.FromResult(batch);
        }));
        var state = new RuntimeMulseState();
        state.ConfigValues["biztalk-map.a"] = "STORED-A";
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(state)).BuildServiceProvider();
        var invoker = new Service.Modules.RenderModuleInvoker(services);
        var batch = new IntegrationBatch([new IntegrationPayload("p", BinaryData.FromString("<a/>"), "application/xml", new Dictionary<string, string>())]);

        await invoker.RenderAsync("xml-mapping-render", Context, batch, new Dictionary<string, string> { ["mappingJson"] = "{{config:biztalk-map.a}}" }, default);
        await invoker.RenderAsync("xml-mapping-render", Context, batch, new Dictionary<string, string> { ["mappingJson"] = "{ inline }" }, default);

        Assert.Equal(["STORED-A", "{ inline }"], seen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.RenderAsync("xml-mapping-render", Context, batch,
            new Dictionary<string, string> { ["mappingJson"] = "{{config:biztalk-map.missing}}" }, default));
        using var withoutStore = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Service.Modules.RenderModuleInvoker(withoutStore).RenderAsync("xml-mapping-render", Context, batch,
            new Dictionary<string, string> { ["mappingJson"] = "{{config:biztalk-map.a}}" }, default));
    }

    [Fact]
    public void StoredName_IsDeterministicSafeAndDistinctPerFullValue()
    {
        var one = BizTalkNativeMapChoiceDeriver.StoredName("Acme.Maps.Order To/Result!, Acme, Version=1.0.0.0");
        var two = BizTalkNativeMapChoiceDeriver.StoredName("Acme.Maps.Order To/Result!, Acme, Version=2.0.0.0");

        Assert.Equal(one, BizTalkNativeMapChoiceDeriver.StoredName("Acme.Maps.Order To/Result!, Acme, Version=1.0.0.0"));
        Assert.NotEqual(one, two);
        Assert.Matches("^biztalk-map\\.OrderToResult-[0-9a-f]{10}$", one);
        Assert.Matches("^biztalk-map\\.map-[0-9a-f]{10}$", BizTalkNativeMapChoiceDeriver.StoredName("..."));
    }

    [Fact]
    public void SharedFlowTableKeepsStoredReferencesDeferredAndStillRequiresThemToExist()
    {
        const string token = "{{config:biztalk-map.a-0123456789}}";
        var choices = JsonSerializer.Serialize(new[] { new Mulse.Modules.Mapping.RenderDispatchCase { Value = "a", Module = "xslt-render", Settings = new() { ["xsltText"] = token } } }, Web);
        var flowJson = JsonSerializer.Serialize(new { version = 1, durable = true, renderChoicesJson = choices, nodes = Array.Empty<object>() }, Web);
        var flow = new PipelineDefinition { Id = "shared", Augments = [new() { Module = "native-control-flow-augment", Settings = new() { ["flowJson"] = flowJson } }] };
        var state = new RuntimeMulseState { ConfigValues = { ["biztalk-map.a-0123456789"] = "<stylesheet/>" } };

        var resolved = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state)).Resolve(flow);

        var resolvedChoices = JsonSerializer.Deserialize<JsonElement>(resolved.Augments[0].Settings["flowJson"]).GetProperty("renderChoicesJson").GetString()!;
        Assert.Equal(token, Assert.Single(Mulse.Modules.Mapping.RenderDispatchDefinition.Read(resolvedChoices)).Settings["xsltText"]);
        state.ConfigValues.Clear();
        Assert.Throws<InvalidOperationException>(() => new ConfigValueResolver(new FakeRuntimeConfigurationStore(state)).Resolve(flow));
    }

    [Fact]
    public void Candidate_KeepsOnlyTheStoredValuesItsFlowReferences()
    {
        var flow = "{\"nodes\":[{\"settings\":{\"casesJson\":\"[{\\\"value\\\":\\\"a\\\",\\\"settings\\\":{\\\"mappingJson\\\":\\\"{{config:biztalk-map.a-0123456789}}\\\"}}]\"}}]}";
        var draft = new BizTalkDraftFlowResponse("f", false, new(PipelineTriggerMode.OnDemand, null, false), [], [],
            [new("native-control-flow-augment", new Dictionary<string, string> { ["flowJson"] = flow })], [], [], []);
        var candidate = new BizTalkFlowCandidateResponse("f", "p", "s", "", "", [], [], [], [], [], draft);
        var stored = new Dictionary<string, string> { ["biztalk-map.a-0123456789"] = "A", ["biztalk-map.z-9999999999"] = "Z" };

        var attached = BizTalkImportService.AttachStoredConfigValues(candidate, stored);

        Assert.Equal(new[] { "biztalk-map.a-0123456789" }, attached.StoredConfigValues.Keys);
        Assert.Contains(attached.DraftFlow.Warnings, warning => warning.Contains("1 converted map(s)", StringComparison.Ordinal));
        Assert.Same(candidate, BizTalkImportService.AttachStoredConfigValues(candidate, new Dictionary<string, string>()));
        Assert.Same(candidate, BizTalkImportService.AttachStoredConfigValues(candidate, new Dictionary<string, string> { ["biztalk-map.other"] = "x" }));
        Assert.DoesNotContain("StoredConfigValues", JsonSerializer.Serialize(attached, Web), StringComparison.Ordinal);
    }
}
