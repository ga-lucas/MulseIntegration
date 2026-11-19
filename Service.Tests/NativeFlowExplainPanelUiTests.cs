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

public sealed class NativeFlowExplainPanelUiTests
{
    private const string Flow = """{"version":1,"nodes":[]}""";

    private static FlowExplanationViewModel Explanation() => new(true, 2, new() { ["Deliver"] = 1, ["Wait"] = 1 },
        [new(0, "w", "Wait", "Pause", null, null, "Waits.", ["5 second(s)"]), new(0, "d", "Deliver", "Send file", "sftp", "SFTP deliver", "Sends.", ["No automatic retry."])],
        ["Sends data with SFTP deliver"],
        [new("Warning", "Send file has no value for the required setting Host.", "d")],
        "Facts only.");

    private static NodeExplanationViewModel NodeExplanation() => new(new(0, "d", "Deliver", "Send file", "sftp", "SFTP deliver", "Sends.", []),
        new("Deliver", "Deliver", "Sends data.", "Sends", "External effect", []), "Sends a file over SFTP.",
        [new("host", "Host", "Server name", true, "not set"), new("password", "Password", "Login", false, "refers to a secret")],
        ["Effect: Sends."], []);

    [Fact]
    public async Task ExplainsTheFlowFromTheServerOnlyAndShowsFindingsAndSteps()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/assist/explain/flow"] = Explanation();
        await fixture.RunAsync(async component =>
        {
            await fixture.CallAsync(component, "ExplainFlowAsync");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("2 step(s), durable", html);
            Assert.Contains("1 Deliver, 1 Wait", html);
            Assert.Contains("Send file has no value for the required setting Host.", html);
            Assert.Contains("SFTP deliver", html);
            Assert.Contains("No automatic retry.", html);
            Assert.Contains("Sends data with SFTP deliver", html);
            Assert.Contains("Facts only.", html);
        });
        var request = Assert.Single(fixture.Requests);
        Assert.Equal(("POST", "/api/assist/explain/flow"), (request.Method, request.Path));
        Assert.Equal(Flow, JsonDocument.Parse(request.Body).RootElement.GetProperty("flowJson").GetString());
    }

    [Fact]
    public async Task ExplainsOneStepWithSettingStatesOnly()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/assist/explain/node"] = NodeExplanation();
        await fixture.RunAsync(async component =>
        {
            await fixture.CallAsync(component, "ExplainNodeAsync", "d");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("data-testid=\"explain-settings\"", html);
            Assert.Contains("not set", html);
            Assert.Contains("refers to a secret", html);
            Assert.Contains("Sends a file over SFTP.", html);
        });
        using var body = JsonDocument.Parse(Assert.Single(fixture.Requests).Body);
        Assert.Equal("d", body.RootElement.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task ShowsTheServerRefusal()
    {
        await using var fixture = new Fixture();
        fixture.Status = HttpStatusCode.BadRequest;
        await fixture.RunAsync(async component =>
        {
            await fixture.CallAsync(component, "ExplainFlowAsync");
            await fixture.RefreshAsync(component);
            var html = fixture.Html();
            Assert.Contains("data-testid=\"explain-error\"", html);
            Assert.Contains("The definition is not valid.", html);
            Assert.DoesNotContain("explain-summary", html);
        });
    }

    [Fact]
    public async Task ChangingTheDefinitionClearsTheOldExplanation()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/assist/explain/flow"] = Explanation();
        await fixture.RunAsync(async component =>
        {
            await fixture.CallAsync(component, "ExplainFlowAsync");
            await fixture.RefreshAsync(component);
            Assert.Contains("explain-summary", fixture.Html());

            await component.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = """{"version":1,"nodes":[{"id":"x"}]}""" }));
            Assert.DoesNotContain("explain-summary", fixture.Html());
        });
    }

    [Fact]
    public async Task LooksUpGlossaryTermsAndSaysWhenNothingMatches()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/assist/glossary"] = new[] { new AssistGlossaryEntryViewModel("Orchestration", "A long-running process.", "A native flow.", []) };
        await fixture.RunAsync(async component =>
        {
            typeof(NativeFlowExplainPanel).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "orch ");
            await fixture.CallAsync(component, "SearchAsync");
            await fixture.RefreshAsync(component);
            Assert.Contains("A long-running process.", fixture.Html());

            fixture.Replies["/api/assist/glossary"] = Array.Empty<AssistGlossaryEntryViewModel>();
            await fixture.CallAsync(component, "SearchAsync");
            await fixture.RefreshAsync(component);
            Assert.Contains("glossary-empty", fixture.Html());
        });
        Assert.Equal("/api/assist/glossary", fixture.Requests[0].Path);
        Assert.Equal("?search=orch", fixture.Requests[0].Query);
    }

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

        public List<(string Method, string Path, string Query, string Body)> Requests { get; } = [];
        public Dictionary<string, object> Replies { get; } = [];
        public HttpStatusCode? Status { get; set; }
        public string Html() => _html!();

        public Task RunAsync(Func<NativeFlowExplainPanel, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            NativeFlowExplainPanel? component = null;
            var root = await _renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Capture)] = (Action<NativeFlowExplainPanel>)(value => component = value)
            }));
            _html = root.ToHtmlString;
            Assert.NotNull(component);
            await test(component);
        });

        public async Task RefreshAsync(NativeFlowExplainPanel component) => await component.SetParametersAsync(ParameterView.Empty);

        public Task CallAsync(object target, string name, params object[] arguments)
        {
            var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
            return (Task)method.Invoke(target, arguments)!;
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
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body));
                if (owner.Status is { } status)
                    return new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "Refused", detail = "The definition is not valid." }) };
                var reply = owner.Replies[request.RequestUri.AbsolutePath];
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, reply.GetType(), options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
        }
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public Action<NativeFlowExplainPanel>? Capture { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<NativeFlowExplainPanel>(0);
            builder.AddComponentParameter(1, nameof(NativeFlowExplainPanel.Value), Flow);
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((NativeFlowExplainPanel)value));
            builder.CloseComponent();
        }
    }
}
