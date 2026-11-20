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

public sealed class NativeFlowCompareUiTests
{
    private static NativeFlowCompareResponseViewModel Reply(string verdict = "Different") => new(
        [new("s1", verdict, "Run",
            [new("Value", "/a/b", "1", "2", false, "The values differ."), new("Whitespace", "/", null, null, true, "Indentation differs.")], false, null)],
        new(1, 0, 0, 1, 0, "Matches 0 of 1 compared sample (0 exactly, 0 apart from formatting); 1 differs. This is evidence from the supplied samples, not proof that the flows are equivalent."));

    [Fact]
    public async Task SectionExplainsTheLimitsAndStartsWithNothingToCompare()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async editor =>
        {
            var html = fixture.Html();
            Assert.Contains("Compare with real output", html);
            Assert.Contains("does not show the flows are equivalent", html);
            Assert.Contains("never sent to AI", html);
            Assert.Contains("Add real-output sample", html);
            Assert.Matches("disabled[^>]*>Compare with real output</button>", html);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ComparePostsEachSampleWithTheSharedInputAndShowsClassifiedDifferences()
    {
        await using var fixture = new Fixture();
        fixture.Reply = Reply();
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "s1", "<a><b>1</b></a>", input: "", delivery: "");
            await fixture.CompareAsync(editor);

            var post = Assert.Single(fixture.Posts);
            Assert.Equal("/api/flow-designer/native/compare", post.Path);
            using var body = JsonDocument.Parse(post.Body);
            var pair = Assert.Single(body.RootElement.GetProperty("pairs").EnumerateArray());
            Assert.Equal("s1", pair.GetProperty("name").GetString());
            Assert.Equal("<a><b>1</b></a>", pair.GetProperty("expected").GetString());
            Assert.Equal("sample.xml", pair.GetProperty("input").GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, pair.GetProperty("deliverModule").ValueKind);

            var html = fixture.Html();
            Assert.Contains("s1: differs", html);
            Assert.Contains("/a/b", html);
            Assert.Contains("Changes content", html);
            Assert.Contains("Formatting only", html);
            Assert.Contains("not proof that the flows are equivalent", html);
            Assert.Contains("data-testid=\"explain-panel\"", html);
        });
    }

    [Fact]
    public async Task SampleInputAndDeliveryOverrideTheSharedTest()
    {
        await using var fixture = new Fixture();
        fixture.Reply = Reply();
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "own", "<x/>", input: "<own-input/>", delivery: "billing-sink");
            await fixture.CompareAsync(editor);

            using var body = JsonDocument.Parse(Assert.Single(fixture.Posts).Body);
            var pair = body.RootElement.GetProperty("pairs")[0];
            Assert.Equal("<own-input/>", pair.GetProperty("input").GetProperty("content").GetString());
            Assert.Equal("billing-sink", pair.GetProperty("deliverModule").GetString());
        });
    }

    [Fact]
    public async Task InvalidSamplesAreRefusedLocallyWithoutCallingTheServer()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async editor =>
        {
            await fixture.CompareAsync(editor);
            Assert.Contains("Add at least one real-output sample", fixture.Html());

            fixture.AddSample(editor, "dup", "x");
            fixture.AddSample(editor, "DUP", "x");
            await fixture.CompareAsync(editor);
            Assert.Contains("Sample names must be nonempty and unique", fixture.Html());

            fixture.Clear(editor);
            fixture.AddSample(editor, "empty", "");
            await fixture.CompareAsync(editor);
            Assert.Contains("needs the expected real output", fixture.Html());
            Assert.Empty(fixture.Posts);
        });
    }

    [Fact]
    public async Task ServerRefusalIsShownAndEditingTheTestClearsTheResult()
    {
        await using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.BadRequest;
        await fixture.RunAsync(async editor =>
        {
            fixture.AddSample(editor, "s1", "<a/>");
            await fixture.CompareAsync(editor);
            Assert.Contains("The server refused it.", fixture.Html());

            fixture.Status = null;
            fixture.Reply = Reply();
            await fixture.CompareAsync(editor);
            Assert.Contains("s1: differs", fixture.Html());

            typeof(NativeFlowTestEditor).GetMethod("AddMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
            await fixture.RefreshAsync(editor);
            Assert.DoesNotContain("s1: differs", fixture.Html());
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;
        private static readonly MethodInfo CompareMethod = typeof(NativeFlowTestEditor).GetMethod("CompareAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo SamplesField = typeof(NativeFlowTestEditor).GetField("_samples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Type SampleType = typeof(NativeFlowTestEditor).GetNestedType("SampleEntry", BindingFlags.NonPublic)!;

        public Fixture()
        {
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(_http)).BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public List<(string Path, string Body)> Posts { get; } = [];
        public NativeFlowCompareResponseViewModel? Reply { get; set; }
        public HttpStatusCode? Status { get; set; }

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

        public async Task CompareAsync(NativeFlowTestEditor editor)
        {
            await (Task)CompareMethod.Invoke(editor, null)!;
            await RefreshAsync(editor);
        }

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

        public void Clear(NativeFlowTestEditor editor)
        {
            var list = SamplesField.GetValue(editor)!;
            list.GetType().GetMethod("Clear")!.Invoke(list, null);
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
                owner.Posts.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
                if (owner.Status is { } status)
                    return new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail = "The server refused it." }) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner.Reply, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
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
