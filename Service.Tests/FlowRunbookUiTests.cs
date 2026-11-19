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

public sealed class FlowRunbookUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static FlowViewModel Flow()
        => new("orders", false, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], []);

    private static FlowRunbookViewModel Runbook()
        => new("orders", "Orders", [new("Overview", ["Flow: Orders"])], "# Runbook: Orders <b>x</b>\n\n- Flow: Orders\n", "It lists what the flow definition says.");

    [Fact]
    public async Task TheRunbookPanelIsClosedUntilAskedForAndShowsQuietTextWhenOpened()
    {
        await Render(Runbook(), async (flows, rendered, handler) =>
        {
            Assert.DoesNotContain("data-testid=\"flow-runbook\"", rendered.ToHtmlString());

            await Invoke(flows, "ShowRunbook", Single(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"flow-runbook\"", html);
            Assert.Contains("never setting values or secrets", html);
            Assert.DoesNotContain("data-testid=\"runbook-markdown\"", html);
            Assert.Equal(0, handler.Requests);
        });
    }

    [Fact]
    public async Task GeneratingSendsThePersonsTextAndShowsTheMarkdownEscaped()
    {
        await Render(Runbook(), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "ShowRunbook", Single(flows));
            await Invoke(flows, "GenerateRunbookAsync", Single(flows), new RunbookRequestViewModel("Sends orders", "ops", null));
            var html = rendered.ToHtmlString();

            Assert.Equal(1, handler.Requests);
            Assert.Contains("Sends orders", handler.LastBody);
            Assert.Contains("data-testid=\"runbook-markdown\"", html);
            Assert.Contains("# Runbook: Orders &lt;b&gt;x&lt;/b&gt;", html);
            Assert.Contains("It lists what the flow definition says.", html);
        });
    }

    [Fact]
    public async Task AnUnavailableRunbookIsAQuietHintAndNothingIsEnabledOrChanged()
    {
        await Render(null, async (flows, rendered, handler) =>
        {
            await Invoke(flows, "ShowRunbook", Single(flows));
            await Invoke(flows, "GenerateRunbookAsync", Single(flows), new RunbookRequestViewModel(null, null, null));
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"runbook-unavailable\"", html);
            Assert.DoesNotContain("status-banner error", html.Substring(html.IndexOf("data-testid=\"flow-runbook\"", StringComparison.Ordinal)));
            Assert.DoesNotContain("data-testid=\"runbook-markdown\"", html);
        });
    }

    [Fact]
    public async Task ClosingTheRunbookHidesIt()
    {
        await Render(Runbook(), async (flows, rendered, _) =>
        {
            await Invoke(flows, "ShowRunbook", Single(flows));
            await Invoke(flows, "CloseRunbook");
            await Task.Yield();

            Assert.DoesNotContain("data-testid=\"flow-runbook\"", rendered.ToHtmlString());
        });
    }

    private static FlowViewModel Single(FlowsPage flows) => ((IReadOnlyList<FlowViewModel>)typeof(FlowsPage).GetField("_flows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flows)!).Single();

    private static Task Invoke(FlowsPage flows, string name, params object?[] args)
        => new EventCallback(flows, (Func<Task>)(() => typeof(FlowsPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(flows, args) as Task ?? Task.CompletedTask)).InvokeAsync();

    private static async Task Render(FlowRunbookViewModel? runbook, Func<FlowsPage, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Runbook = runbook };
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
        public FlowRunbookViewModel? Runbook { get; init; }

        public int Requests { get; private set; }

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows" && request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Flow() }, options: Json) };
            if (path == "/api/assist/runbook/orders" && request.Method == HttpMethod.Post)
            {
                Requests++;
                LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Runbook is null
                    ? new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(Runbook, options: Json) };
            }

            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
        }
    }
}
