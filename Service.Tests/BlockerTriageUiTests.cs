using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using FlowsPage = Mulse.Ui.Components.Pages.Flows;

namespace Service.Tests;

public sealed class BlockerTriageUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static FlowViewModel Flow(bool imported)
        => new("orders", false, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], [],
            Migration: imported ? new("BizTalk", "p", null, [], [], []) : null);

    private static BlockerTriageViewModel Report()
        => new(2, 2,
            [new("BT-CUSTOM-CODE", "needs-stub", "Blocker", "Custom <b>code</b>", "Listed, not translated.", "Implement it.", 2, 3, 1, ["orders", "billing"])],
            [new("needs-stub", "Needs a stub or formula implemented", 1, 2)],
            "2 imported flow(s) have blockers.", ["Groups come from codes."]);

    [Fact]
    public async Task NoTriageButtonIsShownWhenNothingWasImported()
    {
        await Render(imported: false, Report(), async (flows, rendered, handler) =>
        {
            Assert.DoesNotContain("data-testid=\"triage-open\"", rendered.ToHtmlString());
            Assert.Equal(0, handler.Requests);
        });
    }

    [Fact]
    public async Task TriageLoadsOnlyWhenAskedForAndShowsGroupsEscaped()
    {
        await Render(imported: true, Report(), async (flows, rendered, handler) =>
        {
            var before = rendered.ToHtmlString();
            Assert.Contains("data-testid=\"triage-open\"", before);
            Assert.Equal(0, handler.Requests);

            await Invoke(flows, "ShowTriageAsync");
            var html = rendered.ToHtmlString();

            Assert.Equal(1, handler.Requests);
            Assert.Contains("data-testid=\"blocker-triage\"", html);
            Assert.Contains("2 imported flow(s) have blockers.", html);
            Assert.Contains("Custom &lt;b&gt;code&lt;/b&gt;", html);
            Assert.Contains("Would unblock 1 flow(s) on its own.", html);
            Assert.Contains("3 times in all", html);
            Assert.Contains("Needs a stub or formula implemented: 1 group(s), 2 flow(s)", html);
        });
    }

    [Fact]
    public async Task AnUnavailableTriageIsAQuietHint()
    {
        await Render(imported: true, null, async (flows, rendered, _) =>
        {
            await Invoke(flows, "ShowTriageAsync");
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"triage-unavailable\"", html);
            var start = html.IndexOf("data-testid=\"blocker-triage\"", StringComparison.Ordinal);
            var panel = html[start..html.IndexOf("data-testid=\"triage-close\"", StringComparison.Ordinal)];
            Assert.DoesNotContain("status-banner", panel);
        });
    }

    [Fact]
    public async Task ClosingTheTriageBringsBackTheButton()
    {
        await Render(imported: true, Report(), async (flows, rendered, _) =>
        {
            await Invoke(flows, "ShowTriageAsync");
            await Invoke(flows, "CloseTriage");
            await Task.Yield();
            var html = rendered.ToHtmlString();

            Assert.DoesNotContain("data-testid=\"blocker-triage\"", html);
            Assert.Contains("data-testid=\"triage-open\"", html);
        });
    }

    private static Task Invoke(FlowsPage flows, string name, params object?[] args)
        => new EventCallback(flows, (Func<Task>)(() => typeof(FlowsPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(flows, args) as Task ?? Task.CompletedTask)).InvokeAsync();

    private static async Task Render(bool imported, BlockerTriageViewModel? report, Func<FlowsPage, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Flows = [Flow(imported)], Report = report };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<FlowsPage>(ParameterView.Empty);
            await test(Assert.Single(activator.Components.OfType<FlowsPage>()), rendered, handler);
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

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class StaticRenderJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public required FlowViewModel[] Flows { get; init; }

        public BlockerTriageViewModel? Report { get; init; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows" && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flows, options: Json) });
            if (path == "/api/assist/triage")
            {
                Requests++;
                return Task.FromResult(Report is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Report, options: Json) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
