using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
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

public sealed class BizTalkRealSamplePairingTests
{
    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private static BizTalkRealSampleResult Pair(Action<BizTalkImportSession> add)
    {
        var session = new BizTalkImportSession();
        add(session);
        return BizTalkRealSamplePairing.Pair(session.Evidence, session.Content);
    }

    [Fact]
    public void PairsByInOutSuffixInAZip()
    {
        var result = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "pairs.zip",
            Zip(("claim1.in.xml", "<a>1</a>"), ("claim1.out.xml", "<b>1</b>"), ("claim2.input.xml", "<a>2</a>"), ("claim2.output.xml", "<b>2</b>")), TimeProvider.System));

        Assert.Empty(result.Problems);
        Assert.Collection(result.Samples,
            first => { Assert.Equal("claim1", first.Name); Assert.Equal("<a>1</a>", first.Input); Assert.Equal("<b>1</b>", first.Expected); Assert.Equal("claim1.in.xml", first.InputName); },
            second => { Assert.Equal("claim2", second.Name); Assert.Equal("<b>2</b>", second.Expected); });
    }

    [Fact]
    public void PairsByFolderAndKeepsSubfoldersApart()
    {
        var result = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "pairs.zip",
            Zip(("in/a/one.xml", "I1"), ("out/a/one.xml", "O1"), ("in/b/one.xml", "I2"), ("out/b/one.xml", "O2")), TimeProvider.System));

        Assert.Empty(result.Problems);
        Assert.Equal(["a/one", "b/one"], result.Samples.Select(sample => sample.Name));
        Assert.Equal("O2", result.Samples[1].Expected);
    }

    [Fact]
    public void PairsSeparateUploadsAndPastedText()
    {
        var result = Pair(s =>
        {
            s.AddText(BizTalkEvidenceKinds.RealOutput, "x.in.txt", "IN", TimeProvider.System);
            s.AddUpload(BizTalkEvidenceKinds.RealOutput, "x.out.txt", Encoding.UTF8.GetBytes("OUT"), TimeProvider.System);
        });

        var sample = Assert.Single(result.Samples);
        Assert.Equal(("x", "IN", "OUT"), (sample.Name, sample.Input, sample.Expected));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ReportsUnpairedUnmarkedAndDuplicateFilesWithoutInventingPairs()
    {
        var result = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "pairs.zip",
            Zip(("only.in.xml", "I"), ("lonely.out.xml", "O"), ("notes.txt", "n"), ("dup.in.xml", "I"), ("dup/in/x.txt", "Z"), ("dup.out.xml", "O")), TimeProvider.System));

        Assert.Equal(["dup"], result.Samples.Select(sample => sample.Name));
        Assert.Contains(result.Problems, p => p.Contains("input for 'only'") && p.Contains("no output"));
        Assert.Contains(result.Problems, p => p.Contains("output for 'lonely'") && p.Contains("no input"));
        Assert.Contains(result.Problems, p => p.Contains("notes.txt") && p.Contains("not marked"));
    }

    [Fact]
    public void IgnoresOtherEvidenceKindsAndPathEvidence()
    {
        var result = Pair(s =>
        {
            s.AddText(BizTalkEvidenceKinds.Bindings, "b.in.xml", "<x/>", TimeProvider.System);
            s.AddPath(BizTalkEvidenceKinds.RealOutput, @"C:\samples", null, TimeProvider.System);
        });

        Assert.Empty(result.Samples);
        Assert.Contains(result.Problems, p => p.Contains("path on the service machine"));
    }

    [Fact]
    public void LeavesOutUnsafeZipPathsBinaryAndOversizedFiles()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in new (string, byte[])[]
            {
                ("../evil.in.xml", [65]), ("ok.in.xml", "I"u8.ToArray()), ("ok.out.xml", "O"u8.ToArray()),
                ("bin.in.dat", [1, 0, 2]), ("bin.out.dat", [3]), ("big.in.xml", new byte[(int)BizTalkRealSamplePairing.MaxFileBytes + 1]), ("big.out.xml", [65])
            })
            {
                using var entry = archive.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        }

        var result = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "pairs.zip", stream.ToArray(), TimeProvider.System));

        Assert.Equal(["ok"], result.Samples.Select(sample => sample.Name));
        Assert.Contains(result.Problems, p => p.Contains("unsafe path"));
        Assert.Contains(result.Problems, p => p.Contains("'bin' is not text"));
        Assert.Contains(result.Problems, p => p.Contains("big.in.xml") && p.Contains("larger than"));
    }

    [Fact]
    public void LimitsZipEntriesAndSampleCount()
    {
        var many = Enumerable.Range(0, 30).SelectMany(i => new[] { ($"s{i:00}.in.txt", "I"), ($"s{i:00}.out.txt", "O") }).ToArray();
        var result = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "many.zip", Zip(many), TimeProvider.System));
        Assert.Equal(BizTalkRealSamplePairing.MaxSamples, result.Samples.Count);
        Assert.Contains(result.Problems, p => p.Contains($"Only the first {BizTalkRealSamplePairing.MaxSamples}"));

        var tooMany = Enumerable.Range(0, BizTalkRealSamplePairing.MaxZipEntries + 1).Select(i => ($"f{i}.in.txt", "x")).ToArray();
        var capped = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "huge.zip", Zip(tooMany), TimeProvider.System));
        Assert.Contains(capped.Problems, p => p.Contains($"more than {BizTalkRealSamplePairing.MaxZipEntries} files"));
    }

    [Fact]
    public void ReportsABrokenZipAndKeepsTheByteOrderMark()
    {
        var broken = Pair(s => s.AddUpload(BizTalkEvidenceKinds.RealOutput, "bad.zip", [(byte)'P', (byte)'K', 3, 4, 9, 9, 9], TimeProvider.System));
        Assert.Empty(broken.Samples);
        Assert.Contains(broken.Problems, p => p.Contains("not a readable zip"));

        var bom = Pair(s =>
        {
            s.AddUpload(BizTalkEvidenceKinds.RealOutput, "m.in.xml", [.. Encoding.UTF8.GetPreamble(), .. "<a/>"u8.ToArray()], TimeProvider.System);
            s.AddUpload(BizTalkEvidenceKinds.RealOutput, "m.out.xml", "<a/>"u8.ToArray(), TimeProvider.System);
        });
        Assert.Equal("\uFEFF<a/>", Assert.Single(bom.Samples).Input);
    }

    [Theory]
    [InlineData("<a/>", "application/xml")]
    [InlineData("\uFEFF  <a/>", "application/xml")]
    [InlineData("MSH|^~\\&|", "text/plain")]
    public void GuessesTheContentType(string text, string expected) => Assert.Equal(expected, BizTalkRealSamplePairing.ContentTypeOf(text));
}

public sealed class BizTalkRealSampleHandOffUiTests
{
    private const string ScenarioPath = "/api/flow-designer/native/compare/scenarios";

    private static BizTalkImportSession SessionWithPair()
    {
        var session = new BizTalkImportSession();
        session.AddUpload(BizTalkEvidenceKinds.RealOutput, "p.zip", Zip(("c1.in.xml", "<a>PATIENT</a>"), ("c1.out.xml", "<b>PATIENT</b>"), ("stray.txt", "x")), TimeProvider.System);
        return session;
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return stream.ToArray();
    }

    [Fact]
    public async Task ExplainsTheNamingAndOffersNothingWithoutSamples()
    {
        await using var fixture = new Fixture(new BizTalkImportSession());
        await fixture.RunAsync(component =>
        {
            var html = fixture.Html();
            Assert.Contains("name.in.xml", html);
            Assert.Contains("Nothing here is sent to an AI provider", html);
            Assert.Contains("Add a real input and output sample above first.", html);
            Assert.DoesNotContain("handoff-save", html);
            return Task.CompletedTask;
        });
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ListsPairsAndProblemsAndSavesOnlyOnRequest()
    {
        await using var fixture = new Fixture(SessionWithPair());
        fixture.Reply = new ComparisonScenarioSummaryViewModel("id1", "import samples", 1, false, true, "me", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, null);
        await fixture.RunAsync(async component =>
        {
            await fixture.RefreshAsync(component);
            Assert.Empty(fixture.Requests);
            Assert.DoesNotContain("handoff-pairs", fixture.Html());

            fixture.Call(component, "Check");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("1 pair(s) found.", html);
            Assert.Contains("<li>c1</li>", html);
            Assert.Contains("stray.txt", html);
            Assert.Empty(fixture.Requests);

            await fixture.CallAsync(component, "SaveAsync");
            await fixture.RefreshAsync(component);
            Assert.Contains("Saved &#x27;import samples&#x27; with 1 pair(s).", fixture.Html());
        });

        var request = Assert.Single(fixture.Requests);
        Assert.Equal(("POST", ScenarioPath), (request.Method, request.Path));
        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal("import samples", root.GetProperty("name").GetString());
        Assert.Equal(30, root.GetProperty("retentionDays").GetInt32());
        Assert.True(root.GetProperty("containsRealData").GetBoolean());
        var pair = Assert.Single(root.GetProperty("pairs").EnumerateArray());
        Assert.Equal("c1", pair.GetProperty("name").GetString());
        Assert.Equal("<b>PATIENT</b>", pair.GetProperty("expected").GetString());
        Assert.Equal("<a>PATIENT</a>", pair.GetProperty("input").GetProperty("content").GetString());
        Assert.Equal("application/xml", pair.GetProperty("input").GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task ShowsTheServerRefusalAndNothingElseIsSaved()
    {
        await using var fixture = new Fixture(SessionWithPair());
        fixture.Reply = HttpStatusCode.Conflict;
        await fixture.RunAsync(async component =>
        {
            fixture.Call(component, "Check");
            await fixture.CallAsync(component, "SaveAsync");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("data-testid=\"handoff-error\"", html);
            Assert.Contains("The server refused it.", html);
            Assert.DoesNotContain("handoff-notice", html);
        });
        Assert.Single(fixture.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly BizTalkImportSession _session;
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;

        public Fixture(BizTalkImportSession session)
        {
            _session = session;
            _http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(_http)).BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        public object Reply { get; set; } = HttpStatusCode.NotFound;
        public string Html() => _html!();

        public Task RunAsync(Func<BizTalkRealSampleHandOff, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            BizTalkRealSampleHandOff? component = null;
            var root = await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<BizTalkRealSampleHandOff>)(value => component = value),
                [nameof(Host.Session)] = _session
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(component);
            await test(component);
        });

        public async Task RefreshAsync(BizTalkRealSampleHandOff component) => await component.SetParametersAsync(ParameterView.Empty);

        private static MethodInfo Method(object target, string name)
            => target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(method => method.Name == name);

        public object? Call(object target, string name) => Method(target, name).Invoke(target, []);
        public Task CallAsync(object target, string name) => (Task)Call(target, name)!;

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
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
                return owner.Reply is HttpStatusCode status
                    ? new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail = "The server refused it." }) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner.Reply, owner.Reply.GetType(), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public Action<BizTalkRealSampleHandOff>? Capture { get; set; }
        [Parameter] public BizTalkImportSession Session { get; set; } = new();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<BizTalkRealSampleHandOff>(0);
            builder.AddComponentParameter(1, nameof(BizTalkRealSampleHandOff.Evidence), (IReadOnlyList<BizTalkSessionEvidence>)Session.Evidence);
            builder.AddComponentParameter(2, nameof(BizTalkRealSampleHandOff.Content), (IReadOnlyDictionary<string, BizTalkEvidenceContent>)Session.Content);
            builder.AddComponentReferenceCapture(3, value => Capture?.Invoke((BizTalkRealSampleHandOff)value));
            builder.CloseComponent();
        }
    }
}
