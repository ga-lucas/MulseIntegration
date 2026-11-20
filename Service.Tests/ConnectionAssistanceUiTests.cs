using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class ConnectionAssistanceUiTests
{
    private const string Draft = """{"id":"orders","enabled":true,"triggerMode":"OnDemand","fetchIds":[],"parseIds":[],"fetchModules":[],"parseModules":[],"augmentModules":[],"renderModules":[],"deliverModules":[],"fetches":[],"parses":[{"id":"parsed","parse":{"module":"json-parse","settings":{}},"fetchNodeIds":[]}],"augments":[],"deliveries":[{"render":{"module":"json-render","settings":{}},"delivers":[]}]}""";

    [Fact]
    public async Task AssistantOffersConnectionModeWithoutAutomaticApiRequests()
    {
        var handler = new Handler();
        await Render(async (renderer, activator) =>
        {
            var rendered = await renderer.RenderComponentAsync<FlowAssistant>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FlowAssistant.CaptureDraft)] = (Func<Task<string>>)(() => Task.FromResult(Draft))
            }));
            Assert.Contains("Assist a connection with sample data", rendered.ToHtmlString());
            var assistant = Assert.Single(activator.Components.OfType<FlowAssistant>());
            Set(assistant, "_connectionMode", true);
            await assistant.SetParametersAsync(ParameterView.Empty);
            Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Assert.Contains("Analyze samples locally", rendered.ToHtmlString());
            Assert.DoesNotContain("Start assistant and share redacted draft", rendered.ToHtmlString());
            Assert.Empty(handler.Paths);
        }, handler);
    }

    [Fact]
    public async Task AnalysisReviewGenerationAndExplicitApplyUseTheSameSnapshotAndPerSampleConsent()
    {
        var handler = new Handler();
        FlowAuthoringProposal? applied = null;
        await Render(async (renderer, activator) =>
        {
            var rendered = await RenderConnection(renderer, proposal => applied = proposal);
            var component = Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Assert.Empty(handler.Paths);
            var sample = FirstSample(component);
            Assert.False((bool)Field(sample, "ShareInput")!);
            Assert.False((bool)Field(sample, "ShareExpected")!);
            Populate(component);
            Set(sample, "ShareExpected", true);
            await InvokeAsync(component, "AnalyzeAsync");
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Equal(["/api/authoring/connection/analyze", "/api/authoring/settings"], handler.Paths);
            Assert.Contains("Exact shape/example context", rendered.ToHtmlString());
            Assert.Null(Field(component, "_result"));

            await InvokeAsync(component, "GenerateAsync");
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Equal(["/api/authoring/connection/analyze", "/api/authoring/settings", "/api/authoring/connection"], handler.Paths);
            Assert.Equal(handler.AnalysisRequest, handler.GenerationRequest);
            var sent = JsonSerializer.Deserialize<ConnectionAssistanceRequestViewModel>(handler.GenerationRequest!, JsonSerializerOptions.Web)!;
            Assert.False(sent.Samples[0].ShareInputValues);
            Assert.True(sent.Samples[0].ShareExpectedValues);
            Assert.Equal(Draft, sent.DraftJson);
            Assert.Contains("Matches expected JSON", rendered.ToHtmlString());
            Assert.Null(applied);
            await InvokeAsync(component, "ApplyAsync");
            Assert.Null(applied);
            Set(component, "_approved", true);
            await InvokeAsync(component, "ApplyAsync");
            Assert.NotNull(applied);
            Assert.Equal(Draft, applied.BaseDraftJson);
            Assert.Equal(Draft, applied.ProposalJson);
            Assert.Null(Field(component, "_result"));
        }, handler);
    }

    [Fact]
    public async Task ChangedDraftBlocksGenerationAndStaleApply()
    {
        var handler = new Handler();
        var current = Draft;
        FlowAuthoringProposal? applied = null;
        await Render(async (renderer, activator) =>
        {
            await RenderConnection(renderer, proposal => applied = proposal, () => current);
            var component = Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Populate(component);
            await InvokeAsync(component, "AnalyzeAsync");
            current = Draft.Replace("orders", "changed");
            await InvokeAsync(component, "GenerateAsync");
            Assert.Equal(["/api/authoring/connection/analyze", "/api/authoring/settings"], handler.Paths);
            Assert.Contains("changed", (string)Field(component, "_error")!);
            current = Draft;
            await InvokeAsync(component, "GenerateAsync");
            current = Draft.Replace("orders", "changed-again");
            Set(component, "_approved", true);
            await InvokeAsync(component, "ApplyAsync");
            Assert.Null(applied);
            Assert.Contains("draft changed", (string)Field(component, "_error")!);
        }, handler);
    }

    [Fact]
    public async Task EditingSamplesInvalidatesDisclosureAndClearingPanelRemovesSamples()
    {
        var handler = new Handler();
        await Render(async (renderer, activator) =>
        {
            await RenderConnection(renderer);
            var component = Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Populate(component);
            await InvokeAsync(component, "AnalyzeAsync");
            Assert.NotNull(Field(component, "_analysis"));
            Invoke(component, "AddSample");
            Assert.Equal(2, ((IList)Field(component, "_samples")!).Count);
            Assert.Null(Field(component, "_analysis"));
            await InvokeAsync(component, "GenerateAsync");
            Assert.Equal(["/api/authoring/connection/analyze", "/api/authoring/settings"], handler.Paths);
            component.Dispose();
            Assert.Empty((IList)Field(component, "_samples")!);
            Assert.Null(Field(component, "_reviewed"));
        }, handler);
    }

    [Fact]
    public async Task MismatchedPreviewCannotBeAppliedEvenIfResponseIncludesAProposal()
    {
        var handler = new Handler { Matches = false };
        FlowAuthoringProposal? applied = null;
        await Render(async (renderer, activator) =>
        {
            await RenderConnection(renderer, proposal => applied = proposal);
            var component = Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Populate(component);
            await InvokeAsync(component, "AnalyzeAsync");
            await InvokeAsync(component, "GenerateAsync");
            Set(component, "_approved", true);
            await InvokeAsync(component, "ApplyAsync");
            Assert.Null(applied);
            Assert.Contains("Every scenario must match", (string)Field(component, "_error")!);
        }, handler);
    }

    [Fact]
    public async Task ClosingPanelCancelsAnalysisAndDoesNotRetainLateResults()
    {
        var handler = new Handler { BlockAnalysis = true };
        await Render(async (renderer, activator) =>
        {
            await RenderConnection(renderer);
            var component = Assert.Single(activator.Components.OfType<ConnectionAssistant>());
            Populate(component);
            var pending = InvokeAsync(component, "AnalyzeAsync");
            await handler.AnalysisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            component.Dispose();
            await pending;
            Assert.True(handler.AnalysisCancelled);
            Assert.Null(Field(component, "_analysis"));
            Assert.Null(Field(component, "_reviewed"));
            Assert.Empty((IList)Field(component, "_samples")!);
        }, handler);
    }

    private static async Task Render(Func<HtmlRenderer, CapturingActivator, Task> test, Handler handler)
    {
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(() => test(renderer, activator));
    }

    private static Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderConnection(HtmlRenderer renderer,
        Action<FlowAuthoringProposal>? apply = null, Func<string>? draft = null)
        => renderer.RenderComponentAsync<ConnectionAssistant>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(ConnectionAssistant.CaptureDraft)] = (Func<Task<string>>)(() => Task.FromResult(draft?.Invoke() ?? Draft)),
            [nameof(ConnectionAssistant.Apply)] = EventCallback.Factory.Create<FlowAuthoringProposal>(new object(), proposal => apply?.Invoke(proposal))
        }));

    private static void Populate(ConnectionAssistant component)
    {
        Set(component, "_instructions", "Copy order to invoice");
        var sample = FirstSample(component);
        Set(sample, "Input", "{\"order\":1}");
        Set(sample, "Expected", "[{\"invoice\":1}]");
    }
    private static object FirstSample(ConnectionAssistant component) => ((IList)Field(component, "_samples")!)[0]!;
    private static FieldInfo Info(object target, string field) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static object? Field(object target, string field) => Info(target, field).GetValue(target);
    private static void Set(object target, string field, object value) => Info(target, field).SetValue(target, value);
    private static object? Invoke(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static Task InvokeAsync(object target, string method) => (Task)Invoke(target, method)!;

    private sealed class CapturingActivator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string? AnalysisRequest, GenerationRequest;
        public bool Matches = true, BlockAnalysis, AnalysisCancelled;
        public TaskCompletionSource AnalysisStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/authoring/connection/analyze")
            {
                AnalysisRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                AnalysisStarted.TrySetResult();
                if (BlockAnalysis)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AnalysisCancelled = true; throw; }
                }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ConnectionAnalysisViewModel(
                    [new("/order", ["number"], false, false)], [new("/invoice", ["number"], false, false)], [], "{\"inputShape\":[],\"outputShape\":[]}")) };
            }
            if (path == "/api/authoring/connection")
            {
                GenerationRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ConnectionAssistanceResponseViewModel("Copy the field", "{}", Draft, [],
                    [new("Scenario 1", Matches, "[{\"invoice\":1}]", Matches ? null : "Different output")])) };
            }
            if (path == "/api/authoring/settings")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AiAuthoringSettingsViewModel(true, "OpenAI", "m", "", true, 2048)) };
            throw new InvalidOperationException("Unexpected API request: " + path);
        }
    }
}
