using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.ServiceDefaults.Security;
using Service.Ai;
using Service.Endpoints;
using Service.Models;

namespace Service.Tests;

public sealed class CopilotModelDiscoveryTests
{
    #pragma warning disable GHCP001 // Test the same pinned experimental RPC metadata used for explicit-user model discovery.
    [Fact]
    public void CatalogUsesRealIdsFiltersDisabledModelsAndDoesNotInventAlternatives()
    {
        var models = JsonSerializer.Deserialize<GitHub.Copilot.Rpc.Model[]>("""
            [{"id":"z-model","name":"Zulu"},
             {"id":"a-model","name":"Alpha","policy":{"state":"enabled"}},
             {"id":"blocked","name":"Blocked","policy":{"state":"disabled"}},
             {"id":"a-model","name":"Alpha","policy":{"state":"enabled"}}]
            """, JsonSerializerOptions.Web)!;
        var catalog = CopilotAuthoringRuntime.AvailableModels(models);
        Assert.Equal([new AiAuthoringModelResponse("a-model", "Alpha"), new("z-model", "Zulu")], catalog);
    }

    [Theory]
    [InlineData("[]", 409)]
    [InlineData("[{\"id\":\"blocked\",\"name\":\"Blocked\",\"policy\":{\"state\":\"disabled\"}}]", 409)]
    [InlineData("[{\"id\":\"\",\"name\":\"Missing ID\"}]", 502)]
    [InlineData("[{\"id\":\"valid\",\"name\":\"\"}]", 502)]
    public void EmptyAndMalformedCatalogsProduceExplicitErrors(string json, int status)
    {
        var models = JsonSerializer.Deserialize<GitHub.Copilot.Rpc.Model[]>(json, JsonSerializerOptions.Web)!;
        var error = Assert.Throws<AiAuthoringException>(() => CopilotAuthoringRuntime.AvailableModels(models));
        Assert.Equal(status, error.StatusCode);
    }

    #pragma warning restore GHCP001

        [Fact]
        public async Task DiscoveryRejectsInvalidCredentialsAndCancellationBeforeLaunchingRuntime()
    {
        var runtime = new CopilotAuthoringRuntime(NullLogger<CopilotAuthoringRuntime>.Instance, Options.Create(new AiAuthoringOptions()));
        var error = await Assert.ThrowsAsync<AiAuthoringException>(() => runtime.ListModelsAsync("bad token", default));
        Assert.Equal(409, error.StatusCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ListModelsAsync("gho_unused-test-token", cancellation.Token));
    }

    [Fact]
    public async Task EndpointUsesOnlyRequestingUserTokenWithNoSavedProviderOrModelPrerequisite()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAiAuthoring(builder.Configuration, builder.Environment);
        builder.Services.AddSingleton<IAiAuthoringSettingsStore>(new MemoryAiSettingsStore());
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        var runtime = new CatalogRuntime();
        builder.Services.AddSingleton<ICopilotAuthoringRuntime>(runtime);
        await using var app = builder.Build();
        app.MapAiAuthoringEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var settings = app.Services.GetRequiredService<AiAuthoringSettingsService>();
        Assert.Equal("OpenAI", settings.Get().Provider);
        Assert.Equal("", settings.Get().Model);
        Assert.False(settings.Get().Enabled);
        using var missing = await http.PostAsync("/api/authoring/copilot/models", null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        foreach (var values in new[] { new[] { "bad token" }, new[] { "gho_a", "gho_b" } })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/authoring/copilot/models");
            invalid.Headers.Add(CopilotUserCredential.HeaderName, values);
            using var response = await http.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("gho_", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(runtime.Tokens);
        await Task.WhenAll(Discover("gho_alice", "alice-model"), Discover("gho_bob", "bob-model"));
        Assert.Equal(["gho_alice", "gho_bob"], runtime.Tokens.Order());
        Assert.Equal(0, runtime.Completions);
        Assert.Equal("OpenAI", settings.Get().Provider);
        Assert.Equal("", settings.Get().Model);

        async Task Discover(string token, string expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/authoring/copilot/models");
            request.Headers.Add(CopilotUserCredential.HeaderName, token);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            var models = await response.Content.ReadFromJsonAsync<AiAuthoringModelResponse[]>();
            Assert.Equal(expected, Assert.Single(models!).Id);
            Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync());
        }
    }

    private sealed class CatalogRuntime : ICopilotAuthoringRuntime
    {
        public ConcurrentBag<string> Tokens { get; } = [];
        public int Completions { get; private set; }
        public async Task<IReadOnlyList<AiAuthoringModelResponse>> ListModelsAsync(string userToken, CancellationToken cancellationToken)
        {
            Tokens.Add(userToken);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return [new(userToken == "gho_alice" ? "alice-model" : "bob-model", "Account-specific model")];
        }
        public Task<string> CompleteAsync(AiAuthoringEffectiveSettings settings, IReadOnlyList<ChatMessage> messages, int maxOutputTokens, CancellationToken cancellationToken)
        {
            Completions++;
            throw new InvalidOperationException("Model discovery must never request inference.");
        }
    }
}
