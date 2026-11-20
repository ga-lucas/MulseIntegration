using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using FlowsPage = Mulse.Ui.Components.Pages.Flows;

namespace Service.Tests;

public sealed class FlowReviewUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static FlowViewModel Flow(bool enabled = false)
        => new("orders", enabled, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], []);

    private static ReviewItemViewModel Warning(string code = "no-retry", string title = "1 delivery step(s) have no automatic retry")
        => new(code, "Warning", title, "A short outage fails the run.", ["Deliver 1.1 (sftp)"], "Set a retry policy.");

    private static FlowReviewViewModel Review(params ReviewItemViewModel[] items)
        => new("orders", "Orders", false, items.Count(item => item.Severity == "Blocker"), items.Count(item => item.Severity == "Warning"), items.Count(item => item.Severity == "Info"),
            [.. items], ["Passwords are not typed into plain settings."], ["Whether the mappings do what the business needs."], "These are rules applied to the flow definition.");

    [Fact]
    public async Task ReviewShowsFindingsWhereToFixAndWhatWasNotChecked()
    {
        var review = Review(Warning(), new("unlimited-retry", "Info", "Retry <b>without</b> a limit", "Forever.", [], "Set a maximum."));
        await Render(review, async (flows, rendered, handler) =>
        {
            await Invoke(flows, "ShowReviewAsync", Single(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"flow-review\"", html);
            Assert.Contains("0 blocker(s), 1 warning(s), 1 note(s).", html);
            Assert.Contains("1 delivery step(s) have no automatic retry", html);
            Assert.Contains("Deliver 1.1 (sftp)", html);
            Assert.Contains("To fix:", html);
            Assert.Contains("Whether the mappings do what the business needs.", html);
            Assert.Contains("These are rules applied to the flow definition.", html);
            Assert.Contains("Retry &lt;b&gt;without&lt;/b&gt; a limit", html);
            Assert.DoesNotContain("data-testid=\"review-enable\"", html);
            Assert.Equal(0, handler.Enables);
        });
    }

    [Fact]
    public async Task EnablingShowsTheReviewFirstAndEnablesOnlyWhenThePersonConfirms()
    {
        await Render(Review(Warning()), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("Review before enabling", html);
            Assert.Contains("data-testid=\"review-enable\"", html);
            Assert.Contains("Enable anyway", html);
            Assert.Equal(0, handler.Enables);

            await Invoke(flows, "EnableAfterReviewAsync", Single(flows));

            Assert.Equal(1, handler.Enables);
            Assert.DoesNotContain("data-testid=\"flow-review\"", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task ACleanReviewEnablesWithoutAnExtraStep()
    {
        await Render(Review(), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));

            Assert.Equal(1, handler.Enables);
            Assert.Equal(1, handler.Reviews);
            Assert.DoesNotContain("data-testid=\"flow-review\"", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task CancellingTheReviewLeavesTheFlowDisabled()
    {
        await Render(Review(Warning()), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));
            await Invoke(flows, "CloseReview");

            Assert.Equal(0, handler.Enables);
            Assert.DoesNotContain("data-testid=\"flow-review\"", await Rerender(flows, rendered));
        });
    }

    [Fact]
    public async Task ABlockerRemovesTheEnableChoice()
    {
        var blocker = new ReviewItemViewModel("unresolved-reference", "Blocker", "'vault' has no stored value", "Cannot be enabled.", ["Deliver 1.1 (sftp)"], "Enter it.");
        await Render(Review(blocker), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("1 blocker(s)", html);
            Assert.DoesNotContain("data-testid=\"review-enable\"", html);
            Assert.Contains("data-testid=\"review-close\"", html);
            Assert.Equal(0, handler.Enables);
        });
    }

    [Fact]
    public async Task WhenTheReviewIsUnavailableThePersonStillDecidesAndNothingIsEnabledAutomatically()
    {
        await Render(null, async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"review-unavailable\"", html);
            Assert.Contains("nothing was checked", html);
            Assert.Contains("data-testid=\"review-enable\"", html);
            Assert.Equal(0, handler.Enables);

            await Invoke(flows, "EnableAfterReviewAsync", Single(flows));
            Assert.Equal(1, handler.Enables);
        });
    }

    [Fact]
    public async Task DisablingAnEnabledFlowDoesNotNeedAReview()
    {
        await Render(Review(Warning()), async (flows, rendered, handler) =>
        {
            await Invoke(flows, "EnableOrDisableAsync", Single(flows));
            await Invoke(flows, "CloseReview");
            handler.Flows = [Flow(enabled: true)];
            await Invoke(flows, "LoadAsync");
            var reviewsBefore = handler.Reviews;

            await Invoke(flows, "EnableOrDisableAsync", Single(flows));

            Assert.Equal(reviewsBefore, handler.Reviews);
            Assert.Equal(1, handler.Disables);
        });
    }

    private static FlowViewModel Single(FlowsPage flows) => ((IReadOnlyList<FlowViewModel>)typeof(FlowsPage).GetField("_flows", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(flows)!).Single();

    private static Task Invoke(FlowsPage flows, string name, params object?[] args)
        => new EventCallback(flows, (Func<Task>)(() => typeof(FlowsPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(flows, args) as Task ?? Task.CompletedTask)).InvokeAsync();

    private static async Task<string> Rerender(FlowsPage flows, HtmlRootComponent rendered)
    {
        await Task.Yield();
        return rendered.ToHtmlString();
    }

    private static async Task Render(FlowReviewViewModel? review, Func<FlowsPage, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Flows = [Flow()], Review = review };
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
        public required FlowViewModel[] Flows { get; set; }

        public FlowReviewViewModel? Review { get; init; }

        public int Reviews { get; private set; }

        public int Enables { get; private set; }

        public int Disables { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/flows" && request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flows, options: Json) });
            if (path == "/api/assist/review/orders")
            {
                Reviews++;
                return Task.FromResult(Review is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Review, options: Json) });
            }

            if (path == "/api/flows/orders/enabled" && request.Method == HttpMethod.Patch)
            {
                var enable = request.Content!.ReadAsStringAsync(cancellationToken).Result.Contains("true", StringComparison.OrdinalIgnoreCase);
                if (enable) Enables++; else Disables++;
                Flows = [Flow(enable)];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flows[0], options: Json) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
