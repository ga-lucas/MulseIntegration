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
using ConfigPage = Mulse.Ui.Components.Pages.ConfigValues;

namespace Service.Tests;

public sealed class ConfigReviewUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ConfigReviewViewModel Review()
        => new(2, 2, 1, 1,
        [
            new("missing", "Blocker", "sftp-host", "'sftp-host' is used by 1 flow(s) but has no stored value <b>x</b>", "Cannot run.", ["sftp_host"], ["orders"], "Enter a value."),
            new("unused", "Info", "old-key", "'old-key' is not used by any saved flow", "Maybe old.", [], [], "Delete it if not needed.")
        ], ["Only names are shown."]);

    [Fact]
    public async Task TheCheckRunsOnlyWhenAskedForAndShowsNamesEscaped()
    {
        await Render(Review(), async (page, rendered, handler) =>
        {
            var before = rendered.ToHtmlString();
            Assert.Contains("data-testid=\"config-review-open\"", before);
            Assert.Equal(0, handler.Requests);

            await Invoke(page, "ShowReviewAsync");
            var html = rendered.ToHtmlString();

            Assert.Equal(1, handler.Requests);
            Assert.Contains("1 missing, 1 unused, 2 finding(s) in all", html);
            Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
            Assert.Contains("Similar names: sftp_host", html);
            Assert.Contains("Flows: orders", html);
            Assert.Contains("Only names are shown.", html);
        });
    }

    [Fact]
    public async Task ACleanCheckSaysSoPlainly()
    {
        await Render(new(3, 3, 0, 0, [], []), async (page, rendered, _) =>
        {
            await Invoke(page, "ShowReviewAsync");

            Assert.Contains("Nothing to report for 3 stored value(s) and 3 referenced name(s).", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task AnUnavailableCheckIsAQuietHintAndThePageStillWorks()
    {
        await Render(null, async (page, rendered, _) =>
        {
            await Invoke(page, "ShowReviewAsync");
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"config-review-unavailable\"", html);
            Assert.Contains("Stored values", html);
        });
    }

    [Fact]
    public async Task ClosingTheCheckBringsBackTheButton()
    {
        await Render(Review(), async (page, rendered, _) =>
        {
            await Invoke(page, "ShowReviewAsync");
            await Invoke(page, "CloseReview");
            await Task.Yield();
            var html = rendered.ToHtmlString();

            Assert.Contains("data-testid=\"config-review-open\"", html);
            Assert.DoesNotContain("config-review-summary", html);
        });
    }

    private static Task Invoke(ConfigPage page, string name)
        => new EventCallback(page, (Func<Task>)(() => typeof(ConfigPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null) as Task ?? Task.CompletedTask)).InvokeAsync();

    private static async Task Render(ConfigReviewViewModel? review, Func<ConfigPage, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Review = review };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ConfigPage>(ParameterView.Empty);
            await test(Assert.Single(activator.Components.OfType<ConfigPage>()), rendered, handler);
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
        public ConfigReviewViewModel? Review { get; init; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/workspace/encryption")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RuntimeEncryptionViewModel(false, true, "MULSE_KEY", null), options: Json) });
            if (path == "/api/assist/config-review")
            {
                Requests++;
                return Task.FromResult(Review is null
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Review, options: Json) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
