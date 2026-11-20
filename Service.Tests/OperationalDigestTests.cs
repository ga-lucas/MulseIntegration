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

public sealed class OperationalDigestTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static DigestRun Run(string id, string flow, string state, TimeSpan ago, double seconds = 1)
    {
        var started = Now.UtcDateTime - ago;
        return new(id, flow, state, started.Ticks, started.AddSeconds(seconds).Ticks);
    }

    private static DigestFlow Interval(string id, TimeSpan interval, bool enabled = true, string mode = "Interval")
        => new(id, id.ToUpperInvariant(), enabled, mode, interval);

    private static OperationalDigest Build(DigestRun[] runs, DigestFlow[]? flows = null, int hours = 24)
        => AssistOperationalDigest.Build(runs, flows ?? [], Now, hours, 200);

    [Fact]
    public void RunsInThePeriodAreCountedByStateWithASuccessRate()
    {
        var digest = Build(
        [
            Run("1", "a", "Completed", TimeSpan.FromHours(1)),
            Run("2", "a", "Completed", TimeSpan.FromHours(2)),
            Run("3", "b", "Failed", TimeSpan.FromHours(3)),
            Run("4", "b", "Completed", TimeSpan.FromHours(30))
        ]);

        Assert.Equal(3, digest.Runs);
        Assert.Equal(2, digest.FlowsThatRan);
        Assert.Equal(2, digest.RunsByState["Completed"]);
        Assert.Equal(1, digest.RunsByState["Failed"]);
        Assert.Equal(2d / 3, digest.SuccessRate);
        Assert.Contains("3 run(s) started in the last 24 hour(s) across 2 flow(s)", digest.Facts[0]);
        Assert.Contains("2 of 3 finished run(s) completed (67%)", string.Join(" ", digest.Facts));
    }

    [Fact]
    public void AQuietPeriodSaysSoAndHasNoSuccessRate()
    {
        var digest = Build([]);
        Assert.Equal(0, digest.Runs);
        Assert.Null(digest.SuccessRate);
        Assert.Contains("No runs started", digest.Facts[0]);
        Assert.Contains("Nothing needs attention.", digest.Facts);
    }

    [Fact]
    public void HeldRunsAreListedEvenWhenTheyStartedBeforeThePeriod()
    {
        var digest = Build([Run("old", "a", "Held", TimeSpan.FromDays(3)), Run("new", "b", "Held", TimeSpan.FromHours(1))]);
        Assert.Equal(2, digest.HeldTotal);
        Assert.Equal(["new", "old"], digest.HeldRuns.Select(run => run.ExecutionId));
        Assert.Equal(1, digest.Runs);
        Assert.Contains("2 run(s) are held", string.Join(" ", digest.Facts));
    }

    [Fact]
    public void ASlowFlowIsOneWhoseLatestRunTookMuchLongerThanUsualAndHasEnoughHistory()
    {
        var digest = Build(
        [
            Run("s1", "slow", "Completed", TimeSpan.FromHours(5), 10), Run("s2", "slow", "Completed", TimeSpan.FromHours(4), 11),
            Run("s3", "slow", "Completed", TimeSpan.FromHours(3), 9), Run("s4", "slow", "Completed", TimeSpan.FromHours(1), 40),
            Run("q1", "quick", "Completed", TimeSpan.FromHours(5), 1), Run("q2", "quick", "Completed", TimeSpan.FromHours(4), 1),
            Run("q3", "quick", "Completed", TimeSpan.FromHours(3), 1), Run("q4", "quick", "Completed", TimeSpan.FromHours(1), 4),
            Run("f1", "few", "Completed", TimeSpan.FromHours(3), 5), Run("f2", "few", "Completed", TimeSpan.FromHours(2), 5),
            Run("f3", "few", "Completed", TimeSpan.FromHours(1), 60)
        ], [new("slow", "Slow flow", true, "OnDemand", null)]);

        var item = Assert.Single(digest.SlowFlows);
        Assert.Equal(("slow", "Slow flow", 4), (item.FlowId, item.DisplayName, item.CompletedRuns));
        Assert.Equal(10, item.TypicalSeconds);
        Assert.Equal(40, item.LatestSeconds);
    }

    [Fact]
    public void IntervalFlowsThatHaveNotRunForMoreThanThreeIntervalsAreStopped()
    {
        var five = TimeSpan.FromMinutes(5);
        var digest = Build(
        [
            Run("1", "stale", "Completed", TimeSpan.FromMinutes(40)),
            Run("2", "fresh", "Completed", TimeSpan.FromMinutes(7)),
            Run("3", "off", "Completed", TimeSpan.FromHours(5))
        ],
        [
            Interval("stale", five), Interval("fresh", five), Interval("never", five),
            Interval("off", five, enabled: false), Interval("manual", five, mode: "OnDemand"), Interval("daily", TimeSpan.FromDays(2))
        ]);

        Assert.Equal(["never", "stale"], digest.StoppedFlows.Select(flow => flow.FlowId).Order());
        Assert.Contains("40 minute(s) ago", digest.StoppedFlows.Single(flow => flow.FlowId == "stale").Reason);
        Assert.Null(digest.StoppedFlows.Single(flow => flow.FlowId == "never").LastRunUtcTicks);
        Assert.Contains("2 interval flow(s) look like they stopped running", string.Join(" ", digest.Facts));
    }

    [Fact]
    public void NotesAdmitTheLimitsAndNeverOfferARetry()
    {
        var many = Enumerable.Range(0, 200).Select(index => Run(index.ToString(), "a", "Completed", TimeSpan.FromMinutes(index))).ToArray();
        var digest = AssistOperationalDigest.Build(many, [], Now, 24, 200);
        var notes = string.Join(" ", digest.Notes);
        Assert.Contains("Only the 200 most recent runs were read", notes);
        Assert.Contains("never retries or resends", notes);
        Assert.Contains("UTC", notes);
    }

    [Fact]
    public async Task TheDigestIsServedFromStoredRunsWithoutErrorTextOrContent()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        var pair = fixture.Journal();
        var flow = new PipelineDefinition { Id = "orders", Name = "Orders", Enabled = false };
        foreach (var (id, state, detail) in new[] { ("r1", "Completed", (string?)null), ("r2", "Failed", "connect to sftp.partner.example failed for user bob") })
        {
            var context = new FlowExecutionContext("orders", id, DateTimeOffset.UtcNow);
            await pair.Journal.BeginAsync(flow, context, default);
            await pair.Journal.FinishAsync(context, state, detail, default);
        }

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
            using var response = await client.GetAsync("/api/assist/digest?hours=48");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("sftp.partner.example", text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal(48, document.RootElement.GetProperty("hours").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("runs").GetInt32());
            Assert.Equal(0.5, document.RootElement.GetProperty("successRate").GetDouble());
            using var clamped = await client.GetAsync("/api/assist/digest?hours=100000");
            using var clampedDocument = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync());
            Assert.Equal(168, clampedDocument.RootElement.GetProperty("hours").GetInt32());
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
            using var response = await client.GetAsync("/api/assist/digest");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
