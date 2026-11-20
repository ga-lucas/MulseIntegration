using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class NativeListenTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Invoker : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Task<IntegrationBatch> RenderAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Calls.Add(module + ":" + string.Join(",", batch.Payloads.Select(payload => payload.Name))); return Task.FromResult(batch); }
        public Task<IntegrationBatch> AugmentAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
        public Task<IntegrationBatch> DeliverAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
    }

    private static NativeFlowDefinition Flow(params NativeFlowNode[] nodes) => new() { Durable = true, Nodes = nodes.ToList() };
    private static NativeFlowNode Render(string module) => new() { Kind = NativeFlowNodeKind.Render, Module = module };
    private static NativeFlowNode Receive(string name, int seconds = 0) => new()
    {
        Kind = NativeFlowNodeKind.Receive, MessageName = name, Channel = "input", DurationSeconds = seconds, CorrelationKeys = ["tenant"],
        Conditions = [new() { Source = DecisionValueSourceKind.Metadata, Path = "kind", Operator = DecisionComparisonOperator.Equals, Value = "accepted" }]
    };
    private static NativeFlowNode Wait(int seconds) => new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = seconds };
    private static NativeFlowNode Branch(params NativeFlowNode[] nodes) => new() { Kind = NativeFlowNodeKind.Sequence, Children = [.. nodes] };
    private static NativeFlowNode Listen(params NativeFlowNode[] branches) => new() { Kind = NativeFlowNodeKind.Listen, Children = [.. branches] };
    private static NativeFlowNode Set(string name, int value) => new()
    { Kind = NativeFlowNodeKind.SetVariable, Variable = name, Expression = new() { Literal = NativeScalar.From(value) } };
    private static NativeFlowNode Increment(string name) => new()
    {
        Kind = NativeFlowNodeKind.SetVariable, Variable = name,
        Expression = new() { Kind = NativeExpressionKind.Add, Operands = [new() { Kind = NativeExpressionKind.Variable, Variable = name }, new() { Literal = NativeScalar.From(1) }] }
    };
    private static IntegrationBatch Input(string id, string tenant = "T", string channel = "input", string kind = "accepted") => new([
        new(id + ".json", BinaryData.FromString("{\"value\":\"" + id + "\"}"), "application/json",
            new Dictionary<string, string> { ["messageId"] = id, ["group"] = "A", ["tenant"] = tenant, ["channel"] = channel, ["kind"] = kind })]);
    private static FlowExecutionContext Context(Clock clock, string? key = null)
    {
        var context = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        if (key is not null) context.SetOrchestrationInstance("resume", key, "native:resume", true);
        return context;
    }
    private static Task<IntegrationBatch> Run(IFlowOrchestrationStateStore store, Clock clock, Invoker invoker, NativeFlowDefinition flow, FlowExecutionContext context, IntegrationBatch input)
        => new NativeControlFlowAugmentModule(invoker, invoker, store, clock, invoker).AugmentAsync(context, input,
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) } }, default);

    // first message -> Listen{ next message | 60 second timeout } -> after
    private static NativeFlowDefinition Simple() => Flow(Receive("first"), Render("start"),
        Listen(Branch(Receive("next"), Render("got")), Branch(Wait(60), Render("timeout"))), Render("after"));

    [Fact]
    public async Task AMatchingMessageWinsTheListenAndTheTimeoutBranchNeverRuns()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker(); var flow = Simple();
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        Assert.Equal(clock.Now.AddSeconds(60), store.Checkpoints.Single().Native!.DeadlineUtc);
        clock.Now = clock.Now.AddSeconds(10);
        var second = Context(clock, first.OrchestrationCorrelationKey);
        var output = await Run(store, clock, invoker, flow, second, Input("two"));
        Assert.Equal(new[] { "start:one.json", "got:two.json", "after:two.json" }, invoker.Calls);
        Assert.Equal("two.json", Assert.Single(output.Payloads).Name);
        Assert.NotNull(second.PendingCheckpointCompletion);
    }

    [Fact]
    public async Task TheTimeoutBranchRunsOnceWhenTheDeadlinePassesAndLateMessagesAreRejected()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker(); var flow = Simple();
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        var deadline = clock.Now.AddSeconds(60);
        clock.Now = clock.Now.AddSeconds(30);
        // A timer wake-up before the deadline keeps waiting and does not re-arm the deadline.
        var early = Context(clock, first.OrchestrationCorrelationKey);
        await Run(store, clock, invoker, flow, early, IntegrationBatch.Empty);
        Assert.Equal(FlowExecutionDisposition.Suspended, early.Disposition);
        Assert.Equal(deadline, store.Checkpoints.Single().Native!.DeadlineUtc);
        clock.Now = deadline;
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), Input("late")));
        var timer = Context(clock, first.OrchestrationCorrelationKey);
        var output = await Run(store, clock, invoker, flow, timer, IntegrationBatch.Empty);
        Assert.Equal(new[] { "start:one.json", "timeout:one.json", "after:one.json" }, invoker.Calls);
        Assert.Equal("one.json", Assert.Single(output.Payloads).Name);
    }

    [Theory]
    [InlineData("T2", "input", "accepted", "two")]
    [InlineData("T", "other", "accepted", "two")]
    [InlineData("T", "input", "rejected", "two")]
    [InlineData("T", "input", "accepted", "one")]
    public async Task NonMatchingOrDuplicateMessagesLeaveTheListenWaitingAndUnacknowledged(string tenant, string channel, string kind, string id)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker(); var flow = Simple();
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        var before = JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web);
        var bad = Context(clock, first.OrchestrationCorrelationKey);
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow, bad, Input(id, tenant, channel, kind)));
        Assert.Null(bad.PendingCheckpointCompletion);
        Assert.Equal(before, JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web));
        Assert.Equal(new[] { "start:one.json" }, invoker.Calls);
    }

    [Fact]
    public async Task TheFirstMatchingMessageBranchInSourceOrderIsChosen()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Receive("first"), Listen(Branch(Receive("a"), Render("branch-a")), Branch(Receive("b"), Render("branch-b"))));
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        Assert.Null(store.Checkpoints.Single().Native!.DeadlineUtc);
        await Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), Input("two"));
        Assert.Equal(new[] { "branch-a:two.json" }, invoker.Calls);
    }

    [Fact]
    public async Task ALoopOfListensCollectsMessagesThenFallsThroughOnTimeoutWithADeadlinePerIteration()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Receive("first"), new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "n", VariableType = NativeScalarType.Int32, Expression = new() { Literal = NativeScalar.From(0) } },
            new()
            {
                Kind = NativeFlowNodeKind.While, MaxIterations = 10,
                Expression = new() { Kind = NativeExpressionKind.Less, Operands = [new() { Kind = NativeExpressionKind.Variable, Variable = "n" }, new() { Literal = NativeScalar.From(3) }] },
                Children = [Listen(Branch(Receive("next"), Increment("n"), Render("got")), Branch(Wait(60), Set("n", 99), Render("timeout")))]
            }, Render("after"));
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        var key = first.OrchestrationCorrelationKey;
        Assert.Equal(clock.Now.AddSeconds(60), store.Checkpoints.Single().Native!.DeadlineUtc);
        clock.Now = clock.Now.AddSeconds(30);
        await Run(store, clock, invoker, flow, Context(clock, key), Input("two"));
        Assert.Equal(clock.Now.AddSeconds(60), store.Checkpoints.Single().Native!.DeadlineUtc);
        clock.Now = clock.Now.AddSeconds(60);
        var timer = Context(clock, key);
        await Run(store, clock, invoker, flow, timer, IntegrationBatch.Empty);
        Assert.Equal(new[] { "got:two.json", "timeout:two.json", "after:two.json" }, invoker.Calls);
        Assert.NotNull(timer.PendingCheckpointCompletion);
    }

    [Fact]
    public async Task ATryAroundAListenCatchesAFaultFromInsideAChosenBranch()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var failing = new NativeFlowNode
        {
            Kind = NativeFlowNodeKind.SetVariable, Variable = "n",
            Expression = new() { Kind = NativeExpressionKind.Length, Operands = [new() { Literal = NativeScalar.From(5) }] }
        };
        var flow = Flow(Receive("first"), new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "n", VariableType = NativeScalarType.Int32, Expression = new() { Literal = NativeScalar.From(0) } },
            new() { Kind = NativeFlowNodeKind.Try, ExceptionMessageVariable = "ex", Children = [Listen(Branch(Receive("next"), failing, Render("skipped")), Branch(Wait(60)))], Otherwise = [Render("handled")] });
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        await Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), Input("two"));
        Assert.Equal(new[] { "handled:two.json" }, invoker.Calls);
    }

    [Fact]
    public async Task ATamperedListenCursorIsRejectedInsteadOfResetOrReplayed()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker(); var flow = Simple();
        var first = Context(clock);
        await Run(store, clock, invoker, flow, first, Input("one"));
        var cursor = store.Checkpoints.Single().Native!.State.Stack.Last();
        cursor.DeadlineUtc = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        Assert.Equal(new[] { "start:one.json" }, invoker.Calls);
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
    private static readonly JsonSerializerOptions EditorJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static async Task<string> RenderEditor(NativeFlowDefinition flow)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(
            new Dictionary<string, object?>
                        {
                            ["Value"] = JsonSerializer.Serialize(flow, EditorJson),
                            ["Modules"] = new[] { "start", "got", "timeout", "after" }.Select(id => new Mulse.Ui.Models.ModuleViewModel(id, id, "Render", "Synthetic", [], [], [], [])).ToArray()
                        }))).ToHtmlString());
    }

    [Fact]
    public async Task TheEditorOffersListenExplainsItAndOnlyFlagsMalformedBranchShapes()
    {
        const string incomplete = "Some nodes are incomplete or incompatible";
        var valid = await RenderEditor(Simple());
        Assert.Contains("Listen", valid); Assert.Contains("timeout", valid, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(incomplete, valid);
        Assert.Contains(incomplete, await RenderEditor(Flow(Receive("first"), Listen(Branch(Receive("a"))))));
        Assert.Contains(incomplete, await RenderEditor(Flow(Receive("first"), Listen(Branch(Receive("a", seconds: 5)), Branch(Wait(1))))));
        Assert.Contains(incomplete, await RenderEditor(Flow(Receive("first"), Listen(Branch(Wait(1)), Branch(Wait(2))))));
    }

    [Fact]
    public void ListenDefinitionsAreValidatedForBranchShape()
    {
        static void Invalid(NativeFlowNode listen) => Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(Receive("first"), listen), JsonSerializerOptions.Web)));
        Invalid(Listen(Branch(Receive("a"))));
        Invalid(Listen(Branch(Wait(1)), Branch(Wait(2))));
        Invalid(Listen(Branch(Receive("a")), Branch(Wait(1)), Branch(Wait(2))));
        Invalid(Listen(Branch(Receive("a", seconds: 5)), Branch(Wait(1))));
        Invalid(Listen(Branch(Render("x")), Branch(Receive("a"))));
        Invalid(Listen(Branch(), Branch(Receive("a"))));
        Invalid(Listen(new() { Kind = NativeFlowNodeKind.Receive, MessageName = "a", Channel = "input" }, Branch(Wait(1))));
        var valid = Flow(Receive("first"), Listen(Branch(Receive("a"), Render("x")), Branch(Wait(1))));
        Assert.Equal(NativeFlowNodeKind.Listen, NativeFlowDefinition.Read(JsonSerializer.Serialize(valid, JsonSerializerOptions.Web)).Nodes[1].Kind);
        var notDurable = Flow(Listen(Branch(Receive("a")), Branch(Wait(1))));
        notDurable.Durable = false;
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(notDurable, JsonSerializerOptions.Web)));
    }
}
