using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Service.Assist;
using Service.Endpoints;
using Service.Models;

namespace Service.Tests;

public sealed class FlowReviewTests
{
    private const string SecretValue = "p@ssw0rd-literal-value";
    private const string HiddenPath = "/inbox/partner-orders";

    private static readonly ModuleDescriptor[] Modules =
    [
        new("file-fetch", "File fetch", ModuleKind.Fetch, "Reads files.",
        [
            new("path", "Folder", "Where to read", true),
            new("deleteAfterRead", "Delete after read", "Remove files once read", false, ModuleSettingInputKind.Boolean, "false")
        ]),
        new("csv-parse", "CSV parse", ModuleKind.Parse, "Parses CSV."),
        new("text-render", "Text render", ModuleKind.Render, "Renders text."),
        new("sftp", "SFTP deliver", ModuleKind.Deliver, "Sends over SFTP.",
        [
            new("host", "Host", "Server", true),
            new("password", "Password", "Login password", false, ModuleSettingInputKind.Password),
            new("path", "Folder", "Remote folder", false)
        ])
    ];

    private static AssistFlowReviewer Reviewer(params ModuleDescriptor[] modules)
    {
        var catalog = new ReadOnlyCatalog(modules.Length == 0 ? Modules : modules);
        return new(catalog, new AssistExplainer(catalog));
    }

    private static readonly Dictionary<string, string> Config = new(StringComparer.OrdinalIgnoreCase) { ["sftp-password"] = "stored", ["sftp-host"] = "stored" };

    private static ModuleStepDefinition Step(string module, params (string Key, string Value)[] settings)
        => new() { Module = module, Settings = settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) };

    private static PipelineDefinition Flow(
        ModuleStepDefinition? fetch = null,
        ModuleStepDefinition? deliver = null,
        RetryPolicyDefinition? retry = null,
        PipelineTriggerMode mode = PipelineTriggerMode.Interval,
        IEnumerable<ModuleStepDefinition>? augments = null,
        FlowMigrationReport? migration = null,
        bool withDelivery = true)
        => new()
        {
            Id = "orders",
            Name = "Orders",
            Trigger = new() { Mode = mode, Interval = TimeSpan.FromMinutes(5) },
            Fetches = [new() { Id = "f1", Fetch = fetch ?? Step("file-fetch", ("path", HiddenPath), ("deleteAfterRead", "true")) }],
            Parses = [new() { Id = "p1", Parse = Step("csv-parse"), FetchNodeIds = ["f1"] }],
            Augments = [.. augments ?? []],
            Deliveries = withDelivery
                ? [new() { Render = Step("text-render"), Delivers = [new() { Deliver = deliver ?? Step("sftp", ("host", "{{config:sftp-host}}"), ("password", "{{secret:sftp-password}}"), ("path", "/outbox")) }] }]
                : [],
            Retry = retry ?? new() { MaxAttempts = 3 },
            Migration = migration
        };

    private static ReviewItem[] Codes(FlowReview review, string code) => [.. review.Items.Where(item => item.Code == code)];

    [Fact]
    public void ACarefullyBuiltFlowHasNothingToReport()
    {
        var review = Reviewer().Review(Flow(), Config);

        Assert.Empty(review.Items);
        Assert.Equal((0, 0, 0), (review.Blockers, review.Warnings, review.Notes));
        Assert.NotEmpty(review.Checked);
        Assert.NotEmpty(review.NotChecked);
        Assert.Contains("does not show that the flow is correct", review.Disclaimer);
    }

    [Fact]
    public void AnImportedFlowIsABlockerAndIsListedFirst()
    {
        var review = Reviewer().Review(Flow(migration: new("BizTalk", "p", null, [], [], []), retry: new() { MaxAttempts = 1 }), Config);

        Assert.Equal("imported-flow", review.Items[0].Code);
        Assert.Equal("Blocker", review.Items[0].Severity);
        Assert.Equal(1, review.Blockers);
        Assert.True(review.Warnings >= 1);
        Assert.Equal(review.Items.Select(item => item.Severity).OrderBy(Rank).ToArray(), review.Items.Select(item => item.Severity).ToArray());
    }

    private static int Rank(string severity) => severity switch { "Blocker" => 0, "Warning" => 1, _ => 2 };

    [Fact]
    public void AModuleThatIsNotInstalledIsABlocker()
    {
        var review = Reviewer(Modules.Where(module => module.Id != "sftp").ToArray()).Review(Flow(), Config);

        var item = Assert.Single(Codes(review, "missing-module"));
        Assert.Equal("Blocker", item.Severity);
        Assert.Contains(item.Where, text => text.Contains("sftp"));
    }

    [Fact]
    public void AReferenceWithoutAStoredValueIsABlockerNamingWhereItIsUsed()
    {
        var flow = Flow(deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("password", "{{secret:vault-password}}")));

        var review = Reviewer().Review(flow, Config);

        var item = Assert.Single(Codes(review, "unresolved-reference"));
        Assert.Equal("Blocker", item.Severity);
        Assert.Contains("vault-password", item.Title);
        Assert.Contains(item.Where, text => text.StartsWith("Deliver 1.1"));
        Assert.DoesNotContain(review.Items, other => other.Title.Contains("sftp-host"));
    }

    [Fact]
    public void ATypedInCredentialIsFlaggedByNameAndNeverByValue()
    {
        var flow = Flow(deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("password", SecretValue), ("tokenUrl", "https://login.example/token"), ("path", "/outbox")),
            augments: [Step("some-augment", ("apiKey", SecretValue))]);

        var review = Reviewer().Review(flow, Config);

        var items = Codes(review, "plain-secret");
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.Detail.Contains("'password'") && !item.Detail.Contains("tokenUrl"));
        Assert.Contains(items, item => item.Detail.Contains("'apiKey'"));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(review));
    }

    [Fact]
    public void AReferenceInACredentialSettingIsFine()
    {
        var review = Reviewer().Review(Flow(), Config);

        Assert.Empty(Codes(review, "plain-secret"));
    }

    [Theory]
    [InlineData("TODO")]
    [InlineData("<host>")]
    [InlineData("please CHANGEME first")]
    public void AnUnfilledPlaceholderIsFlagged(string value)
    {
        var review = Reviewer().Review(Flow(deliver: Step("sftp", ("host", value), ("path", "/outbox"))), Config);

        var item = Assert.Single(Codes(review, "placeholder-left"));
        Assert.Contains("'host'", item.Detail);
        Assert.DoesNotContain(value, JsonSerializer.Serialize(review.Items.Where(other => other.Code == "placeholder-left")));
    }

    [Fact]
    public void ASourceAndDestinationOnTheSamePlaceAreFlaggedWhateverTheSlashesAndCase()
    {
        var flow = Flow(fetch: Step("file-fetch", ("path", "D:\\Data\\Out\\"), ("deleteAfterRead", "true")),
            deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("path", "d:/data/out")));

        var review = Reviewer().Review(flow, Config);

        var item = Assert.Single(Codes(review, "source-is-destination"));
        Assert.Equal(2, item.Where.Count);
        Assert.Contains("'path'", item.Detail);
        Assert.DoesNotContain("data", item.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentSourceAndDestinationLocationsAreNotFlagged()
        => Assert.Empty(Codes(Reviewer().Review(Flow(), Config), "source-is-destination"));

    [Fact]
    public void DeliveriesWithoutRetriesAreFlaggedUnlessTheFlowOrStepSetsOne()
    {
        var plain = Step("sftp", ("host", "{{config:sftp-host}}"), ("path", "/outbox"));
        var withStepRetry = new ModuleStepDefinition { Module = "sftp", Settings = plain.Settings, Retry = new() { MaxAttempts = 4 } };

        var noPolicy = new PipelineDefinition { Id = "x", Fetches = Flow().Fetches, Parses = Flow().Parses, Deliveries = Flow(deliver: plain).Deliveries, Retry = null };
        Assert.Single(Codes(Reviewer().Review(noPolicy, Config), "no-retry"));

        var singleAttempt = Flow(deliver: plain, retry: new() { MaxAttempts = 1 });
        Assert.Single(Codes(Reviewer().Review(singleAttempt, Config), "no-retry"));

        Assert.Empty(Codes(Reviewer().Review(Flow(deliver: plain), Config), "no-retry"));
        Assert.Empty(Codes(Reviewer().Review(Flow(deliver: withStepRetry, retry: new() { MaxAttempts = 1 }), Config), "no-retry"));
    }

    [Fact]
    public void RetryWithoutALimitIsOnlyANote()
    {
        var review = Reviewer().Review(Flow(retry: new() { MaxAttempts = null }), Config);

        var item = Assert.Single(Codes(review, "unlimited-retry"));
        Assert.Equal("Info", item.Severity);
        Assert.Empty(Codes(review, "no-retry"));
    }

    [Fact]
    public void ASourceThatNeverMarksWhatItReadIsANoteOnlyForScheduledFlows()
    {
        var unmarked = Step("file-fetch", ("path", HiddenPath));

        var interval = Reviewer().Review(Flow(fetch: unmarked), Config);
        var item = Assert.Single(Codes(interval, "no-source-cleanup"));
        Assert.Equal("Info", item.Severity);
        Assert.Contains("Delete after read", item.Detail);

        Assert.Empty(Codes(Reviewer().Review(Flow(fetch: unmarked, mode: PipelineTriggerMode.OnDemand), Config), "no-source-cleanup"));
        Assert.Empty(Codes(Reviewer().Review(Flow(), Config), "no-source-cleanup"));
        Assert.DoesNotContain(HiddenPath, JsonSerializer.Serialize(interval));
    }

    [Fact]
    public void AFlowWithNoDeliveryIsFlagged()
    {
        var review = Reviewer().Review(Flow(withDelivery: false), Config);

        Assert.Single(Codes(review, "no-delivery"));
    }

    [Fact]
    public void NativeStepsAreReadForBlockedStepsRetriesAndTypedInCredentials()
    {
        var json = $$$"""
            {"version":1,"durable":true,"nodes":[
              {"id":"d","kind":"Deliver","name":"Send file","module":"sftp","settings":{"host":"h","password":"{{{SecretValue}}}"}},
              {"id":"b","kind":"Blocked","name":"Old rule","blockReason":"Unknown functoid"}
            ]}
            """;
        var flow = Flow(withDelivery: false, augments: [new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new(StringComparer.OrdinalIgnoreCase) { ["flowJson"] = json } }]);

        var review = Reviewer().Review(flow, Config);

        Assert.Contains(Codes(review, "native-step"), item => item.Title.Contains("Old rule"));
        Assert.Contains(Codes(review, "no-retry"), item => item.Where.Any(text => text.Contains("Send file")));
        Assert.Contains(Codes(review, "plain-secret"), item => item.Where.Any(text => text.Contains("Send file")) && item.Detail.Contains("'password'"));
        Assert.Empty(Codes(review, "no-delivery"));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(review));
    }

    [Fact]
    public void AnUnreadableNativeFlowIsReportedNotThrown()
    {
        var flow = Flow(withDelivery: false, augments: [new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new(StringComparer.OrdinalIgnoreCase) { ["flowJson"] = "{oops" } }]);

        var review = Reviewer().Review(flow, Config);

        Assert.Single(Codes(review, "native-unreadable"));
    }

    [Fact]
    public async Task TheEndpointReturnsTheReviewWithoutCachingOrValues()
    {
        var flow = Flow(deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("password", SecretValue), ("path", "/outbox")));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog(Modules));
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(new FakeFlowDefinitionService(flow));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = new(Config) }));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.GetAsync("/api/assist/review/orders");
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(SecretValue, text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal("orders", document.RootElement.GetProperty("flowId").GetString());
            Assert.Contains(document.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("code").GetString() == "plain-secret");

            using var missing = await client.GetAsync("/api/assist/review/nope");
            Assert.False(missing.IsSuccessStatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
