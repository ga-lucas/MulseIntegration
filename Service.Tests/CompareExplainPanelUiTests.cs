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

public sealed class CompareExplainPanelUiTests
{
    private const string PreviewPath = "/api/authoring/compare-explain/preview";
    private const string ExplainPath = "/api/authoring/compare-explain";

    private static List<NativeFlowComparePairResultViewModel> Results() =>
    [
        new("PATIENT-SAMPLE-NAME", "Different", "Run", [new("Value", "/a/b", "REAL-EXPECTED-VALUE", "MULSE-ACTUAL-VALUE", false, "SECRET-EXPLANATION")], false, "SECRET-ERROR"),
        new("second", "FormattingOnly", "Run", [new("Whitespace", "/", null, null, true, "Indentation differs.")], false, null)
    ];

    private static CompareExplainDisclosureViewModel Disclosure(bool available = true, string? unavailable = null, string sha = "HASH-1")
        => new(available, unavailable, "OpenAI", "gpt-test", "PAYLOAD-MARKER", sha, ["Only difference classes are sent."]);

    private static readonly CompareExplainResponseViewModel Reply = new("Value differences usually come from a map or a pipeline component.", "OpenAI", "gpt-test");

    [Fact]
    public async Task ExplainsLimitsAndIsDisabledWithoutDifferences()
    {
        await using var fixture = new Fixture([new("s", "Identical", "Run", [], false, null)]);
        await fixture.RunAsync(panel =>
        {
            var html = fixture.Html();
            Assert.Contains("never sees your real output", html);
            Assert.Contains("nothing is sent until you agree", html);
            Assert.Matches("disabled[^>]*data-testid=\"explain-start\"|disabled[^>]*>Explain the differences", html);
            return Task.CompletedTask;
        });
        Assert.Empty(fixture.Posts);
    }

    [Fact]
    public async Task PreviewCarriesOnlyClassesPathsAndVerdictsAndSendsNothingUntilApproved()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("data-testid=\"explain-consent\"", html);
            Assert.Contains("PAYLOAD-MARKER", html);
            Assert.Contains("Nothing has been sent yet.", html);
            Assert.DoesNotContain("explain-reply", html);
        });

        var post = Assert.Single(fixture.Posts);
        Assert.Equal(PreviewPath, post.Path);
        Assert.DoesNotContain("PATIENT-SAMPLE-NAME", post.Body);
        Assert.DoesNotContain("REAL-EXPECTED-VALUE", post.Body);
        Assert.DoesNotContain("MULSE-ACTUAL-VALUE", post.Body);
        Assert.DoesNotContain("SECRET", post.Body);
        using var json = JsonDocument.Parse(post.Body);
        var samples = json.RootElement.GetProperty("samples");
        Assert.Equal(2, samples.GetArrayLength());
        Assert.Equal("Value", samples[0].GetProperty("differences")[0].GetProperty("class").GetString());
        Assert.Equal("/a/b", samples[0].GetProperty("differences")[0].GetProperty("path").GetString());
        Assert.True(samples[1].GetProperty("differences")[0].GetProperty("cosmetic").GetBoolean());
    }

    [Fact]
    public async Task SendingUsesTheApprovedHashAndLabelsTheReply()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure(sha: "HASH-APPROVED");
        fixture.Replies[ExplainPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.CallAsync(panel, "SendAsync", false);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("AI-generated explanation", html);
            Assert.Contains("could not see any values", html);
            Assert.Contains("pipeline component", html);
            Assert.DoesNotContain("explain-consent", html);
            Assert.False(fixture.Consent.SkipMetadataPrompt);
        });

        Assert.Equal([PreviewPath, ExplainPath], fixture.Posts.Select(post => post.Path));
        Assert.Contains("\"disclosureSha256\":\"HASH-APPROVED\"", fixture.Posts[1].Body);
    }

    [Fact]
    public async Task DontAskAgainSkipsTheConfirmationForLaterExplanationsThisSession()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[ExplainPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.CallAsync(panel, "SendAsync", true);
            Assert.True(fixture.Consent.SkipMetadataPrompt);

            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.RefreshAsync(panel);
            Assert.DoesNotContain("explain-consent", fixture.Html());
            Assert.Contains("AI-generated explanation", fixture.Html());
        });

        Assert.Equal(2, fixture.Posts.Count(post => post.Path == ExplainPath));
    }

    [Fact]
    public async Task UnavailableAiShowsWhyAndNeverAsksForConsent()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure(available: false, unavailable: "AI assistance is turned off.");
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.RefreshAsync(panel);
            var html = fixture.Html();
            Assert.Contains("AI help is not available: AI assistance is turned off.", html);
            Assert.DoesNotContain("explain-consent", html);
        });
        Assert.DoesNotContain(fixture.Posts, post => post.Path == ExplainPath);
    }

    [Fact]
    public async Task CancelAndFailuresSendNothingAndAreShown()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[ExplainPath] = HttpStatusCode.Conflict;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            fixture.Call(panel, "Cancel");
            await fixture.RefreshAsync(panel);
            Assert.DoesNotContain("explain-consent", fixture.Html());
            Assert.DoesNotContain(fixture.Posts, post => post.Path == ExplainPath);

            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.CallAsync(panel, "SendAsync", false);
            await fixture.RefreshAsync(panel);
            Assert.Contains("The AI request failed: The server refused it.", fixture.Html());
        });
    }

    [Fact]
    public async Task NewComparisonResultsClearAnOldExplanationAndApproval()
    {
        await using var fixture = new Fixture(Results());
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[ExplainPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "PrepareAsync");
            await fixture.CallAsync(panel, "SendAsync", false);
            await fixture.RefreshAsync(panel);
            Assert.Contains("AI-generated explanation", fixture.Html());

            await fixture.ReplaceResultsAsync(Results());
            await fixture.RefreshAsync(panel);
            Assert.DoesNotContain("AI-generated explanation", fixture.Html());
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private readonly List<NativeFlowComparePairResultViewModel> _initial;
        private Func<string>? _html;
        private Host? _host;

        public Fixture(List<NativeFlowComparePairResultViewModel> results)
        {
            _initial = results;
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(_http)).BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public BizTalkAssistConsent Consent { get; } = new();
        public List<(string Path, string Body)> Posts { get; } = [];
        public Dictionary<string, object> Replies { get; } = new(StringComparer.Ordinal);

        public string Html() => _html!();

        public Task RunAsync(Func<CompareExplainPanel, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            CompareExplainPanel? panel = null;
            var root = await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<Host, CompareExplainPanel>)((host, value) => { _host = host; panel = value; }),
                [nameof(Host.Results)] = _initial,
                [nameof(Host.Consent)] = Consent
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(panel);
            await test(panel);
        });

        public async Task RefreshAsync(CompareExplainPanel panel) => await panel.SetParametersAsync(ParameterView.Empty);

        public async Task ReplaceResultsAsync(List<NativeFlowComparePairResultViewModel> results)
        {
            _host!.Results = results;
            await _host.RefreshAsync();
        }

        private static MethodInfo Method(object target, string name, object?[] args)
            => target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(method => method.Name == name && method.GetParameters().Length == args.Length);

        public object? Call(object target, string name, params object?[] args) => Method(target, name, args).Invoke(target, args);

        public Task CallAsync(object target, string name, params object?[] args) => (Task)Call(target, name, args)!;

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
                owner.Posts.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (!owner.Replies.TryGetValue(path, out var reply)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return reply is HttpStatusCode status
                    ? new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail = "The server refused it." }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, reply.GetType(), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public Action<Host, CompareExplainPanel>? Capture { get; set; }
        [Parameter] public List<NativeFlowComparePairResultViewModel> Results { get; set; } = [];
        [Parameter] public BizTalkAssistConsent Consent { get; set; } = new();

        public Task RefreshAsync() => InvokeAsync(StateHasChanged);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CompareExplainPanel>(0);
            builder.AddComponentParameter(1, nameof(CompareExplainPanel.Results), Results);
            builder.AddComponentParameter(2, nameof(CompareExplainPanel.Consent), Consent);
            builder.AddComponentReferenceCapture(3, value => Capture?.Invoke(this, (CompareExplainPanel)value));
            builder.CloseComponent();
        }
    }
}
