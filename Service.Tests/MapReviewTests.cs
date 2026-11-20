using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules;
using Mulse.Modules.Pipeline;
using Service.Assist;
using Service.Endpoints;

namespace Service.Tests;

public sealed class MapReviewTests
{
    private const string SecretPath = "C:\\secret-share\\partner\\Orders.btm";
    private const string ScriptHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static MigrationMapCandidate Map(string name = "Orders", int direct = 4, int constants = 0, int unsupported = 0,
        IReadOnlyDictionary<int, int>? fids = null, MapScriptInventory? scripts = null, string source = "Schemas.In", string target = "Schemas.Out",
        MigrationSchemaReferenceEvidence? sourceSchema = null, MigrationSchemaReferenceEvidence? targetSchema = null)
        => new(name, "p", SecretPath.Replace("Orders", name, StringComparison.Ordinal), source, target, MigrationTranslationStatus.ReviewRequired, direct, constants, unsupported,
            fids ?? new Dictionary<int, int>(), "limitation")
        {
            ScriptInventory = scripts,
            SourceSchema = sourceSchema,
            TargetSchema = targetSchema
        };

    private static MigrationSchemaReferenceEvidence Schema(string status)
        => new("Schemas.X", status, 0, 0, [], 0, "");

    private static PipelineDefinition Flow(params MigrationMapCandidate[] maps)
        => new()
        {
            Id = "billing",
            Name = "Billing",
            Migration = new("BizTalk", "p", null,
                [.. maps.Select(map => new MigrationArtifactReport("Map", map.SourcePath, MigrationTranslationStatus.ReviewRequired, []))], [], [])
            {
                MapInventory = new(maps.Length, 0, maps)
            }
        };

    private static string[] Codes(Service.Models.MapReviewReport report, string map)
        => [.. report.Maps.Single(item => item.Name == map).Items.Select(item => item.Code)];

    [Fact]
    public void AFlowThatWasNotImportedHasNothingToCheck()
    {
        var report = AssistMapReview.Review(new PipelineDefinition { Id = "native" });

        Assert.Equal(0, report.MapsReviewed);
        Assert.Contains("not imported", string.Join(" ", report.Remarks));
    }

    [Fact]
    public void AnImportWithoutMapsSaysSo()
    {
        var report = AssistMapReview.Review(new PipelineDefinition { Id = "a", Migration = new("BizTalk", "p", null, [], [], []) });

        Assert.Empty(report.Maps);
        Assert.Contains("did not list any maps", string.Join(" ", report.Remarks));
    }

    [Fact]
    public void AMapListedButNotRecordedIsAWarning()
    {
        var flow = new PipelineDefinition
        {
            Id = "a",
            Migration = new("BizTalk", "p", null, [new("Map", SecretPath, MigrationTranslationStatus.ReviewRequired, [])], [], [])
        };

        var report = AssistMapReview.Review(flow);

        Assert.Equal(["not-recorded"], Codes(report, "Orders"));
        Assert.Equal(1, report.Warnings);
    }

    [Fact]
    public void AMapThatWasNotAnalyzedIsABlockerAndNothingElseIsGuessed()
    {
        var report = AssistMapReview.Review(Flow(Map(direct: 0, source: "(not analyzed)", target: "(not analyzed)")));

        Assert.Equal(["not-analyzed"], Codes(report, "Orders"));
        Assert.Equal(1, report.Blockers);
    }

    [Fact]
    public void FunctoidLinksAreABlockerAndNameTheTypesByUse()
    {
        var report = AssistMapReview.Review(Flow(Map(unsupported: 3, fids: new Dictionary<int, int> { [101] = 1, [260] = 2 })));

        var item = report.Maps[0].Items.Single(entry => entry.Code == "functoid-links");
        Assert.Equal("Blocker", item.Severity);
        Assert.Equal(["type 260: 2 use(s)", "type 101: 1 use(s)"], item.Where);
    }

    [Fact]
    public void FunctoidsWithoutUnsupportedLinksAreOnlyAWarning()
    {
        var report = AssistMapReview.Review(Flow(Map(fids: new Dictionary<int, int> { [101] = 2 })));

        var item = report.Maps[0].Items.Single(entry => entry.Code.StartsWith("functoid", StringComparison.Ordinal));
        Assert.Equal("functoid-types", item.Code);
        Assert.Equal("Warning", item.Severity);
    }

    [Fact]
    public void LongFunctoidListsAreCappedWithACount()
    {
        var fids = Enumerable.Range(1, 20).ToDictionary(index => index, _ => 1);

        var item = AssistMapReview.Review(Flow(Map(unsupported: 20, fids: fids))).Maps[0].Items.Single(entry => entry.Code == "functoid-links");

        Assert.Equal(AssistMapReview.MaxFunctoidTypesListed + 1, item.Where.Count);
        Assert.Equal("and 8 more type(s)", item.Where[^1]);
    }

    [Fact]
    public void ScriptsAreABlockerWithLanguagesAndApiNamesButNoBodyOrHash()
    {
        var scripts = new MapScriptInventory(2, 0,
        [
            new(10, "F1", "CSharp", 120, ScriptHash, ["Microsoft.BizTalk.Streaming.VirtualStream", "System.Convert"]),
            new(20, "F2", "CSharp", 80, ScriptHash, [])
        ]);

        var report = AssistMapReview.Review(Flow(Map(scripts: scripts)));

        var item = report.Maps[0].Items.Single(entry => entry.Code == "scripts");
        Assert.Equal("Blocker", item.Severity);
        Assert.Contains("CSharp: 2", item.Detail);
        Assert.Contains("functoid F1 (CSharp, 120 characters)", item.Where);
        Assert.Contains("mentions Microsoft.BizTalk.Streaming.VirtualStream", item.Where);
        Assert.Equal(2, report.Maps[0].Scripts);
        Assert.DoesNotContain(ScriptHash, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void ScriptsLeftOutOfTheInventoryAreCounted()
    {
        var scripts = new MapScriptInventory(30, 20, [.. Enumerable.Range(1, 10).Select(index => new MapScriptEvidence(index, $"F{index}", "CSharp", 5, ScriptHash, []))]);

        var item = AssistMapReview.Review(Flow(Map(scripts: scripts))).Maps[0].Items.Single(entry => entry.Code == "scripts");

        Assert.Equal("and 20 more script(s)", item.Where[^1]);
    }

    [Fact]
    public void UnresolvedAndAmbiguousSchemasAreWarnings()
    {
        var report = AssistMapReview.Review(Flow(Map(sourceSchema: Schema("Unresolved"), targetSchema: Schema("Ambiguous"))));

        Assert.Contains("schema-unresolved", Codes(report, "Orders"));
        Assert.Contains("schema-ambiguous", Codes(report, "Orders"));
        Assert.All(report.Maps[0].Items.Where(item => item.Code.StartsWith("schema-", StringComparison.Ordinal)), item => Assert.Equal("Warning", item.Severity));
    }

    [Fact]
    public void ASchemaThatResolvedIsNotMentioned()
    {
        var report = AssistMapReview.Review(Flow(Map(sourceSchema: Schema("Resolved"), targetSchema: Schema("Resolved"))));

        Assert.DoesNotContain(Codes(report, "Orders"), code => code.StartsWith("schema-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSameSchemaOnBothSidesIsAWarning()
    {
        var report = AssistMapReview.Review(Flow(Map(source: "Schemas.Same", target: "schemas.same")));

        Assert.Contains("same-schema", Codes(report, "Orders"));
    }

    [Fact]
    public void AMapWithNoLinksIsAWarning()
    {
        var report = AssistMapReview.Review(Flow(Map(direct: 0)));

        Assert.Equal(["no-links"], Codes(report, "Orders"));
    }

    [Fact]
    public void MostlyReworkedMapsGetANoteAndStarterLinksAreNoted()
    {
        var report = AssistMapReview.Review(Flow(Map(direct: 1, constants: 1, unsupported: 5, fids: new Dictionary<int, int> { [1] = 5 })));

        var codes = Codes(report, "Orders");
        Assert.Contains("mostly-reworked", codes);
        Assert.Contains("starter-links", codes);
        Assert.Contains("functoid-links", codes);
        Assert.Equal(1, report.Blockers);
        Assert.Equal(2, report.Notes);
    }

    [Fact]
    public void ACleanMapOnlyGetsTheStarterNoteAndTheDisclaimerSaysItIsNotProof()
    {
        var report = AssistMapReview.Review(Flow(Map()));

        Assert.Equal(["starter-links"], Codes(report, "Orders"));
        Assert.Equal(0, report.Blockers + report.Warnings);
        Assert.Contains("not shown to be correct", report.Disclaimer);
        Assert.NotEmpty(report.NotChecked);
    }

    [Fact]
    public void ItemsAreOrderedBySeverityAndMapsByBlockerCount()
    {
        var report = AssistMapReview.Review(Flow(Map("Clean"), Map("Broken", unsupported: 2, fids: new Dictionary<int, int> { [1] = 2 }, sourceSchema: Schema("Unresolved"))));

        Assert.Equal(["Broken", "Clean"], report.Maps.Select(map => map.Name));
        Assert.Equal(["Blocker", "Warning", "Info"], report.Maps[0].Items.Select(item => item.Severity));
    }

    [Fact]
    public void OmittedInventoryMapsAreMentioned()
    {
        var map = Map();
        var flow = new PipelineDefinition
        {
            Id = "a",
            Migration = new("BizTalk", "p", null, [new("Map", map.SourcePath, MigrationTranslationStatus.ReviewRequired, [])], [], []) { MapInventory = new(5, 4, [map]) }
        };

        Assert.Contains("4 map(s)", string.Join(" ", AssistMapReview.Review(flow).Remarks));
    }

    [Fact]
    public void NamesAreCleanedAndNoPathIsReturned()
    {
        var report = AssistMapReview.Review(Flow(Map("Or\u0007ders\r\n" + new string('x', 200))));

        var name = report.Maps[0].Name;
        Assert.DoesNotContain('\u0007', name);
        Assert.True(name.Length <= 123);
        Assert.DoesNotContain("secret-share", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task TheEndpointReturnsTheReviewWithoutCachingOrPaths()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(new FakeFlowDefinitionService(Flow(Map(unsupported: 2, fids: new Dictionary<int, int> { [7] = 2 }))));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/map-review/billing");
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("secret-share", text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal(1, document.RootElement.GetProperty("mapsReviewed").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("blockers").GetInt32());

            using var missing = await client.GetAsync("/api/assist/map-review/unknown");
            Assert.False(missing.IsSuccessStatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
