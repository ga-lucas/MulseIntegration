using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.BizTalkImport;
using Service.Endpoints;
using Service.Middleware;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkWizardSessionTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly Clock _time = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-wizard-ui-" + Guid.NewGuid().ToString("N"));

    private string WriteProject()
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Generic.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Generic</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Process.odx'/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Process.odx"),
            "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='CallRules'><om:Property Name='PolicyName' Value='Any Policy'/></om:Element></om:MetaModel>");
        return project;
    }

    [Fact]
    public void EvidenceIdsAreValidUniqueAndDerivedFromKindAndName()
    {
        var session = new BizTalkImportSession();
        var first = session.AddText(BizTalkEvidenceKinds.Bindings, "My bindings (prod).xml", "<BindingInfo/>", _time);
        var second = session.AddText(BizTalkEvidenceKinds.Bindings, "My bindings (prod).xml", "<BindingInfo/>", _time);
        Assert.Equal("bindings-My-bindings--prod", first.Id);
        Assert.Equal("bindings-My-bindings--prod-2", second.Id);
        Assert.All(session.Evidence, e => Assert.True(BizTalkImportSession.IsValidId(e.Id)));
        var longName = session.AddText(BizTalkEvidenceKinds.RulePolicy, new string('x', 80), "<brl/>", _time);
        Assert.True(longName.Id.Length <= BizTalkImportSession.MaxIdLength);
    }

    [Fact]
    public void ItemLimitUnknownKindsAndEmptyContentAreNamedErrors()
    {
        var session = new BizTalkImportSession();
        for (var i = 0; i < BizTalkImportSession.MaxEvidenceItems; i++) session.AddPath(BizTalkEvidenceKinds.Bindings, $"C:\\b\\{i}.xml", null, _time);
        Assert.Contains("at most 32", Assert.Throws<InvalidOperationException>(() => session.AddText(BizTalkEvidenceKinds.Bindings, "x", "y", _time)).Message);
        session.RemoveEvidence(session.Evidence[0].Id);
        Assert.Throws<ArgumentException>(() => session.AddText("Nonsense", "x", "y", _time));
        Assert.Throws<ArgumentException>(() => session.AddText(BizTalkEvidenceKinds.Bindings, "x", "  ", _time));
        Assert.Throws<ArgumentException>(() => session.AddUpload(BizTalkEvidenceKinds.Bindings, "x.zip", [], _time));
        Assert.Throws<ArgumentException>(() => session.AddPath(BizTalkEvidenceKinds.Bindings, " ", null, _time));
    }

    [Fact]
    public void ReplacingEvidenceKeepsItsIdAndReplacesTheContent()
    {
        var session = new BizTalkImportSession();
        var original = session.AddText(BizTalkEvidenceKinds.HostConfig, "host.config", "<one/>", _time);
        _time.Advance(TimeSpan.FromMinutes(5));
        var replaced = session.AddText(BizTalkEvidenceKinds.HostConfig, "host.config", "<two/>", _time, original.Id);
        Assert.Equal(original.Id, replaced.Id);
        Assert.Single(session.Evidence);
        Assert.Equal("<two/>", session.Content[original.Id].Text);
        Assert.Equal(_time.GetUtcNow(), session.Evidence[0].AddedUtc);
    }

    [Fact]
    public void RequestCarriesPathsTextAndUploadsAndLeavesOutUnusedOptions()
    {
        var session = new BizTalkImportSession { SourcePath = " C:\\src\\app.sln ", AdditionalBindingPaths = ["C:\\b.xml", "c:\\B.xml", " "], GenerateNativeFlow = true, DeriveNativeMapChoices = true };
        session.NativePolicyVersions["Policy"] = "1.2";
        session.AddPath(BizTalkEvidenceKinds.HostConfig, "C:\\host.config", null, _time);
        session.AddText(BizTalkEvidenceKinds.RulePolicy, "rules.xml", "<brl/>", _time);
        session.AddUpload(BizTalkEvidenceKinds.Source, "tree.zip", [1, 2, 3], _time);
        var request = session.BuildAnalyzeRequest();
        Assert.Equal("C:\\src\\app.sln", request.SourcePath);
        Assert.Equal(["C:\\b.xml"], request.AdditionalBindingPaths);
        Assert.Null(request.AdditionalRulePaths);
        Assert.True(request.DeriveNativeMapChoices);
        Assert.Equal("1.2", request.NativePolicyVersions!["Policy"]);
        Assert.Equal(["Path", "Text", "Upload"], request.Evidence!.Select(e => e.Origin));
        Assert.Equal("AQID", request.Evidence![2].ContentBase64);
        Assert.Null(request.Evidence[0].Text);

        session.GenerateNativeFlow = false;
        var plain = session.BuildAnalyzeRequest();
        Assert.Null(plain.NativePolicyVersions);
        Assert.False(plain.DeriveNativeMapChoices);

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("previousSummary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceEvidenceStandsInForAnEmptySourcePath()
    {
        var session = new BizTalkImportSession();
        Assert.Equal(string.Empty, session.EffectiveSourcePath());
        var source = session.AddUpload(BizTalkEvidenceKinds.Source, "tree.zip", [9], _time);
        Assert.Equal($"evidence:{source.Id}", session.BuildAnalyzeRequest().SourcePath);
        session.SourcePath = "C:\\real";
        Assert.Equal("C:\\real", session.BuildAnalyzeRequest().SourcePath);
    }

    [Fact]
    public void ImportRequestMatchesTheAnalysisInputsAndDropsThePreviousSummary()
    {
        var session = new BizTalkImportSession { SourcePath = "C:\\src", LastSummary = new() { Requests = [new("bindings", "Bindings")] } };
        session.AddText(BizTalkEvidenceKinds.Bindings, "b.xml", "<BindingInfo/>", _time);
        Assert.NotNull(session.BuildAnalyzeRequest().PreviousSummary);
        var import = session.BuildImportRequest("flow-a", " flow-a-v2 ");
        Assert.Equal("flow-a", import.SuggestedFlowId);
        Assert.Equal("flow-a-v2", import.NewFlowId);
        Assert.Equal(session.BuildAnalyzeRequest().Evidence, import.Evidence);
        Assert.Null(session.BuildImportRequest("flow-a").NewFlowId);
    }

    [Fact]
    public void RequestStatusIsOpenSuppliedOrTheUsersAnswerAndAnswersExpireWithTheirRequests()
    {
        var bindings = new BizTalkEvidenceRequestViewModel("bindings", "bindings", "Bindings", "why", ["step"], [BizTalkEvidenceKinds.Bindings], "Certain", [], null);
        var session = new BizTalkImportSession();
        Assert.Equal("Open", session.StatusOf(bindings));
        session.AddText(BizTalkEvidenceKinds.Bindings, "b.xml", "<BindingInfo/>", _time);
        Assert.Equal("Supplied", session.StatusOf(bindings));
        session.Answer("bindings", BizTalkRequestAnswers.NotAvailable, "  no access  ");
        Assert.Equal("NotAvailable", session.StatusOf(bindings));
        Assert.Equal("no access", session.RequestNotes["bindings"].Note);
        Assert.Throws<ArgumentException>(() => session.Answer("bindings", "Done", null));

        var analysis = Analysis([]);
        session.ApplyAnalysis(analysis, _time);
        Assert.Empty(session.RequestNotes);
        session.Answer("bindings", BizTalkRequestAnswers.Skipped, null);
        Assert.Empty(session.StillOpen(Analysis([bindings])));
        session.Reopen("bindings");
        Assert.Single(session.StillOpen(Analysis([bindings])));
    }

    [Fact]
    public void IndexJsonHoldsNoContentAndExportIncludesContentOnlyWhenAsked()
    {
        var session = new BizTalkImportSession { Name = "billing", SourcePath = "C:\\src", ImportedFlowId = "billing-flow" };
        session.AddText(BizTalkEvidenceKinds.HostConfig, "host.config", "<secret-looking-value/>", _time);
        session.AddUpload(BizTalkEvidenceKinds.Bindings, "b.zip", [1, 2, 3], _time);
        session.Answer("x", BizTalkRequestAnswers.Skipped, "later");
        Assert.DoesNotContain("secret-looking-value", session.ToIndexJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("AQID", session.ToIndexJson(), StringComparison.Ordinal);

        var bare = BizTalkImportSession.Import(session.Export(includeContent: false));
        Assert.Equal("billing-flow", bare.ImportedFlowId);
        Assert.Equal(2, bare.Evidence.Count);
        Assert.Empty(bare.Content);
        Assert.Equal(["hostconfig-host", "bindings-b"], bare.EvidenceMissingContent().Select(e => e.Id));
        Assert.Empty(bare.BuildEvidence());
        Assert.Equal("later", bare.RequestNotes["x"].Note);

        var full = BizTalkImportSession.Import(session.Export(includeContent: true));
        Assert.Empty(full.EvidenceMissingContent());
        Assert.Equal(2, full.BuildEvidence().Count);
        Assert.Equal("<secret-looking-value/>", full.Content["hostconfig-host"].Text);

        var index = BizTalkImportSession.FromIndexJson(session.ToIndexJson());
        Assert.Equal(session.Evidence.Select(e => e.Id), index.Evidence.Select(e => e.Id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"session\":{\"version\":9,\"name\":\"x\"}}")]
    [InlineData("{\"session\":{\"name\":\"\"}}")]
    [InlineData("{\"session\":{\"name\":\"x\",\"evidence\":[{\"id\":\"bad id\",\"kind\":\"Bindings\",\"origin\":\"Text\"}]}}")]
    [InlineData("{\"session\":{\"name\":\"x\",\"evidence\":[{\"id\":\"a\",\"kind\":\"Nope\",\"origin\":\"Text\"}]}}")]
    [InlineData("{\"session\":{\"name\":\"x\",\"evidence\":[{\"id\":\"a\",\"kind\":\"Bindings\",\"origin\":\"Path\"}]}}")]
    [InlineData("{\"session\":{\"name\":\"x\",\"evidence\":[{\"id\":\"a\",\"kind\":\"Bindings\",\"origin\":\"Text\"},{\"id\":\"A\",\"kind\":\"Bindings\",\"origin\":\"Text\"}]}}")]
    public void ImportingAnInvalidSessionIsANamedError(string json)
        => Assert.Throws<InvalidDataException>(() => BizTalkImportSession.Import(json));

    [Fact]
    public void RequestJsonDeserializesIntoTheServiceModelsWithEvidenceAndPreviousSummary()
    {
        var session = new BizTalkImportSession { SourcePath = "C:\\src", LastSummary = new() { Requests = [new("bindings", "Bindings")], OpenBlockersByFlow = new Dictionary<string, int> { ["f"] = 2 }, DerivedChoiceStatuses = new Dictionary<string, string> { ["m"] = "Derived" } } };
        session.AddText(BizTalkEvidenceKinds.RulePolicy, "rules.xml", "<brl/>", _time);
        session.AddUpload(BizTalkEvidenceKinds.Bindings, "b.zip", [7], _time);
        var wire = JsonSerializer.Serialize(session.BuildAnalyzeRequest(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var service = JsonSerializer.Deserialize<AnalyzeBizTalkSolutionRequest>(wire, Web)!;
        Assert.Equal([BizTalkEvidenceKind.RulePolicy, BizTalkEvidenceKind.Bindings], service.Evidence.Select(e => e.Kind));
        Assert.Equal([BizTalkEvidenceOrigin.Text, BizTalkEvidenceOrigin.Upload], service.Evidence.Select(e => e.Origin));
        Assert.Equal("<brl/>", service.Evidence[0].Text);
        Assert.Equal("Bw==", service.Evidence[1].ContentBase64);
        Assert.Equal(2, service.PreviousSummary!.OpenBlockersByFlow["f"]);
        Assert.Equal("Derived", service.PreviousSummary.DerivedChoiceStatuses["m"]);

        var create = JsonSerializer.Deserialize<CreateImportedBizTalkFlowRequest>(JsonSerializer.Serialize(session.BuildImportRequest("f", "f2"), new JsonSerializerOptions(JsonSerializerDefaults.Web)), Web)!;
        Assert.Equal("f2", create.NewFlowId);
        Assert.Equal(2, create.Evidence.Count);
    }

    [Fact]
    public void ServiceUpdatePlanAndResultDeserializeIntoTheViewModels()
    {
        var plan = new BizTalkUpdatePlan("flow", BizTalkUpdateMode.InPlace, "reason", _time.GetUtcNow(),
            [new("node:a", "Node", "Step a", BizTalkUpdatePieceState.Conflict, true, false)], new Dictionary<string, int> { ["Conflict"] = 1 }, ["note"]);
        var viewModel = JsonSerializer.Deserialize<BizTalkUpdatePlanViewModel>(JsonSerializer.Serialize(plan, Web), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("InPlace", viewModel.Mode);
        Assert.Equal("Conflict", viewModel.Pieces[0].State);
        Assert.False(viewModel.Pieces[0].SelectedByDefault);
        Assert.Equal(1, viewModel.Counts["Conflict"]);
        Assert.Equal(_time.GetUtcNow(), viewModel.ImportedUtc);

        var summary = new BizTalkAnalysisSummary { Requests = [new("a", "A")], OpenBlockersByFlow = new Dictionary<string, int> { ["f"] = 1 } };
        var analysis = JsonSerializer.Deserialize<BizTalkSolutionAnalysisViewModel>(JsonSerializer.Serialize(new BizTalkSolutionAnalysisResponse("s", "Project", "d", "r", 0, 0, 0, 0, 0, 0, [], [], [], [], [])
        {
            EvidenceRequests = [new("a", "rule", "Title", "Why", ["step"], [BizTalkEvidenceKind.Bindings, BizTalkEvidenceKind.RulePolicy], "Certain", ["x"], "risk")],
            Summary = summary,
            Delta = new([new("c", "C")], [], [new("a", "A")], [new("f", 3, 1)], [new("m", null, "Derived")])
        }, Web), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(["Bindings", "RulePolicy"], analysis.EvidenceRequests[0].Accepts);
        Assert.Equal("risk", analysis.EvidenceRequests[0].Risk);
        Assert.Equal("a", analysis.Summary.Requests[0].Id);
        Assert.Equal("c", analysis.Delta!.ClearedRequests[0].Id);
        Assert.Equal(1, analysis.Delta.FlowChanges[0].After);
        Assert.Equal("Derived", analysis.Delta.DerivedChoiceChanges[0].After);
    }

    [Fact]
    public async Task TypedClientAnalyzesThroughASessionAndReportsTheDeltaAfterEvidenceIsAdded()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IBizTalkImportService>(new BizTalkImportService());
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(_ => throw new InvalidOperationException("Analysis must not touch flows."));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapBizTalkMigrationEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var session = new BizTalkImportSession { SourcePath = WriteProject(), GenerateNativeFlow = false };

            var first = await client.AnalyzeBizTalkSolutionAsync(session.BuildAnalyzeRequest(), default);
            session.ApplyAnalysis(first, _time);
            var policyRequest = Assert.Single(first.EvidenceRequests, r => r.Id == "rule-policies");
            Assert.Equal("Open", session.StatusOf(policyRequest));
            Assert.NotEmpty(policyRequest.HowToGet);
            Assert.Null(first.Delta);
            Assert.NotNull(session.LastSummary);

            session.AddText(BizTalkEvidenceKinds.RulePolicy, "rules.xml", BizTalkRulePolicyTests.Export(BizTalkRulePolicyTests.Policy(1, 2)), _time);
            Assert.Equal("Supplied", session.StatusOf(policyRequest));
            var second = await client.AnalyzeBizTalkSolutionAsync(session.BuildAnalyzeRequest(), default);
            session.ApplyAnalysis(second, _time);
            Assert.DoesNotContain(second.EvidenceRequests, r => r.Id == "rule-policies");
            Assert.Equal(["rule-policies"], second.Delta!.ClearedRequests.Select(r => r.Id));
            Assert.Single(second.RulePolicies);
        }
        finally { await app.StopAsync(); }
    }

    private static BizTalkSolutionAnalysisViewModel Analysis(IReadOnlyList<BizTalkEvidenceRequestViewModel> requests)
        => new("s", "Project", "d", "r", 0, 0, 0, 0, 0, 0, [], [], [], [], []) { EvidenceRequests = requests };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
