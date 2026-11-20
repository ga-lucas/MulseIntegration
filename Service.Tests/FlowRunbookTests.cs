using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Pipeline;
using Service.Assist;
using Service.Endpoints;
using Service.Models;

namespace Service.Tests;

public sealed class FlowRunbookTests
{
    private const string SecretValue = "p@ssw0rd-literal-value";
    private const string HiddenPath = "/inbox/partner-orders";
    private const string StoredValue = "stored-host.example.net";
    private static readonly DateTimeOffset Now = new(2025, 3, 4, 5, 6, 0, TimeSpan.Zero);

    private static readonly ModuleDescriptor[] Modules =
    [
        new("file-fetch", "File fetch", ModuleKind.Fetch, "Reads files.", [new("path", "Folder", "Where to read", true)]),
        new("csv-parse", "CSV parse", ModuleKind.Parse, "Parses CSV."),
        new("text-render", "Text render", ModuleKind.Render, "Renders text."),
        new("sftp", "SFTP deliver", ModuleKind.Deliver, "Sends over SFTP.", [new("host", "Host", "Server", true), new("password", "Password", "Login", false, ModuleSettingInputKind.Password)])
    ];

    private static readonly Dictionary<string, string> Config = new(StringComparer.OrdinalIgnoreCase) { ["sftp-host"] = StoredValue };

    private static AssistFlowRunbook Runbook(params ModuleDescriptor[] modules)
    {
        var catalog = new ReadOnlyCatalog(modules.Length == 0 ? Modules : modules);
        var explainer = new AssistExplainer(catalog);
        return new(catalog, explainer, new AssistFlowReviewer(catalog, explainer));
    }

    private static ModuleStepDefinition Step(string module, params (string Key, string Value)[] settings)
        => new() { Module = module, Settings = settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) };

    private static PipelineDefinition Flow(
        PipelineTriggerMode mode = PipelineTriggerMode.Interval,
        RetryPolicyDefinition? retry = null,
        ModuleStepDefinition? deliver = null,
        string fetchModule = "file-fetch",
        IEnumerable<ModuleStepDefinition>? augments = null)
        => new()
        {
            Id = "orders",
            Name = "Orders",
            Trigger = new() { Mode = mode, Interval = TimeSpan.FromMinutes(5), RunOnStartup = true },
            Fetches = [new() { Id = "f1", Fetch = Step(fetchModule, ("path", HiddenPath)) }],
            Parses = [new() { Id = "p1", Parse = Step("csv-parse"), FetchNodeIds = ["f1"] }],
            Augments = [.. augments ?? []],
            Deliveries = [new() { Render = Step("text-render"), Delivers = [new() { Deliver = deliver ?? Step("sftp", ("host", "{{config:sftp-host}}"), ("password", "{{secret:sftp-password}}")) }] }],
            Retry = retry ?? new() { MaxAttempts = 3, Delay = TimeSpan.FromSeconds(30) }
        };

    private static string Section(FlowRunbook runbook, string title) => string.Join("\n", runbook.Sections.Single(section => section.Title == title).Lines);

    [Fact]
    public void ItDescribesTheScheduleInputsOutputsAndRetry()
    {
        var runbook = Runbook().Build(Flow(), Config, null, Now);

        Assert.Equal("Orders", runbook.Title);
        Assert.Contains("Automatically every 5 minutes, and once when the service starts", Section(runbook, "Overview"));
        Assert.Contains("File fetch ('file-fetch')", Section(runbook, "Inputs"));
        Assert.Contains("Settings in use: path", Section(runbook, "Inputs"));
        Assert.Contains("SFTP deliver ('sftp')", Section(runbook, "Outputs"));
        Assert.Contains("up to 3 attempts, 30 seconds apart", Section(runbook, "Failure behavior"));
        Assert.Contains("# Runbook: Orders", runbook.Markdown);
        Assert.Contains("2025-03-04 05:06 UTC", runbook.Markdown);
        Assert.Contains("does not show that the flow is correct", runbook.Disclaimer);
    }

    [Fact]
    public void ReferencesAreListedByNameWithTheirStoredStateAndNoValuesAnywhere()
    {
        var flow = Flow(deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("password", "{{secret:sftp-password}}")));

        var runbook = Runbook().Build(flow, Config, null, Now);
        var dependencies = Section(runbook, "Dependencies");

        Assert.Contains("Config value 'sftp-host': has a stored value", dependencies);
        Assert.Contains("Secret 'sftp-password': has NO stored value yet", dependencies);
        var all = JsonSerializer.Serialize(runbook);
        Assert.DoesNotContain(StoredValue, all);
        Assert.DoesNotContain(HiddenPath, all);
    }

    [Fact]
    public void ATypedInPasswordIsNeverCopiedIntoTheRunbook()
    {
        var runbook = Runbook().Build(Flow(deliver: Step("sftp", ("host", "h"), ("password", SecretValue))), Config, null, Now);

        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(runbook));
        Assert.DoesNotContain(SecretValue, runbook.Markdown);
    }

    [Fact]
    public void AMissingModuleIsSaidToBeNotInstalled()
    {
        var runbook = Runbook().Build(Flow(fetchModule: "ghost-fetch"), Config, null, Now);

        Assert.Contains("'ghost-fetch' (not installed on this server)", Section(runbook, "Inputs"));
    }

    [Theory]
    [InlineData(PipelineTriggerMode.OnDemand, "Only when someone starts it.")]
    [InlineData(PipelineTriggerMode.Disabled, "Automatic starting is switched off.")]
    public void TheTriggerIsPutInPlainWords(PipelineTriggerMode mode, string expected)
        => Assert.Contains(expected, Section(Runbook().Build(Flow(mode: mode), Config, null, Now), "Overview"));

    [Fact]
    public void NoRetryIsStatedPlainly()
        => Assert.Contains("no automatic retry (one attempt)", Section(Runbook().Build(Flow(retry: new() { MaxAttempts = 1 }), Config, null, Now), "Failure behavior"));

    [Fact]
    public void NativeStepsAreListedAndTheirRetryAmbiguityIsExplained()
    {
        var json = $$$"""
            {"version":1,"durable":true,"nodes":[
              {"id":"d","kind":"Deliver","name":"Send file","module":"sftp","settings":{"host":"{{config:sftp-host}}","password":"{{{SecretValue}}}"},"retry":{"attempts":2,"intervalSeconds":60,"retryAmbiguous":true}}
            ]}
            """;
        var flow = Flow(augments: [new ModuleStepDefinition { Module = Mulse.Modules.ControlFlow.NativeFlowDefinition.ModuleId, Settings = new(StringComparer.OrdinalIgnoreCase) { ["flowJson"] = json } }]);

        var runbook = Runbook().Build(flow, Config, null, Now);

        Assert.Contains("Send file", Section(runbook, "Processing"));
        Assert.Contains("up to 2 more attempt(s), 1 minute apart, including after an ambiguous failure, which could send the data twice", Section(runbook, "Failure behavior"));
        Assert.Contains("Config value 'sftp-host'", Section(runbook, "Dependencies"));
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(runbook));
    }

    [Fact]
    public void AnUnreadableNativeFlowIsSaidSoNotThrown()
    {
        var flow = Flow(augments: [new ModuleStepDefinition { Module = Mulse.Modules.ControlFlow.NativeFlowDefinition.ModuleId, Settings = new(StringComparer.OrdinalIgnoreCase) { ["flowJson"] = "{oops" } }]);

        Assert.Contains("could not be read", Section(Runbook().Build(flow, Config, null, Now), "Processing"));
    }

    [Fact]
    public void EmptyPersonTextShowsAPlaceholderAndGivenTextIsCleanedAndCapped()
    {
        var empty = Runbook().Build(Flow(), Config, null, Now);
        Assert.Contains("not provided yet", Section(empty, "Purpose"));
        Assert.Contains("not provided yet", Section(empty, "Contact"));

        var request = new RunbookRequest("Sends orders\u0007 to the partner", new string('c', 1000), new string('n', 5000));
        var given = Runbook().Build(Flow(), Config, request, Now);

        Assert.Equal("Sends orders  to the partner", Section(given, "Purpose"));
        Assert.True(Section(given, "Contact").Length <= 303);
        Assert.True(Section(given, "Notes").Length <= 3003);
        Assert.DoesNotContain('\u0007', given.Markdown);
    }

    [Fact]
    public void TheReviewStateIsIncluded()
    {
        var runbook = Runbook().Build(Flow(retry: new() { MaxAttempts = 1 }), Config, null, Now);

        Assert.Contains("The pre-enable review found", Section(runbook, "Before relying on it"));
        Assert.Contains("Not checked:", Section(runbook, "Before relying on it"));
    }

    [Fact]
    public async Task TheEndpointReturnsTheRunbookWithoutCachingOrValues()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Modules.IModuleCatalog>(new ReadOnlyCatalog(Modules));
        builder.Services.AddSingleton<Service.Flows.IFlowDefinitionService>(new FakeFlowDefinitionService(Flow(deliver: Step("sftp", ("host", "{{config:sftp-host}}"), ("password", SecretValue)))));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = new(Config) }));
        builder.Services.AddAssist();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapAssistEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.PostAsJsonAsync("/api/assist/runbook/orders", new RunbookRequest("Sends orders", "ops@example.com", null));
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain(SecretValue, text);
            Assert.DoesNotContain(StoredValue, text);
            using var document = JsonDocument.Parse(text);
            Assert.Equal("orders", document.RootElement.GetProperty("flowId").GetString());
            Assert.Contains("Sends orders", document.RootElement.GetProperty("markdown").GetString());

            using var empty = await client.PostAsync("/api/assist/runbook/orders", null);
            Assert.True(empty.IsSuccessStatusCode, await empty.Content.ReadAsStringAsync());

            using var missing = await client.PostAsJsonAsync("/api/assist/runbook/nope", new RunbookRequest());
            Assert.False(missing.IsSuccessStatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
