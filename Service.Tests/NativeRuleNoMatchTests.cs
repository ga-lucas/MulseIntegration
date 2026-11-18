using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Service.BizTalkImport;

namespace Service.Tests;

public sealed class NativeRuleNoMatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-nomatch-" + Guid.NewGuid().ToString("N"));

    private string PolicyJson()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "rules.xml");
        File.WriteAllText(path, BizTalkRulePolicyTests.Export(BizTalkRulePolicyTests.Policy(1, 0)));
        return Assert.IsType<string>(Assert.Single(BizTalkRulePolicyAnalyzer.Analyze(path)).ProposedPolicyJson);
    }

    private static IntegrationBatch Message(string group) => new([new("facts.xml", BinaryData.FromString(BizTalkRulePolicyTests.Facts(group, "true")), "application/xml",
        new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);

    private async Task<IntegrationBatch> Run(string group, bool stop)
    {
        var flow = new NativeFlowDefinition
        {
            Durable = true,
            Nodes =
            [
                new() { Id = "save", Kind = NativeFlowNodeKind.SaveMessage, MessageName = "facts" },
                new() { Id = "rules", Kind = NativeFlowNodeKind.ApplyXmlRules, MessageName = "facts", PolicyJson = PolicyJson(), StopWhenNoRuleMatches = stop },
                new() { Id = "use", Kind = NativeFlowNodeKind.UseMessage, MessageName = "facts" }
            ]
        };
        var module = new Mulse.Modules.BuiltIn.Augment.NativeControlFlowAugmentModule(null!, null!, new InMemoryFlowOrchestrationStateStore(), TimeProvider.System, null);
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) } };
        return await module.AugmentAsync(new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), Message(group), step, default);
    }

    [Fact]
    public async Task ApplyXmlRulesContinuesUnchangedByDefaultAndStopsWhenAskedTo()
    {
        Assert.DoesNotContain("Map.", (await Run("999", stop: false)).Payloads[0].GetText());
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Run("999", stop: true));
        Assert.Contains("No XML rule matched", failure.ToString());
        Assert.Contains("Map.A", (await Run("001", stop: true)).Payloads[0].GetText());
    }

    [Fact]
    public void TheFlagIsAdditiveAndOnlyValidOnApplyXmlRules()
    {
        var policy = PolicyJson();
        var plain = new NativeFlowDefinition { Durable = true, Nodes = [new() { Id = "r", Kind = NativeFlowNodeKind.ApplyXmlRules, MessageName = "facts", PolicyJson = policy }] };
        Assert.DoesNotContain("stopWhenNoRuleMatches", JsonSerializer.Serialize(plain, JsonSerializerOptions.Web), StringComparison.OrdinalIgnoreCase);

        plain.Nodes[0].StopWhenNoRuleMatches = true;
        var json = JsonSerializer.Serialize(plain, JsonSerializerOptions.Web);
        Assert.True(NativeFlowDefinition.Read(json, allowBlocked: true).Nodes[0].StopWhenNoRuleMatches);

        var misplaced = new NativeFlowDefinition { Durable = true, Nodes = [new() { Id = "s", Kind = NativeFlowNodeKind.Sequence, StopWhenNoRuleMatches = true }] };
        Assert.Contains("only on ApplyXmlRules", Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(misplaced, JsonSerializerOptions.Web))).Message);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Stop", false)]
    [InlineData("Continue", true)]
    public async Task TheRulesAugmentModuleStopsByDefaultAndContinuesOnlyWhenConfigured(string? setting, bool continues)
    {
        var module = new XmlRulesAugmentModule();
        var step = new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = new() { ["policyJson"] = PolicyJson() } };
        if (setting is not null) step.Settings["onNoMatch"] = setting;
        var context = new FlowExecutionContext("rules", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        if (continues)
            Assert.Single((await module.AugmentAsync(context, Message("999"), step, default)).Payloads);
        else
            await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(context, Message("999"), step, default));
        step.Settings["onNoMatch"] = "Maybe";
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(context, Message("001"), step, default));
    }

    [Fact]
    public async Task EditorShowsTheNoMatchChoiceAndExplainsBothSettings()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Mulse.Ui.Services.IMulseApiClient>(new Mulse.Ui.Services.MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(bool stop)
        {
            var flow = new NativeFlowDefinition { Durable = true, Nodes = [new() { Id = "rules", Kind = NativeFlowNodeKind.ApplyXmlRules, MessageName = "facts", PolicyJson = PolicyJson(), StopWhenNoRuleMatches = stop }] };
            return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Mulse.Ui.Components.Shared.NativeFlowEditor>(
                Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) }))).ToHtmlString());
        }
        var continuing = await Render(false);
        Assert.Contains("Stop when no rule matches", continuing); Assert.Contains("left unchanged and the flow continues", continuing);
        Assert.Contains("fails the flow here", await Render(true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
