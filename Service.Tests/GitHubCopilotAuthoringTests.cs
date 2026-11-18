using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
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

public sealed class GitHubCopilotAuthoringTests
{
    private static AiAuthoringEffectiveSettings Settings() => new(true, "GitHubCopilot", "copilot-test-model", null, 2048, "github_pat_test-only");
    private static AiAuthoringSettingsService SettingsService(MemoryAiSettingsStore store, AiAuthoringOptions? options = null)
        => new(Options.Create(options ?? new AiAuthoringOptions()), store, new TestHost("Production"));
    private static AiAuthoringSettingsRequest Request(bool enabled = true, string? key = null, string? endpoint = null,
        bool clear = false, string provider = "GitHubCopilot") => new(enabled, provider, "copilot-test-model", endpoint, 2048, key, clear);

    [Fact]
    public void CopilotProviderSettingsAreIndependentOfUserCredentials()
    {
        var store = new MemoryAiSettingsStore();
        var service = SettingsService(store);
        var result = service.Update(Request(provider: "githubcopilot"));
        Assert.Equal("GitHubCopilot", result.Provider);
        Assert.Null(result.Endpoint);
        Assert.False(result.CredentialConfigured);
        Assert.True(result.Enabled);
        Assert.Null(store.Current!.ApiKey);
        Assert.True(SettingsService(store).Get().Enabled);
        Assert.Throws<ArgumentException>(() => service.Update(Request(key: "github_pat_test-only")));
    }

    [Fact]
    public void CopilotRequiresExplicitCredentialAndRejectsCustomEndpoint()
    {
        var service = SettingsService(new());
        Assert.True(service.Update(Request()).Enabled);
        Assert.Throws<ArgumentException>(() => service.Update(Request(endpoint: "https://elsewhere.example/v1")));
        Assert.Throws<ArgumentException>(() => service.Update(Request(enabled: false, endpoint: "https://api.githubcopilot.com")));
        var effective = Settings() with { ApiKey = null };
        Assert.Contains("no credential", service.Validate(effective));
        Assert.Contains("does not accept a custom", service.Validate(Settings() with { Endpoint = new Uri("https://example.test") }));
    }

    [Fact]
    public void ProviderSwitchDoesNotReuseOpenAiOrGitHubCredentials()
    {
        var store = new MemoryAiSettingsStore();
        var service = SettingsService(store, new AiAuthoringOptions { Provider = "OpenAI", ApiKey = "openai-deployment" });
        Assert.False(service.Update(Request()).CredentialConfigured);
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "OpenAICompatible", endpoint: "https://example.test/v1", key: null)));
        Assert.Equal("GitHubCopilot", store.Current!.Provider);
        Assert.Null(store.Current.ApiKey);
    }

    [Fact]
    public void ClearingDeploymentCopilotTokenRemainsClearedAfterReload()
    {
        var store = new MemoryAiSettingsStore();
        var options = new AiAuthoringOptions { Provider = "GitHubCopilot", Model = "m", ApiKey = "deployment-github-token" };
        var service = SettingsService(store, options);
        Assert.False(service.Get().CredentialConfigured);
        Assert.Null(service.Resolve().ApiKey);
        Assert.False(service.Update(Request(enabled: false, key: null, clear: true)).CredentialConfigured);
        Assert.False(SettingsService(store, options).Get().CredentialConfigured);
        Assert.True(SettingsService(store, options).Update(Request()).Enabled);
    }

    [Fact]
    public async Task SdkConfigurationHasNoToolsAmbientIdentityOrRepositoryDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-copilot-config-test");
        var client = CopilotAuthoringRuntime.CreateClientOptions(Settings(), directory);
        await using var sdk = new CopilotClient(client);
        Assert.Equal(CopilotClientMode.Empty, client.Mode);
        Assert.IsType<StdioRuntimeConnection>(client.Connection);
        Assert.Equal(directory, client.BaseDirectory);
        Assert.Equal(directory, client.WorkingDirectory);
        Assert.False(client.UseLoggedInUser);
        Assert.Equal("github_pat_test-only", client.GitHubToken);
        Assert.Equal(CopilotLogLevel.None, client.LogLevel);
        Assert.Equal(directory, client.Environment!["HOME"]);
        Assert.Equal("true", client.Environment["COPILOT_TELEMETRY_DISABLED"]);
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN", "COPILOT_API_URL", "COPILOT_CLI_PATH", "OPENAI_API_KEY", "NODE_OPTIONS" })
            Assert.False(client.Environment.ContainsKey(name));

        var session = CopilotAuthoringRuntime.CreateSessionConfig(Settings(), [new(ChatRole.System, "Only draft flows."), new(ChatRole.User, "hello")], 512, directory);
        Assert.Equal("copilot-test-model", session.Model);
        Assert.Equal("github_pat_test-only", session.GitHubToken);
        Assert.Empty(session.AvailableTools!);
        Assert.Empty(session.Tools!);
        Assert.Empty(session.McpServers!);
        Assert.Empty(session.IncludedBuiltInAgents!);
        Assert.False(session.EnableConfigDiscovery);
        Assert.True(session.SkipCustomInstructions);
        Assert.False(session.EnableOnDemandInstructionDiscovery);
        Assert.False(session.EnableFileHooks);
        Assert.False(session.EnableHostGitOperations);
        Assert.False(session.EnableSkills);
        Assert.False(session.EnableSessionStore);
        Assert.False(session.EnableSessionTelemetry);
        Assert.False(session.InfiniteSessions!.Enabled);
        Assert.Equal(SystemMessageMode.Replace, session.SystemMessage!.Mode);
        Assert.StartsWith("Only draft flows.", session.SystemMessage.Content);
#pragma warning disable GHCP001 // Inspect the same pinned experimental safeguards used by the adapter.
        Assert.Equal(512, session.ModelCapabilities!.Limits!.MaxOutputTokens);
        var decision = await session.OnPermissionRequest!(null!, null!);
        Assert.Equal("reject", decision.Kind);
#pragma warning restore GHCP001
    }

    [Fact]
    public async Task CopilotProposalUsesExistingRedactionValidationAndDisabledDraftGuards()
    {
        const string draft = """
            {"id":"orders","name":"Original","enabled":true,"runOnStartup":true,"triggerMode":"OnDemand","category":"Customer A",
             "fetches":[{"id":"f","fetch":{"module":"fetch","settings":{"path":"private-inbox"}},"inputParseIds":[]}],
             "parses":[{"id":"p","parse":{"module":"parse","settings":{}},"fetchNodeIds":["f"]}],"augments":[],
             "deliveries":[{"render":{"module":"render","settings":{}},"delivers":[{"deliver":{"module":"deliver","settings":{}},"requireConfirmation":true}]}]}
            """;
        var runtime = new FakeRuntime((_, messages, _, _) =>
        {
            var context = messages.Single(message => message.Text.Contains("FLOW_DRAFT:", StringComparison.Ordinal)).Text;
            Assert.DoesNotContain("private-inbox", context);
            Assert.DoesNotContain("github_pat_test-only", string.Join("\n", messages.Select(message => message.Text)));
            var json = context.Split("FLOW_DRAFT:\n", 2)[1].Split("\nMODULE_CATALOG:", 2)[0];
            var proposal = JsonNode.Parse(json)!;
            proposal["name"] = "Copilot proposal";
            proposal["id"] = "wrong-id";
            proposal["enabled"] = true;
            proposal["runOnStartup"] = true;
            proposal["category"] = "Other customer";
            return Task.FromResult(new JsonObject { ["reply"] = "Review this proposal.", ["proposal"] = proposal }.ToJsonString());
        });
        var store = new MemoryAiSettingsStore();
        var settings = SettingsService(store);
        settings.Update(Request());
        var catalog = new ReadOnlyCatalog(new("fetch", "Fetch", ModuleKind.Fetch, "fetch"), new("parse", "Parse", ModuleKind.Parse, "parse"),
            new("render", "Render", ModuleKind.Render, "render"), new("deliver", "Deliver", ModuleKind.Deliver, "deliver"));
        var service = new AiAuthoringService(settings, catalog, new AiAuthoringChatClientFactory(runtime), NullLogger<AiAuthoringService>.Instance);
        var result = await service.ChatAsync(new("Rename this flow", draft), CancellationToken.None, "github_pat_test-only");
        Assert.Empty(result.ValidationErrors);
        var proposal = JsonNode.Parse(Assert.IsType<string>(result.ProposalJson))!;
        Assert.Equal("orders", proposal["id"]!.GetValue<string>());
        Assert.Equal("Customer A", proposal["category"]!.GetValue<string>());
        Assert.Equal("Copilot proposal", proposal["name"]!.GetValue<string>());
        Assert.False(proposal["enabled"]!.GetValue<bool>());
        Assert.False(proposal["runOnStartup"]!.GetValue<bool>());
        Assert.Equal("private-inbox", proposal["fetches"]![0]!["fetch"]!["settings"]!["path"]!.GetValue<string>());
        Assert.True(proposal["deliveries"]![0]!["delivers"]![0]!["requireConfirmation"]!.GetValue<bool>());
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(2048, runtime.MaxTokens);
    }

    [Fact]
    public async Task CancellationDoesNotPublishReplyAndDoesNotReplayRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new FakeRuntime((_, _, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromResult("Late reply");
        });
        using var client = new AiAuthoringChatClientFactory(runtime).Create(Settings());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: cancellation.Token));
        Assert.Equal(1, runtime.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: cancellation.Token));
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public async Task CopilotFailuresAreSanitizedAndNotReplayed()
    {
        var runtime = new FakeRuntime((_, _, _, _) => throw new InvalidOperationException("Credential github_pat_test-only and private prompt"));
        var store = new MemoryAiSettingsStore();
        var settings = SettingsService(store);
        settings.Update(Request());
        var service = new AiAuthoringService(settings, new ReadOnlyCatalog(), new AiAuthoringChatClientFactory(runtime), NullLogger<AiAuthoringService>.Instance);
        var error = await Assert.ThrowsAsync<AiAuthoringException>(() => service.TestAsync(CancellationToken.None, "github_pat_test-only"));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("github_pat_test-only", error.Message);
        Assert.DoesNotContain("private prompt", error.Message);
        Assert.Contains("Copilot Requests permission", error.Message);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(256, runtime.MaxTokens);
    }

    [Fact]
    public async Task ConcurrentUsersKeepSeparateRuntimeCredentialsWithoutSharedFallback()
    {
        var tokens = new System.Collections.Concurrent.ConcurrentBag<string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(async (settings, _, _, cancellation) =>
        {
            tokens.Add(settings.ApiKey!);
            if (tokens.Count == 2) bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            return "OK";
        });
        var settings = SettingsService(new(), new AiAuthoringOptions
        {
            Enabled = true, Provider = "GitHubCopilot", Model = "m", ApiKey = "obsolete-shared-token"
        });
        var service = new AiAuthoringService(settings, new ReadOnlyCatalog(), new AiAuthoringChatClientFactory(runtime), NullLogger<AiAuthoringService>.Instance);
        var missing = await Assert.ThrowsAsync<AiAuthoringException>(() => service.TestAsync(default));
        Assert.Equal(409, missing.StatusCode);
        Assert.Equal(0, runtime.Calls);
        await Task.WhenAll(service.TestAsync(default, "gho_alice"), service.TestAsync(default, "gho_bob"));
        Assert.Equal(["gho_alice", "gho_bob"], tokens.Order());
        Assert.Equal(2, runtime.Calls);
        Assert.Null(settings.Resolve().ApiKey);
    }

    [Fact]
    public async Task AuthoringEndpointsRejectMissingMalformedDuplicateAndOversizedCredentials()
    {
        var runtime = new FakeRuntime((settings, _, _, _) =>
        {
            Assert.Equal("gho_personal", settings.ApiKey);
            return Task.FromResult("OK");
        });
        var settings = SettingsService(new(), new AiAuthoringOptions { Enabled = true, Provider = "GitHubCopilot", Model = "m", ApiKey = "obsolete-shared" });
        var service = new AiAuthoringService(settings, new ReadOnlyCatalog(), new AiAuthoringChatClientFactory(runtime), NullLogger<AiAuthoringService>.Instance);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(settings).AddSingleton(service).AddSingleton<ICopilotAuthoringRuntime>(runtime);
        await using var app = builder.Build();
        app.MapAiAuthoringEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        using var missing = await http.PostAsync("/api/authoring/test", null);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        foreach (var values in new[] { new[] { "bad token" }, new[] { "gho_a", "gho_b" }, new[] { new string('x', CopilotUserCredential.MaxTokenLength + 1) } })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/authoring/test");
            request.Headers.Add(CopilotUserCredential.HeaderName, values);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(values[0], await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, runtime.Calls);
        using var valid = new HttpRequestMessage(HttpMethod.Post, "/api/authoring/test");
        valid.Headers.Add(CopilotUserCredential.HeaderName, "gho_personal");
        using var success = await http.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.DoesNotContain("gho_personal", await success.Content.ReadAsStringAsync());
        Assert.Equal(1, runtime.Calls);
    }

    [Fact]
    public void RegisteredFactoryRoutesCopilotWithoutStartingRuntime()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAiAuthoring(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), new TestHost("Production"));
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IAiChatClientFactory>().Create(Settings());
        Assert.IsType<GitHubCopilotChatClient>(client);
        using var openAi = provider.GetRequiredService<IAiChatClientFactory>().Create(Settings() with { Provider = "OpenAI" });
        Assert.IsNotType<GitHubCopilotChatClient>(openAi);
        Assert.Throws<AiAuthoringException>(() => provider.GetRequiredService<IAiChatClientFactory>().Create(Settings() with { Provider = "Unknown" }));
    }

    private sealed class FakeRuntime(Func<AiAuthoringEffectiveSettings, IReadOnlyList<ChatMessage>, int, CancellationToken, Task<string>> complete) : ICopilotAuthoringRuntime
    {
        public int Calls { get; private set; }
        public int MaxTokens { get; private set; }
        public Task<IReadOnlyList<AiAuthoringModelResponse>> ListModelsAsync(string userToken, CancellationToken cancellationToken)
            => throw new NotSupportedException("This completion-only test runtime does not list models.");
        public Task<string> CompleteAsync(AiAuthoringEffectiveSettings settings, IReadOnlyList<ChatMessage> messages, int maxOutputTokens, CancellationToken cancellationToken)
        {
            Calls++;
            MaxTokens = maxOutputTokens;
            Assert.Equal("GitHubCopilot", settings.Provider);
            return complete(settings, messages, maxOutputTokens, cancellationToken);
        }
    }
}
