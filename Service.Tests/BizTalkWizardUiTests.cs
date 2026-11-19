using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class BizTalkWizardUiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static BizTalkEvidenceRequestViewModel Request(string id, string title, params string[] accepts)
        => new(id, id, title, "Because " + id + " matters.", ["Open the console for " + id, "Export " + id], accepts, "Needed for " + id + ".", ["Changes " + id], id == "real-output" ? "Samples can hold patient data." : null);

    private static BizTalkSolutionAnalysisViewModel Analysis(BizTalkAnalysisDeltaViewModel? delta, params BizTalkEvidenceRequestViewModel[] requests) => new(
        "C:\\x.sln", "Solution", "x", "C:\\", 1, 0, 1, 0, 0, 0, [], [], [], [], [])
    {
        EvidenceRequests = requests,
        Summary = new() { Requests = [.. requests.Select(r => new BizTalkSummaryRequestViewModel(r.Id, r.Title))] },
        Delta = delta
    };

    private static readonly BizTalkEvidenceRequestViewModel Bindings = Request("bindings", "Export the deployed bindings", BizTalkEvidenceKinds.Bindings);
    private static readonly BizTalkEvidenceRequestViewModel Policies = Request("rule-policies", "Export the business rule policies", BizTalkEvidenceKinds.RulePolicy);
    private static readonly BizTalkEvidenceRequestViewModel RealOutput = Request("real-output", "Provide samples of what BizTalk really produced", BizTalkEvidenceKinds.RealOutput);
    private static readonly BizTalkEvidenceRequestViewModel Unknown = Request("future-rule", "A request this build has not met");

    [Fact]
    public void RequestsAreGroupedIntoStagesAndUnknownOnesAreNeverHidden()
    {
        Assert.Equal(BizTalkWizardStage.Environment, BizTalkWizardStages.StageOf("bindings"));
        Assert.Equal(BizTalkWizardStage.Environment, BizTalkWizardStages.StageOf("host-config"));
        Assert.All(new[] { "rule-policies", "policy-versions", "unresolved-projects", "map-owners", "map-stubs" }, id => Assert.Equal(BizTalkWizardStage.RulesAndMaps, BizTalkWizardStages.StageOf(id)));
        Assert.Equal(BizTalkWizardStage.Behavior, BizTalkWizardStages.StageOf("unknown-components"));
        Assert.Equal(BizTalkWizardStage.Behavior, BizTalkWizardStages.StageOf("real-output"));
        Assert.Equal(BizTalkWizardStage.Review, BizTalkWizardStages.StageOf("future-rule"));
        Assert.Equal(5, BizTalkWizardStages.All.Count);
    }

    [Fact]
    public async Task WizardStartsAtSourceAndStatesWhereTheSessionIsKept()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async wizard =>
        {
            var html = fixture.Html();
            Assert.Contains("saved in this browser only", html);
            Assert.Contains("1. Source", html);
            Assert.Contains("5. Review and import", html);
            Assert.Contains("wizard-source-path", html);
            Assert.Contains("Analyze and continue", html);
            Assert.DoesNotContain("wizard-open-summary", html);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AnalysisShowsEachRequestWithItsRationaleStepsAndOpenCounts()
    {
        await using var fixture = new Fixture();
        fixture.Responses.Enqueue(Analysis(null, Bindings, Policies, RealOutput));
        await fixture.RunAsync(async wizard =>
        {
            fixture.Call(wizard, "SetSourcePath", "C:\\x.sln");
            await fixture.CallAsync(wizard, "AnalyzeAsync");

            Assert.Equal("Environment", fixture.Field(wizard, "_stage")!.ToString());
            await fixture.RefreshAsync(wizard);
            var html = fixture.Html();
            Assert.Contains("Export the deployed bindings", html);
            Assert.Contains("Because bindings matters.", html);
            Assert.Contains("Open the console for bindings", html);
            Assert.Contains("data-status=\"Open\"", html);
            Assert.Contains("3 request(s) still open.", html);
            Assert.Contains("2. Environment (1 open)", html);
            Assert.Contains("3. Rules and maps (1 open)", html);
            Assert.Contains("4. Evidence of behavior (1 open)", html);
            Assert.DoesNotContain("Samples can hold patient data", html);

            fixture.Call(wizard, "GoTo", BizTalkWizardStage.Behavior);
            await fixture.RefreshAsync(wizard);
            html = fixture.Html();
            Assert.Contains("Samples can hold patient data.", html);
            Assert.Contains("Comparing them against native output is not built yet", html);
        });
        Assert.Single(fixture.Bodies);
        Assert.Contains("\"sourcePath\":\"C:\\\\x.sln\"", fixture.Bodies[0]);
    }

    [Fact]
    public async Task AnsweringARequestIsRememberedSavedAndReversible()
    {
        await using var fixture = new Fixture();
        fixture.Responses.Enqueue(Analysis(null, Bindings, Policies));
        await fixture.RunAsync(async wizard =>
        {
            fixture.Call(wizard, "SetSourcePath", "C:\\x.sln");
            await fixture.CallAsync(wizard, "AnalyzeAsync");
            await fixture.RefreshAsync(wizard);

            fixture.Call(wizard, "Answer", "bindings", new BizTalkRequestAnswer(BizTalkRequestAnswers.NotAvailable, "no server access"));
            await fixture.RefreshAsync(wizard);

            var html = fixture.Html();
            Assert.Contains("data-status=\"NotAvailable\"", html);
            Assert.Contains("Marked not available. Note: no server access", html);
            Assert.Contains("1 request(s) still open.", html);
            var saved = BizTalkImportSession.Import(fixture.Store.Saved["import"]);
            Assert.Equal(BizTalkRequestAnswers.NotAvailable, saved.RequestNotes["bindings"].Answer);
            Assert.Equal("no server access", saved.RequestNotes["bindings"].Note);

            fixture.Call(wizard, "Reopen", "bindings");
            await fixture.RefreshAsync(wizard);
            Assert.Contains("2 request(s) still open.", fixture.Html());
            Assert.DoesNotContain("Marked not available", fixture.Html());
        });
    }

    [Fact]
    public async Task AddedEvidenceMarksTheAnalysisStaleAndReanalysisReportsWhatChanged()
    {
        await using var fixture = new Fixture();
        fixture.Responses.Enqueue(Analysis(null, Bindings, Policies));
        fixture.Responses.Enqueue(Analysis(
            new([new("bindings", "Export the deployed bindings")], [], [new("rule-policies", "Export the business rule policies")], [new("flow-a", 3, 1)], [new("Some.Map", null, "converted")]),
            Policies));
        await fixture.RunAsync(async wizard =>
        {
            fixture.Call(wizard, "SetSourcePath", "C:\\x.sln");
            await fixture.CallAsync(wizard, "AnalyzeAsync");

            fixture.Call(wizard, "AddEvidence", new BizTalkEvidenceAdd(BizTalkEvidenceKinds.Bindings, BizTalkEvidenceOrigins.Text, "prod bindings", null, "<BindingInfo/>", null));
            await fixture.RefreshAsync(wizard);
            Assert.Contains("settings changed since the last analysis", fixture.Html());
            Assert.Contains("data-status=\"Supplied\"", fixture.Html());

            await fixture.CallAsync(wizard, "AnalyzeAsync");
            fixture.Call(wizard, "GoTo", BizTalkWizardStage.Review);
            await fixture.RefreshAsync(wizard);

            var html = fixture.Html();
            Assert.DoesNotContain("settings changed since the last analysis", html);
            Assert.Contains("Resolved: Export the deployed bindings", html);
            Assert.Contains("flow-a: 3 blocker(s) before, 1 now", html);
            Assert.Contains("Map choice", html);
            Assert.Contains("1 request(s) still open.", html);
        });

        Assert.Equal(2, fixture.Bodies.Count);
        Assert.DoesNotContain("BindingInfo", fixture.Bodies[0]);
        Assert.Contains("BindingInfo", fixture.Bodies[1]);
        Assert.Contains("previousSummary", fixture.Bodies[1]);
        Assert.Contains("rule-policies", fixture.Bodies[1]);
    }

    [Fact]
    public async Task RequestsFromARuleThisBuildDoesNotKnowAppearInReview()
    {
        await using var fixture = new Fixture();
        fixture.Responses.Enqueue(Analysis(null, Unknown));
        await fixture.RunAsync(async wizard =>
        {
            fixture.Call(wizard, "SetSourcePath", "C:\\x.sln");
            await fixture.CallAsync(wizard, "AnalyzeAsync");
            fixture.Call(wizard, "GoTo", BizTalkWizardStage.Review);
            await fixture.RefreshAsync(wizard);
            Assert.Contains("A request this build has not met", fixture.Html());
            Assert.Contains("5. Review and import (1 open)", fixture.Html());
        });
    }

    [Fact]
    public async Task AnalyzingWithoutASourceIsAnExplainedError()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async wizard =>
        {
            await fixture.CallAsync(wizard, "AnalyzeAsync");
            await fixture.RefreshAsync(wizard);
            Assert.Contains("Give a source path or upload the source first.", fixture.Html());
        });
        Assert.Empty(fixture.Bodies);
    }

    [Fact]
    public async Task ASavedSessionCanBeResumedWithItsEvidenceAndAnswers()
    {
        var store = new MemoryStore();
        var original = new BizTalkImportSession { Name = "billing", SourcePath = "C:\\x.sln" };
        original.AddText(BizTalkEvidenceKinds.Bindings, "prod bindings", "<BindingInfo/>", TimeProvider.System);
        original.Answer("rule-policies", BizTalkRequestAnswers.Skipped, "later");
        await store.SaveAsync(original, default);

        await using var fixture = new Fixture(store);
        await fixture.RunAsync(async wizard =>
        {
            Assert.Contains("wizard-saved-sessions", fixture.Html());
            Assert.Contains("billing", fixture.Html());
            fixture.SetField(wizard, "_selectedSaved", "billing");
            await fixture.CallAsync(wizard, "LoadSelectedAsync");
            await fixture.RefreshAsync(wizard);

            var session = (BizTalkImportSession)fixture.Field(wizard, "_session")!;
            Assert.Equal("billing", session.Name);
            Assert.Equal("C:\\x.sln", session.SourcePath);
            Assert.Equal("<BindingInfo/>", session.Content[session.Evidence.Single().Id].Text);
            Assert.Equal("later", session.RequestNotes["rule-policies"].Note);
            Assert.Contains("Resumed", fixture.Html());
        });
    }

    [Fact]
    public async Task StorageFailureIsShownAndTheWizardKeepsWorking()
    {
        var store = new MemoryStore { FailWith = new InvalidOperationException("quota exceeded") };
        await using var fixture = new Fixture(store);
        await fixture.RunAsync(async wizard =>
        {
            Assert.Contains("Browser storage is not available", fixture.Html());
            fixture.Call(wizard, "Answer", "bindings", new BizTalkRequestAnswer(BizTalkRequestAnswers.Skipped, null));
            await fixture.RefreshAsync(wizard);
            Assert.Contains("could not be saved in this browser", fixture.Html());
            Assert.Contains("Analyze and continue", fixture.Html());
        });
    }

    [Fact]
    public async Task SessionNamesThatCannotBeStorageKeysAreRefused()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async wizard =>
        {
            fixture.SetField(wizard, "_sessionName", "a/b");
            fixture.Call(wizard, "Answer", "bindings", new BizTalkRequestAnswer(BizTalkRequestAnswers.Skipped, null));
            await fixture.RefreshAsync(wizard);
            Assert.Contains("Name the session with up to 80 letters", fixture.Html());
        });
        Assert.Empty(fixture.Store.Saved);
    }

    private static readonly BizTalkFlowCandidateViewModel BillingCandidate = new(
        "billing-send", "Core.Billing", "Sends billing.", "Fetch guidance", "Parse guidance", [], [], [], [], [],
        new BizTalkDraftFlowViewModel("billing-send", false, new BizTalkDraftTriggerViewModel("Interval", null, false), [], [], [], [], [], [], null));

    private static FlowViewModel Flow(string id) => new(id, false, "Interval", null, false, [], [], [], [], [], [], [], [], [], [], []);

    private static BizTalkUpdatePlanViewModel InPlacePlan(string flowId) => new(flowId, "InPlace", "The flow can be updated piece by piece.", null,
        [new("augment/1", "Augment", "Rule policy", "UpdateInPlace", true, true)], new Dictionary<string, int>(), []);

    private static async Task ReachReviewWithACandidateAsync(Fixture fixture, BizTalkImportWizard wizard)
    {
        fixture.Responses.Enqueue(Analysis(null, Bindings) with { FlowCandidates = [BillingCandidate] });
        fixture.Call(wizard, "SetSourcePath", "C:\\x.sln");
        await fixture.CallAsync(wizard, "AnalyzeAsync");
        fixture.Call(wizard, "GoTo", BizTalkWizardStage.Review);
        await fixture.RefreshAsync(wizard);
    }

    [Fact]
    public async Task SavingACandidateAsANewFlowSendsTheSameInputsAndRemembersTheFlow()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows"] = Flow("billing-copy");
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            fixture.Call(wizard, "AddEvidence", new BizTalkEvidenceAdd(BizTalkEvidenceKinds.Bindings, BizTalkEvidenceOrigins.Text, "prod bindings", null, "<BindingInfo/>", null));
            await fixture.CallAsync(wizard, "AnalyzeAsync");
            fixture.Call(wizard, "GoTo", BizTalkWizardStage.Review);
            fixture.SetField(wizard, "_newFlowId", " billing-copy ");

            await fixture.CallAsync(wizard, "SaveNewAsync", BillingCandidate);
            await fixture.RefreshAsync(wizard);

            var post = Assert.Single(fixture.Posts);
            Assert.Equal("/api/biztalk-import/flows", post.Path);
            Assert.Contains("\"suggestedFlowId\":\"billing-send\"", post.Body);
            Assert.Contains("\"newFlowId\":\"billing-copy\"", post.Body);
            Assert.Contains("BindingInfo", post.Body);

            Assert.Equal("billing-copy", ((BizTalkImportSession)fixture.Field(wizard, "_session")!).ImportedFlowId);
            Assert.Equal("billing-copy", fixture.Field(wizard, "_updateFlowId"));
            Assert.Contains("Saved &#x27;billing-copy&#x27; as a disabled flow", fixture.Html());
            Assert.Equal("billing-copy", BizTalkImportSession.Import(fixture.Store.Saved["import"]).ImportedFlowId);
        });
    }

    [Fact]
    public async Task SavingWithoutANewIdUsesTheSuggestedIdAndSendsNoOverride()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows"] = Flow("billing-send");
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            await fixture.CallAsync(wizard, "SaveNewAsync", BillingCandidate);
            Assert.DoesNotContain("newFlowId", Assert.Single(fixture.Posts).Body);
        });
    }

    [Fact]
    public async Task ARefusedSaveIsExplainedAndRemembersNothing()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows"] = HttpStatusCode.Conflict;
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            await fixture.CallAsync(wizard, "SaveNewAsync", BillingCandidate);
            await fixture.RefreshAsync(wizard);

            Assert.Contains("Unable to save the flow", fixture.Html());
            Assert.Null(((BizTalkImportSession)fixture.Field(wizard, "_session")!).ImportedFlowId);
            Assert.DoesNotContain("as a disabled flow", fixture.Html());
            Assert.Contains("Save as new disabled flow", fixture.Html());
        });
    }

    [Fact]
    public async Task UpdatingAnImportedFlowShowsThePlanThenReplacesOnlyTheChosenPieces()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows"] = Flow("billing-copy");
        fixture.Replies["/api/biztalk-import/flows/billing-copy/update-plan"] = InPlacePlan("billing-copy");
        fixture.Replies["/api/biztalk-import/flows/billing-copy/update"] = new BizTalkUpdateResultViewModel("InPlace", Flow("billing-copy"), ["augment/1"], ["Nothing was deleted."]);
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            await fixture.CallAsync(wizard, "SaveNewAsync", BillingCandidate);

            await fixture.CallAsync(wizard, "PlanUpdateAsync", BillingCandidate);
            await fixture.RefreshAsync(wizard);
            Assert.Equal("/api/biztalk-import/flows/billing-copy/update-plan", fixture.Posts[1].Path);
            Assert.Contains("Replace 1 selected piece(s)", fixture.Html());
            Assert.Equal(2, fixture.Posts.Count);

            await fixture.CallAsync(wizard, "ApplyUpdateAsync", (object)new[] { "augment/1" });
            await fixture.RefreshAsync(wizard);

            var update = fixture.Posts[2];
            Assert.Equal("/api/biztalk-import/flows/billing-copy/update", update.Path);
            Assert.Contains("\"replacePieces\":[\"augment/1\"]", update.Body);
            Assert.Contains("\"suggestedFlowId\":\"billing-send\"", update.Body);
            var html = fixture.Html();
            Assert.Contains("Updated &#x27;billing-copy&#x27;: replaced 1 piece(s). Nothing was deleted.", html);
            Assert.DoesNotContain("Replace 1 selected piece(s)", html);
        });
    }

    [Fact]
    public async Task UpdatePlanTargetsTheTypedFlowIdEscapedOtherwiseTheSuggestedOne()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows/billing-send/update-plan"] = InPlacePlan("billing-send");
        fixture.Replies["/api/biztalk-import/flows/other%20flow/update-plan"] = InPlacePlan("other flow");
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);

            await fixture.CallAsync(wizard, "PlanUpdateAsync", BillingCandidate);
            Assert.Equal("/api/biztalk-import/flows/billing-send/update-plan", fixture.Posts[0].Path);

            fixture.SetField(wizard, "_updateFlowId", " other flow ");
            await fixture.CallAsync(wizard, "PlanUpdateAsync", BillingCandidate);
            Assert.Equal("/api/biztalk-import/flows/other%20flow/update-plan", fixture.Posts[1].Path);
        });
    }

    [Fact]
    public async Task ARefusedPlanShowsTheErrorAndNoPlan()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows/billing-send/update-plan"] = HttpStatusCode.NotFound;
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            await fixture.CallAsync(wizard, "PlanUpdateAsync", BillingCandidate);
            await fixture.RefreshAsync(wizard);
            Assert.Contains("Unable to plan the update", fixture.Html());
            Assert.DoesNotContain("selected piece(s)", fixture.Html());
        });
    }

    [Fact]
    public async Task ApplyingWithoutAPlanAndClosingAPlanSendNothing()
    {
        await using var fixture = new Fixture();
        fixture.Replies["/api/biztalk-import/flows/billing-send/update-plan"] = InPlacePlan("billing-send");
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            await fixture.CallAsync(wizard, "ApplyUpdateAsync", (object)new[] { "augment/1" });
            Assert.Empty(fixture.Posts);

            await fixture.CallAsync(wizard, "PlanUpdateAsync", BillingCandidate);
            fixture.Call(wizard, "ClosePlan");
            await fixture.RefreshAsync(wizard);
            Assert.DoesNotContain("selected piece(s)", fixture.Html());
            await fixture.CallAsync(wizard, "ApplyUpdateAsync", (object)new[] { "augment/1" });
            Assert.Single(fixture.Posts);
        });
    }

    [Fact]
    public async Task ImportActionsAreDisabledWhileTheAnalysisIsOutOfDate()
    {
        await using var fixture = new Fixture();
        await fixture.RunAsync(async wizard =>
        {
            await ReachReviewWithACandidateAsync(fixture, wizard);
            Assert.DoesNotMatch(@"<button[^>]*disabled[^>]*>Save as new disabled flow", fixture.Html());

            fixture.Call(wizard, "AddEvidence", new BizTalkEvidenceAdd(BizTalkEvidenceKinds.Bindings, BizTalkEvidenceOrigins.Text, "prod bindings", null, "<BindingInfo/>", null));
            await fixture.RefreshAsync(wizard);

            var html = fixture.Html();
            Assert.Matches(@"<button[^>]*disabled[^>]*>Save as new disabled flow", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Update an existing flow", html);
            Assert.Matches(@"<button[^>]*disabled[^>]*>Load onto canvas", html);
        });
    }

    [Fact]
    public async Task UpdatePlanSelectsOnlyWhatTheServiceSuggestsAndNeverOfferedEditsByDefault()
    {
        var plan = new BizTalkUpdatePlanViewModel("imported", "InPlace", "The flow can be updated piece by piece.", new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
        [
            new("augment/1", "Augment", "Rule policy", "UpdateInPlace", true, true),
            new("augment/2", "Augment", "Hand edited map", "Conflict", true, false),
            new("fetch/1", "Fetch", "Source file", "AlreadyCurrent", false, false)
        ], new Dictionary<string, int>(), ["Nothing is deleted."]);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<BizTalkUpdatePlanView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Plan"] = plan }))).ToHtmlString());

        var rows = html.Split("<tr data-piece=\"", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToDictionary(row => row[..row.IndexOf('"')]);
        Assert.Contains("checked", rows["augment/1"]);
        Assert.DoesNotContain("disabled", rows["augment/1"]);
        Assert.DoesNotContain("checked", rows["augment/2"]);
        Assert.Contains("discards your edit", rows["augment/2"]);
        Assert.Contains("disabled", rows["fetch/1"]);
        Assert.Contains("Replace 1 selected piece(s)", html);
        Assert.Contains("Nothing is deleted.", html);
    }

    [Theory]
    [InlineData("NewDraft")]
    [InlineData("NoOrigin")]
    [InlineData("NothingToDo")]
    public async Task UpdatePlanWithoutAnInPlaceModeOffersNoPieceReplacement(string mode)
    {
        var plan = new BizTalkUpdatePlanViewModel("imported", mode, "Reason text.", null, [new("augment/1", "Augment", "Rule policy", "UpdateInPlace", true, true)], new Dictionary<string, int>(), []);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<BizTalkUpdatePlanView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Plan"] = plan }))).ToHtmlString());
        Assert.DoesNotContain("type=\"checkbox\"", html);
        Assert.DoesNotContain("selected piece(s)", html);
        Assert.Contains("Reason text.", html);
    }

    [Fact]
    public async Task BrowserStoreRoundTripsLargeContentInSlicesThePageCanReceive()
    {
        var js = new FakeBrowser();
        await using var store = new BizTalkSessionStore(js);
        var session = new BizTalkImportSession { Name = "big", SourcePath = "C:\\x.sln" };
        var large = new string('x', 100_000) + "<end/>";
        session.AddText(BizTalkEvidenceKinds.Bindings, "large", large, TimeProvider.System);
        session.AddUpload(BizTalkEvidenceKinds.RulePolicy, "rules.zip", [1, 2, 3, 250], TimeProvider.System);
        session.AddPath(BizTalkEvidenceKinds.HostConfig, "C:\\host.config", null, TimeProvider.System);
        await store.SaveAsync(session, default);

        var listing = Assert.Single(await store.ListAsync(default));
        Assert.Equal("big", listing.Name);
        var loaded = await store.LoadAsync("big", default);
        Assert.NotNull(loaded);
        Assert.Equal(large, loaded!.Content[loaded.Evidence[0].Id].Text);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 250]), loaded.Content[loaded.Evidence[1].Id].ContentBase64);
        Assert.Equal(2, loaded.Content.Count);
        Assert.True(js.Slices > 4);
        Assert.True(js.LargestSlice <= 16 * 1024, $"A slice of {js.LargestSlice} characters would exceed the circuit's receive limit.");
        Assert.Equal(0, js.OpenReads);

        Assert.Null(await store.LoadAsync("missing", default));

        session.RemoveEvidence(session.Evidence[0].Id);
        await store.SaveAsync(session, default);
        Assert.Single(js.ContentKeys);
        await store.DeleteAsync("big", default);
        Assert.Empty(js.ContentKeys);
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task BrowserStoreReportsStorageFailuresAsOneExceptionType()
    {
        var js = new FakeBrowser { Throw = new JSException("QuotaExceededError") };
        await using var store = new BizTalkSessionStore(js);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync(default));
        Assert.Contains("QuotaExceededError", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new BizTalkImportSession { Name = "a/b" }, default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly HtmlRenderer _renderer;
        private Func<string>? _html;

        public Fixture(MemoryStore? store = null)
        {
            Store = store ?? new MemoryStore();
            var handler = new Handler(this);
            _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            _services = new ServiceCollection().AddLogging()
                .AddSingleton<IMulseApiClient>(new MulseApiClient(_http))
                .AddSingleton<IBizTalkSessionStore>(Store)
                .AddSingleton<IJSRuntime>(new NoJs())
                .BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public MemoryStore Store { get; }
        public Queue<BizTalkSolutionAnalysisViewModel> Responses { get; } = new();
        public List<string> Bodies { get; } = [];

        /// <summary>Every non-analyze request the wizard made.</summary>
        public List<(string Path, string Body)> Posts { get; } = [];

        /// <summary>Canned answers by absolute path: a view model to return as JSON, or an HttpStatusCode to fail with.</summary>
        public Dictionary<string, object> Replies { get; } = new(StringComparer.Ordinal);

        public string Html() => _html!();

        public Task RunAsync(Func<BizTalkImportWizard, Task> test) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            BizTalkImportWizard? wizard = null;
            var root = await _renderer.RenderComponentAsync<WizardHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WizardHost.Capture)] = (Action<BizTalkImportWizard>)(value => wizard = value) }));
            _html = root.ToHtmlString;
            Assert.NotNull(wizard);
            await CallAsync(wizard, "OnAfterRenderAsync", true);
            await test(wizard);
        });

        // The static renderer never runs after-render hooks, so they are invoked the way an interactive circuit would.
        public async Task RefreshAsync(BizTalkImportWizard wizard)
        {
            await wizard.SetParametersAsync(ParameterView.Empty);
            await CallAsync(wizard, "OnAfterRenderAsync", false);
        }

        private static MethodInfo Method(object target, string name, object?[] args)
            => target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(m => m.Name == name && m.GetParameters().Length == args.Length);

        public object? Call(object target, string name, params object?[] args) => Method(target, name, args).Invoke(target, args);

        public Task CallAsync(object target, string name, params object?[] args) => (Task)Call(target, name, args)!;

        public object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

        public void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

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
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (path == "/api/biztalk-import/analyze")
                {
                    owner.Bodies.Add(body);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner.Responses.Dequeue(), options: Web) };
                }

                owner.Posts.Add(new(path, body));
                if (!owner.Replies.TryGetValue(path, out var reply)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return reply is HttpStatusCode status
                    ? new HttpResponseMessage(status) { Content = new StringContent("The server refused it.") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, reply.GetType(), options: Web) };
            }
        }
    }

    private sealed class WizardHost : ComponentBase
    {
        [Parameter] public Action<BizTalkImportWizard>? Capture { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<BizTalkImportWizard>(0);
            builder.AddComponentReferenceCapture(1, value => Capture?.Invoke((BizTalkImportWizard)value));
            builder.CloseComponent();
        }
    }

    private sealed class MemoryStore : IBizTalkSessionStore
    {
        public Dictionary<string, string> Saved { get; } = new(StringComparer.Ordinal);
        public Exception? FailWith { get; set; }

        public Task<IReadOnlyList<BizTalkSessionListing>> ListAsync(CancellationToken cancellationToken)
            => FailWith is null
                ? Task.FromResult<IReadOnlyList<BizTalkSessionListing>>([.. Saved.Keys.Select(name => new BizTalkSessionListing(name, null))])
                : Task.FromException<IReadOnlyList<BizTalkSessionListing>>(FailWith);

        public Task<BizTalkImportSession?> LoadAsync(string name, CancellationToken cancellationToken)
            => Task.FromResult(Saved.TryGetValue(name, out var json) ? BizTalkImportSession.Import(json) : null);

        public Task SaveAsync(BizTalkImportSession session, CancellationToken cancellationToken)
        {
            if (FailWith is not null) return Task.FromException(FailWith);
            Saved[session.Name] = session.Export(includeContent: true);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Saved.Remove(name);
            return Task.CompletedTask;
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
    }

    /// <summary>Stands in for biztalk-session-store.js: same function names, strings kept in memory, results passed through JSON like real interop.</summary>
    private sealed class FakeBrowser : IJSRuntime
    {
        private readonly Dictionary<string, string> _index = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _content = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _open = new(StringComparer.Ordinal);

        public Exception? Throw { get; set; }
        public int Slices { get; private set; }
        public int LargestSlice { get; private set; }
        public int OpenReads => _open.Count;
        public IReadOnlyCollection<string> ContentKeys => _content.Keys;

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (Throw is not null) throw Throw;
            if (identifier == "import") return new((T)(object)new Module(this));
            throw new NotSupportedException(identifier);
        }

        private object? Invoke(string function, object?[] args)
        {
            string Key() => $"{args[0]}/{args[1]}";
            switch (function)
            {
                case "listSessions":
                    return _index.Select(pair => new { name = pair.Key, updatedUtc = JsonDocument.Parse(pair.Value).RootElement.TryGetProperty("updatedUtc", out var updated) ? updated.GetString() : null }).ToArray();
                case "loadIndex": return _index.GetValueOrDefault((string)args[0]!);
                case "saveIndex": _index[(string)args[0]!] = (string)args[1]!; return null;
                case "deleteIndex": _index.Remove((string)args[0]!); return null;
                case "putContent": _content[Key()] = (string)args[2]!; return null;
                case "openContent":
                    if (!_content.TryGetValue(Key(), out var data)) return null;
                    _open[Key()] = data;
                    return data.Length;
                case "readContentSlice":
                    var slice = _open[Key()].Substring((int)args[2]!, (int)args[3]!);
                    Slices++;
                    LargestSlice = Math.Max(LargestSlice, slice.Length);
                    return slice;
                case "releaseContent": _open.Remove(Key()); return null;
                case "deleteContent": _content.Remove(Key()); return null;
                case "deleteSessionContent":
                    foreach (var key in _content.Keys.Where(key => key.StartsWith(args[0] + "/", StringComparison.Ordinal)).ToArray()) _content.Remove(key);
                    return null;
                default: throw new NotSupportedException(function);
            }
        }

        private sealed class Module(FakeBrowser owner) : IJSObjectReference
        {
            public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);

            public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
            {
                if (owner.Throw is not null) throw owner.Throw;
                var result = owner.Invoke(identifier, args ?? []);
                return new(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result), JsonSerializerOptions.Web)!);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
