using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class AuthoringUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static FlowViewModel Draft() => new("orders", true, "Interval", TimeSpan.FromMinutes(5), true,
        [], [], [], [], [], [], [],
        [new("source", new("file-system-fetch", new Dictionary<string, string> { ["path"] = "private-path" }), [])],
        [new("parsed", new("json-parse", new Dictionary<string, string>()), ["source"])], [],
        [new(new("json-render", new Dictionary<string, string>()), [new(new("http-deliver", new Dictionary<string, string> { ["url"] = "{{config:destination}}" }), RequireConfirmation: true)])],
        Category: "Customer A", Name: "Orders");

    [Fact]
    public async Task ChatClientCapturesCurrentDraftAndKeepsProposalSeparateFromChatText()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        FlowAuthoringProposal? received = null;
        var captured = 0;
        using var client = new AuthoringChatClient(new MulseApiClient(http), () =>
        {
            captured++; return Task.FromResult(JsonSerializer.Serialize(Draft(), Json));
        }, (proposal, errors) => { received = proposal; Assert.Empty(errors); return Task.CompletedTask; });
        var response = await client.GetResponseAsync([new(ChatRole.User, "Explain"), new(ChatRole.Assistant, "A flow"), new(ChatRole.User, "Rename")]);
        Assert.Equal(1, captured);
        Assert.Equal("Review the proposal", response.Text);
        Assert.NotNull(received);
        var sent = Assert.IsType<AiAuthoringRequestViewModel>(handler.ChatRequest);
        Assert.Equal("Rename", sent.Message);
        Assert.False(sent.IncludeEnvironmentSettings);
        Assert.Equal(["user", "assistant"], sent.History.Select(item => item.Role));
        Assert.Equal(sent.DraftJson, received.BaseDraftJson);
        Assert.Contains("Renamed orders", received.ProposalJson);
        Assert.DoesNotContain("private-path", response.Text);
        Assert.Equal(["/api/authoring/chat"], handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatClientForwardsConversationEnvironmentConsent(bool consent)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        using var client = new AuthoringChatClient(new MulseApiClient(http), () => Task.FromResult(JsonSerializer.Serialize(Draft(), Json)),
            (_, _) => Task.CompletedTask, consent);
        await client.GetResponseAsync([new(ChatRole.User, "Explain")]);
        Assert.Equal(consent, handler.ChatRequest!.IncludeEnvironmentSettings);
    }

    [Fact]
    public async Task ChatClientSendsOnlyTwelvePriorMessages()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        using var client = new AuthoringChatClient(new MulseApiClient(http), () => Task.FromResult(JsonSerializer.Serialize(Draft(), Json)),
            (_, _) => Task.CompletedTask);
        var messages = Enumerable.Range(0, 20).Select(index => new ChatMessage(index % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"Turn {index}"))
            .Append(new ChatMessage(ChatRole.User, "Current request"));
        await client.GetResponseAsync(messages);
        var request = Assert.IsType<AiAuthoringRequestViewModel>(handler.ChatRequest);
        Assert.Equal("Current request", request.Message);
        Assert.Equal(Enumerable.Range(8, 12).Select(index => $"Turn {index}"), request.History.Select(message => message.Text));
    }

    [Fact]
    public async Task CanceledReplyDoesNotPublishAProposal()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler { OnChatResponse = cancellation.Cancel };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var proposals = new List<FlowAuthoringProposal?>();
        using var client = new AuthoringChatClient(new MulseApiClient(http), () => Task.FromResult(JsonSerializer.Serialize(Draft(), Json)),
            (proposal, _) => { proposals.Add(proposal); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync([new(ChatRole.User, "Rename")], cancellationToken: cancellation.Token));
        Assert.Null(Assert.Single(proposals));
        Assert.Equal(["/api/authoring/chat"], handler.Requests);
    }

    [Fact]
    public void ProposalReviewNeverDisplaysSettingValuesButIncludesAddedAndRemovedSettings()
    {
        var before = "{\"name\":\"Before\",\"settings\":{\"token\":\"secret-one\",\"removed\":\"secret-two\"}}";
        var after = "{\"name\":\"After\",\"settings\":{\"token\":\"secret-three\",\"added\":\"secret-four\"}}";
        var changes = AuthoringProposalReview.Compare(before, after);
        Assert.Equal(4, changes.Count);
        Assert.Contains(new AuthoringChange("flow.name", "Before", "After"), changes);
        var rendered = JsonSerializer.Serialize(changes);
        foreach (var secret in new[] { "secret-one", "secret-two", "secret-three", "secret-four" }) Assert.DoesNotContain(secret, rendered);
        Assert.Contains(changes, change => change.Path == "flow.settings.removed" && change.After == "(not set)");
        Assert.Contains(changes, change => change.Path == "flow.settings.added" && change.Before == "(not set)");
    }

    [Fact]
    public async Task ExistingFlowAssistantCanOpenCloseAndReopenAlongsideCheckpointsWithoutLosingDraft()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            Set(designer, "_flowName", "Unsaved manual edit");
            var draft = (string)Invoke(designer, "CaptureAuthoringDraft")!;
            var initialRequests = handler.Requests.ToArray();
            foreach (var open in new[] { true, false, true, false })
            {
                Set(designer, "_showAssistant", open);
                await designer.SetParametersAsync(ParameterView.Empty);
                var html = rendered.ToHtmlString();
                Assert.Contains("Pending batches / orchestration checkpoints", html);
                Assert.Equal(open, html.Contains("Flow authoring assistant", StringComparison.Ordinal));
                Assert.Contains("Unsaved manual edit", html);
                Assert.Equal(draft, Invoke(designer, "CaptureAuthoringDraft"));
            }
            Assert.Equal(initialRequests, handler.Requests);
            Assert.Equal(0, handler.Mutations);
        });
    }

    [Fact]
    public async Task DesignerProposalRequiresUnchangedDraftAndApplyAndUndoNeverSaveOrRun()
    {
        await RenderDesigner((designer, rendered, handler) =>
        {
            Assert.Contains("AI assistant", rendered.ToHtmlString());
            var baseline = (string)Invoke(designer, "CaptureAuthoringDraft")!;
            var original = JsonSerializer.Deserialize<FlowViewModel>(baseline, Json)!;
            Assert.True(original.Enabled); Assert.True(original.RunOnStartup);
            Assert.True(original.Deliveries[0].Delivers[0].RequireConfirmation);
            Assert.Equal("Customer A", original.Category);
            var proposed = JsonSerializer.Serialize(original with { Name = "Renamed orders", Enabled = false, RunOnStartup = false }, Json);
            Set(designer, "_flowName", "Newer manual edit");
            var stale = Assert.Throws<TargetInvocationException>(() => Invoke(designer, "ApplyAuthoringProposal", new FlowAuthoringProposal(baseline, proposed)));
            Assert.Contains("draft changed", stale.InnerException!.Message);
            Assert.Equal("Newer manual edit", Get(designer, "_flowName"));
            Set(designer, "_flowName", original.Name);
            Invoke(designer, "ApplyAuthoringProposal", new FlowAuthoringProposal(baseline, proposed));
            var applied = JsonSerializer.Deserialize<FlowViewModel>((string)Invoke(designer, "CaptureAuthoringDraft")!, Json)!;
            Assert.Equal("Renamed orders", applied.Name);
            Assert.False(applied.Enabled); Assert.False(applied.RunOnStartup);
            Assert.Equal("private-path", applied.Fetches[0].Fetch.Settings["path"]);
            Assert.True(applied.Deliveries[0].Delivers[0].RequireConfirmation);
            Invoke(designer, "UndoAuthoringProposal");
            Assert.Equal(baseline, Invoke(designer, "CaptureAuthoringDraft"));
            Assert.DoesNotContain(handler.Requests, path => path.Contains("/run") || path == "/api/authoring/chat");
            Assert.Equal(0, handler.Mutations);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task UndoPreservesNewerManualEditsAndClearCanvasPreservesCustomerCategory()
    {
        await RenderDesigner((designer, _, _) =>
        {
            var baseline = (string)Invoke(designer, "CaptureAuthoringDraft")!;
            var original = JsonSerializer.Deserialize<FlowViewModel>(baseline, Json)!;
            Invoke(designer, "ApplyAuthoringProposal", new FlowAuthoringProposal(baseline,
                JsonSerializer.Serialize(original with { Name = "AI name" }, Json)));
            Set(designer, "_flowName", "New manual name");
            Invoke(designer, "UndoAuthoringProposal");
            Assert.Equal("New manual name", Get(designer, "_flowName"));
            Assert.Contains("newer edits", (string)Get(designer, "_errorMessage")!);
            Invoke(designer, "ClearCanvas");
            var cleared = JsonSerializer.Deserialize<FlowViewModel>((string)Invoke(designer, "CaptureAuthoringDraft")!, Json)!;
            Assert.Equal("Customer A", cleared.Category);
            Assert.Empty(cleared.Fetches);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DesignerRejectsIdentityAndCategoryChangesWithoutReplacingDraft()
    {
        await RenderDesigner((designer, _, _) =>
        {
            var baseline = (string)Invoke(designer, "CaptureAuthoringDraft")!;
            var original = JsonSerializer.Deserialize<FlowViewModel>(baseline, Json)!;
            foreach (var next in new[] { original with { Id = "other" }, original with { Category = "Customer B" } })
            {
                var error = Assert.Throws<TargetInvocationException>(() => Invoke(designer, "ApplyAuthoringProposal",
                    new FlowAuthoringProposal(baseline, JsonSerializer.Serialize(next, Json))));
                Assert.Contains("cannot change", error.InnerException!.Message);
                Assert.Equal(baseline, Invoke(designer, "CaptureAuthoringDraft"));
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MalformedNestedProposalDoesNotPartiallyClearTheCanvas()
    {
        await RenderDesigner((designer, _, _) =>
        {
            var baseline = (string)Invoke(designer, "CaptureAuthoringDraft")!;
            var malformed = System.Text.Json.Nodes.JsonNode.Parse(baseline)!;
            malformed["deliveries"]![0]!["render"] = null;
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(designer, "ApplyAuthoringProposal",
                new FlowAuthoringProposal(baseline, malformed.ToJsonString())));
            Assert.Contains("incomplete steps", error.InnerException!.Message);
            Assert.Equal(baseline, Invoke(designer, "CaptureAuthoringDraft"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ConsentGateDoesNotCallProviderAndUsesNativeChatPageOnlyAfterStarting()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<FlowAssistant>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FlowAssistant.CaptureDraft)] = (Func<Task<string>>)(() => Task.FromResult(JsonSerializer.Serialize(Draft(), Json)))
            }));
            Assert.Contains("Start assistant and share redacted draft", rendered.ToHtmlString());
            Assert.Empty(handler.Requests);
            var component = Assert.Single(activator.Components.OfType<FlowAssistant>());
            Assert.False((bool)Get(component, "_includeEnvironmentSettings")!);
            Assert.Contains("Include environment-specific settings", rendered.ToHtmlString());
            Set(component, "_includeEnvironmentSettings", true);
            await (Task)Invoke(component, "StartAsync")!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("included for this conversation", rendered.ToHtmlString());
            var chatClient = Assert.IsType<AuthoringChatClient>(Get(component, "_client"));
            await chatClient.GetResponseAsync([new(ChatRole.User, "Explain")]);
            Assert.True(handler.ChatRequest!.IncludeEnvironmentSettings);
            Assert.Contains(activator.Components, instance => instance is Microsoft.AspNetCore.Components.AI.ChatPage);
            Assert.Equal(["/api/authoring/settings", "/api/authoring/chat"], handler.Requests);
        });
    }

    [Fact]
    public async Task ClosingAssistantDuringSettingsLoadCancelsInitialization()
    {
        var handler = new Handler { BlockSettingsRead = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<FlowAssistant>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FlowAssistant.CaptureDraft)] = (Func<Task<string>>)(() => throw new InvalidOperationException("Disposed initialization must not capture a draft."))
            }));
            var component = Assert.Single(activator.Components.OfType<FlowAssistant>());
            var pending = (Task)Invoke(component, "StartAsync")!;
            Assert.False(pending.IsCompleted);
            component.Dispose();
            await pending;
            Assert.Null(Get(component, "_agent"));
            Assert.Equal(["/api/authoring/settings"], handler.Requests);
            Assert.True(handler.SettingsReadCanceled);
        });
    }

    [Fact]
    public async Task SettingsNeverRenderAnExistingKeyAndUsePasswordInput()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AiAssistance>()).ToHtmlString());
        Assert.Contains("Configured (hidden)", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("Test saved connection", html);
        Assert.DoesNotContain("private-path", html);
        Assert.Equal(["/api/authoring/settings"], handler.Requests);
    }

    [Fact]
    public async Task CopilotSettingsExplainSharedIdentityAndDoNotShowCustomEndpoint()
    {
        var handler = new Handler { Settings = new(true, "GitHubCopilot", "copilot-test-model", "", true, 2048) };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AiAssistance>()).ToHtmlString());
        Assert.Contains("GitHub Copilot", html);
        Assert.Contains("Connect or manage your GitHub account", html);
        Assert.Contains("Each Mulse user connects their own account", html);
        Assert.Contains("shared Service tokens are not used", html);
        Assert.DoesNotContain("type=\"password\"", html);
        Assert.DoesNotContain("<label>API endpoint", html);
        Assert.Equal(["/api/authoring/settings"], handler.Requests);
    }

    [Fact]
    public async Task SwitchingToCopilotClearsUnsavedKeyAndSavesWithoutEndpoint()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<AiAssistance>();
            var component = Assert.Single(activator.Components.OfType<AiAssistance>());
            Set(component, "_apiKey", "unsaved-openai-key");
            Set(component, "_provider", "GitHubCopilot");
            Invoke(component, "ProviderChanged");
            Assert.Equal("", Get(component, "_apiKey"));
            Assert.Equal("", Get(component, "_endpoint"));
            Assert.Equal("", Get(component, "_model"));
            Set(component, "_model", "copilot-test-model");
            Set(component, "_apiKey", "github_pat_ui-test");
            await (Task)Invoke(component, "SaveAsync")!;
            var request = Assert.IsType<UpdateAiAuthoringSettingsViewModel>(handler.SettingsRequest);
            Assert.Equal("GitHubCopilot", request.Provider);
            Assert.Equal("", request.Endpoint);
            Assert.Null(request.ApiKey);
            Assert.Equal("", Get(component, "_apiKey"));
            Assert.Equal(["/api/authoring/settings", "/api/authoring/settings"], handler.Requests);
        });
    }

    [Fact]
    public async Task FailedSettingsLoadDoesNotShowEditableDefaults()
    {
        var handler = new Handler { SettingsFailure = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<AiAssistance>()).ToHtmlString());
        Assert.Contains("Unable to load AI settings", html);
        Assert.Contains("Retry loading settings", html);
        Assert.DoesNotContain("type=\"password\"", html);
        Assert.DoesNotContain("Test saved connection", html);
        Assert.Equal(["/api/authoring/settings"], handler.Requests);
    }

    [Fact]
    public async Task CopilotDropdownLoadsWithoutSavingAndPersistsTheSelectedId()
    {
        var handler = new Handler();
        await RenderSettings(handler, async (component, rendered) =>
        {
            Assert.Contains("placeholder=\"Provider model or deployment name\"", rendered.ToHtmlString());
            Set(component, "_provider", "GitHubCopilot");
            Invoke(component, "ProviderChanged");
            await (Task)Invoke(component, "LoadModelsAsync")!;
            await component.SetParametersAsync(ParameterView.Empty);
            var html = rendered.ToHtmlString();
            Assert.Contains("Refresh available models", html);
            Assert.Contains("value=\"catalog-model\"", html);
            Assert.Contains("Catalog display name (catalog-model)", html);
            Assert.DoesNotContain("placeholder=\"Provider model or deployment name\"", html);
            Assert.Equal("", Get(component, "_model"));
            Assert.Null(handler.SettingsRequest);
            Set(component, "_model", "catalog-model");
            await (Task)Invoke(component, "SaveAsync")!;
            Assert.Equal("catalog-model", handler.SettingsRequest!.Model);
            Assert.Equal("GitHubCopilot", handler.SettingsRequest.Provider);
            Assert.Null(handler.SettingsRequest.ApiKey);
            Assert.Equal(["/api/authoring/settings", "/api/authoring/copilot/models", "/api/authoring/settings"], handler.Requests);
        });
    }

    [Fact]
    public async Task RefreshDoesNotSilentlyReplaceSavedModelMissingFromCurrentAccount()
    {
        var handler = new Handler { Settings = new(true, "GitHubCopilot", "previous-model", "", false, 2048) };
        await RenderSettings(handler, async (component, rendered) =>
        {
            Assert.Contains("availability not checked", rendered.ToHtmlString());
            await (Task)Invoke(component, "LoadModelsAsync")!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("previous-model (not in your available models)", rendered.ToHtmlString());
            Assert.Equal("previous-model", Get(component, "_model"));
            Assert.Null(handler.SettingsRequest);
            Set(component, "_provider", "OpenAI");
            Invoke(component, "ProviderChanged");
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotContain("catalog-model", rendered.ToHtmlString());
            Assert.Contains("placeholder=\"Provider model or deployment name\"", rendered.ToHtmlString());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelDiscoveryFailuresAreVisibleAndCanBeRetried(bool httpFailure)
    {
        var handler = new Handler
        {
            Settings = new(true, "GitHubCopilot", "saved-model", "", false, 2048),
            ModelsFailure = httpFailure,
            Models = []
        };
        await RenderSettings(handler, async (component, rendered) =>
        {
            await (Task)Invoke(component, "LoadModelsAsync")!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("Unable to load Copilot models", rendered.ToHtmlString());
            Assert.Contains("Load available models", rendered.ToHtmlString());
            Assert.Equal("saved-model", Get(component, "_model"));
            Assert.Null(handler.SettingsRequest);
            handler.ModelsFailure = false;
            handler.Models = [new("recovered-model", "Recovered")];
            await (Task)Invoke(component, "LoadModelsAsync")!;
            await component.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotContain("Unable to load Copilot models", rendered.ToHtmlString());
            Assert.Contains("recovered-model", rendered.ToHtmlString());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposingOrSwitchingProviderCancelsDiscoveryAndRejectsLateCatalog(bool dispose)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler { Settings = new(true, "GitHubCopilot", "saved-model", "", false, 2048), ModelsWait = release.Task };
        await RenderSettings(handler, async (component, _) =>
        {
            var load = (Task)Invoke(component, "LoadModelsAsync")!;
            Assert.False(load.IsCompleted);
            if (dispose) component.Dispose();
            else { Set(component, "_provider", "OpenAI"); Invoke(component, "ProviderChanged"); }
            release.SetResult();
            await load;
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<AiAuthoringModelViewModel>>(Get(component, "_models")));
            Assert.False((bool)Get(component, "_modelsLoaded")!);
            Assert.Null(handler.SettingsRequest);
        });
    }

    private static async Task RenderSettings(Handler handler, Func<AiAssistance, Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent, Task> test)
    {
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http, copilotCredential: new DiscoveryCredential())).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<AiAssistance>();
            await test(Assert.Single(activator.Components.OfType<AiAssistance>()), rendered);
        });
    }

    private sealed class DiscoveryCredential : IUserCopilotCredential
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("gho_ui-catalog-test");
    }

    private static async Task RenderDesigner(Func<Designer, Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigationManager()).AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Designer>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Designer.FlowId)] = "orders" }));
            await test(Assert.Single(activator.Components.OfType<Designer>()), rendered, handler);
        });
    }

    private static object? Invoke(object instance, string name, params object?[] args)
        => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static object? Get(object instance, string name) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private sealed class Activator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            Components.Add(component); return component;
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
        public List<string> Requests { get; } = [];
        public int Mutations { get; private set; }
        public AiAuthoringRequestViewModel? ChatRequest { get; private set; }
        public UpdateAiAuthoringSettingsViewModel? SettingsRequest { get; private set; }
        public AiAuthoringSettingsViewModel Settings { get; init; } = new(true, "OpenAI", "test-model", "https://example.test/v1", true, 4096);
        public Action? OnChatResponse { get; init; }
        public bool SettingsFailure { get; init; }
        public bool BlockSettingsRead { get; init; }
        public bool SettingsReadCanceled { get; private set; }
        public bool ModelsFailure { get; set; }
        public IReadOnlyList<AiAuthoringModelViewModel> Models { get; set; } = [new("catalog-model", "Catalog display name")];
        public Task? ModelsWait { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == "/api/authoring/copilot/models")
            {
                Assert.Equal("gho_ui-catalog-test", request.Headers.GetValues(Mulse.ServiceDefaults.Security.CopilotUserCredential.HeaderName).Single());
                if (ModelsWait is not null) await ModelsWait;
                return ModelsFailure
                    ? new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "Reconnect your GitHub account." }) }
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(Models) };
            }
            if (BlockSettingsRead && path == "/api/authoring/settings")
            {
                using var registration = cancellationToken.Register(() => SettingsReadCanceled = true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (SettingsFailure && path == "/api/authoring/settings")
                return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Settings unavailable") };
            if (request.Method != HttpMethod.Get && !path.StartsWith("/api/authoring/", StringComparison.Ordinal)) Mutations++;
            object body = path switch
            {
                "/api/flows/orders" => Draft(),
                "/api/authoring/settings" => request.Method == HttpMethod.Put ? await SaveSettings(request, cancellationToken) : Settings,
                "/api/authoring/chat" => await Chat(request, cancellationToken),
                _ => Array.Empty<object>()
            };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
        private async Task<AiAuthoringSettingsViewModel> SaveSettings(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SettingsRequest = await request.Content!.ReadFromJsonAsync<UpdateAiAuthoringSettingsViewModel>(cancellationToken);
            return new(SettingsRequest!.Enabled, SettingsRequest.Provider, SettingsRequest.Model, SettingsRequest.Endpoint, true, SettingsRequest.MaxOutputTokens);
        }
        private async Task<AiAuthoringResponseViewModel> Chat(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ChatRequest = await request.Content!.ReadFromJsonAsync<AiAuthoringRequestViewModel>(cancellationToken);
            OnChatResponse?.Invoke();
            return new("Review the proposal", JsonSerializer.Serialize(Draft() with { Name = "Renamed orders", Enabled = false, RunOnStartup = false }, Json), []);
        }
    }
}
