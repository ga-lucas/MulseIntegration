using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;

namespace Service.Tests;

public sealed class NativeDateTimeTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 30, 15, TimeSpan.Zero);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static NativeExpression Lit(object value) => new() { Literal = NativeScalar.From(value) };
    private static NativeExpression Op(NativeExpressionKind kind, params NativeExpression[] operands) => new() { Kind = kind, Operands = [.. operands] };
    private static NativeExpression Moment(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => Lit(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
    private static object? Eval(NativeExpression expression, Clock? clock = null)
    {
        NativeExpression.Validate(expression);
        return expression.EvaluateWithClock(new Dictionary<string, NativeScalar>(), clock ?? new Clock()).Read();
    }

    [Fact]
    public void NowIsTheClockInUtcAndRoundTripsAsExplicitUtcText()
    {
        var now = Assert.IsType<DateTime>(Eval(Op(NativeExpressionKind.Now)));
        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.Equal(Start.UtcDateTime, now);
        Assert.Equal("2026-03-01T12:30:15.0000000Z", NativeScalar.From(now).Value);
        Assert.Equal("2026-03-01T12:30:15.0000000Z", Eval(Op(NativeExpressionKind.ToText, Op(NativeExpressionKind.Now))));
    }

    [Theory]
    [InlineData("2026-03-01T10:00:00+02:00", 8, 0)]
    [InlineData("2026-03-01T10:00:00Z", 10, 0)]
    [InlineData("2026-03-01 10:45", 10, 45)]
    [InlineData("03/01/2026 10:45", 10, 45)]
    public void ParseAssumesUtcWhenNoOffsetIsGivenAndNormalizesOffsetsToUtc(string text, int hour, int minute)
    {
        var parsed = Assert.IsType<DateTime>(Eval(Op(NativeExpressionKind.ParseDateTime, Lit(text))));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 3, 1, hour, minute, 0, DateTimeKind.Utc), parsed);
    }

    [Theory]
    [InlineData("42", true, 42)]
    [InlineData(" -7 ", true, -7)]
    [InlineData("", false, 0)]
    [InlineData("4.5", false, 0)]
    [InlineData("1,000", false, 0)]
    [InlineData("99999999999", false, 0)]
    [InlineData("abc", false, 0)]
    public void Int32TryParseIsAnExactCheckAndAParseOrZero(string text, bool valid, int value)
    {
        Assert.Equal(valid, Eval(Op(NativeExpressionKind.IsInt32, Lit(text))));
        Assert.Equal(value, Eval(Op(NativeExpressionKind.ParseInt32OrZero, Lit(text))));
        Assert.Equal(false, Eval(Op(NativeExpressionKind.IsInt32, Lit(null!))));
        Assert.Equal(0, Eval(Op(NativeExpressionKind.ParseInt32OrZero, Lit(null!))));
    }

    [Fact]
    public void ParsingATimeOnlyTextTakesTheDateFromTheFlowClockNotTheMachine()
    {
        var parsed = Assert.IsType<DateTime>(Eval(Op(NativeExpressionKind.ParseDateTime, Lit("06:15")), new Clock { Now = new(2031, 7, 9, 23, 59, 0, TimeSpan.Zero) }));
        Assert.Equal(new DateTime(2031, 7, 9, 6, 15, 0, DateTimeKind.Utc), parsed);
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.ParseDateTime, Lit("not a date"))));
    }

    [Fact]
    public void PartsConstructionArithmeticAndComparisonAreExactAndUtc()
    {
        var moment = Moment(2026, 12, 31, 23, 59, 58);
        Assert.Equal(new object[] { 2026, 12, 31, 23, 59, 58 }, new[] { NativeExpressionKind.Year, NativeExpressionKind.Month, NativeExpressionKind.Day,
            NativeExpressionKind.Hour, NativeExpressionKind.Minute, NativeExpressionKind.Second }.Select(kind => Eval(Op(kind, moment))).ToArray());
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 1, DateTimeKind.Utc), Eval(Op(NativeExpressionKind.AddSeconds, moment, Lit(3))));
        Assert.Equal(new DateTime(2027, 1, 1, 23, 59, 58, DateTimeKind.Utc), Eval(Op(NativeExpressionKind.AddDays, moment, Lit(1))));
        Assert.Equal(new DateTime(2026, 12, 31, 22, 29, 58, DateTimeKind.Utc), Eval(Op(NativeExpressionKind.AddMinutes, Op(NativeExpressionKind.AddHours, moment, Lit(-1)), Lit(-30))));
        Assert.Equal(new DateTime(2026, 2, 28, 4, 5, 0, DateTimeKind.Utc), Eval(Op(NativeExpressionKind.DateTimeFromParts, Lit(2026), Lit(2), Lit(28), Lit(4), Lit(5), Lit(0))));
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.DateTimeFromParts, Lit(2026), Lit(2), Lit(30), Lit(0), Lit(0), Lit(0))));
        Assert.Equal(-1, Eval(Op(NativeExpressionKind.CompareDateTime, Moment(2026, 1, 1), Moment(2026, 1, 2))));
        Assert.Equal(0, Eval(Op(NativeExpressionKind.CompareDateTime, Moment(2026, 1, 1), Moment(2026, 1, 1))));
        Assert.True(Assert.IsType<bool>(Eval(Op(NativeExpressionKind.Greater, Moment(2026, 1, 2), Moment(2026, 1, 1)))));
        Assert.True(Assert.IsType<bool>(Eval(Op(NativeExpressionKind.Equal, Moment(2026, 1, 1), Moment(2026, 1, 1)))));
        Assert.Equal(90, Eval(Op(NativeExpressionKind.SecondsBetween, Moment(2026, 1, 1, 0, 1, 30), Moment(2026, 1, 1))));
        Assert.Equal(-86400, Eval(Op(NativeExpressionKind.SecondsBetween, Moment(2026, 1, 1), Moment(2026, 1, 2))));
    }

    [Fact]
    public void DateTimeRefusesCoercionNonUtcLiteralsAndOutOfRangeArithmetic()
    {
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.AddDays, Lit("2026-01-01"), Lit(1))));
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.Less, Moment(2026, 1, 1), Lit(5))));
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.Equal, Moment(2026, 1, 1), Lit("2026-01-01T00:00:00.0000000Z"))));
        Assert.Throws<InvalidOperationException>(() => Eval(Op(NativeExpressionKind.AddDays, Moment(9999, 12, 31), Lit(1))));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(new() { Literal = new(NativeScalarType.DateTime, "2026-01-01T00:00:00") }));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(new() { Literal = new(NativeScalarType.DateTime, "2026-01-01T00:00:00.0000000+02:00") }));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(Op(NativeExpressionKind.Year)));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(Op(NativeExpressionKind.DateTimeFromParts, Lit(1), Lit(1))));
    }

    [Fact]
    public async Task DateTimeVariablesAndAnExpressionWaitSurviveARestartWithAnExactUtcDeadline()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        var flow = new NativeFlowDefinition
        {
            Durable = true,
            Nodes =
            [
                new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "end", VariableType = NativeScalarType.DateTime,
                    Expression = Op(NativeExpressionKind.AddMinutes, Op(NativeExpressionKind.Now), Lit(2)) },
                new() { Kind = NativeFlowNodeKind.Wait,
                    DurationExpression = Op(NativeExpressionKind.SecondsBetween, new() { Kind = NativeExpressionKind.Variable, Variable = "end" }, Op(NativeExpressionKind.Now)) },
                new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "done", VariableType = NativeScalarType.Boolean,
                    Expression = Op(NativeExpressionKind.GreaterOrEqual, Op(NativeExpressionKind.Now), new() { Kind = NativeExpressionKind.Variable, Variable = "end" }) }
            ]
        };
        var module = new NativeControlFlowAugmentModule(null, null, store, clock, null);
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) } };
        var input = new IntegrationBatch([new("one.json", BinaryData.FromString("{}"), "application/json",
            new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);
        var first = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        await module.AugmentAsync(first, input, step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        var saved = store.Checkpoints.Single().Native!;
        Assert.Equal(clock.Now.AddMinutes(2), saved.DeadlineUtc);
        Assert.Equal("2026-03-01T12:32:15.0000000Z", saved.State.Variables["end"].Value);
        Assert.Equal(NativeScalarType.DateTime, saved.State.VariableTypes["end"]);

        clock.Now = clock.Now.AddMinutes(2);
        var second = new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.Now);
        second.SetOrchestrationInstance("resume", first.OrchestrationCorrelationKey!, "native:resume", true);
        await module.AugmentAsync(second, IntegrationBatch.Empty, step, default);
        Assert.NotEqual(FlowExecutionDisposition.Suspended, second.Disposition);
        Assert.NotNull(second.PendingCheckpointCompletion);
    }

    [Fact]
    public async Task EditorShowsDateTimeLiteralAndExpressionWaitWithoutDraftErrors()
    {
        var flow = new NativeFlowDefinition
        {
            Durable = true,
            Nodes =
            [
                new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "end", VariableType = NativeScalarType.DateTime, Expression = Moment(2026, 3, 1, 10, 30) },
                new() { Kind = NativeFlowNodeKind.Wait, DurationExpression = Op(NativeExpressionKind.SecondsBetween,
                    new() { Kind = NativeExpressionKind.Variable, Variable = "end" }, Op(NativeExpressionKind.Now)) }
            ]
        };
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Mulse.Ui.Components.Shared.NativeFlowEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) }))).ToHtmlString());
        Assert.Contains("datetime-local", html); Assert.Contains("Stored and executed as UTC", html); Assert.Contains("2026-03-01T10:30:00.0000000Z", html);
        Assert.Contains("Computed from an expression", html); Assert.Contains("SecondsBetween", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
    }

    [Fact]
    public async Task OneGlobalDisplaySettingDecidesWhetherEveryDateTimeFieldShowsUtcOrServerLocal()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(Mulse.Ui.Models.UiTimeDisplay? display)
        {
            var value = new Mulse.Ui.Models.NativeExpressionViewModel { Literal = new() { Type = "DateTime", Value = "2026-03-01T10:30:00.0000000Z" } };
            RenderFragment child = builder =>
            {
                builder.OpenComponent<Mulse.Ui.Components.Shared.NativeExpressionEditor>(0);
                builder.AddComponentParameter(1, "Value", value);
                builder.CloseComponent();
            };
            return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<CascadingValue<Mulse.Ui.Models.UiTimeDisplay?>>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = display, ["ChildContent"] = child }))).ToHtmlString());
        }
        var utc = await Render(new Mulse.Ui.Models.UiTimeDisplay { ServerLocal = false });
        Assert.Contains("(UTC)", utc); Assert.Contains("value=\"2026-03-01T10:30:00\"", utc);
        Assert.DoesNotContain("entered as", utc);
        var local = await Render(new Mulse.Ui.Models.UiTimeDisplay());
        Assert.Contains(Mulse.Ui.Models.UiDateTime.ServerZoneLabel, System.Net.WebUtility.HtmlDecode(local));
        Assert.Contains("value=\"" + Mulse.Ui.Models.UiDateTime.ToInput("2026-03-01T10:30:00.0000000Z", true) + "\"", local);
        Assert.Contains(Mulse.Ui.Models.UiDateTime.ServerZoneLabel, System.Net.WebUtility.HtmlDecode(await Render(null)));
    }

    [Fact]
    public void TheSavedDisplayChoiceIsKeyedByWorkspaceAndDefaultsToServerLocal()
    {
        Assert.True(new Mulse.Ui.Models.UiTimeDisplay().ServerLocal);
        Assert.NotEqual(new Mulse.Ui.Models.UiTimeDisplay("alpha").StorageKey, new Mulse.Ui.Models.UiTimeDisplay("beta").StorageKey);
        Assert.Equal(new Mulse.Ui.Models.UiTimeDisplay().StorageKey, new Mulse.Ui.Models.UiTimeDisplay(" ").StorageKey);
        Assert.False(Mulse.Ui.Models.UiTimeDisplay.FromStored(Mulse.Ui.Models.UiTimeDisplay.ToStored(false)));
        Assert.True(Mulse.Ui.Models.UiTimeDisplay.FromStored(Mulse.Ui.Models.UiTimeDisplay.ToStored(true)));
        Assert.True(Mulse.Ui.Models.UiTimeDisplay.FromStored(null)); Assert.True(Mulse.Ui.Models.UiTimeDisplay.FromStored("garbage"));
    }

    [Fact]
    public void WaitDurationExpressionIsOnlyAllowedOnWaitWithoutAFixedDuration()
    {
        NativeFlowDefinition Flow(NativeFlowNode node) => new() { Durable = true, Nodes = [node] };
        void Check(NativeFlowNode node) => Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(node), JsonSerializerOptions.Web)));
        Check(new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5, DurationExpression = Lit(1) });
        Check(new() { Kind = NativeFlowNodeKind.Sequence, DurationExpression = Lit(1) });
        var valid = NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(new() { Kind = NativeFlowNodeKind.Wait, DurationExpression = Lit(1) }), JsonSerializerOptions.Web));
        Assert.NotNull(Assert.Single(valid.Nodes).DurationExpression);
    }
}
