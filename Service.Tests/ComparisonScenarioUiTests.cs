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

public sealed class ComparisonScenarioUiTests
{
    private const string Root = "/api/flow-designer/native/compare/scenarios";
    private static readonly DateTimeOffset Saved = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private static ComparisonScenarioSummaryViewModel Summary(string id = "abc", string name = "Claims", DateTimeOffset? expires = null, string? lastRun = null, bool real = true)
        => new(id, name, 2, false, real, "alice", Saved, expires, lastRun is null ? null : Saved.AddDays(1), lastRun);

    [Fact]
    public async Task SectionExplainsStorageAndRetentionAndNeedsSamplesToSave()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(editor =>
        {
            var html = fixture.Html();
            Assert.Contains("Saved scenarios", html);
            Assert.Contains("stored encrypted", html);
            Assert.Contains("never sent to AI", html);
            Assert.Contains("The definition itself is not saved", html);
            Assert.Contains("Until I delete it", html);
            Assert.Contains("365 days", html);
            Assert.Matches("disabled[^>]*data-testid=\"scenario-save\"|data-testid=\"scenario-save\"[^>]*disabled|disabled[^>]*>Save as scenario", html);
            return Task.CompletedTask;
        });
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task SavePostsSamplesWithRetentionRealDataAndSavedByThenRefreshesTheList()
    {
        await using var fixture = new Fixture();
        fixture.Respond("POST " + Root, Summary(expires: Saved.AddDays(30)));
        fixture.Respond("GET " + Root, new[] { Summary(expires: Saved.AddDays(30)) });
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "s1", "<a>1</a>", input: "<in/>", delivery: "sink");
            fixture.Set(editor, "_scenarioName", "Claims");
            fixture.Set(editor, "_scenarioRetention", "30");
            fixture.Set(editor, "_scenarioSavedBy", "alice");
            await fixture.CallAsync(editor, "SaveScenarioAsync");

            var post = fixture.Requests.First(request => request.Method == "POST");
            using var body = JsonDocument.Parse(post.Body);
            Assert.Equal("Claims", body.RootElement.GetProperty("name").GetString());
            Assert.Equal(30, body.RootElement.GetProperty("retentionDays").GetInt32());
            Assert.True(body.RootElement.GetProperty("containsRealData").GetBoolean());
            Assert.Equal("alice", body.RootElement.GetProperty("savedBy").GetString());
            var pair = body.RootElement.GetProperty("pairs")[0];
            Assert.Equal("<a>1</a>", pair.GetProperty("expected").GetString());
            Assert.Equal("<in/>", pair.GetProperty("input").GetProperty("content").GetString());
            Assert.Equal("sink", pair.GetProperty("deliverModule").GetString());

            var html = fixture.Html();
            Assert.Contains("Saved &#x27;Claims&#x27;", html);
            Assert.Contains("deleted after 2026-03-31 09:30 UTC", html);
            Assert.Contains("(real data)", html);
        });
    }

    [Fact]
    public async Task KeepForeverRetentionSendsNoRetention()
    {
        await using var fixture = new Fixture();
        fixture.Respond("POST " + Root, Summary());
        fixture.Respond("GET " + Root, new[] { Summary() });
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "s1", "x");
            fixture.Set(editor, "_scenarioName", "Claims");
            await fixture.CallAsync(editor, "SaveScenarioAsync");
            using var body = JsonDocument.Parse(fixture.Requests.First(request => request.Method == "POST").Body);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("retentionDays").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("savedBy").ValueKind);
            Assert.Contains("kept until you delete it", fixture.Html());
        });
    }

    [Fact]
    public async Task SavingInvalidSamplesIsRefusedLocally()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "empty", "");
            await fixture.CallAsync(editor, "SaveScenarioAsync");
            Assert.Contains("needs the expected real output", fixture.Html());
        });
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ServerRefusalIsShown()
    {
        await using var fixture = new Fixture();
        fixture.Fail("POST " + Root, HttpStatusCode.Conflict, "At most 50 scenarios can be kept. Delete one first.");
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "s1", "x");
            fixture.Set(editor, "_scenarioName", "Claims");
            await fixture.CallAsync(editor, "SaveScenarioAsync");
            Assert.Contains("At most 50 scenarios can be kept", fixture.Html());
        });
    }

    [Fact]
    public async Task ListShowsRetentionAndLastResultAndAnEmptyState()
    {
        await using var fixture = new Fixture();
        fixture.Respond("GET " + Root, new[] { Summary(lastRun: "Matches 2 of 2 compared samples."), Summary("def", "Old", Saved.AddDays(3), real: false) });
        await fixture.RunAsync(async editor =>
        {
            await fixture.CallAsync(editor, "RefreshScenariosAsync");
            var html = fixture.Html();
            Assert.Contains("Until deleted", html);
            Assert.Contains("2026-03-04 09:30 UTC", html);
            Assert.Contains("Matches 2 of 2 compared samples.", html);
            Assert.Contains("Not run yet", html);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, @"\(real data\)").Count);

            fixture.Respond("GET " + Root, Array.Empty<ComparisonScenarioSummaryViewModel>());
            await fixture.CallAsync(editor, "RefreshScenariosAsync");
            Assert.Contains("No scenarios are saved.", fixture.Html());
        });
    }

    [Fact]
    public async Task RunSendsTheCurrentDefinitionAndShowsResultsWithoutKeepingValues()
    {
        await using var fixture = new Fixture();
        fixture.Respond("POST " + Root + "/abc/run", new NativeFlowCompareResponseViewModel(
            [new("s1", "Different", "Run", [new("Value", "/a/b", "1", "2", false, "The values differ.")], false, null)],
            new(1, 0, 0, 1, 0, "Matches 0 of 1 compared sample; 1 differs.")));
        fixture.Respond("GET " + Root, new[] { Summary(lastRun: "Matches 0 of 1 compared sample; 1 differs.") });
        await fixture.RunAsync(async editor =>
        {
            await fixture.CallAsync(editor, "RunScenarioAsync", Summary());
            using var body = JsonDocument.Parse(fixture.Requests.First(request => request.Path.EndsWith("/run")).Body);
            Assert.Equal("{}", body.RootElement.GetProperty("flowJson").GetString());
            var html = fixture.Html();
            Assert.Contains("s1: differs", html);
            Assert.Contains("never the values", html);
            Assert.Contains("Matches 0 of 1 compared sample; 1 differs.", html);
        });
    }

    [Fact]
    public async Task LoadCopiesSamplesIntoTheEditor()
    {
        await using var fixture = new Fixture();
        fixture.Respond("GET " + Root + "/abc", new ComparisonScenarioViewModel("abc", "Claims",
            [new("loaded-1", "<expected/>", new("p", "<saved-input/>", "application/xml", []), [], "sink")], true, true, "alice", Saved, null));
        await fixture.RunAsync(async editor =>
        {
            await fixture.CallAsync(editor, "LoadScenarioAsync", Summary());
            var html = fixture.Html();
            Assert.Contains("Sample: loaded-1", html);
            Assert.Contains("&lt;saved-input/&gt;", html);
            Assert.Contains("Loaded the samples of", html);
            Assert.Equal("Claims", fixture.Get(editor, "_scenarioName"));
        });
    }

    [Fact]
    public async Task DeleteNeedsConfirmationAndThenRefreshes()
    {
        await using var fixture = new Fixture();
        fixture.Respond("GET " + Root, new[] { Summary() });
        fixture.Respond("DELETE " + Root + "/abc", null);
        await fixture.RunAsync(async editor =>
        {
            await fixture.CallAsync(editor, "RefreshScenariosAsync");
            Assert.DoesNotContain("Confirm delete", fixture.Html());

            fixture.Set(editor, "_confirmDeleteId", "abc");
            await fixture.RefreshAsync(editor);
            Assert.Contains("Confirm delete", fixture.Html());
            Assert.DoesNotContain(fixture.Requests, request => request.Method == "DELETE");

            fixture.Respond("GET " + Root, Array.Empty<ComparisonScenarioSummaryViewModel>());
            await fixture.CallAsync(editor, "DeleteScenarioAsync", Summary());
            Assert.Contains(fixture.Requests, request => request.Method == "DELETE" && request.Path == Root + "/abc");
            var html = fixture.Html();
            Assert.Contains("Deleted &#x27;Claims&#x27;", html);
            Assert.DoesNotContain("Confirm delete", html);
        });
    }

    private sealed record Seen(string Method, string Path, string Body);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = [];
        private static readonly FieldInfo SamplesField = typeof(NativeFlowTestEditor).GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Type SampleType = typeof(NativeFlowTestEditor).GetNestedType("SampleEntry", BindingFlags.NonPublic)!;

        public Fixture()
        {
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(_http)).BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public List<Seen> Requests { get; } = [];

        public void Respond(string route, object? body)
            => _routes[route] = () => body is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };

        public void Fail(string route, HttpStatusCode status, string detail)
            => _routes[route] = () => new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail }) };

        public string Html() => _html!();

        public Task RunAsync(Func<NativeFlowTestEditor, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            NativeFlowTestEditor? editor = null;
            var root = await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<NativeFlowTestEditor>)(value => editor = value)
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(editor);
            await test(editor);
        });

        public async Task RefreshAsync(NativeFlowTestEditor editor) => await editor.SetParametersAsync(ParameterView.Empty);

        public async Task CallAsync(NativeFlowTestEditor editor, string method, params object[] arguments)
        {
            await (Task)typeof(NativeFlowTestEditor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, arguments)!;
            await RefreshAsync(editor);
        }

        public void Set(NativeFlowTestEditor editor, string field, object value)
            => typeof(NativeFlowTestEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

        public object? Get(NativeFlowTestEditor editor, string field)
            => typeof(NativeFlowTestEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);

        public void AddSample(NativeFlowTestEditor editor, string name, string expected, string input = "", string delivery = "")
        {
            var sample = Activator.CreateInstance(SampleType, nonPublic: true)!;
            SampleType.GetProperty("Name")!.SetValue(sample, name);
            SampleType.GetProperty("Expected")!.SetValue(sample, expected);
            SampleType.GetProperty("Input")!.SetValue(sample, input);
            SampleType.GetProperty("Delivery")!.SetValue(sample, delivery);
            var list = SamplesField.GetValue(editor)!;
            list.GetType().GetMethod("Add")!.Invoke(list, [sample]);
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
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add(new(request.Method.Method, path, body));
                return owner._routes.TryGetValue($"{request.Method.Method} {path}", out var respond)
                    ? respond()
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { title = "Not Found", detail = "No route." }) };
            }
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public Action<NativeFlowTestEditor>? Capture { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<NativeFlowTestEditor>(0);
            builder.AddComponentParameter(1, nameof(NativeFlowTestEditor.Value), "{}");
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((NativeFlowTestEditor)value));
            builder.CloseComponent();
        }
    }
}
