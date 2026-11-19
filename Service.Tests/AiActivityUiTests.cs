using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class AiActivityUiTests
{
    private static AiActivityEntryViewModel Entry(string surface, string action, string outcome = "completed", string? flowId = null, bool? proposal = null, int? errors = null)
        => new(Guid.NewGuid(), new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero), "alice", surface, action, "OpenAI", "test-model", ["Structure"], flowId, 321, "ABC123", outcome, null, proposal, errors);

    [Fact]
    public async Task ShowsRecentRequestsOnlyOnDemandAndNeverAnyText()
    {
        var handler = new Handler([Entry("native-editor", "explain-flow-wording", flowId: "orders"), Entry("designer", "draft", proposal: true, errors: 2)]);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(new Activator()).AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<AiAssistance>();
            Assert.DoesNotContain("activity-list", root.ToHtmlString());
            Assert.DoesNotContain(handler.Requests, path => path.StartsWith("/api/assist/activity"));
            Assert.Contains("never the text sent", root.ToHtmlString());
        });
    }

    [Fact]
    public async Task LoadsTheListFilteredByFlowAndShowsOutcomes()
    {
        var handler = new Handler([Entry("native-editor", "explain-flow-wording", flowId: "orders"), Entry("designer", "draft", proposal: true, errors: 2)]);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator).AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<AiAssistance>();
            var component = Assert.Single(activator.Components.OfType<AiAssistance>());
            typeof(AiAssistance).GetField("_activityFlow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, " orders ");
            await (Task)typeof(AiAssistance).GetMethod("LoadActivityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, null)!;
            await component.SetParametersAsync(ParameterView.Empty);
            var html = root.ToHtmlString();
            Assert.Contains("data-testid=\"activity-list\"", html);
            Assert.Contains("explain-flow-wording (orders)", html);
            Assert.Contains("alice", html);
            Assert.Contains("321 chars", html);
            Assert.Contains("proposal (2 error(s))", html);
            Assert.Contains("does not show whether a proposal was later applied", html);
        });
        Assert.Equal("?take=50&flowId=orders", handler.ActivityQuery);
    }

    [Fact]
    public async Task SaysSoWhenNothingWasRecordedOrTheListCannotBeLoaded()
    {
        var handler = new Handler([]);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator).AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<AiAssistance>();
            var component = Assert.Single(activator.Components.OfType<AiAssistance>());
            var load = typeof(AiAssistance).GetMethod("LoadActivityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)load.Invoke(component, null)!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("activity-empty", root.ToHtmlString());

            handler.Fail = true;
            await (Task)load.Invoke(component, null)!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("activity-error", root.ToHtmlString());
        });
    }

    private sealed class Activator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class Handler(IReadOnlyList<AiActivityEntryViewModel> entries) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? ActivityQuery { get; private set; }
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == "/api/authoring/settings")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AiAuthoringSettingsViewModel(true, "OpenAI", "test-model", "https://example.test/v1", true, 4096)) });
            if (path == "/api/assist/activity")
            {
                ActivityQuery = request.RequestUri.Query;
                return Task.FromResult(Fail
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = JsonContent.Create(new { title = "Failed", detail = "The log is unavailable." }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(entries) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
