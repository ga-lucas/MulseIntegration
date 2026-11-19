using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class EditHistoryTests
{
    [Fact]
    public void Undo_AndRedo_WalkTheEditsInOrder()
    {
        var history = new EditHistory<string>("a");
        history.Record("b");
        history.Record("c");

        Assert.True(history.Undo(out var state));
        Assert.Equal("b", state);
        Assert.True(history.Undo(out state));
        Assert.Equal("a", state);
        Assert.False(history.Undo(out state));
        Assert.Equal("a", state);
        Assert.True(history.Redo(out state));
        Assert.Equal("b", state);
        Assert.True(history.Redo(out state));
        Assert.Equal("c", state);
        Assert.False(history.Redo(out _));
    }

    [Fact]
    public void Record_OfTheCurrentState_AddsNothing()
    {
        var history = new EditHistory<string>("a");

        Assert.False(history.Record("a"));
        Assert.False(history.CanUndo);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void NewEdit_AfterUndo_ClearsRedo()
    {
        var history = new EditHistory<string>("a");
        history.Record("b");
        history.Undo(out _);
        Assert.True(history.CanRedo);

        history.Record("x");

        Assert.False(history.CanRedo);
        Assert.True(history.Undo(out var state));
        Assert.Equal("a", state);
    }

    [Fact]
    public void Entries_AreCappedAndTheOldestAreDroppedFirst()
    {
        var history = new EditHistory<int>(0, maxEntries: 3);
        for (var value = 1; value <= 10; value++) history.Record(value);

        Assert.Equal(3, history.UndoCount);
        history.Undo(out var state);
        history.Undo(out state);
        history.Undo(out state);
        Assert.Equal(7, state);
        Assert.False(history.Undo(out _));
    }

    [Fact]
    public void TotalSize_BoundsTheHistoryButKeepsTheNewestEntry()
    {
        var history = new EditHistory<string>("", measure: static value => value.Length, maxSize: 25);
        history.Record(new string('a', 10));
        history.Record(new string('b', 10));
        history.Record(new string('c', 10));
        history.Record(new string('d', 10));

        Assert.True(history.CanUndo);
        Assert.True(history.UndoCount < 4);
        history.Undo(out var state);
        Assert.Equal(new string('c', 10), state);
    }

    [Fact]
    public void RapidEditsWithTheSameKey_BecomeOneUndoStep()
    {
        var clock = new StepClock();
        var history = new EditHistory<string>("", clock: clock);
        history.Record("h", "field");
        clock.Advance(TimeSpan.FromMilliseconds(200));
        history.Record("he", "field");
        clock.Advance(TimeSpan.FromMilliseconds(200));
        history.Record("hey", "field");

        Assert.Equal(1, history.UndoCount);
        history.Undo(out var state);
        Assert.Equal("", state);
    }

    [Fact]
    public void Edits_AreNotCoalescedAcrossKeysPausesOrUndo()
    {
        var clock = new StepClock();
        var history = new EditHistory<string>("", clock: clock);
        history.Record("a", "one");
        history.Record("ab", "two");
        Assert.Equal(2, history.UndoCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        history.Record("abc", "two");
        Assert.Equal(3, history.UndoCount);

        history.Undo(out _);
        history.Redo(out _);
        history.Record("abcd", "two");
        Assert.Equal(4, history.UndoCount);

        history.Record("abcde");
        history.Record("abcdef");
        Assert.Equal(6, history.UndoCount);
    }

    [Fact]
    public void Baseline_TracksSavedState_AndRevertIsAnUndoableEdit()
    {
        var history = new EditHistory<string>("saved");
        history.Record("edit one");
        history.Record("edit two");
        Assert.True(history.IsDirty);

        Assert.True(history.RevertToBaseline(out var state));
        Assert.Equal("saved", state);
        Assert.False(history.IsDirty);
        Assert.False(history.RevertToBaseline(out _));

        Assert.True(history.Undo(out state));
        Assert.Equal("edit two", state);
        Assert.True(history.IsDirty);
    }

    [Fact]
    public void MarkSaved_KeepsHistoryAndMovesTheBaseline()
    {
        var history = new EditHistory<string>("a");
        history.Record("b");
        history.MarkSaved();

        Assert.False(history.IsDirty);
        Assert.True(history.CanUndo);
        history.Undo(out _);
        Assert.True(history.IsDirty);
        history.Redo(out _);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void Reinitialize_StartsOver()
    {
        var history = new EditHistory<string>("a");
        history.Record("b");
        history.Undo(out _);

        history.Reinitialize("z");

        Assert.Equal("z", history.Current);
        Assert.Equal("z", history.Baseline);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.IsDirty);
    }

    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public sealed class DesignerEditHistoryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static FlowViewModel Saved(string? name = "Orders") => new("orders", true, "Interval", TimeSpan.FromMinutes(5), true,
        [], [], [], [], [], [], [],
        [new("source", new("file-system-fetch", new Dictionary<string, string> { ["path"] = "private-path" }), [])],
        [new("parsed", new("json-parse", new Dictionary<string, string>()), ["source"])], [],
        [new(new("json-render", new Dictionary<string, string>()), [new(new("http-deliver", new Dictionary<string, string> { ["url"] = "https://example.test" }), RequireConfirmation: true)])],
        Category: "Customer A", Name: name);

    [Fact]
    public async Task FreshlyLoadedFlow_HasNothingToUndoRedoOrReset_AndOffersReload()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            var html = rendered.ToHtmlString();

            Assert.Matches(@"<button[^>]*disabled[^>]*>Undo</button>", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Redo</button>", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Reset</button>", html);
            Assert.Matches(@"<button(?![^>]*disabled)[^>]*>Reload</button>", html);
            Assert.DoesNotContain("data-testid=\"unsaved-changes\"", html);
            Assert.Equal(0, handler.Mutations);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AnEditMadeThroughAnEventIsRecorded_ShownAsUnsaved_AndUndoneAndRedone()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Renamed"));

            var html = rendered.ToHtmlString();
            Assert.Contains("data-testid=\"unsaved-changes\"", html);
            Assert.Matches(@"<button(?![^>]*disabled)[^>]*>Undo</button>", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Redo</button>", html);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.Equal("Orders", Get(designer, "_flowName"));
            html = rendered.ToHtmlString();
            Assert.DoesNotContain("data-testid=\"unsaved-changes\"", html);
            Assert.Matches(@"<button(?![^>]*disabled)[^>]*>Redo</button>", html);

            await Raise(designer, () => Invoke(designer, "RedoEdit"));
            Assert.Equal("Renamed", Get(designer, "_flowName"));
            Assert.Contains("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());
            Assert.Equal(0, handler.Mutations);
        });
    }

    [Fact]
    public async Task RestoringTheDraft_IsLossless_AndNeverAddsAPhantomEntry()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            var nodes = Nodes(designer);
            var fetchId = Id(nodes.First(node => Stage(node) == "Fetch"));
            designer.OnNodeMoved(fetchId, 123, 456);
            var before = (string)Invoke(designer, "CaptureAuthoringDraft")!;

            await Raise(designer, () => Set(designer, "_flowName", "Renamed"));
            await Raise(designer, () => Invoke(designer, "UndoEdit"));

            Assert.Equal(before, Invoke(designer, "CaptureAuthoringDraft"));
            var fetch = Nodes(designer).First(node => Stage(node) == "Fetch");
            Assert.Equal(123d, Prop<double>(fetch, "X"));
            Assert.Equal(456d, Prop<double>(fetch, "Y"));
            Assert.Equal("private-path", Prop<Dictionary<string, string>>(fetch, "Settings")["path"]);
            Assert.False((bool)Invoke(designer, "RecordChange", (object?)null)!);
        });
    }

    [Fact]
    public async Task DraggingANode_IsOneUndoableStep()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            var node = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            var (x, y) = (Prop<double>(node, "X"), Prop<double>(node, "Y"));

            await designer.OnNodeMoved(Id(node), x + 300, y + 200);
            Assert.True((bool)Get(designer, "CanUndoEdit")!);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            var restored = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            Assert.Equal(x, Prop<double>(restored, "X"));
            Assert.Equal(y, Prop<double>(restored, "Y"));
        });
    }

    [Fact]
    public async Task ANewEditAfterUndo_DropsTheRedoHistory()
    {
        await RenderDesigner(async (designer, rendered, _) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "First"));
            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.True((bool)Get(designer, "CanRedoEdit")!);

            await Raise(designer, () => Set(designer, "_enabled", false));

            Assert.False((bool)Get(designer, "CanRedoEdit")!);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Redo</button>", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task TypingInOneField_IsOneUndoStep_ButDifferentEditsAreSeparate()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "R"), new ChangeEventArgs { Value = "R" });
            await Raise(designer, () => Set(designer, "_flowName", "Re"), new ChangeEventArgs { Value = "Re" });
            await Raise(designer, () => Set(designer, "_flowName", "Ren"), new ChangeEventArgs { Value = "Ren" });
            await Raise(designer, () => Set(designer, "_enabled", false));

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.Equal(true, Get(designer, "_enabled"));
            Assert.Equal("Ren", Get(designer, "_flowName"));

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.Equal("Orders", Get(designer, "_flowName"));
            Assert.False((bool)Get(designer, "CanUndoEdit")!);
        });
    }

    [Fact]
    public async Task Reset_ReturnsToTheLoadedState_AndCanBeUndone()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "One"));
            await Raise(designer, () => Set(designer, "_flowName", "Two"));
            await Raise(designer, () => Set(designer, "_triggerMode", "OnDemand"));

            await Raise(designer, () => Invoke(designer, "ResetEdits"));

            Assert.Equal("Orders", Get(designer, "_flowName"));
            Assert.Equal("Interval", Get(designer, "_triggerMode"));
            Assert.DoesNotContain("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());
            Assert.Contains("Undo brings your changes back", (string)Get(designer, "_statusMessage")!);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.Equal("OnDemand", Get(designer, "_triggerMode"));
            Assert.Equal("Two", Get(designer, "_flowName"));
            Assert.Equal(0, handler.Mutations);
        });
    }

    [Fact]
    public async Task Reload_WithUnsavedChanges_AsksFirst_AndKeepEditingKeepsTheDraft()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Draft name"));
            var requests = handler.Requests.Count(path => path == "/api/flows/orders");

            await Raise(designer, () => (Task)Invoke(designer, "RequestReloadAsync")!);

            Assert.Contains("Discard and reload", rendered.ToHtmlString());
            Assert.Equal(requests, handler.Requests.Count(path => path == "/api/flows/orders"));
            Assert.Equal("Draft name", Get(designer, "_flowName"));

            await Raise(designer, () => Invoke(designer, "CancelReload"));
            Assert.DoesNotContain("Discard and reload", rendered.ToHtmlString());
            Assert.Equal("Draft name", Get(designer, "_flowName"));
            Assert.True((bool)Get(designer, "CanUndoEdit")!);
        });
    }

    [Fact]
    public async Task ConfirmedReload_LoadsTheSavedFlowAndClearsHistory()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Draft name"));
            await Raise(designer, () => (Task)Invoke(designer, "RequestReloadAsync")!);
            handler.Flow = Saved("Changed elsewhere");

            await Raise(designer, () => (Task)Invoke(designer, "ReloadSavedAsync")!);

            Assert.Equal("Changed elsewhere", Get(designer, "_flowName"));
            Assert.False((bool)Get(designer, "CanUndoEdit")!);
            Assert.DoesNotContain("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());
            Assert.Equal(0, handler.Mutations);
        });
    }

    [Fact]
    public async Task Reload_WithoutChanges_ReloadsImmediately()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            handler.Flow = Saved("Changed elsewhere");

            await Raise(designer, () => (Task)Invoke(designer, "RequestReloadAsync")!);

            Assert.Equal("Changed elsewhere", Get(designer, "_flowName"));
            Assert.DoesNotContain("Discard and reload", rendered.ToHtmlString());
        });
    }

    [Fact]
    public async Task FailedReload_KeepsTheDraftAndItsHistory()
    {
        await RenderDesigner(async (designer, _, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Draft name"));
            await Raise(designer, () => (Task)Invoke(designer, "RequestReloadAsync")!);
            handler.FailFlowReads = true;

            await Raise(designer, () => (Task)Invoke(designer, "ReloadSavedAsync")!);

            Assert.Equal("Draft name", Get(designer, "_flowName"));
            Assert.Contains("Your draft was kept", (string)Get(designer, "_errorMessage")!);
            Assert.True((bool)Get(designer, "CanUndoEdit")!);
        });
    }

    [Fact]
    public async Task Saving_MovesTheBaseline_ButKeepsUndo()
    {
        await RenderDesigner(async (designer, rendered, handler) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Saved name"));
            handler.Flow = Saved("Saved name");
            Assert.Contains("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());

            await Raise(designer, () => (Task)Invoke(designer, "SaveAsync")!);

            Assert.Equal(1, handler.Mutations);
            Assert.DoesNotContain("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());
            Assert.True((bool)Get(designer, "CanUndoEdit")!);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));
            Assert.Contains("data-testid=\"unsaved-changes\"", rendered.ToHtmlString());
            Assert.Equal(1, handler.Mutations);
        });
    }

    [Fact]
    public async Task HistoryNeverEnablesAnImportedFlowOrSaves()
    {
        await RenderDesigner(async (designer, _, handler) =>
        {
            var migration = new FlowMigrationReportViewModel("BizTalk", "C:\\source\\project", "Orchestration", [], [], ["warning"]);
            Set(designer, "_migration", migration);
            Set(designer, "_enabled", false);
            Invoke(designer, "ResetHistory");
            await Raise(designer, () => Set(designer, "_flowName", "Imported edit"));
            await Raise(designer, () => Set(designer, "_flowName", "Imported edit two"), new ChangeEventArgs { Value = "x" });

            foreach (var action in new[] { "UndoEdit", "RedoEdit", "ResetEdits", "UndoEdit", "RedoEdit" })
            {
                await Raise(designer, () => Invoke(designer, action));
                Assert.Equal(false, Get(designer, "_enabled"));
                var report = Assert.IsType<FlowMigrationReportViewModel>(Get(designer, "_migration"));
                Assert.Equal("BizTalk", report.SourceSystem);
                Assert.Equal(["warning"], report.Warnings);
            }

            Assert.Equal(0, handler.Mutations);
        });
    }

    [Fact]
    public async Task Undo_RemapsTheSelectionOntoTheRestoredNodes()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            var node = Nodes(designer).First(candidate => Stage(candidate) == "Parse");
            Set(designer, "_selectedNodeId", Id(node));
            await Raise(designer, () => Set(designer, "_flowName", "Renamed"));

            await Raise(designer, () => Invoke(designer, "UndoEdit"));

            var selected = Assert.IsType<string>(Get(designer, "_selectedNodeId"));
            var restored = Nodes(designer).Single(candidate => Id(candidate) == selected);
            Assert.Equal("Parse", Stage(restored));
            Assert.NotEqual(Id(node), selected);
        });
    }

    [Fact]
    public async Task NodeEditsMadeInPlace_ByChildEditors_AreRecordedAndUndone()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            var fetch = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");

            // A child editor mutates Settings directly and only a bubbled change event reaches the designer.
            Prop<Dictionary<string, string>>(fetch, "Settings")["path"] = "edited-path";
            await Raise(designer, () => Invoke(designer, "ObserveSurfaceEvent"), new ChangeEventArgs { Value = "edited-path" });
            Assert.True((bool)Get(designer, "HasUnsavedChanges")!);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));

            fetch = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            Assert.Equal("private-path", Prop<Dictionary<string, string>>(fetch, "Settings")["path"]);
        });
    }

    [Fact]
    public async Task ObservedEventsThatChangeNothing_DoNotAddHistory()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            await Raise(designer, () => Invoke(designer, "ObserveSurfaceEvent"), new MouseEventArgs());
            await Raise(designer, () => Invoke(designer, "ObserveSurfaceEvent"), new ChangeEventArgs { Value = "x" });

            Assert.False((bool)Get(designer, "CanUndoEdit")!);
            Assert.False((bool)Get(designer, "HasUnsavedChanges")!);
        });
    }

    [Fact]
    public async Task UndoPendingChildEdit_IsCapturedBeforeItIsLost()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            var fetch = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            Prop<Dictionary<string, string>>(fetch, "Settings")["path"] = "not yet observed";

            await Raise(designer, () => Invoke(designer, "UndoEdit"));

            fetch = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            Assert.Equal("private-path", Prop<Dictionary<string, string>>(fetch, "Settings")["path"]);
            await Raise(designer, () => Invoke(designer, "RedoEdit"));
            fetch = Nodes(designer).First(candidate => Stage(candidate) == "Fetch");
            Assert.Equal("not yet observed", Prop<Dictionary<string, string>>(fetch, "Settings")["path"]);
        });
    }

    [Fact]
    public async Task UndoDoesNothingWhileSaving()
    {
        await RenderDesigner(async (designer, _, _) =>
        {
            await Raise(designer, () => Set(designer, "_flowName", "Renamed"));
            Set(designer, "_isSaving", true);

            await Raise(designer, () => Invoke(designer, "UndoEdit"));

            Assert.Equal("Renamed", Get(designer, "_flowName"));
        });
    }

    private static Task Raise(Designer designer, Action change, object? argument = null)
        => new EventCallback(designer, change).InvokeAsync(argument);

    private static Task Raise(Designer designer, Func<Task> change, object? argument = null)
        => new EventCallback(designer, change).InvokeAsync(argument);

    private static System.Collections.IList NodeList(Designer designer) => (System.Collections.IList)Get(designer, "_nodes")!;

    private static List<object> Nodes(Designer designer) => NodeList(designer).Cast<object>().ToList();

    private static string Id(object node) => Prop<string>(node, "Id");

    private static string Stage(object node) => Prop<object>(node, "Stage").ToString()!;

    private static T Prop<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private static object? Invoke(object instance, string name, params object?[] args)
        => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);

    private static object? Get(object instance, string name)
        => (instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance))
            ?? instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);

    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static async Task RenderDesigner(Func<Designer, HtmlRootComponent, Handler, Task> test)
    {
        var handler = new Handler { Flow = Saved() };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        var activator = new Activator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new StaticRenderJsRuntime())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Designer>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Designer.FlowId)] = "orders" }));
            await test(Assert.Single(activator.Components.OfType<Designer>()), rendered, handler);
        });
    }

    private sealed class Activator : IComponentActivator
    {
        public List<IComponent> Components { get; } = [];

        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)System.Activator.CreateInstance(type)!;
            Components.Add(component);
            return component;
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class StaticRenderJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => throw new NotSupportedException("Static rendering must not invoke JavaScript.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public int Mutations { get; private set; }

        public required FlowViewModel Flow { get; set; }

        public bool FailFlowReads { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path == "/api/flows/orders")
            {
                if (request.Method != HttpMethod.Get)
                {
                    Mutations++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flow, options: Json) });
                }

                return Task.FromResult(FailFlowReads
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Flow, options: Json) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
