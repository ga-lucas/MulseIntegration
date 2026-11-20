using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;

namespace Service.Tests;

public sealed class NativeDeliverRetryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 12, 30, 15, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeDeliveries(Func<int, Exception?> failure) : IDeliverModuleInvoker
    {
        public int Calls { get; private set; }
        public Task<IntegrationBatch> DeliverAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
        {
            Calls++;
            if (failure(Calls) is { } error) throw error;
            return Task.FromResult(IntegrationBatch.Empty);
        }
    }

    private static NativeFlowDefinition Flow(NativeDeliverRetry? retry) => new()
    {
        Durable = true,
        Nodes = [new() { Id = "send", Kind = NativeFlowNodeKind.Deliver, Module = "fake-deliver", Retry = retry }]
    };

    private static string Json(NativeFlowDefinition flow) => JsonSerializer.Serialize(flow, JsonSerializerOptions.Web);

    private static IntegrationBatch Input() => new([new("one.json", BinaryData.FromString("{}"), "application/json",
        new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);

    private static (NativeControlFlowAugmentModule Module, InMemoryFlowOrchestrationStateStore Store, ModuleStepDefinition Step) Setup(NativeDeliverRetry? retry, FakeDeliveries deliveries, Clock clock)
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new NativeControlFlowAugmentModule(null!, null!, store, clock, deliveries);
        return (module, store, new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = Json(Flow(retry)) } });
    }

    private static async Task<FlowExecutionContext> Resume(NativeControlFlowAugmentModule module, ModuleStepDefinition step, Clock clock, FlowExecutionContext first)
    {
        var context = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        context.SetOrchestrationInstance("resume", first.OrchestrationCorrelationKey!, "native:resume", true);
        await module.AugmentAsync(context, IntegrationBatch.Empty, step, default);
        return context;
    }

    [Fact]
    public void RetryIsAnOptionalDeliverOnlyFieldWithinBounds()
    {
        var valid = NativeFlowDefinition.Read(Json(Flow(new() { Attempts = 100, IntervalSeconds = 604800, RetryAmbiguous = true })));
        Assert.Equal(100, valid.Nodes[0].Retry!.Attempts);
        Assert.Equal(604800, valid.Nodes[0].Retry!.IntervalSeconds);
        Assert.True(valid.Nodes[0].Retry!.RetryAmbiguous);

        // Flows written before the field existed do not carry it and keep their hash.
        Assert.DoesNotContain("retry", Json(Flow(null)), StringComparison.OrdinalIgnoreCase);

        void Rejected(NativeFlowDefinition flow) => Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(flow)));
        Rejected(Flow(new() { Attempts = 0, IntervalSeconds = 1 }));
        Rejected(Flow(new() { Attempts = 101, IntervalSeconds = 1 }));
        Rejected(Flow(new() { Attempts = 1, IntervalSeconds = -1 }));
        Rejected(Flow(new() { Attempts = 1, IntervalSeconds = 604801 }));
        Rejected(new() { Durable = true, Nodes = [new() { Id = "wait", Kind = NativeFlowNodeKind.Wait, DurationSeconds = 1, Retry = new() { Attempts = 1 } }] });
    }

    [Fact]
    public async Task ANotSentFailureParksTheInstanceUntilTheIntervalThenSendsAgain()
    {
        var clock = new Clock();
        var deliveries = new FakeDeliveries(call => call == 1 ? new DeliveryNotSentException("refused", new SocketException((int)SocketError.ConnectionRefused)) : null);
        var (module, store, step) = Setup(new() { Attempts = 2, IntervalSeconds = 30 }, deliveries, clock);

        var first = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await module.AugmentAsync(first, Input(), step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        Assert.Equal(1, deliveries.Calls);
        Assert.Equal(clock.Now.AddSeconds(30), store.Checkpoints.Single().Native!.DeadlineUtc);
        Assert.Contains("attempt 1 of 3", first.SuspensionReason ?? "", StringComparison.Ordinal);
        Assert.Contains("nothing was sent", first.SuspensionReason ?? "", StringComparison.Ordinal);

        clock.Now = clock.Now.AddSeconds(10);
        var early = await Resume(module, step, clock, first);
        Assert.Equal(FlowExecutionDisposition.Suspended, early.Disposition);
        Assert.Equal(1, deliveries.Calls);

        clock.Now = clock.Now.AddSeconds(20);
        var due = await Resume(module, step, clock, first);
        Assert.NotEqual(FlowExecutionDisposition.Suspended, due.Disposition);
        Assert.Equal(2, deliveries.Calls);
    }

    [Fact]
    public async Task ExhaustedAttemptsEndInTheAmbiguousHoldNotASilentDrop()
    {
        var clock = new Clock();
        var deliveries = new FakeDeliveries(_ => new DeliveryNotSentException("refused", null));
        var (module, _, step) = Setup(new() { Attempts = 1, IntervalSeconds = 0 }, deliveries, clock);

        var first = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await module.AugmentAsync(first, Input(), step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);

        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Resume(module, step, clock, first));
        Assert.Equal(2, deliveries.Calls);
    }

    [Fact]
    public async Task AnAmbiguousFailureIsHeldNotRetriedByDefault()
    {
        var clock = new Clock();
        var deliveries = new FakeDeliveries(_ => new TimeoutException("no reply"));
        var (module, _, step) = Setup(new() { Attempts = 3, IntervalSeconds = 0 }, deliveries, clock);

        var context = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => module.AugmentAsync(context, Input(), step, default));
        Assert.Equal(1, deliveries.Calls);
    }

    [Fact]
    public async Task AnAmbiguousFailureIsRetriedOnlyWhenTheNodeOptsIn()
    {
        var clock = new Clock();
        var deliveries = new FakeDeliveries(call => call == 1 ? new TimeoutException("no reply") : null);
        var (module, _, step) = Setup(new() { Attempts = 3, IntervalSeconds = 0, RetryAmbiguous = true }, deliveries, clock);

        var first = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await module.AugmentAsync(first, Input(), step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        Assert.Contains("ambiguous", first.SuspensionReason ?? "", StringComparison.Ordinal);

        var second = await Resume(module, step, clock, first);
        Assert.NotEqual(FlowExecutionDisposition.Suspended, second.Disposition);
        Assert.Equal(2, deliveries.Calls);
    }

    [Fact]
    public async Task ANodeWithoutRetryNeverRetriesEvenWhenTheFailureIsProvenNotSent()
    {
        var clock = new Clock();
        var deliveries = new FakeDeliveries(_ => new DeliveryNotSentException("refused", null));
        var (module, _, step) = Setup(null, deliveries, clock);

        var context = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => module.AugmentAsync(context, Input(), step, default));
        Assert.Equal(1, deliveries.Calls);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, true)]
    [InlineData(SocketError.HostNotFound, true)]
    [InlineData(SocketError.HostUnreachable, true)]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.TimedOut, false)]
    [InlineData(SocketError.ConnectionReset, false)]
    public void OnlyConnectPhaseSocketErrorsProveNothingWasSent(SocketError error, bool expected)
    {
        Assert.Equal(expected, DeliveryFailureClassifier.IsConnectPhaseFailure(new SocketException((int)error)));
        // The proof is found through wrapping exceptions too.
        Assert.Equal(expected, DeliveryFailureClassifier.IsConnectPhaseFailure(new InvalidOperationException("outer", new IOException("middle", new SocketException((int)error)))));
    }

    [Fact]
    public void HttpAndSmtpClassificationIsConservative()
    {
        Assert.True(DeliveryFailureClassifier.IsConnectPhaseFailure(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")));
        Assert.True(DeliveryFailureClassifier.IsConnectPhaseFailure(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")));
        Assert.False(DeliveryFailureClassifier.IsConnectPhaseFailure(new HttpRequestException(HttpRequestError.ConnectionError, "dropped")));
        Assert.False(DeliveryFailureClassifier.IsConnectPhaseFailure(new TaskCanceledException("timeout")));
        Assert.False(DeliveryFailureClassifier.IsConnectPhaseFailure(new TimeoutException()));
        Assert.False(DeliveryFailureClassifier.IsConnectPhaseFailure(new SmtpFailedRecipientException("rejected")));
        Assert.True(DeliveryFailureClassifier.IsConnectPhaseFailure(new SmtpException(SmtpStatusCode.ClientNotPermitted)));
    }

    [Fact]
    public async Task HttpDeliverReportsARefusedConnectionAsNotSentButADroppedOneAsAmbiguous()
    {
        var module = new Mulse.Modules.BuiltIn.Deliver.HttpDeliverModule(new HttpClientFactoryStub());
        Task Send(string url) => module.DeliverAsync(new FlowExecutionContext("http", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), Input(),
            new ModuleStepDefinition { Module = "http-deliver", Settings = new() { ["url"] = url } }, default);

        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        await Assert.ThrowsAsync<DeliveryNotSentException>(() => Send($"http://127.0.0.1:{closedPort}/"));

        // A server that accepts the connection and drops it mid-request may already have received the data.
        var dropping = new TcpListener(IPAddress.Loopback, 0); dropping.Start();
        try
        {
            var accepted = Task.Run(async () => { using var client = await dropping.AcceptTcpClientAsync(); });
            var failure = await Record.ExceptionAsync(() => Send($"http://127.0.0.1:{((IPEndPoint)dropping.LocalEndpoint).Port}/"));
            await accepted;
            Assert.NotNull(failure);
            Assert.IsNotType<DeliveryNotSentException>(failure);
        }
        finally { dropping.Stop(); }
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(10) };
    }

    [Fact]
    public async Task EditorOffersRetrySettingsOnADeliverNodeAndWarnsAboutAmbiguousRetries()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(NativeDeliverRetry? retry) => await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Mulse.Ui.Components.Shared.NativeFlowEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = Json(Flow(retry)) }))).ToHtmlString());

        var off = await Render(null);
        Assert.Contains("Retry failed sends", off);
        Assert.DoesNotContain("Retry attempts", off);

        var on = await Render(new() { Attempts = 4, IntervalSeconds = 90 });
        Assert.Contains("Retry attempts", on); Assert.Contains("value=\"4\"", on);
        Assert.Contains("Retry interval seconds", on); Assert.Contains("value=\"90\"", on);
        Assert.DoesNotContain("can duplicate it", on);

        var ambiguous = await Render(new() { Attempts = 1, IntervalSeconds = 0, RetryAmbiguous = true });
        Assert.Contains("can duplicate it", ambiguous);
    }
}
