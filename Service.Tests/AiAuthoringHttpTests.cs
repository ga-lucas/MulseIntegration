using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Service.Ai;
using Service.Endpoints;
using Service.Models;

namespace Service.Tests;

public sealed class AiAuthoringHttpTests
{
    private sealed class FakeProvider : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string? Authorization { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public int Hits { get; private set; }
        public string BaseUrl { get; }

        private FakeProvider(WebApplication app, string baseUrl) { _app = app; BaseUrl = baseUrl; }

        public static async Task<FakeProvider> StartAsync(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FakeProvider? provider = null;
            app.Run(async context =>
            {
                provider!.Hits++;
                provider.Path = context.Request.Path;
                provider.Authorization = context.Request.Headers.Authorization;
                using var reader = new StreamReader(context.Request.Body);
                provider.Body = await reader.ReadToEndAsync();
                await handler(context);
            });
            await app.StartAsync();
            var address = app.Urls.First();
            provider = new FakeProvider(app, address);
            return provider;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    private static AiAuthoringService CreateService(string endpoint, string? apiKey = "sk-http-test")
    {
        var options = new AiAuthoringOptions { Enabled = true, Provider = "OpenAICompatible", Model = "llama-test", Endpoint = endpoint + "/v1", ApiKey = apiKey, TimeoutSeconds = 20 };
        var settings = new AiAuthoringSettingsService(Options.Create(options), new MemoryAiSettingsStore(), new TestHost("Development"));
        return new AiAuthoringService(settings, new ReadOnlyCatalog(new ModuleDescriptor("fetch-a", "Fetch", ModuleKind.Fetch, "d")), new OpenAiChatClientFactory(),
            NullLogger<AiAuthoringService>.Instance);
    }

    private static string Completion(string content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        @object = "chat.completion",
        created = 1,
        model = "llama-test",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
    });

    [Fact]
    public async Task ChatUsesOpenAiCompatibleWireFormatWithBearerKey()
    {
        await using var provider = await FakeProvider.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(Completion("""{"reply":"hello from fake","proposal":null}"""));
        });
        var draft = """{"id":"f","fetches":[],"parses":[],"augments":[],"deliveries":[]}""";
        var response = await CreateService(provider.BaseUrl.TrimEnd('/')).ChatAsync(new("hi", draft), CancellationToken.None);

        Assert.Equal("hello from fake", response.Reply);
        Assert.Equal("/v1/chat/completions", provider.Path);
        Assert.Equal("Bearer sk-http-test", provider.Authorization);
        Assert.Contains("llama-test", provider.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tools\"", provider.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectsAreNotFollowedAndKeyIsNotForwarded()
    {
        await using var target = await FakeProvider.StartAsync(context => context.Response.WriteAsync("{}"));
        await using var provider = await FakeProvider.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = target.BaseUrl + "/stolen";
            return Task.CompletedTask;
        });
        var exception = await Assert.ThrowsAsync<AiAuthoringException>(() => CreateService(provider.BaseUrl.TrimEnd('/'))
            .TestAsync(CancellationToken.None));
        Assert.Equal(502, exception.StatusCode);
        Assert.Equal(0, target.Hits);
        Assert.Equal(1, provider.Hits);
    }

    [Fact]
    public async Task ProviderErrorsAreSanitizedAndNotRetried()
    {
        await using var provider = await FakeProvider.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("""{"error":{"message":"bad key sk-http-test"}}""");
        });
        var exception = await Assert.ThrowsAsync<AiAuthoringException>(() => CreateService(provider.BaseUrl.TrimEnd('/')).TestAsync(CancellationToken.None));
        Assert.DoesNotContain("sk-http-test", exception.Message, StringComparison.Ordinal);
        Assert.Contains("credentials", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, provider.Hits);
    }

    [Fact]
    public async Task KeylessLoopbackEndpointWorks()
    {
        await using var provider = await FakeProvider.StartAsync(async context => await context.Response.WriteAsync(Completion("OK")));
        var result = await CreateService(provider.BaseUrl.TrimEnd('/'), apiKey: null).TestAsync(CancellationToken.None);
        Assert.Contains("llama-test", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoutesAreMapped()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddAiAuthoring(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.MapAiAuthoringEndpoints();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(static endpoint => (endpoint.RoutePattern.RawText, Methods: string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])))
            .ToArray();
        Assert.Contains(("/api/authoring/settings", "GET"), routes);
        Assert.Contains(("/api/authoring/settings", "PUT"), routes);
        Assert.Contains(("/api/authoring/test", "POST"), routes);
        Assert.Contains(("/api/authoring/chat", "POST"), routes);
        Assert.Contains(("/api/authoring/copilot/models", "POST"), routes);
        Assert.Contains(("/api/authoring/connection/analyze", "POST"), routes);
        Assert.Contains(("/api/authoring/connection", "POST"), routes);
        Assert.Contains(("/api/authoring/biztalk-assist/preview", "POST"), routes);
        Assert.Contains(("/api/authoring/biztalk-assist", "POST"), routes);
    }

    [Fact]
    public void AddAiAuthoringRegistersServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiAuthoring:Model"] = "m", ["AiAuthoring:ApiKey"] = "dep-key" }).Build();
        services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        services.AddAiAuthoring(configuration, new TestHost("Production"));
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<AiAuthoringService>());
        var settings = provider.GetRequiredService<AiAuthoringSettingsService>().Get();
        Assert.True(settings.CredentialConfigured);
        Assert.Equal("m", settings.Model);
        _ = IPAddress.Loopback;
    }
}
