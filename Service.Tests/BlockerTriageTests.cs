using System.Net.Http.Json;
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

public sealed class BlockerTriageTests
{
    private const string SecretText = "C:\\secret-share\\partner-orders.btproj with password hunter2";

    private static MigrationDiagnostic Diagnostic(string code, MigrationDiagnosticSeverity severity = MigrationDiagnosticSeverity.Blocking, string message = SecretText)
        => new(code, severity, SecretText, message, SecretText);

    private static PipelineDefinition Flow(string id, params MigrationDiagnostic[] diagnostics)
        => new()
        {
            Id = id,
            Migration = new("BizTalk", "p", null, [], diagnostics, [])
        };

    [Fact]
    public void FlowsThatWereNotImportedAreIgnored()
    {
        var report = AssistBlockerTriage.Analyze([new PipelineDefinition { Id = "native" }]);

        Assert.Equal(0, report.ImportedFlows);
        Assert.Empty(report.Groups);
        Assert.Null(report.Headline);
        Assert.Contains("no imported flows", string.Join(" ", report.Notes));
    }

    [Fact]
    public void GroupsAreRankedByFlowsAffectedAndCarryATag()
    {
        var report = AssistBlockerTriage.Analyze(
        [
            Flow("a", Diagnostic("BT-BINDING-MISSING"), Diagnostic("BT-ODX-LOOPS")),
            Flow("b", Diagnostic("BT-BINDING-MISSING")),
            Flow("c", Diagnostic("BT-BINDING-MISSING"), Diagnostic("BT-CUSTOM-CODE"))
        ]);

        Assert.Equal(["BT-BINDING-MISSING", "BT-CUSTOM-CODE", "BT-ODX-LOOPS"], report.Groups.Select(group => group.Code));
        var first = report.Groups[0];
        Assert.Equal(AssistBlockerTriage.NeedsSourceInfo, first.Tag);
        Assert.Equal(3, first.Flows);
        Assert.Equal(["a", "b", "c"], first.FlowIds);
        Assert.Equal(AssistBlockerTriage.NeedsStub, report.Groups.Single(group => group.Code == "BT-CUSTOM-CODE").Tag);
        Assert.Equal(AssistBlockerTriage.NeedsDecision, report.Groups.Single(group => group.Code == "BT-ODX-LOOPS").Tag);
    }

    [Fact]
    public void ClearsAloneCountsOnlyFlowsWhoseSingleBlockerItIs()
    {
        var report = AssistBlockerTriage.Analyze(
        [
            Flow("a", Diagnostic("BT-BINDING-MISSING"), Diagnostic("BT-ODX-LOOPS")),
            Flow("b", Diagnostic("BT-BINDING-MISSING")),
            Flow("c", Diagnostic("BT-BINDING-MISSING"))
        ]);

        Assert.Equal(2, report.Groups.Single(group => group.Code == "BT-BINDING-MISSING").ClearsFlowsAlone);
        Assert.Equal(0, report.Groups.Single(group => group.Code == "BT-ODX-LOOPS").ClearsFlowsAlone);
        Assert.Equal(3, report.FlowsWithBlockers);
        Assert.Contains("2 flow(s) have no other blocker", report.Headline);
    }

    [Fact]
    public void WarningsAreListedAfterBlockersAndDoNotCountAsBlocking()
    {
        var report = AssistBlockerTriage.Analyze(
        [
            Flow("a", Diagnostic("BT-SCHEMA-REVIEW", MigrationDiagnosticSeverity.Review), Diagnostic("BT-SCHEMA-REVIEW", MigrationDiagnosticSeverity.Review, "other"), Diagnostic("BT-INFO", MigrationDiagnosticSeverity.Information)),
            Flow("b", Diagnostic("BT-ODX-LOOPS"))
        ]);

        Assert.Equal(["BT-ODX-LOOPS", "BT-SCHEMA-REVIEW"], report.Groups.Select(group => group.Code));
        var warning = report.Groups[1];
        Assert.Equal("Warning", warning.Severity);
        Assert.Equal(2, warning.Occurrences);
        Assert.Equal(1, warning.Flows);
        Assert.Equal(1, report.FlowsWithBlockers);
        Assert.DoesNotContain(report.Groups, group => group.Code == "BT-INFO");
    }

    [Fact]
    public void ArtifactFindingsAreIncludedAndRepeatedOnesAreCountedOnce()
    {
        var repeated = Diagnostic("BT-MAP-UNTRANSLATED");
        var flow = new PipelineDefinition
        {
            Id = "a",
            Migration = new("BizTalk", "p", null, [new("Map", "m.btm", MigrationTranslationStatus.Unsupported, [repeated])], [repeated], [])
        };

        var group = Assert.Single(AssistBlockerTriage.Analyze([flow]).Groups);

        Assert.Equal(1, group.Occurrences);
    }

    [Fact]
    public void UnknownOrUnsafeCodesAreGroupedAsOtherAndNeverEchoed()
    {
        var report = AssistBlockerTriage.Analyze([Flow("a", Diagnostic("BT-FUTURE-THING"), Diagnostic("free text with spaces and a password hunter2"))]);

        Assert.All(report.Groups, group => Assert.Equal(AssistBlockerTriage.Other, group.Tag));
        Assert.Contains(report.Groups, group => group.Code == "BT-FUTURE-THING");
        Assert.Contains(report.Groups, group => group.Code == "UNKNOWN");
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void NoMessagePathOrRemediationTextReachesTheReport()
    {
        var report = AssistBlockerTriage.Analyze([Flow("a", Diagnostic("BT-CUSTOM-CODE"), Diagnostic("BT-BINDING-MISSING"))]);

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("secret-share", json);
    }

    [Fact]
    public void EveryRuleHasAKnownTagAndPlainText()
    {
        var codes = new[]
        {
            "BT-ARTIFACT-MISSING", "BT-ARTIFACT-UNSUPPORTED", "BT-BINDING-MISSING", "BT-BINDING-REVIEW", "BT-CONFIGURATION-REQUIRED", "BT-CUSTOM-CODE", "BT-DEPENDENCY-UNRESOLVED",
            "BT-MAP-NOT-ANALYZED", "BT-MAP-NOT-WIRED", "BT-MAP-SCRIPTS-UNTRANSLATED", "BT-MAP-UNTRANSLATED", "BT-NATIVE-TRANSLATION-FAILED", "BT-NATIVE-UNRESOLVED", "BT-ODX-ASSIGNMENTS",
            "BT-ODX-FAILURE-SCOPES", "BT-ODX-LOOPS", "BT-ODX-NOT-ANALYZED", "BT-ODX-PORTS", "BT-ODX-RULES", "BT-ODX-WAITS", "BT-ORCHESTRATION-REVIEW", "BT-ORCHESTRATION-STUB",
            "BT-PIPELINE-COMPONENT-HINT", "BT-PIPELINE-COMPONENT-METADATA", "BT-PIPELINE-DIRECTION", "BT-PIPELINE-INVENTORY-LIMIT", "BT-PIPELINE-METADATA-LIMIT", "BT-PIPELINE-NOT-ANALYZED",
            "BT-PIPELINE-STRUCTURE", "BT-PIPELINE-UNTRANSLATED", "BT-RULE-POLICY-MISSING", "BT-RULE-POLICY-REVIEW", "BT-RULE-POLICY-UNSUPPORTED", "BT-SCHEMA-REVIEW", "BT-SEMANTICS-UNVERIFIED"
        };

        var report = AssistBlockerTriage.Analyze([Flow("a", [.. codes.Select(code => Diagnostic(code))])]);

        Assert.Equal(codes.Length, report.Groups.Count);
        Assert.DoesNotContain(report.Groups, group => group.Tag == AssistBlockerTriage.Other);
        Assert.All(report.Groups, group =>
        {
            Assert.False(string.IsNullOrWhiteSpace(group.Title));
            Assert.False(string.IsNullOrWhiteSpace(group.WhatToDo));
        });
    }

    [Fact]
    public void FlowListsAreCappedButTheCountIsExact()
    {
        var flows = Enumerable.Range(0, 25).Select(index => Flow($"f{index:00}", Diagnostic("BT-ODX-LOOPS"))).ToArray();

        var group = Assert.Single(AssistBlockerTriage.Analyze(flows).Groups);

        Assert.Equal(25, group.Flows);
        Assert.Equal(AssistBlockerTriage.MaxFlowsListed, group.FlowIds.Count);
    }

    [Fact]
    public async Task TheEndpointReturnsTheTriageWithoutCachingOrText()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(new FakeFlowDefinitionService(Flow("a", Diagnostic("BT-CUSTOM-CODE")), Flow("b", Diagnostic("BT-CUSTOM-CODE"))));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/triage");
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("hunter2", text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal(2, document.RootElement.GetProperty("importedFlows").GetInt32());
            Assert.Equal("BT-CUSTOM-CODE", document.RootElement.GetProperty("groups")[0].GetProperty("code").GetString());
        }
        finally { await app.StopAsync(); }
    }
}
