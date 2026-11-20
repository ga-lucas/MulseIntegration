using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules;
using Mulse.Modules.Pipeline;
using Service.Assist;
using Service.ConfigValues;
using Service.Endpoints;

namespace Service.Tests;

public sealed class ConfigReviewTests
{
    private const string SecretValue = "hunter2-literal-secret";

    private static Dictionary<string, string> Stored(params (string Name, string Value)[] entries)
        => entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

    private static ConfigReferenceUsage Use(string reference, string flow, bool resolved, string path = "fetches[0].fetch.settings.path")
        => new(reference, flow, path, resolved);

    private static string[] Codes(Service.Models.ConfigReviewReport report, string code) => [.. report.Items.Where(item => item.Code == code).Select(item => item.Reference)];

    [Fact]
    public void ACleanSetupHasNothingToReport()
    {
        var report = AssistConfigReview.Analyze(Stored(("sftp-host", "a"), ("sftp-user", "b")), [Use("sftp-host", "f1", true), Use("sftp-user", "f1", true)]);

        Assert.Empty(report.Items);
        Assert.Equal((2, 2, 0, 0), (report.StoredValues, report.ReferencedNames, report.Missing, report.Unused));
    }

    [Fact]
    public void AMissingNameIsABlockerWithItsFlowsListedOnce()
    {
        var report = AssistConfigReview.Analyze(Stored(), [Use("api-key", "b", false), Use("api-key", "a", false, "x"), Use("api-key", "a", false, "y")]);

        var item = Assert.Single(report.Items);
        Assert.Equal("missing", item.Code);
        Assert.Equal("Blocker", item.Severity);
        Assert.Equal(["a", "b"], item.FlowIds);
        Assert.Contains("2 flow(s)", item.Title);
        Assert.Equal(1, report.Missing);
    }

    [Fact]
    public void AMissingNameSuggestsSimilarStoredNamesAndPrefersUnusedOnes()
    {
        var stored = Stored(("sftp_host", "h"), ("sftp-hots", "h2"), ("unrelated-thing", "z"), ("sftp-host-used", "q"));
        var usages = new[] { Use("sftp-host", "f1", false), Use("sftp-host-used", "f2", true) };

        var report = AssistConfigReview.Analyze(stored, usages);
        var missing = report.Items.Single(item => item.Code == "missing");

        Assert.Contains("sftp_host", missing.Related);
        Assert.Contains("sftp-hots", missing.Related);
        Assert.DoesNotContain("unrelated-thing", missing.Related);
        Assert.Equal("sftp_host", missing.Related[0]);
        Assert.Contains("looks similar", missing.Fix);
    }

    [Fact]
    public void UnusedAndEmptyValuesAreFlaggedAndEmptyUsedOnesAreWarnings()
    {
        var report = AssistConfigReview.Analyze(Stored(("old-key", "v"), ("blank-used", " "), ("blank-unused", "")), [Use("blank-used", "f1", true)]);

        Assert.Contains("old-key", Codes(report, "unused"));
        Assert.Contains("blank-unused", Codes(report, "unused"));
        Assert.DoesNotContain("blank-used", Codes(report, "unused"));
        Assert.Equal("Warning", report.Items.Single(item => item.Code == "empty-value" && item.Reference == "blank-used").Severity);
        Assert.Equal("Info", report.Items.Single(item => item.Code == "empty-value" && item.Reference == "blank-unused").Severity);
        Assert.Equal(["f1"], report.Items.Single(item => item.Code == "empty-value" && item.Reference == "blank-used").FlowIds);
    }

    [Fact]
    public void NamesThatDifferOnlyByCaseOrSeparatorsAreReportedAsOneSetting()
    {
        var report = AssistConfigReview.Analyze(Stored(("Partner.Host", "x"), ("partner-host", "y"), ("partner_host", "z")), []);

        var item = Assert.Single(report.Items, item => item.Code == "similar-names");
        Assert.Equal(2, item.Related.Count);
        Assert.Contains("3 stored names", item.Title);
    }

    [Fact]
    public void RepeatedValuesAreReportedByNameWithoutTheValueAndNotForNearDuplicateNames()
    {
        var report = AssistConfigReview.Analyze(Stored(("db-host", SecretValue), ("report-db-host", SecretValue), ("sftp-host", "other"), ("Sftp_Host", "other")), []);

        var item = Assert.Single(report.Items, item => item.Code == "same-value");
        Assert.Equal("db-host", item.Reference);
        Assert.Equal(["report-db-host"], item.Related);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(report));
        Assert.Single(report.Items, other => other.Code == "similar-names");
    }

    [Fact]
    public void BlockersComeFirstAndNoValueEverAppears()
    {
        var report = AssistConfigReview.Analyze(Stored(("unused-one", SecretValue), ("blank", "")), [Use("needed", "f1", false)]);

        Assert.Equal(["Blocker", "Info", "Info", "Info"], report.Items.Select(item => item.Severity));
        Assert.Equal("missing", report.Items[0].Code);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(report));
    }

    [Fact]
    public void ControlCharactersAndLongNamesAreCleaned()
    {
        var name = "bad\u0007name" + new string('x', 300);

        var report = AssistConfigReview.Analyze(Stored(), [Use(name, "f1", false)]);

        var shown = report.Items.Single().Reference;
        Assert.DoesNotContain('\u0007', shown);
        Assert.True(shown.Length <= 203);
    }

    [Fact]
    public async Task TheEndpointUsesTheSavedFlowsAndNeverReturnsValues()
    {
        var flow = new PipelineDefinition
        {
            Id = "orders",
            Fetches = [new() { Id = "f1", Fetch = new() { Module = "file-fetch", Settings = new(StringComparer.OrdinalIgnoreCase) { ["path"] = "{{config:inbox-path}}" } } }]
        };
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = new(StringComparer.OrdinalIgnoreCase) { ["old-key"] = SecretValue } });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog());
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(new FakeFlowDefinitionService(flow));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.AddSingleton<IConfigValueResolver>(new ConfigValueResolver(store));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/config-review");
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(SecretValue, text);
            using var document = JsonDocument.Parse(text);
            var codes = document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
            Assert.Equal(["missing", "unused"], codes);
        }
        finally { await app.StopAsync(); }
    }
}
