using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Ai;
using Service.Endpoints;
using ConnectionAssistantComponent = Mulse.Ui.Components.Shared.ConnectionAssistant;
using AiAssistancePage = Mulse.Ui.Components.Pages.AiAssistance;

namespace Service.Tests;

/// <summary>Everything except the AI send paths must work with no AI configured at all, and a broken AI settings file must never take local features down with it.</summary>
public sealed class NoAiSetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-no-ai-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestServer(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private async Task<TestServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = Directory.CreateDirectory(_directory).FullName });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAuthoring:SettingsDirectory"] = Path.Combine(_directory, "settings"),
            ["AiAuthoring:ActivityDirectory"] = Path.Combine(_directory, "activity")
        });
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddAiAuthoring(builder.Configuration, builder.Environment);
        builder.Services.AddAssist();
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAiAuthoringEndpoints();
        app.MapAssistEndpoints();
        await app.StartAsync();
        return new TestServer(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static object Evidence(string? sha = null) => new
    {
        operation = "explain",
        request = new { id = "bindings", title = "Export the deployed bindings", why = "Ports.", howToGet = new[] { "Open the console." }, impact = new[] { "billing-send" }, risk = "Passwords.", accepts = new[] { "Bindings" } },
        question = (string?)null,
        evidence = Array.Empty<object>(),
        includeContent = false,
        disclosureSha256 = sha
    };

    private static object Compare(string? sha = null) => new
    {
        samples = new[] { new { name = "s", expected = "e", actual = "a", verdict = "Different", differences = new[] { new { @class = "Value", path = "/a/b", cosmetic = false, expected = "e", actual = "a", explanation = "x" } } } },
        question = "Why?",
        disclosureSha256 = sha
    };

    [Fact]
    public async Task WithNothingConfiguredTheSettingsReadAsOffAndNothingFails()
    {
        await using var server = await StartAsync();

        var settings = await server.Client.GetFromJsonAsync<JsonElement>("/api/authoring/settings");

        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.False(settings.GetProperty("credentialConfigured").GetBoolean());
        Assert.True(settings.GetProperty("notice").ValueKind is JsonValueKind.Null);
    }

    [Fact]
    public async Task WithNothingConfiguredEveryAiSendIsRefusedCleanlyAndNeverAServerError()
    {
        await using var server = await StartAsync();
        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/authoring/test", new { }),
            ("/api/authoring/chat", new { message = "hi", draftJson = "{}", history = Array.Empty<object>() }),
            ("/api/authoring/connection", new { }),
            ("/api/authoring/copilot/models", new { }),
            ("/api/authoring/biztalk-assist", Evidence("ABC")),
            ("/api/authoring/compare-explain", Compare("ABC")),
            ("/api/authoring/flow-wording", new { flowJson = "{}", disclosureSha256 = "ABC" })
        })
        {
            using var response = await server.Client.PostAsJsonAsync(path, body, JsonSerializerOptions.Web);
            Assert.True((int)response.StatusCode is 400 or 409, $"{path} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task WithNothingConfiguredTheLocalChecksStillRunAndSayAiIsOff()
    {
        await using var server = await StartAsync();

        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/authoring/biztalk-assist/preview", Evidence()),
            ("/api/authoring/compare-explain/preview", Compare())
        })
        {
            using var response = await server.Client.PostAsJsonAsync(path, body, JsonSerializerOptions.Web);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{path}: {text}");
            var json = JsonDocument.Parse(text).RootElement;
            Assert.False(json.GetProperty("aiAvailable").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("unavailable").GetString()));
        }
    }

    [Fact]
    public async Task WithNothingConfiguredTheDeterministicAssistEndpointsAllWork()
    {
        await using var server = await StartAsync();

        foreach (var path in new[] { "/api/assist/surfaces", "/api/assist/catalog/node-kinds", "/api/assist/glossary?search=orchestration", "/api/assist/activity" })
        {
            using var response = await server.Client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}");
        }

        using var explain = await server.Client.PostAsJsonAsync("/api/assist/explain/flow", new { flowJson = """{"version":1,"durable":true,"nodes":[]}""" }, JsonSerializerOptions.Web);
        Assert.True((int)explain.StatusCode < 500, $"explain answered {(int)explain.StatusCode}");
    }

    [Fact]
    public async Task AnUnreadableSettingsFileTurnsAiOffInsteadOfBreakingTheLocalChecks()
    {
        await using var server = await StartAsync();
        var directory = Path.Combine(_directory, "settings");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "settings.bin"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);

        var settings = await server.Client.GetFromJsonAsync<JsonElement>("/api/authoring/settings");
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.Contains("could not be read", settings.GetProperty("notice").GetString());

        foreach (var (path, body) in new (string, object)[] { ("/api/authoring/biztalk-assist/preview", Evidence()), ("/api/authoring/compare-explain/preview", Compare()) })
        {
            using var response = await server.Client.PostAsJsonAsync(path, body, JsonSerializerOptions.Web);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{path}: {text}");
            var json = JsonDocument.Parse(text).RootElement;
            Assert.False(json.GetProperty("aiAvailable").GetBoolean());
            Assert.Contains("could not be read", json.GetProperty("unavailable").GetString());
        }

        using var send = await server.Client.PostAsJsonAsync("/api/authoring/biztalk-assist", Evidence("ABC"), JsonSerializerOptions.Web);
        Assert.True((int)send.StatusCode is 409 or 503, $"send answered {(int)send.StatusCode}");
    }

    [Fact]
    public async Task SavingNewSettingsRepairsAnUnreadableFile()
    {
        await using var server = await StartAsync();
        var directory = Path.Combine(_directory, "settings");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "settings.bin"), [9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9]);

        using var saved = await server.Client.PutAsJsonAsync("/api/authoring/settings",
            new { enabled = false, provider = "OpenAI", model = "gpt-test", endpoint = "", maxOutputTokens = 2048 }, JsonSerializerOptions.Web);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

        var settings = await server.Client.GetFromJsonAsync<JsonElement>("/api/authoring/settings");
        Assert.True(settings.GetProperty("notice").ValueKind is JsonValueKind.Null);
        Assert.Equal("gpt-test", settings.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TheSettingsPageShowsTheNoticeAndStillOffersTheForm()
    {
        var handler = new Handler(settings: new AiAuthoringSettingsViewModel(false, "OpenAI", "", "", false, 2048, "The saved AI settings could not be read, so AI assistance is off."));
        await Render<AiAssistancePage>(handler, ParameterView.Empty, async (_, rendered) =>
        {
            await Task.Yield();
            var html = rendered.ToHtmlString();
            Assert.Contains("data-testid=\"ai-settings-notice\"", html);
            Assert.Contains("could not be read", html);
            Assert.Contains("Enable AI authoring assistance", html);
        });
    }

    [Fact]
    public async Task TheConnectionAssistantSaysAiIsNotSetUpAndOffersNoSendButton()
    {
        var handler = new Handler(settings: new AiAuthoringSettingsViewModel(false, "OpenAI", "", "", false, 2048));
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["CaptureDraft"] = (Func<Task<string>>)(() => Task.FromResult("""{"parses":[],"deliveries":[]}"""))
        });
        await Render<ConnectionAssistantComponent>(handler, parameters, async (component, rendered) =>
        {
            Assert.DoesNotContain("data-testid=\"connection-ai-off\"", rendered.ToHtmlString());

            await new EventCallback(component, (Func<Task>)(() => (Task)typeof(ConnectionAssistantComponent)
                .GetMethod("AnalyzeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(component, null)!)).InvokeAsync();

            var html = rendered.ToHtmlString();
            Assert.Contains("data-testid=\"connection-ai-off\"", html);
            Assert.Contains("Nothing was sent anywhere", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Share reviewed context and propose mapping</button>", html);
            Assert.Equal(0, handler.AssistCalls);
        });
    }

    private static async Task Render<TComponent>(Handler handler, ParameterView parameters, Func<TComponent, HtmlRootComponent, Task> test) where TComponent : IComponent
    {
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<TComponent>(parameters);
            await test(Assert.Single(activator.Components.OfType<TComponent>()), rendered);
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

    private sealed class TestNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
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

    private sealed class Handler(AiAuthoringSettingsViewModel settings) : HttpMessageHandler
    {
        public int AssistCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/authoring/settings")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(settings, options: JsonSerializerOptions.Web) });
            if (path == "/api/authoring/connection/analyze")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ConnectionAnalysisViewModel([], [], [], "{}"), options: JsonSerializerOptions.Web)
                });
            if (path == "/api/authoring/connection") AssistCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
