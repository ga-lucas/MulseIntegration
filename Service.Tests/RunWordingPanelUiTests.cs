using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class RunWordingPanelUiTests
{
    private static RunWordingDisclosureViewModel Disclosure(bool available = true, bool errorText = false)
        => new(available, available ? null : "AI assistance is turned off.", "OpenAI", "test-model", "RUN: facts only", "HASH1", errorText, ["Only facts are sent."]);

    [Fact]
    public async Task NothingIsSentUntilTheUserApprovesThePreview()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async (component, consent) =>
        {
            SetField(component, "_question", " What should I check? ");
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("data-testid=\"run-wording-consent\"", html);
            Assert.Contains("RUN: facts only", html);
            Assert.DoesNotContain("run-wording-reply", html);
            Assert.DoesNotContain(fixture.Requests, request => request.Path == "/api/authoring/run-wording/run-1");

            await fixture.CallAsync(component, "SendAsync", false);
            await fixture.RefreshAsync(component);
            html = fixture.Html();
            Assert.Contains("It stopped while parsing.", html);
            Assert.Contains("AI-generated explanation", html);
            Assert.DoesNotContain("run-wording-consent", html);
            Assert.False(consent.SkipMetadataPrompt);
        });
        using var preview = JsonDocument.Parse(fixture.Requests[0].Body);
        Assert.Equal("What should I check?", preview.RootElement.GetProperty("question").GetString());
        Assert.False(preview.RootElement.GetProperty("includeErrorText").GetBoolean());
        Assert.Equal(("POST", "/api/authoring/run-wording/run-1"), (fixture.Requests[1].Method, fixture.Requests[1].Path));
        using var send = JsonDocument.Parse(fixture.Requests[1].Body);
        Assert.Equal("HASH1", send.RootElement.GetProperty("disclosureSha256").GetString());
    }

    [Fact]
    public async Task DontAskAgainSkipsTheReviewButErrorTextAlwaysAsks()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async (component, consent) =>
        {
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.CallAsync(component, "SendAsync", true);
            Assert.True(consent.SkipMetadataPrompt);

            fixture.Requests.Clear();
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.RefreshAsync(component);
            Assert.Equal(["/api/authoring/run-wording/run-1/preview", "/api/authoring/run-wording/run-1"], fixture.Requests.Select(request => request.Path));
            Assert.DoesNotContain("run-wording-consent", fixture.Html());

            fixture.Disclosure = Disclosure(errorText: true);
            fixture.Requests.Clear();
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.RefreshAsync(component);
            Assert.Single(fixture.Requests);
            var html = fixture.Html();
            Assert.Contains("run-wording-consent", html);
            Assert.DoesNotContain("run-wording-send-remember", html);
            Assert.DoesNotContain("run-wording-reply", html);
        });
    }

    [Fact]
    public async Task SaysWhenAiIsUnavailableAndShowsServerRefusals()
    {
        await using var fixture = new Fixture();
        fixture.Disclosure = Disclosure(available: false);
        await fixture.RunAsync(async (component, _) =>
        {
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("run-wording-unavailable", html);
            Assert.Contains("AI assistance is turned off.", html);
            Assert.DoesNotContain("run-wording-consent", html);
            Assert.Single(fixture.Requests);

            fixture.Disclosure = Disclosure();
            await fixture.CallAsync(component, "PrepareAsync");
            fixture.Status = HttpStatusCode.Conflict;
            await fixture.CallAsync(component, "SendAsync", false);
            await fixture.RefreshAsync(component);
            html = fixture.Html();
            Assert.Contains("The AI request failed", html);
            Assert.DoesNotContain("run-wording-reply", html);
        });
    }

    [Fact]
    public async Task ChangingTheRunDropsAPendingApprovalAndAnOldReply()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async (component, _) =>
        {
            await fixture.CallAsync(component, "PrepareAsync");
            await fixture.RefreshAsync(component);
            Assert.Contains("run-wording-consent", fixture.Html());

            await component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(RunWordingPanel.ExecutionId)] = "run-2" }));
            Assert.DoesNotContain("run-wording-consent", fixture.Html());

            await fixture.CallAsync(component, "PrepareAsync");
            Assert.Equal("/api/authoring/run-wording/run-2/preview", fixture.Requests[^1].Path);
        });
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;

        public Fixture()
        {
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(_http)).BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        public RunWordingDisclosureViewModel Disclosure { get; set; } = RunWordingPanelUiTests.Disclosure();
        public HttpStatusCode? Status { get; set; }
        public string Html() => _html!();

        public Task RunAsync(Func<RunWordingPanel, BizTalkAssistConsent, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            RunWordingPanel? component = null;
            var consent = new BizTalkAssistConsent();
            var root = await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<RunWordingPanel>)(value => component = value),
                [nameof(Host.Consent)] = consent
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(component);
            await test(component, consent);
        });

        public async Task RefreshAsync(RunWordingPanel component) => await component.SetParametersAsync(ParameterView.Empty);

        public async Task CallAsync(object target, string name, params object[] arguments)
        {
            var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
            await (Task)method.Invoke(target, arguments)!;
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
            _http.Dispose();
        }

        private sealed class Handler(Fixture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                owner.Requests.Add((request.Method.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
                if (path.EndsWith("/preview", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(owner.Disclosure) };
                if (owner.Status is { } status)
                    return new(status) { Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "What would be sent no longer matches." }) };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new FlowWordingResponseViewModel("It stopped while parsing.", "OpenAI", "test-model")) };
            }
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public Action<RunWordingPanel>? Capture { get; set; }
        [Parameter] public BizTalkAssistConsent Consent { get; set; } = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RunWordingPanel>(0);
            builder.AddAttribute(1, nameof(RunWordingPanel.ExecutionId), "run-1");
            builder.AddAttribute(2, nameof(RunWordingPanel.Consent), Consent);
            builder.AddComponentReferenceCapture(3, component => Capture?.Invoke((RunWordingPanel)component));
            builder.CloseComponent();
        }
    }
}
