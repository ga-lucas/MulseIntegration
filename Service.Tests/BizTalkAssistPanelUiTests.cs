using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class BizTalkAssistPanelUiTests
{
    private const string PreviewPath = "/api/authoring/biztalk-assist/preview";
    private const string AssistPath = "/api/authoring/biztalk-assist";

    private static readonly BizTalkEvidenceRequestViewModel Request = new("bindings", "bindings", "Export the deployed bindings", "Because it matters.",
        ["Open the console", "Export it"], [BizTalkEvidenceKinds.Bindings], "Needed.", ["billing-send"], "Bindings can hold passwords.");

    private static readonly BizTalkEvidenceFindingViewModel Finding = new("e1", "bindings.xml", BizTalkEvidenceKinds.Bindings, "Bindings", true,
        ["1 receive port(s): RP.In"], ["1 credential-looking value(s)."], 1);

    private static BizTalkAssistDisclosureViewModel Disclosure(bool available = true, bool content = false, string? unavailable = null, string sha = "HASH-1")
        => new(available, unavailable, "OpenAI", "gpt-test", "Untrusted context data. PAYLOAD-MARKER", sha, content, [Finding], ["Only the request text is sent."]);

    private static readonly BizTalkAssistResponseViewModel Reply = new("Open the BizTalk Administration Console and export the bindings.", [Finding], "OpenAI", "gpt-test");

    private static BizTalkAssistEvidenceViewModel Evidence() => new("e1", BizTalkEvidenceKinds.Bindings, BizTalkEvidenceOrigins.Text, "bindings.xml", "<BindingInfo/>");

    [Fact]
    public async Task ExplainShowsExactlyWhatWouldBeSentAndSendsNothingUntilApproved()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("data-testid=\"assist-consent\"", html);
            Assert.Contains("PAYLOAD-MARKER", html);
            Assert.Contains("OpenAI", html);
            Assert.Contains("gpt-test", html);
            Assert.Contains("Nothing has been sent yet.", html);
            Assert.Contains("data-testid=\"assist-send-remember\"", html);
            Assert.DoesNotContain("assist-reply", html);
        });

        Assert.Equal([PreviewPath], fixture.Posts.Select(p => p.Path));
        Assert.Contains("\"operation\":\"explain\"", fixture.Posts[0].Body);
    }

    [Fact]
    public async Task SendingUsesTheApprovedHashAndLabelsTheReplyAsAiGenerated()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure(sha: "HASH-APPROVED");
        fixture.Replies[AssistPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.CallAsync(panel, "SendAsync", false);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("AI-generated reply", html);
            Assert.Contains("It has not been applied to anything.", html);
            Assert.Contains("export the bindings.", html);
            Assert.DoesNotContain("assist-consent", html);
            Assert.False(fixture.Consent.SkipMetadataPrompt);
        });

        Assert.Equal([PreviewPath, AssistPath], fixture.Posts.Select(p => p.Path));
        Assert.Contains("\"disclosureSha256\":\"HASH-APPROVED\"", fixture.Posts[1].Body);
        Assert.Contains("\"operation\":\"explain\"", fixture.Posts[1].Body);
    }

    [Fact]
    public async Task DontAskAgainSkipsTheConfirmationForLaterSendsThisSession()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[AssistPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.CallAsync(panel, "SendAsync", true);
            await fixture.RefreshAsync(panel);
            Assert.True(fixture.Consent.SkipMetadataPrompt);
            Assert.Equal(1, fixture.Posts.Count(p => p.Path == AssistPath));

            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.DoesNotContain("assist-consent", html);
            Assert.Contains("AI-generated reply", html);
        });

        Assert.Equal(2, fixture.Posts.Count(p => p.Path == AssistPath));
    }

    [Fact]
    public async Task ExcerptsAlwaysAskEvenAfterDontAskAgainAndCannotBeRemembered()
    {
        await using var fixture = new PanelFixture();
        fixture.Consent.SkipMetadataPrompt = true;
        fixture.Replies[PreviewPath] = Disclosure(content: true);
        fixture.Replies[AssistPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            fixture.SetField(panel, "_includeContent", true);
            await fixture.CallAsync(panel, "ReviewAsync", true);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("data-testid=\"assist-consent\"", html);
            Assert.DoesNotContain("assist-send-remember", html);
            Assert.Contains("always ask", html);
            Assert.DoesNotContain(fixture.Posts, post => post.Path == AssistPath);

            await fixture.CallAsync(panel, "SendAsync", true);
            Assert.True(fixture.Consent.SkipMetadataPrompt);
        });

        Assert.Contains("\"includeContent\":true", fixture.Posts[0].Body);
    }

    [Fact]
    public async Task ExcerptsAreNotRequestedWhenOnlyTheLocalCheckIsRun()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            fixture.SetField(panel, "_includeContent", true);
            await fixture.CallAsync(panel, "ReviewAsync", false);
        });

        Assert.Contains("\"includeContent\":false", fixture.Posts[0].Body);
    }

    [Fact]
    public async Task TheLocalCheckShowsFindingsWithoutAskingOrSendingAnything()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ReviewAsync", false);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("data-testid=\"assist-findings\"", html);
            Assert.Contains("bindings.xml", html);
            Assert.Contains("1 receive port(s): RP.In", html);
            Assert.Contains("1 credential-looking value(s).", html);
            Assert.DoesNotContain("assist-consent", html);
            Assert.DoesNotContain("assist-reply", html);
        });

        Assert.Equal([PreviewPath], fixture.Posts.Select(p => p.Path));
        Assert.Contains("\"operation\":\"review\"", fixture.Posts[0].Body);
        Assert.Contains("<BindingInfo/>", fixture.Posts[0].Body.Replace("\\u003C", "<").Replace("\\u003E", ">").Replace("\\u002F", "/"));
    }

    [Fact]
    public async Task WhenAiIsNotSetUpTheReasonIsShownAndTheLocalCheckStillApplies()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure(available: false, unavailable: "AI assistance is turned off.");
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ReviewAsync", true);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("AI help is not available: AI assistance is turned off.", html);
            Assert.Contains("data-testid=\"assist-findings\"", html);
            Assert.DoesNotContain("assist-consent", html);
        });

        Assert.DoesNotContain(fixture.Posts, post => post.Path == AssistPath);
    }

    [Fact]
    public async Task AFailedSendIsShownAndNoReplyAppears()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[AssistPath] = HttpStatusCode.Conflict;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.CallAsync(panel, "SendAsync", false);
            await fixture.RefreshAsync(panel);

            var html = fixture.Html();
            Assert.Contains("data-testid=\"assist-error\"", html);
            Assert.Contains("The AI request failed", html);
            Assert.DoesNotContain("assist-reply", html);
            Assert.DoesNotContain("assist-consent", html);
        });
    }

    [Fact]
    public async Task AFailedPreviewIsShownAndNothingIsSent()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = HttpStatusCode.BadRequest;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.RefreshAsync(panel);
            Assert.Contains("Unable to prepare the check", fixture.Html());
        });

        Assert.DoesNotContain(fixture.Posts, post => post.Path == AssistPath);
    }

    [Fact]
    public async Task CancellingDiscardsTheApprovalSoNothingIsSent()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[AssistPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            fixture.Call(panel, "Cancel");
            await fixture.RefreshAsync(panel);
            Assert.DoesNotContain("assist-consent", fixture.Html());

            await fixture.CallAsync(panel, "SendAsync", false);
        });

        Assert.DoesNotContain(fixture.Posts, post => post.Path == AssistPath);
    }

    [Fact]
    public async Task AQuestionIsTrimmedAndSentAndABlankOneIsLeftOut()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            fixture.SetField(panel, "_question", "  We only have PowerShell.  ");
            await fixture.CallAsync(panel, "ExplainAsync");
            fixture.SetField(panel, "_question", "   ");
            await fixture.CallAsync(panel, "ExplainAsync");
        });

        Assert.Contains("\"question\":\"We only have PowerShell.\"", fixture.Posts[0].Body);
        Assert.Contains("\"question\":null", fixture.Posts[1].Body);
    }

    [Fact]
    public async Task ANewSessionConsentClearsWhatWasShownAndApproved()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        fixture.Replies[AssistPath] = Reply;
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ExplainAsync");
            await fixture.CallAsync(panel, "SendAsync", true);
            await fixture.RefreshAsync(panel);
            Assert.Contains("AI-generated reply", fixture.Html());

            await fixture.ReplaceConsentAsync(new BizTalkAssistConsent());
            var html = fixture.Html();
            Assert.DoesNotContain("AI-generated reply", html);
            Assert.DoesNotContain("assist-findings", html);
            Assert.False(fixture.Consent.SkipMetadataPrompt);
        });
    }

    [Fact]
    public async Task ReviewSendsOnlyTheEvidenceTheWizardHandsOver()
    {
        await using var fixture = new PanelFixture();
        fixture.Replies[PreviewPath] = Disclosure();
        await fixture.RunAsync(async panel =>
        {
            await fixture.CallAsync(panel, "ReviewAsync", false);
        });

        using var body = JsonDocument.Parse(fixture.Posts[0].Body);
        var evidence = body.RootElement.GetProperty("evidence");
        Assert.Equal(1, evidence.GetArrayLength());
        Assert.Equal("e1", evidence[0].GetProperty("id").GetString());
        Assert.Equal("Text", evidence[0].GetProperty("origin").GetString());
        Assert.Equal("bindings", body.RootElement.GetProperty("request").GetProperty("id").GetString());
    }

    private sealed class PanelFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;
        private PanelHost? _host;

        public PanelFixture()
        {
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging()
                .AddSingleton<IMulseApiClient>(new MulseApiClient(_http))
                .BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public BizTalkAssistConsent Consent { get; private set; } = new();
        public List<(string Path, string Body)> Posts { get; } = [];
        public Dictionary<string, object> Replies { get; } = new(StringComparer.Ordinal);

        public string Html() => _html!();

        public Task RunAsync(Func<BizTalkAssistPanel, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            BizTalkAssistPanel? panel = null;
            var root = await _renderer.RenderComponentAsync<PanelHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PanelHost.Capture)] = (Action<PanelHost, BizTalkAssistPanel>)((host, value) => { _host = host; panel = value; }),
                [nameof(PanelHost.Consent)] = Consent
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(panel);
            await test(panel);
        });

        public async Task RefreshAsync(BizTalkAssistPanel panel) => await panel.SetParametersAsync(ParameterView.Empty);

        public async Task ReplaceConsentAsync(BizTalkAssistConsent consent)
        {
            Consent = consent;
            _host!.Consent = consent;
            await _host.RefreshAsync();
        }

        private static MethodInfo Method(object target, string name, object?[] args)
            => target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(m => m.Name == name && m.GetParameters().Length == args.Length);

        public object? Call(object target, string name, params object?[] args) => Method(target, name, args).Invoke(target, args);

        public Task CallAsync(object target, string name, params object?[] args) => (Task)Call(target, name, args)!;

        public void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _services.DisposeAsync();
            _http.Dispose();
        }

        private sealed class Handler(PanelFixture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                owner.Posts.Add(new(path, await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (!owner.Replies.TryGetValue(path, out var reply)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return reply is HttpStatusCode status
                    ? new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail = "The server refused it." }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, reply.GetType(), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
        }
    }

    private sealed class PanelHost : ComponentBase
    {
        [Parameter] public Action<PanelHost, BizTalkAssistPanel>? Capture { get; set; }
        [Parameter] public BizTalkAssistConsent Consent { get; set; } = new();

        public Task RefreshAsync() => InvokeAsync(StateHasChanged);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<BizTalkAssistPanel>(0);
            builder.AddComponentParameter(1, nameof(BizTalkAssistPanel.Request), Request);
            builder.AddComponentParameter(2, nameof(BizTalkAssistPanel.GetEvidence), (Func<IReadOnlyList<BizTalkAssistEvidenceViewModel>>)(() => [Evidence()]));
            builder.AddComponentParameter(3, nameof(BizTalkAssistPanel.Consent), Consent);
            builder.AddComponentParameter(4, nameof(BizTalkAssistPanel.HasEvidence), true);
            builder.AddComponentReferenceCapture(5, value => Capture?.Invoke(this, (BizTalkAssistPanel)value));
            builder.CloseComponent();
        }
    }
}
