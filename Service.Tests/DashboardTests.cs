using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class DashboardTests
{
    private static readonly DateTime Now = new(2025, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private static FlowViewModel Flow(string id, bool enabled = true, IReadOnlyList<string>? missing = null, FlowMigrationReportViewModel? migration = null)
        => new(id, enabled, "OnDemand", null, false, ["input"], ["parsed"], ["file-fetch"], ["json-parse"], [], [], [], [], [], [], [],
            MissingModules: missing, Migration: migration);

    private static HistoryExecutionViewModel Run(string id, string flowId, string state, DateTime started)
        => new(id, flowId, state, started.Ticks, started.Ticks);

    private static DashboardSummary Build(IReadOnlyList<FlowViewModel> flows, IReadOnlyList<HistoryExecutionViewModel>? runs,
        IReadOnlyList<ConfigReferenceUsageViewModel>? usages = null)
        => DashboardSummary.Build(flows, [], usages ?? [], runs, Now);

    [Fact]
    public void Health_UsesTheLatestRunOfEachFlowWithFailureFirst()
    {
        var summary = Build([Flow("a"), Flow("b"), Flow("c"), Flow("d")],
        [
            Run("1", "a", "Failed", Now.AddHours(-5)),
            Run("2", "a", "Completed", Now.AddHours(-1)),
            Run("3", "b", "Completed", Now.AddHours(-3)),
            Run("4", "b", "Failed", Now.AddHours(-2)),
            Run("5", "c", "Held", Now.AddHours(-2))
        ]);

        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.Healthy]);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.Failing]);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.NeedsAttention]);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.NotRunRecently]);
        Assert.Equal(summary.FlowCount, summary.FlowsByHealth.Values.Sum());
    }

    [Fact]
    public void Health_FlowIdsMatchWithoutRegardToCase()
    {
        var summary = Build([Flow("Billing")], [Run("1", "billing", "Completed", Now.AddHours(-1))]);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.Healthy]);
    }

    [Fact]
    public void Setup_ProblemsOutrankHealthyButNotFailureAndAreListedForAttention()
    {
        var usages = new[] { new ConfigReferenceUsageViewModel("db", "setup", "x", false), new ConfigReferenceUsageViewModel("ok", "setup", "y", true) };
        var summary = Build([Flow("setup"), Flow("modules", missing: ["Fetch: gone"]), Flow("failing", missing: ["Fetch: gone"])],
        [
            Run("1", "setup", "Completed", Now.AddHours(-1)),
            Run("2", "modules", "Completed", Now.AddHours(-1)),
            Run("3", "failing", "Failed", Now.AddHours(-1))
        ], usages);

        Assert.Equal(2, summary.FlowsByHealth[FlowHealth.NeedsSetup]);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.Failing]);
        Assert.Equal("failing", summary.Attention[0].FlowId);
        Assert.Contains(summary.Attention, item => item.FlowId == "setup" && item.Reason.Contains("1 unresolved") && item.Href == "/designer/setup");
        Assert.Contains(summary.Attention, item => item.FlowId == "modules" && item.Reason.Contains("Fetch: gone"));
    }

    [Fact]
    public void Setup_ImportedFlowsAreFlaggedAsBlocked()
    {
        var report = new FlowMigrationReportViewModel("BizTalk", @"C:\x.btproj", "Orders", [], [], []);
        var summary = Build([Flow("imported", enabled: false, migration: report)], []);
        Assert.Equal(1, summary.FlowsByHealth[FlowHealth.NeedsSetup]);
        Assert.Contains("blocked", summary.Attention.Single().Reason);
    }

    [Fact]
    public void Runs_CountStatesSuccessRateAndSevenDayBuckets()
    {
        var summary = Build([Flow("a")],
        [
            Run("1", "a", "Completed", Now),
            Run("2", "a", "Completed", Now.AddHours(-1)),
            Run("3", "a", "Failed", Now.AddDays(-1)),
            Run("4", "a", "Running", Now.AddDays(-6)),
            Run("5", "a", "Completed", Now.AddDays(-10))
        ]);

        Assert.Equal(5, summary.RunCount);
        Assert.Equal(3, summary.Completed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(0.75, summary.SuccessRate);
        Assert.Equal(7, summary.Last7Days.Count);
        Assert.Equal(new DateOnly(2025, 6, 4), summary.Last7Days[0].Date);
        Assert.Equal(new DateOnly(2025, 6, 10), summary.Last7Days[^1].Date);
        Assert.Equal(2, summary.Last7Days[^1].Completed);
        Assert.Equal(1, summary.Last7Days[^2].Failed);
        Assert.Equal(1, summary.Last7Days[0].Other);
        Assert.Equal(4, summary.Last7Days.Sum(day => day.Total));
        Assert.Equal("1", summary.RecentRuns[0].ExecutionId);
    }

    [Fact]
    public void Runs_NoFinishedRunsMeansNoSuccessRateAndMissingHistoryIsReported()
    {
        Assert.Null(Build([Flow("a")], []).SuccessRate);
        Assert.Null(Build([Flow("a")], [Run("1", "a", "Running", Now)]).SuccessRate);

        var unavailable = Build([Flow("a")], null);
        Assert.False(unavailable.HasRunData);
        Assert.Equal(1, unavailable.FlowsByHealth[FlowHealth.NotRunRecently]);
    }

    [Fact]
    public void Attention_IsCappedButTheTotalIsKept()
    {
        var flows = Enumerable.Range(1, 12).Select(index => Flow($"flow-{index:00}", missing: ["Fetch: gone"])).ToArray();
        var summary = Build(flows, []);
        Assert.Equal(DashboardSummary.MaxAttention, summary.Attention.Count);
        Assert.Equal(12, summary.AttentionTotal);
    }

    [Fact]
    public async Task Page_ShowsStatsChartsAndLinksWhenHistoryIsAvailable()
    {
        var html = await RenderAsync(historyAvailable: true);

        Assert.Matches("<h1[^>]*>Dashboard</h1>", html);
        Assert.Contains("Flow health", html);
        Assert.Contains("conic-gradient", html);
        Assert.Contains("Run outcomes", html);
        Assert.Contains("Last 7 days", html);
        Assert.Contains("Recent runs", html);
        Assert.Contains("href=\"/flows\"", html);
        Assert.Contains("50%", html);
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("href=\"/history/flow-a\"", html);
        Assert.Contains("The last run failed", html);
        Assert.DoesNotContain("Run history is not available", html);
    }

    [Fact]
    public async Task Page_ShowsFailurePatternsWithoutRepeatingAnything()
    {
        var html = await RenderAsync(historyAvailable: true);

        Assert.Contains("Failure patterns", html);
        Assert.Contains("Authentication or permission", html);
        Assert.Matches("<code[^>]*>deliver:0</code>", html);
        Assert.Contains("2 run(s) (67%) in 2 flow(s): flow-a, flow-b", html);
        Assert.Contains("One change would clear the most", html);
        Assert.Contains("href=\"/history/flow-a\"", html);
        Assert.Contains("never retries or resends", html);
    }

    [Fact]
    public async Task Page_KeepsWorkingWhenFailurePatternsCannotBeLoaded()
    {
        var html = await RenderAsync(historyAvailable: true, patternsFail: true);

        Assert.Contains("Failure patterns are not available", html);
        Assert.Contains("Pattern service down", html);
        Assert.Contains("Run outcomes", html);
    }

    [Fact]
    public async Task Page_DoesNotAskForFailurePatternsWhenNothingFailed()
    {
        var html = await RenderAsync(historyAvailable: true, onlyCompleted: true);

        Assert.Contains("No recent failed, held or retrying runs.", html);
    }

    [Fact]
    public async Task Page_DegradesToFlowStatsWhenHistoryIsUnavailable()
    {
        var html = await RenderAsync(historyAvailable: false);

        Assert.Contains("Run history is not available", html);
        Assert.Contains("Flow health", html);
        Assert.DoesNotContain("Run outcomes", html);
        Assert.DoesNotContain("Recent runs", html);
        Assert.DoesNotContain("Failure patterns", html);
        Assert.Contains("n/a", html);
    }

    [Fact]
    public async Task Page_ExplainsAnEmptyWorkspace()
    {
        var html = await RenderAsync(historyAvailable: true, empty: true);
        Assert.Contains("No flows yet", html);
        Assert.Contains("Nothing needs attention", html);
    }

    private static async Task<string> RenderAsync(bool historyAvailable, bool empty = false, bool patternsFail = false, bool onlyCompleted = false)
    {
        using var httpClient = new HttpClient(new Handler(historyAvailable, empty, patternsFail, onlyCompleted)) { BaseAddress = new Uri("http://localhost") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<NavigationManager>(new TestNavigationManager())
            .AddSingleton<IMulseApiClient>(new MulseApiClient(httpClient)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Home>(ParameterView.Empty)).ToHtmlString());
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    }

    private sealed class Handler(bool historyAvailable, bool empty, bool patternsFail, bool onlyCompleted) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/assist/explain/failures")
            {
                Assert.False(onlyCompleted, "Failure patterns must not be requested when no run failed.");
                if (patternsFail)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "Pattern service down" })
                    });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new FailurePatternReportViewModel(3, 3,
                    [
                        new("Authentication or permission", "deliver:0", 2, 2d / 3, ["flow-a", "flow-b"], 2, new() { ["Failed"] = 2 }, "2", 2, "Check the credential.", "Renew the credential."),
                        new("Timeout", "fetch:input", 1, 1d / 3, ["flow-a"], 1, new() { ["Failed"] = 1 }, "3", 1, "Check the destination.", "Raise the timeout.")
                    ],
                    "One change would clear the most: 'Authentication or permission' at 'deliver:0'.",
                    ["This never retries or resends anything."]))
                });
            }
            if (path == "/api/history/executions")
            {
                if (!historyAvailable)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "History needs a database" })
                    });
                Assert.Equal("?take=200", request.RequestUri.Query);
                var now = DateTime.UtcNow;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(empty ? [] : new[]
                    {
                        Run("1", "flow-a", "Completed", now.AddMinutes(-5)),
                        Run("2", "flow-b", onlyCompleted ? "Completed" : "Failed", now.AddMinutes(-4))
                    })
                });
            }

            object payload = path switch
            {
                "/api/flows" => empty ? [] : new[] { Flow("flow-a"), Flow("flow-b") },
                "/api/modules" => Array.Empty<ModuleViewModel>(),
                "/api/config-values/usages" => Array.Empty<ConfigReferenceUsageViewModel>(),
                _ => throw new InvalidOperationException("Unexpected request " + path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload, payload.GetType()) });
        }
    }
}
