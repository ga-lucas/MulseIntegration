using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Service.Assist;
using Service.Endpoints;
using Service.Execution;
using Service.Models;

namespace Service.Tests;

public sealed class FailurePatternTests
{
    private const string Secret = "sftp.partner.example for user bob";

    private static AssistFailureRun Run(string id, string flow, string state, string stage, string? detail, long ticks, string eventState = "Failed", AssistRunDeliveryInput[]? deliveries = null)
        => new(new(id, flow, new AssistRunInput(state,
            [new(stage, "Started", null), new(stage, eventState, detail)], deliveries)), ticks);

    [Theory]
    [InlineData("The operation timed out after 30s", "Timeout")]
    [InlineData("No such host is known", "Cannot reach host")]
    [InlineData("Connection refused by 10.0.0.1", "Cannot reach host")]
    [InlineData("401 Unauthorized", "Authentication or permission")]
    [InlineData("Access to the path 'C:\\x' is denied", "Authentication or permission")]
    [InlineData("Could not find file 'C:\\in\\orders.xml'", "Missing file or path")]
    [InlineData("The process cannot access the file because it is being used by another process", "File locked or in use")]
    [InlineData("Config value 'db-host' is unresolved", "Unresolved configuration")]
    [InlineData("Unexpected token '<' while parsing JSON", "Invalid input data")]
    [InlineData("Deadlock victim, transaction rolled back", "Database error")]
    [InlineData("There is not enough space on the disk", "Disk or quota")]
    [InlineData("Something odd happened", "Other (not recognised)")]
    [InlineData("", "No error detail recorded")]
    public void ErrorTextIsSortedIntoACategory(string detail, string category)
    {
        var report = AssistFailurePatterns.Analyze([Run("1", "orders", "Failed", "fetch:input", detail, 1)]);
        Assert.Equal(category, Assert.Single(report.Patterns).Category);
    }

    [Fact]
    public void RunsWithTheSameCauseAndStageAreGroupedAcrossFlowsAndTheBiggestGroupLeads()
    {
        var report = AssistFailurePatterns.Analyze(
        [
            Run("1", "orders", "Failed", "deliver:0", "401 Unauthorized", 10),
            Run("2", "Billing", "Failed", "deliver:0", "403 Forbidden", 30),
            Run("3", "billing", "Held", "deliver:0", "authentication failed", 20),
            Run("4", "invoices", "Failed", "parse:p", "invalid xml", 40)
        ]);

        Assert.Equal(4, report.RunsConsidered);
        Assert.Equal(2, report.Patterns.Count);
        var top = report.Patterns[0];
        Assert.Equal(("Authentication or permission", "deliver:0", 3), (top.Category, top.Stage, top.Runs));
        Assert.Equal(0.75, top.Share);
        Assert.Equal(["billing", "orders"], top.FlowIds.Select(flow => flow.ToLowerInvariant()));
        Assert.Equal(2, top.FlowCount);
        Assert.Equal("2", top.MostRecentExecutionId);
        Assert.Equal(2, top.States["Failed"]);
        Assert.Contains("3 of 4", report.Headline);
        Assert.Contains("75%", report.Headline);
    }

    [Fact]
    public void ATieIsReportedAsNoSingleCause()
    {
        var report = AssistFailurePatterns.Analyze([Run("1", "a", "Failed", "s1", "timed out", 1), Run("2", "b", "Failed", "s2", "invalid json", 2)]);
        Assert.Contains("No single cause stands out", report.Headline);
    }

    [Fact]
    public void HeldRunsWithUncertainDeliveriesAreAmbiguousAndNothingIsReportedWhenThereAreNoProblems()
    {
        var held = new AssistFailureRun(new("h", "orders", new AssistRunInput("Held", [new("execution", "Held", null)], [new("delivery:0", "Unknown"), new("delivery:1", "Succeeded")])), 5);
        var pattern = Assert.Single(AssistFailurePatterns.Analyze([held]).Patterns);
        Assert.Equal(("Ambiguous delivery", "delivery:0"), (pattern.Category, pattern.Stage));

        var onlyDelivery = new AssistFailureRun(new("h2", "orders", new AssistRunInput("Held", [new("execution", "Started", null)], [new("delivery:0", "InFlight")])), 6);
        Assert.Equal(("Ambiguous delivery", "delivery:0"), (AssistFailurePatterns.Analyze([onlyDelivery]).Patterns[0].Category, AssistFailurePatterns.Analyze([onlyDelivery]).Patterns[0].Stage));

        var empty = AssistFailurePatterns.Analyze([]);
        Assert.Empty(empty.Patterns);
        Assert.Null(empty.Headline);
        Assert.Equal(0, empty.RunsConsidered);
    }

    [Fact]
    public void ErrorTextNeverAppearsInTheReportAndRetryIsNeverOffered()
    {
        var report = AssistFailurePatterns.Analyze([Run("1", "orders", "Failed", "fetch:input", "connection refused to " + Secret, 1)], totalProblemRuns: 250);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("sftp.partner.example", json);
        Assert.DoesNotContain("bob", json);
        Assert.Contains("Only the 1 most recent of 250", string.Join(" ", report.Notes));
        Assert.DoesNotContain("retry the run", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never retries or resends", string.Join(" ", report.Notes));
    }

    [Fact]
    public async Task StoredProblemRunsAreGroupedOverHttpWithoutMessageContent()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        var pair = fixture.Journal();
        var flow = new PipelineDefinition { Id = "orders", Name = "Orders", Enabled = false };
        var batch = new IntegrationBatch([new("message", BinaryData.FromString("payload-sentinel"), "text/plain", new Dictionary<string, string>())]);
        foreach (var (id, detail) in new[] { ("run-1", "401 Unauthorized " + Secret), ("run-2", "403 Forbidden"), ("run-3", "timed out") })
        {
            var context = new FlowExecutionContext("orders", id, DateTimeOffset.UtcNow);
            await pair.Journal.BeginAsync(flow, context, default);
            await pair.Journal.StageAsync(context, "deliver:0", "Started", batch, null, default);
            await pair.Journal.StageAsync(context, "deliver:0", "Failed", null, detail, default);
            await pair.Journal.FinishAsync(context, "Failed", detail, default);
        }
        var okContext = new FlowExecutionContext("orders", "run-ok", DateTimeOffset.UtcNow);
        await pair.Journal.BeginAsync(flow, okContext, default);
        await pair.Journal.FinishAsync(okContext, "Completed", null, default);

        var (runs, total) = await pair.Journal.FailuresForAssistAsync(10, default);
        Assert.Equal(3, total);
        Assert.Equal(3, runs.Count);
        Assert.DoesNotContain(runs, run => run.Source.ExecutionId == "run-ok");
        Assert.DoesNotContain("payload-sentinel", JsonSerializer.Serialize(runs));
        var (limited, limitedTotal) = await pair.Journal.FailuresForAssistAsync(2, default);
        Assert.Equal((2, 3), (limited.Count, limitedTotal));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddAssist();
        builder.Services.AddSingleton(pair.Journal);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/explain/failures?take=50");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("sftp.partner.example", text);
            Assert.DoesNotContain("payload-sentinel", text);
            using var document = JsonDocument.Parse(text);
            var top = document.RootElement.GetProperty("patterns")[0];
            Assert.Equal("Authentication or permission", top.GetProperty("category").GetString());
            Assert.Equal("deliver:0", top.GetProperty("stage").GetString());
            Assert.Equal(2, top.GetProperty("runs").GetInt32());
            Assert.Equal(3, document.RootElement.GetProperty("runsConsidered").GetInt32());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task WithoutADatabaseTheEndpointSaysSo()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/explain/failures");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
