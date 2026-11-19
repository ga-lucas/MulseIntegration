using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Service.BizTalkImport;
using Service.BizTalkImport.Origin;
using Service.Flows;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkImportUpdateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, string> NoConfig = new Dictionary<string, string>();

    private static NativeFlowNode Aug(string id, string value, string? cases = null)
    {
        var node = new NativeFlowNode { Id = id, Name = "Step " + id, Kind = NativeFlowNodeKind.Augment, Module = "m", Settings = { ["x"] = value } };
        if (cases is not null) node.Settings["casesJson"] = cases;
        return node;
    }

    private static NativeFlowNode Seq(string id, params NativeFlowNode[] children) => new() { Id = id, Name = id, Kind = NativeFlowNodeKind.Sequence, Children = [.. children] };

    private static string Cases(params (string Value, string Setting)[] choices)
        => JsonSerializer.Serialize(choices.Select(choice => new { value = choice.Value, module = "r", settings = new { s = choice.Setting } }), Json);

    private static string Flow(string? shared, params NativeFlowNode[] nodes)
        => JsonSerializer.Serialize(new NativeFlowDefinition { Nodes = [.. nodes], RenderChoicesJson = shared }, Json);

    private static string StandardFlow(string aValue = "1", string? choices = null, string? shared = null, string outputMessage = "")
        => JsonSerializer.Serialize(new NativeFlowDefinition
        {
            Nodes = [Seq("seq", Aug("a", aValue), Aug("b", "2", choices ?? Cases(("v1", "one"), ("v2", "two"), ("v4", "four")))), Aug("c", "3")],
            RenderChoicesJson = shared ?? Cases(("s1", "shared")),
            OutputMessage = outputMessage
        }, Json);

    private static BizTalkFlowCandidateResponse Candidate(string flowJson, IReadOnlyDictionary<string, string>? config = null)
    {
        var draft = new BizTalkDraftFlowResponse("flow", false, new BizTalkDraftTriggerResponse(PipelineTriggerMode.OnDemand, null, false),
            [new BizTalkDraftFetchResponse("in", new BizTalkDraftStepResponse("fetch", new Dictionary<string, string>()), [])],
            [new BizTalkDraftParseResponse("parsed", new BizTalkDraftStepResponse("parse", new Dictionary<string, string>()), ["in"])],
            [new BizTalkDraftStepResponse(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = flowJson })],
            [], [], [], new FlowMigrationReport("BizTalk", "p", null, [], [], []));
        return BizTalkImportService.AttachOrigin(new BizTalkFlowCandidateResponse("flow", "p", "s", "", "", [], [], [], [], [], draft)
        {
            StoredConfigValues = config ?? NoConfig
        });
    }

    private static PipelineDefinition Saved(BizTalkFlowCandidateResponse candidate) => FlowMappings.MapPipeline(candidate.DraftFlow);

    private static PipelineDefinition WithFlow(PipelineDefinition pipeline, string flowJson)
        => FlowMappings.CopyPipeline(pipeline, pipeline.Id, false, pipeline.Category, null,
            [new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new(StringComparer.OrdinalIgnoreCase) { ["flowJson"] = flowJson } }]);

    private static PipelineDefinition Edit(PipelineDefinition pipeline, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(pipeline)!)!.AsObject();
        edit(root);
        return WithFlow(pipeline, root.ToJsonString());
    }

    private static JsonObject Find(JsonNode? nodes, string id)
    {
        foreach (var item in nodes as JsonArray ?? [])
        {
            var node = item!.AsObject();
            if (node["id"]!.GetValue<string>() == id) return node;
            var inner = Find(node["children"], id);
            if (inner is not null) return inner;
        }

        return null!;
    }

    private static BizTalkUpdatePlan Plan(PipelineDefinition saved, BizTalkFlowCandidateResponse fresh, IReadOnlyDictionary<string, string>? config = null)
        => BizTalkUpdatePlanner.Plan(saved, fresh, name => (config ?? NoConfig).GetValueOrDefault(name)).Plan;

    private static BizTalkUpdatePlanner.Applied Apply(PipelineDefinition saved, BizTalkFlowCandidateResponse fresh, IReadOnlyDictionary<string, string>? config, params string[] keys)
        => BizTalkUpdatePlanner.Apply(saved, fresh, name => (config ?? NoConfig).GetValueOrDefault(name), keys, Now);

    private static BizTalkUpdatePieceState StateOf(BizTalkUpdatePlan plan, string key) => Assert.Single(plan.Pieces, piece => piece.Key == key).State;

    [Fact]
    public void Origin_RecordsHashesOfEveryPieceAndIgnoresPropertyOrder()
    {
        var first = Candidate(StandardFlow(), new Dictionary<string, string> { ["biztalk-map.x"] = "content" });
        var origin = first.DraftFlow.Migration!.Origin!;
        Assert.Null(origin.ImportedUtc);
        foreach (var key in new[] { "structure", "flow", "node:seq", "node:a", "node:b", "node:c", "choice:b:v1", "choice:b:v2", "choice:b:v4", "shared-choice:s1", "config:biztalk-map.x" })
            Assert.Contains(key, origin.Pieces.Keys);
        Assert.All(origin.Pieces.Values, hash => Assert.Equal(16, hash.Length));
        Assert.DoesNotContain("content", string.Concat(origin.Pieces.Values));

        var reordered = JsonNode.Parse(StandardFlow())!.AsObject();
        var node = Find(reordered["nodes"], "a");
        var settings = node["settings"]!.AsObject();
        node.Remove("settings");
        node["settings"] = settings;
        reordered.Remove("durable");
        reordered["durable"] = false;
        Assert.Equal(origin.Pieces, BizTalkOriginPieces.Read(reordered.ToJsonString(), new Dictionary<string, string> { ["biztalk-map.x"] = "content" }).Hashes);
    }

    [Fact]
    public void Plan_WithNothingChanged_HasNothingToDo()
    {
        var candidate = Candidate(StandardFlow());
        var plan = Plan(Saved(candidate), candidate);
        Assert.Equal(BizTalkUpdateMode.NothingToDo, plan.Mode);
        Assert.Empty(plan.Pieces);
        Assert.True(plan.Counts["Unchanged"] > 5);
    }

    [Fact]
    public void Plan_ImporterChangeToAnUneditedNodeUpdatesJustThatNodeInPlace()
    {
        var saved = Saved(Candidate(StandardFlow()));
        var fresh = Candidate(StandardFlow(aValue: "changed"));
        var plan = Plan(saved, fresh);
        Assert.Equal(BizTalkUpdateMode.InPlace, plan.Mode);
        var piece = Assert.Single(plan.Pieces);
        Assert.Equal("node:a", piece.Key);
        Assert.Equal(BizTalkUpdatePieceState.UpdateInPlace, piece.State);
        Assert.True(piece.Selectable);
        Assert.True(piece.SelectedByDefault);

        var applied = Apply(saved, fresh, null, "node:a");
        Assert.False(applied.Pipeline.Enabled);
        var patched = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(applied.Pipeline)!)!.AsObject();
        Assert.Equal("changed", Find(patched["nodes"], "a")["settings"]!["x"]!.GetValue<string>());
        Assert.Equal("seq", Find(patched["nodes"], "seq")["id"]!.GetValue<string>());
        Assert.Equal(2, Find(patched["nodes"], "seq")["children"]!.AsArray().Count);
        _ = NativeFlowDefinition.Read(BizTalkUpdatePlanner.SavedNativeFlowJson(applied.Pipeline)!, allowBlocked: true);
        Assert.Equal(Now, applied.Pipeline.Migration!.Origin!.ImportedUtc);
        Assert.Contains(applied.Pipeline.Migration.Warnings, warning => warning.Contains("Updated in place", StringComparison.Ordinal));
        Assert.Equal(BizTalkUpdateMode.NothingToDo, Plan(applied.Pipeline, fresh).Mode);
    }

    [Fact]
    public void Plan_UserEditWithNoImporterChangeIsKeptAndNeverNeedsAnUpdate()
    {
        var candidate = Candidate(StandardFlow());
        var saved = Edit(Saved(candidate), root => Find(root["nodes"], "a")["settings"]!["x"] = "mine");
        var plan = Plan(saved, candidate);
        Assert.Equal(BizTalkUpdateMode.NothingToDo, plan.Mode);
        var piece = Assert.Single(plan.Pieces);
        Assert.Equal(("node:a", BizTalkUpdatePieceState.KeptEdit, false), (piece.Key, piece.State, piece.SelectedByDefault));
        Assert.Throws<InvalidOperationException>(() => Apply(saved, candidate, null, "node:a"));
    }

    [Fact]
    public void Plan_EditAndImporterChangeIsAConflictThatIsReplacedOnlyWhenSelected()
    {
        var saved = Edit(Saved(Candidate(StandardFlow())), root => Find(root["nodes"], "a")["settings"]!["x"] = "mine");
        var fresh = Candidate(StandardFlow(aValue: "new").Replace("\"3\"", "\"3b\"", StringComparison.Ordinal));
        var plan = Plan(saved, fresh);
        Assert.Equal(BizTalkUpdatePieceState.Conflict, StateOf(plan, "node:a"));
        Assert.False(Assert.Single(plan.Pieces, piece => piece.Key == "node:a").SelectedByDefault);
        Assert.Equal(BizTalkUpdatePieceState.UpdateInPlace, StateOf(plan, "node:c"));

        var onlyC = Apply(saved, fresh, null, "node:c");
        var kept = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(onlyC.Pipeline)!)!.AsObject();
        Assert.Equal("mine", Find(kept["nodes"], "a")["settings"]!["x"]!.GetValue<string>());
        Assert.Equal("3b", Find(kept["nodes"], "c")["settings"]!["x"]!.GetValue<string>());
        Assert.Equal(BizTalkUpdatePieceState.Conflict, StateOf(Plan(onlyC.Pipeline, fresh), "node:a"));

        var both = Apply(saved, fresh, null, "node:a", "node:c");
        var replaced = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(both.Pipeline)!)!.AsObject();
        Assert.Equal("new", Find(replaced["nodes"], "a")["settings"]!["x"]!.GetValue<string>());
        Assert.Equal(BizTalkUpdateMode.NothingToDo, Plan(both.Pipeline, fresh).Mode);
    }

    [Fact]
    public void Plan_TracksDispatchChoicesIndividuallyAndNeverDeletes()
    {
        var saved = Edit(Saved(Candidate(StandardFlow())), root =>
        {
            var settings = Find(root["nodes"], "b")["settings"]!.AsObject();
            settings["casesJson"] = Cases(("v1", "one"), ("v2", "edited"), ("v4", "four"));
        });
        var fresh = Candidate(StandardFlow(choices: Cases(("v1", "one-new"), ("v2", "two"), ("v3", "three"))));
        var plan = Plan(saved, fresh);
        Assert.Equal(BizTalkUpdatePieceState.UpdateInPlace, StateOf(plan, "choice:b:v1"));
        Assert.Equal(BizTalkUpdatePieceState.KeptEdit, StateOf(plan, "choice:b:v2"));
        Assert.Equal(BizTalkUpdatePieceState.Add, StateOf(plan, "choice:b:v3"));
        var removed = Assert.Single(plan.Pieces, piece => piece.Key == "choice:b:v4");
        Assert.Equal((BizTalkUpdatePieceState.Removed, false), (removed.State, removed.Selectable));
        Assert.DoesNotContain(plan.Pieces, piece => piece.Key == "node:b");

        var applied = Apply(saved, fresh, null, "choice:b:v1", "choice:b:v3");
        var root = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(applied.Pipeline)!)!.AsObject();
        var cases = JsonNode.Parse(Find(root["nodes"], "b")["settings"]!["casesJson"]!.GetValue<string>())!.AsArray()
            .ToDictionary(entry => entry!["value"]!.GetValue<string>(), entry => entry!["settings"]!["s"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["v1"] = "one-new", ["v2"] = "edited", ["v4"] = "four", ["v3"] = "three" }, cases);
        Assert.Throws<ArgumentException>(() => Apply(saved, fresh, null, "choice:b:v4"));
    }

    [Fact]
    public void Plan_TracksSharedChoicesAndFlowLevelSettings()
    {
        var saved = Saved(Candidate(StandardFlow()));
        var fresh = Candidate(StandardFlow(shared: Cases(("s1", "shared"), ("s2", "added")), outputMessage: "out"));
        var plan = Plan(saved, fresh);
        Assert.Equal(BizTalkUpdatePieceState.Add, StateOf(plan, "shared-choice:s2"));
        Assert.Equal(BizTalkUpdatePieceState.UpdateInPlace, StateOf(plan, "flow"));

        var applied = Apply(saved, fresh, null, "shared-choice:s2", "flow");
        var root = JsonNode.Parse(BizTalkUpdatePlanner.SavedNativeFlowJson(applied.Pipeline)!)!.AsObject();
        Assert.Equal("out", root["outputMessage"]!.GetValue<string>());
        var shared = JsonNode.Parse(root["renderChoicesJson"]!.GetValue<string>())!.AsArray();
        Assert.Equal(["s1", "s2"], shared.Select(entry => entry!["value"]!.GetValue<string>()));
        Assert.Equal(2, root["nodes"]!.AsArray().Count);
    }

    [Fact]
    public void Plan_StructureChangeOnEitherSideRequiresANewDraft()
    {
        var saved = Saved(Candidate(StandardFlow()));
        var importerAdds = Candidate(JsonSerializer.Serialize(new NativeFlowDefinition
        {
            Nodes = [Seq("seq", Aug("a", "1"), Aug("b", "2", Cases(("v1", "one"), ("v2", "two"), ("v4", "four")))), Aug("c", "3"), Aug("d", "4")],
            RenderChoicesJson = Cases(("s1", "shared"))
        }, Json));
        var plan = Plan(saved, importerAdds);
        Assert.Equal(BizTalkUpdateMode.NewDraft, plan.Mode);
        Assert.Contains("importer now produces a different node structure", plan.Reason, StringComparison.Ordinal);
        Assert.False(Assert.Single(plan.Pieces).Selectable);
        Assert.Throws<InvalidOperationException>(() => Apply(saved, importerAdds, null, "node:a"));

        var userAdds = Edit(saved, root => root["nodes"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(Aug("mine", "9"), Json))));
        var userPlan = Plan(userAdds, Candidate(StandardFlow()));
        Assert.Equal(BizTalkUpdateMode.NewDraft, userPlan.Mode);
        Assert.Contains("edited after import", userPlan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_FlowsWithoutOriginOrWithoutAnImportAreHandledExplicitly()
    {
        var candidate = Candidate(StandardFlow());
        var saved = Saved(candidate);
        var legacy = FlowMappings.CopyPipeline(saved, saved.Id, false, null, saved.Migration! with { Origin = null });
        var plan = Plan(legacy, candidate);
        Assert.Equal(BizTalkUpdateMode.NoOrigin, plan.Mode);
        Assert.Empty(plan.Pieces);
        Assert.Throws<InvalidOperationException>(() => Apply(legacy, candidate, null, "node:a"));

        var plain = new PipelineDefinition { Id = "plain" };
        Assert.Throws<InvalidOperationException>(() => Plan(plain, candidate));
    }

    [Fact]
    public void Plan_StoredConfigValuesAreComparedAndOverwrittenOnlyWhenSelected()
    {
        var first = Candidate(StandardFlow(), new Dictionary<string, string> { ["biztalk-map.keep"] = "k", ["biztalk-map.edit"] = "e1", ["biztalk-map.stale"] = "s1" });
        var saved = Saved(first);
        var store = new Dictionary<string, string> { ["biztalk-map.keep"] = "k", ["biztalk-map.edit"] = "e1-mine", ["biztalk-map.stale"] = "s1" };
        var fresh = Candidate(StandardFlow(), new Dictionary<string, string>
        {
            ["biztalk-map.keep"] = "k", ["biztalk-map.edit"] = "e2", ["biztalk-map.stale"] = "s2", ["biztalk-map.new"] = "n"
        });
        var plan = Plan(saved, fresh, store);
        Assert.Equal(BizTalkUpdatePieceState.Conflict, StateOf(plan, "config:biztalk-map.edit"));
        Assert.Equal(BizTalkUpdatePieceState.UpdateInPlace, StateOf(plan, "config:biztalk-map.stale"));
        Assert.Equal(BizTalkUpdatePieceState.Add, StateOf(plan, "config:biztalk-map.new"));
        Assert.DoesNotContain(plan.Pieces, piece => piece.Key == "config:biztalk-map.keep");

        var applied = Apply(saved, fresh, store, "config:biztalk-map.stale", "config:biztalk-map.new");
        Assert.Equal([("biztalk-map.new", "n", false), ("biztalk-map.stale", "s2", true)],
            applied.ConfigWrites.OrderBy(write => write.Name, StringComparer.Ordinal).Select(write => (write.Name, write.Content, write.Overwrite)));
        Assert.Equal(BizTalkUpdatePieceState.Conflict, StateOf(Plan(applied.Pipeline, fresh, store), "config:biztalk-map.edit"));
    }

    [Fact]
    public void Apply_RejectsEmptyAndUnknownSelections()
    {
        var saved = Saved(Candidate(StandardFlow()));
        var fresh = Candidate(StandardFlow(aValue: "changed"));
        Assert.Throws<ArgumentException>(() => Apply(saved, fresh, null));
        Assert.Throws<ArgumentException>(() => Apply(saved, fresh, null, "node:b"));
        Assert.Throws<ArgumentException>(() => Apply(saved, fresh, null, "structure"));
    }

    [Fact]
    public async Task ReplaceImported_SavesTheFreshReportDisabledAndOnlyForImportedFlows()
    {
        var catalog = FakeModuleCatalog.WithModules(("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse), (NativeFlowDefinition.ModuleId, ModuleKind.OrchestrationAugment));
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var service = new FlowDefinitionService(store, catalog);
        var first = Candidate(Flow(null, Seq("seq", new NativeFlowNode { Id = "blocked", Kind = NativeFlowNodeKind.Blocked, BlockReason = "needs evidence" })));
        var created = await service.CreateAsync(Saved(first), CancellationToken.None);
        var fresh = Candidate(Flow(null, Seq("seq", new NativeFlowNode { Id = "blocked", Kind = NativeFlowNodeKind.Blocked, BlockReason = "still needs evidence" })));
        var applied = Apply(created, fresh, null, "node:blocked");

        var viaUpdate = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(created.Id, applied.Pipeline, CancellationToken.None));
        Assert.Contains("provenance cannot be replaced", viaUpdate.Message, StringComparison.Ordinal);

        var saved = await service.ReplaceImportedAsync(created.Id, applied.Pipeline, CancellationToken.None);
        Assert.False(saved.Enabled);
        Assert.Equal(Now, saved.Migration!.Origin!.ImportedUtc);
        Assert.Contains("still needs evidence", BizTalkUpdatePlanner.SavedNativeFlowJson(saved), StringComparison.Ordinal);
        Assert.Equal(BizTalkUpdateMode.NothingToDo, Plan(saved, fresh).Mode);

        await store.UpsertFlowAsync(new PipelineDefinition { Id = "hand-made", Fetches = created.Fetches, Parses = created.Parses, Augments = created.Augments }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceImportedAsync("hand-made", FlowMappings.CopyPipeline(applied.Pipeline, "hand-made", false, null), CancellationToken.None));
    }

    [Fact]
    public void Origin_SurvivesTheUiViewModelRoundTripSoSavingAnImportedFlowStillPassesTheProvenanceGuard()
    {
        var report = Candidate(StandardFlow()).DraftFlow.Migration! with { Origin = new MigrationOrigin(new Dictionary<string, string> { ["node:a"] = "abc" }) { ImportedUtc = Now } };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var viaUi = JsonSerializer.Deserialize<Mulse.Ui.Models.FlowMigrationReportViewModel>(JsonSerializer.Serialize(report, options), options)!;
        var back = JsonSerializer.Deserialize<FlowMigrationReport>(JsonSerializer.Serialize(viaUi, options), options)!;
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(back));
        Assert.Equal(Now, back.Origin!.ImportedUtc);
    }
}
