using System.Net.Http.Json;
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

public sealed class RunExplanationTests
{
    private const string ErrorSentinel = "connect to sftp.partner.example failed for user bob";

    private static AssistRunSource Source(string state, AssistRunEventInput[]? events = null, AssistRunDeliveryInput[]? deliveries = null)
        => new("exec-1", "orders", new AssistRunInput(state, events, deliveries));

    [Theory]
    [InlineData("Running", false)]
    [InlineData("Retrying", false)]
    [InlineData("Held", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Closed", false)]
    [InlineData("Surprising", false)]
    public void EveryStateGetsAMeaningAndOnlyHeldNeedsAPerson(string state, bool needsPerson)
    {
        var explanation = AssistRunExplainer.Explain(Source(state));
        Assert.Equal(state, explanation.State);
        Assert.False(string.IsNullOrWhiteSpace(explanation.Meaning));
        Assert.Equal(needsPerson, explanation.NeedsPerson);
        Assert.Contains("No events were recorded", Assert.Single(explanation.WhatHappened.Take(1)));
        Assert.Equal("exec-1", explanation.ExecutionId);
    }

    [Fact]
    public void AnUncertainDeliveryNeedsAPersonAndTheAdviceNeverOffersAResend()
    {
        var explanation = AssistRunExplainer.Explain(Source("Held",
            [new("execution", "Running", null), new("deliver:0", "Unknown", ErrorSentinel)],
            [new("deliver:0", "Unknown"), new("deliver:1", "Succeeded"), new("deliver:2", "InFlight")]));

        Assert.True(explanation.NeedsPerson);
        Assert.Equal(["Unknown", "Succeeded", "InFlight"], explanation.Deliveries.Select(delivery => delivery.State));
        Assert.Equal([true, false, true], explanation.Deliveries.Select(delivery => delivery.NeedsPerson));
        var all = string.Join("\n", explanation.WhatHappened.Concat(explanation.WhatToCheck).Concat(explanation.Deliveries.Select(delivery => delivery.Meaning)).Append(explanation.Meaning));
        Assert.Contains("will not resend it by itself", all);
        Assert.Contains("Confirm independently verified delivery", all);
        Assert.Contains("Close held execution without replay", all);
        Assert.Contains("does not send anything", all);
        Assert.Contains("'deliver:0' (Unknown)", all);
        Assert.Contains("1 event(s) carry detail text", all);
        foreach (var banned in new[] { "retry this", "resend this", "click retry", "Retry now" })
            Assert.DoesNotContain(banned, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ErrorTextAndDetailAreNeverRepeated()
    {
        var explanation = AssistRunExplainer.Explain(Source("Failed", [new("parse:p", "Failed", ErrorSentinel)]));
        var json = JsonSerializer.Serialize(explanation);
        Assert.DoesNotContain("sftp.partner.example", json);
        Assert.DoesNotContain("bob", json);
        Assert.Contains("'parse:p' (Failed)", string.Join(" ", explanation.WhatHappened));
        Assert.Contains("not retried automatically", explanation.Meaning);
        Assert.Contains("start a new run", string.Join(" ", explanation.WhatToCheck));
    }

    [Fact]
    public void ACompletedRunHasNothingToDoAndRetryingSaysWait()
    {
        Assert.Equal(["Nothing to do for this run."], AssistRunExplainer.Explain(Source("Completed", [new("execution", "Completed", null)], [new("deliver:0", "Succeeded")])).WhatToCheck);
        Assert.Contains("keeps retrying", string.Join(" ", AssistRunExplainer.Explain(Source("Retrying")).WhatToCheck));
    }

    [Fact]
    public async Task AStoredRunIsReadWithoutMessageContentAndExplainedOverHttp()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        var pair = fixture.Journal();
        var flow = new PipelineDefinition { Id = "orders", Name = "Orders", Category = "Invoices", Enabled = false };
        var context = new FlowExecutionContext("orders", "run-42", DateTimeOffset.UtcNow);
        await pair.Journal.BeginAsync(flow, context, default);
        var batch = new IntegrationBatch([new("message", BinaryData.FromString("payload-sentinel"), "text/plain", new Dictionary<string, string>())]);
        await pair.Journal.StageAsync(context, "parse:p", "Failed", batch, ErrorSentinel, default);
        Assert.True(await pair.Journal.PrepareDeliveryAsync(context, "delivery:0:deliver:0", new() { Module = "external" }, batch, default));
        await pair.Journal.DeliveryStateAsync(context, "delivery:0:deliver:0", "Unknown", default);

        var source = await pair.Journal.RunForAssistAsync("run-42", default);
        Assert.Equal("orders", source.FlowId);
        Assert.Contains(source.Run.Events!, item => item.Stage == "parse:p" && item.Detail == ErrorSentinel);
        Assert.Contains(source.Run.Deliveries!, item => item.State == "Unknown");
        Assert.DoesNotContain("payload-sentinel", JsonSerializer.Serialize(source));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pair.Journal.RunForAssistAsync("nope", default));

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
            using var response = await client.GetAsync("/api/assist/explain/run/run-42");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("sftp.partner.example", text);
            Assert.DoesNotContain("payload-sentinel", text);
            Assert.Contains("will not resend it by itself", text);
            using var missing = await client.GetAsync("/api/assist/explain/run/nope");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
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
            using var response = await client.GetAsync("/api/assist/explain/run/anything");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
