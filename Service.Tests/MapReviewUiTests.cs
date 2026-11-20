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

public sealed class MapReviewUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static FlowViewModel Flow(bool withMaps, bool imported = true)
        => new("orders", false, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], [],
            Migration: imported ? new("BizTalk", "p", null, withMaps ? [new("Map", "a.btm", "ReviewRequired", [])] : [], [], []) : null);

    private static MapReviewViewModel Report()
        => new("orders", "Orders", 1, 1, 0, 0,
            [new("Orders <b>map</b>", "ReviewRequired", 2, 0, 3, 1,
                [new("functoid-links", "Blocker", "3 link(s) go through functoids", "Left out.", ["type 101: 3 use(s)"], "Rebuild them.")])],
            ["A remark."], ["Links."], ["Field meaning."], "Not proof.");

    [Fact]
    public async Task NoButtonIsShownWithoutMaps()
    {
        await Render(Flow(withMaps: false), Report(), async (flows, rendered, handler) =>
        {
            Assert.DoesNotContain("data-testid=\"map-review-open\"", rendered.ToHtmlString());
            Assert.Equal(0, handler.Requests);
        });
        await Render(Flow(withMaps: false, imported: false), Report(), (flows, rendered, _) =>
        {
            Assert.DoesNotContain("data-testid=\"map-review-open\"", rendered.ToHtmlString());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TheChecklistLoadsOnlyWhenAskedForAndIsEscaped()
    {
        await Render(Flow(withMaps: true), Report(), async (flows, rendered, handler) =>
        {
            Assert.Contains("data-testid=\"map-review-open\"", rendered.ToHtmlString());
            Assert.Equal(0, handler.Requests);

            await Invoke(flows, "ShowMapReviewAsync", FlowsPageFlow(flows));
            var html = rendered.ToHtmlString();

            Assert.Equal(1, handler.Requests);
            Assert.Contains("data-testid=\"map-review\"", html);
            Assert.Contains("1 map(s): 1 blocker(s), 0 warning(s), 0 note(s).", html);
            Assert.Contains("Orders &lt;b&gt;map&lt;/b&gt;", html);
            Assert.Contains("type 101: 3 use(s)", html);
            Assert.Contains("Rebuild them.", html);
            Assert.Contains("Not proof.", html);
        });
    }

    [Fact]
    public async Task AnUnavailableChecklistIsAQuietHint()
    {
        await Render(Flow(withMaps: true), null, async (flows, rendered, _) =>
        {
            await Invoke(flows, "ShowMapReviewAsync", FlowsPageFlow(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"map-review-unavailable\"", html);
            var start = html.IndexOf("data-testid=\"map-review\"", StringComparison.Ordinal);
            var panel = html[start..html.IndexOf("data-testid=\"map-review-close\"", StringComparison.Ordinal)];
            Assert.DoesNotContain("status-banner", panel);
        });
    }

    [Fact]
    public async Task ClosingTheChecklistRemovesIt()
    {
        await Render(Flow(withMaps: true), Report(), async (flows, rendered, _) =>
        {
            await Invoke(flows, "ShowMapReviewAsync", FlowsPageFlow(flows));
            await Invoke(flows, "CloseMapReview");
            await Task.Yield();

            Assert.DoesNotContain("data-testid=\"map-review\"", rendered.ToHtmlString());
        });
    }

    private static FlowViewModel FlowsPageFlow(FlowsPage flows)
    {
        var field = typeof(FlowsPage).GetFields(BindingFlags.NonPublic | BindingFlags.Instance).First(item => item.FieldType == typeof(IReadOnlyList<FlowViewModel>));
        return ((IReadOnlyList<FlowViewModel>)field.GetValue(flows)!).Single();
    }

    private static Task Invoke(FlowsPage flows, string name, params object?[] args)
        => new EventCallback(flows, (Func<Task>)(() => typeof(FlowsPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(flows, args) as Task ?? Task.CompletedTask)).InvokeAsync();

    private static async Task Render(FlowViewModel flow, MapReviewViewModel? report, Func<FlowsPage, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Flows = [flow], Report = report };
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

        public MapReviewViewModel? Report { get; init; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows" && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flows, options: Json) });
            if (path == "/api/assist/map-review/orders")
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
