using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;

namespace Service.Tests;

public sealed class NativeSubSecondWaitTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 12, 30, 15, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static NativeFlowDefinition Flow(NativeFlowNode wait) => new()
    {
        Durable = true,
        Nodes =
        [
            wait,
            new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "done", VariableType = NativeScalarType.Boolean, Expression = new() { Literal = NativeScalar.From(true) } }
        ]
    };

    private static string Json(NativeFlowDefinition flow) => JsonSerializer.Serialize(flow, JsonSerializerOptions.Web);

    [Fact]
    public void MillisecondsAreAnOptionalAdditiveFieldOnAFixedWaitOnly()
    {
        var valid = NativeFlowDefinition.Read(Json(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = 200 })));
        Assert.Equal(200, valid.Nodes[0].DurationMilliseconds);
        Assert.Equal(0, valid.Nodes[0].DurationSeconds);

        var combined = NativeFlowDefinition.Read(Json(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 3, DurationMilliseconds = 999 })));
        Assert.Equal(3, combined.Nodes[0].DurationSeconds); Assert.Equal(999, combined.Nodes[0].DurationMilliseconds);

        // Waits written before the field existed read back with zero milliseconds and do not write the field.
        var legacy = Json(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 }));
        Assert.DoesNotContain("durationMilliseconds", legacy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, NativeFlowDefinition.Read(legacy).Nodes[0].DurationMilliseconds);

        void Rejected(NativeFlowNode node) => Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(node))));
        Rejected(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = 1000 });
        Rejected(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = -1 });
        Rejected(new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 31622400, DurationMilliseconds = 1 });
        Rejected(new() { Kind = NativeFlowNodeKind.Sequence, DurationMilliseconds = 1 });
        Rejected(new() { Kind = NativeFlowNodeKind.Receive, MessageName = "m", Channel = "c", DurationMilliseconds = 1 });
        Rejected(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = 10, DurationExpression = new() { Literal = NativeScalar.From(1) } });
    }

    [Fact]
    public async Task AFixedSubSecondWaitPausesInPlaceInsteadOfParkingTheInstance()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(null, null, store, clock, null);
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = Json(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = 150 })) } };
        var input = new IntegrationBatch([new("one.json", BinaryData.FromString("{}"), "application/json",
            new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);
        var context = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        var clockTime = Stopwatch.StartNew();
        await module.AugmentAsync(context, input, step, default);
        clockTime.Stop();
        Assert.NotEqual(FlowExecutionDisposition.Suspended, context.Disposition);
        Assert.NotNull(context.PendingCheckpointCompletion);
        Assert.True(clockTime.ElapsedMilliseconds >= 120, $"The pause was {clockTime.ElapsedMilliseconds} ms.");
        Assert.DoesNotContain(store.Checkpoints, checkpoint => checkpoint.Native?.DeadlineUtc is not null);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 500)]
    [InlineData(2, 1)]
    public async Task AWaitOfOneSecondOrMoreStillParksTheInstanceWithAnExactDeadline(int seconds, int milliseconds)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(null, null, store, clock, null);
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = Json(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = seconds, DurationMilliseconds = milliseconds })) } };
        var input = new IntegrationBatch([new("one.json", BinaryData.FromString("{}"), "application/json",
            new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);
        var first = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await module.AugmentAsync(first, input, step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds), store.Checkpoints.Single().Native!.DeadlineUtc);

        clock.Now = clock.Now.AddSeconds(seconds).AddMilliseconds(milliseconds);
        var second = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        second.SetOrchestrationInstance("resume", first.OrchestrationCorrelationKey!, "native:resume", true);
        await module.AugmentAsync(second, IntegrationBatch.Empty, step, default);
        Assert.NotEqual(FlowExecutionDisposition.Suspended, second.Disposition);
    }

    [Fact]
    public async Task EditorOffersMillisecondsOnAFixedWaitAndExplainsTheDifference()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(NativeFlowNode node) => await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Mulse.Ui.Components.Shared.NativeFlowEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = Json(Flow(node)) }))).ToHtmlString());

        var html = await Render(new() { Kind = NativeFlowNodeKind.Wait, DurationMilliseconds = 200 });
        Assert.Contains("Wait milliseconds", html); Assert.Contains("value=\"200\"", html);
        Assert.Contains("pauses the running flow in place", html);
        Assert.DoesNotContain("Some nodes are incomplete", html);

        // A zero-second, zero-millisecond fixed wait is still an incomplete node.
        Assert.Contains("Some nodes are incomplete", await Render(new() { Kind = NativeFlowNodeKind.Wait }));
    }
}
