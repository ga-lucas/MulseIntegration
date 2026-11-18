using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Service.Ai;
using Service.Models;
using Service.Modules;

namespace Service.Tests;

internal sealed class MemoryAiSettingsStore : IAiAuthoringSettingsStore
{
    public AiAuthoringOverrides? Current { get; set; }
    public AiAuthoringOverrides? Load() => Current;
    public void Save(AiAuthoringOverrides overrides) => Current = overrides;
}

internal sealed class ReadOnlyCatalog(params ModuleDescriptor[] descriptors) : IModuleCatalog
{
    public int GetAllCalls { get; private set; }

    public IReadOnlyList<ModuleCatalogEntry> GetAll()
    {
        GetAllCalls++;
        return descriptors.Select(static item => new ModuleCatalogEntry("test", RuntimePackageSourceKind.BuiltIn, "", item)).ToArray();
    }

    public IReadOnlyList<ModulePackageInfo> GetPackages() => throw new NotSupportedException();
    public IDisposable PinFlowModules(PipelineDefinition flow) => throw new NotSupportedException();
    public ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ModuleLease<ITransformModule>> LeaseTransformAsync(string moduleId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SynchronizeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, string> reply) : IChatClient
{
    public List<ChatMessage> Received { get; } = [];
    public ChatOptions? Options { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Received.AddRange(messages);
        Options = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(Received))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class ScriptedFactory(ScriptedChatClient client) : IAiChatClientFactory
{
    public int Created { get; private set; }
    public IChatClient Create(AiAuthoringEffectiveSettings settings) { Created++; return client; }
}

internal sealed class TestHost(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

public sealed class AiAuthoringSettingsTests
{
    private static (AiAuthoringSettingsService Service, MemoryAiSettingsStore Store) Create(AiAuthoringOptions? options = null, string environment = "Production")
    {
        var store = new MemoryAiSettingsStore();
        return (new AiAuthoringSettingsService(Options.Create(options ?? new AiAuthoringOptions()), store, new TestHost(environment)), store);
    }

    private static AiAuthoringSettingsRequest Request(bool enabled = true, string provider = "OpenAI", string model = "gpt-test", string? endpoint = null,
        int tokens = 2048, string? apiKey = null, bool clear = false)
        => new(enabled, provider, model, endpoint, tokens, apiKey, clear);

    [Fact]
    public void ApiKeyIsWriteOnlyAndNeverReturned()
    {
        var (service, store) = Create();
        var response = service.Update(Request(apiKey: "sk-super-secret-value"));
        Assert.True(response.CredentialConfigured);
        Assert.DoesNotContain("sk-super-secret-value", JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-super-secret-value", JsonSerializer.Serialize(service.Get()), StringComparison.Ordinal);
        Assert.Equal("sk-super-secret-value", store.Current!.ApiKey);

        var kept = service.Update(Request(model: "gpt-other"));
        Assert.True(kept.CredentialConfigured);
        var cleared = service.Update(Request(enabled: false, clear: true));
        Assert.False(cleared.CredentialConfigured);
        Assert.Null(store.Current!.ApiKey);
    }

    [Fact]
    public void DeploymentKeyCountsAsCredentialAndIsMasked()
    {
        var (service, _) = Create(new AiAuthoringOptions { Enabled = true, Model = "m", ApiKey = "deploy-secret" });
        var response = service.Get();
        Assert.True(response.CredentialConfigured);
        Assert.DoesNotContain("deploy-secret", JsonSerializer.Serialize(response), StringComparison.Ordinal);
    }

    [Fact]
    public void StoredKeyIsNotReusedWhenEndpointOrProviderChanges()
    {
        var (service, store) = Create();
        service.Update(Request(provider: "OpenAICompatible", endpoint: "https://one.example/v1", apiKey: "key-one"));
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "OpenAICompatible", endpoint: "https://two.example/v1")));
        var disabled = service.Update(Request(enabled: false, provider: "OpenAICompatible", endpoint: "https://two.example/v1"));
        Assert.False(disabled.CredentialConfigured);
        Assert.Null(store.Current!.ApiKey);

        service.Update(Request(provider: "OpenAICompatible", endpoint: "https://two.example/v1", apiKey: "key-two"));
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "OpenAI")));
    }

    [Fact]
    public void DeploymentKeyIsNotReusedForOverriddenEndpoint()
    {
        var (service, _) = Create(new AiAuthoringOptions { Provider = "OpenAI", Model = "m", ApiKey = "deploy-secret" });
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "OpenAICompatible", endpoint: "https://elsewhere.example/v1")));
        Assert.Throws<ArgumentException>(() => service.Update(Request(endpoint: "https://elsewhere.example/v1")));
        Assert.True(service.Update(Request()).CredentialConfigured);
    }

    [Theory]
    [InlineData("http://example.com/v1", "Development")]
    [InlineData("http://localhost:11434/v1", "Production")]
    [InlineData("https://user:pw@example.com/v1", "Production")]
    [InlineData("https://example.com/v1?x=1", "Production")]
    [InlineData("ftp://example.com/v1", "Production")]
    [InlineData("not a url", "Production")]
    public void InvalidEndpointsAreRejected(string endpoint, string environment)
    {
        var (service, _) = Create(environment: environment);
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "OpenAICompatible", endpoint: endpoint, apiKey: "k")));
    }

    [Fact]
    public void OllamaLoopbackIsKeylessInDevelopmentOnly()
    {
        var (service, _) = Create(environment: "Development");
        var response = service.Update(Request(provider: "OpenAICompatible", model: "llama3", endpoint: "http://127.0.0.1:11434/v1"));
        Assert.False(response.CredentialConfigured);
        Assert.Equal("http://127.0.0.1:11434/v1", response.Endpoint);
        Assert.Equal("OpenAICompatible", response.Provider);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(16385)]
    public void OutputTokenBoundsAreEnforced(int tokens)
    {
        var (service, _) = Create();
        Assert.Throws<ArgumentException>(() => service.Update(Request(tokens: tokens, apiKey: "k")));
    }

    [Fact]
    public void InvalidProviderKeyAndConflictingClearAreRejected()
    {
        var (service, _) = Create();
        Assert.Throws<ArgumentException>(() => service.Update(Request(provider: "Other", apiKey: "k")));
        Assert.Throws<ArgumentException>(() => service.Update(Request(apiKey: "has space")));
        Assert.Throws<ArgumentException>(() => service.Update(Request(apiKey: "k", clear: true)));
        Assert.Throws<ArgumentException>(() => service.Update(Request(model: "", apiKey: "k")));
    }

    [Fact]
    public void ClearingTheKeyAlsoSuppressesTheDeploymentKeyAcrossRestart()
    {
        var options = new AiAuthoringOptions { Provider = "OpenAI", Model = "m", ApiKey = "deploy-secret" };
        var (service, store) = Create(options);
        Assert.True(service.Get().CredentialConfigured);
        Assert.False(service.Update(Request(enabled: false, clear: true)).CredentialConfigured);

        var restarted = new AiAuthoringSettingsService(Options.Create(options), store, new TestHost("Production"));
        Assert.False(restarted.Get().CredentialConfigured);
        Assert.Null(restarted.Resolve().ApiKey);
        Assert.False(restarted.Update(Request(enabled: false)).CredentialConfigured);
        Assert.Throws<ArgumentException>(() => restarted.Update(Request()));
        Assert.True(restarted.Update(Request(apiKey: "fresh-key")).CredentialConfigured);
    }

    [Fact]
    public void ImplicitOpenAiDefaultBindsToExplicitOfficialEndpoint()
    {
        var (service, _) = Create(new AiAuthoringOptions { Provider = "OpenAI", Model = "m", ApiKey = "deploy-secret" });
        Assert.True(service.Update(Request(endpoint: "https://api.openai.com/v1")).CredentialConfigured);
        Assert.Equal("deploy-secret", service.Resolve().ApiKey);
        Assert.Throws<ArgumentException>(() => service.Update(Request(endpoint: "https://api.openai.com.evil.example/v1")));
        Assert.Throws<ArgumentException>(() => service.Update(Request(endpoint: "https://proxy.example/v1")));
    }

    [Fact]
    public void CorruptStoreFailsClosedInsteadOfFallingBackToDeployment()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ai-authoring-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AiAuthoringSettingsStore(directory, NullLogger<AiAuthoringSettingsStore>.Instance);
            var options = Options.Create(new AiAuthoringOptions { Enabled = true, Model = "m", ApiKey = "deploy-secret" });
            var service = new AiAuthoringSettingsService(options, store, new TestHost("Production"));
            service.Update(Request(enabled: false, apiKey: "k"));
            File.WriteAllBytes(Path.Combine(directory, "settings.bin"), [1, 2, 3, 4]);

            var fresh = new AiAuthoringSettingsService(options, new AiAuthoringSettingsStore(directory, NullLogger<AiAuthoringSettingsStore>.Instance), new TestHost("Production"));
            var exception = Assert.Throws<AiAuthoringException>(() => fresh.Resolve());
            Assert.Equal(503, exception.StatusCode);
            Assert.DoesNotContain("deploy-secret", exception.Message, StringComparison.Ordinal);
            Assert.Throws<AiAuthoringException>(() => fresh.Get());
            Assert.Throws<AiAuthoringException>(() => fresh.Update(Request(apiKey: "k2")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidDeploymentProviderOrEndpointIsReportedNotDefaulted()
    {
        var (badProvider, _) = Create(new AiAuthoringOptions { Enabled = true, Provider = "Bogus", Model = "m", ApiKey = "k" });
        var resolved = badProvider.Resolve();
        Assert.Equal("Bogus", resolved.Provider);
        Assert.Contains("unsupported provider", badProvider.Validate(resolved), StringComparison.Ordinal);

        var (badEndpoint, _) = Create(new AiAuthoringOptions { Enabled = true, Model = "m", ApiKey = "k", Endpoint = "not a url" });
        resolved = badEndpoint.Resolve();
        Assert.Null(resolved.Endpoint);
        Assert.Contains("malformed endpoint", badEndpoint.Validate(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void FileStoreEncryptsApiKeyAtRest()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ai-authoring-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AiAuthoringSettingsStore(directory, NullLogger<AiAuthoringSettingsStore>.Instance);
            store.Save(new AiAuthoringOverrides(true, "OpenAI", "m", null, 1024, "sk-at-rest-secret"));
            var raw = File.ReadAllBytes(Path.Combine(directory, "settings.bin"));
            Assert.DoesNotContain("sk-at-rest-secret", System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.Equal("sk-at-rest-secret", new AiAuthoringSettingsStore(directory, NullLogger<AiAuthoringSettingsStore>.Instance).Load()!.ApiKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
