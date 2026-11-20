using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Service.Endpoints;
using Service.Execution;
using Service.Models;

namespace Service.Tests;

public sealed class ComparisonScenarioTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-scenarios-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IComparisonScenarioStore
    {
        private List<ComparisonScenario> _items = [];
        public List<ComparisonScenario> Load() => [.. _items];
        public void Save(IReadOnlyList<ComparisonScenario> scenarios) => _items = [.. scenarios];
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SaveComparisonScenarioRequest Request(string name = "March claims", string actual = "<a>2</a>", int? retention = null, string? savedBy = null)
        => new(name, [new("s1", "<a>1</a>", actual)], RetentionDays: retention, SavedBy: savedBy);

    [Fact]
    public void StoreEncryptsOnDiskAndRoundTrips()
    {
        var store = new EncryptedComparisonScenarioStore(_directory, NullLogger<EncryptedComparisonScenarioStore>.Instance);
        var service = new ComparisonScenarioService(store, new FakeTime(Start));
        service.Save(Request(actual: "<patient>SECRETVALUE</patient>"), null);

        var raw = File.ReadAllText(Path.Combine(_directory, "scenarios.bin"), System.Text.Encoding.Latin1);
        Assert.DoesNotContain("SECRETVALUE", raw);
        Assert.DoesNotContain("March claims", raw);

        var reloaded = new ComparisonScenarioService(new EncryptedComparisonScenarioStore(_directory, NullLogger<EncryptedComparisonScenarioStore>.Instance), new FakeTime(Start));
        var summary = Assert.Single(reloaded.List());
        Assert.Equal("<patient>SECRETVALUE</patient>", reloaded.Get(summary.Id).Pairs[0].Actual);
    }

    [Fact]
    public void CorruptStoreFailsClosedWithoutExposingData()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "scenarios.bin"), [1, 2, 3, 4]);
        var service = new ComparisonScenarioService(new EncryptedComparisonScenarioStore(_directory, NullLogger<EncryptedComparisonScenarioStore>.Instance), new FakeTime(Start));
        Assert.Throws<InvalidOperationException>(() => service.List());
    }

    [Fact]
    public void SavingByNameReplacesKeepsIdAndRecordsWhoAndWhen()
    {
        var time = new FakeTime(Start);
        var service = new ComparisonScenarioService(new MemoryStore(), time);
        var first = service.Save(Request("Claims", retention: 30, savedBy: "typed user"), null);
        time.Now = Start.AddDays(2);
        var second = service.Save(Request("claims", actual: "<a>3</a>"), "DOMAIN\\alice");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.List());
        Assert.Equal("typed user", first.SavedBy);
        Assert.Equal(Start.AddDays(30), first.ExpiresAtUtc);
        Assert.Equal("DOMAIN\\alice", second.SavedBy);
        Assert.Null(second.ExpiresAtUtc);
        Assert.Equal(Start.AddDays(2), second.SavedAtUtc);
        Assert.True(second.ContainsRealData);
        Assert.Equal("<a>3</a>", service.Get(second.Id).Pairs[0].Actual);
    }

    [Fact]
    public void ExpiredScenariosAreDeletedWhenRead()
    {
        var time = new FakeTime(Start);
        var store = new MemoryStore();
        var service = new ComparisonScenarioService(store, time);
        var kept = service.Save(Request("Kept"), null);
        var expiring = service.Save(Request("Expiring", retention: 1), null);

        time.Now = Start.AddDays(1).AddSeconds(1);
        Assert.Equal(kept.Id, Assert.Single(service.List()).Id);
        Assert.Single(store.Load());
        Assert.Throws<KeyNotFoundException>(() => service.Get(expiring.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("-leading")]
    [InlineData("has/slash")]
    public void InvalidNamesAreRejected(string name)
        => Assert.Throws<ArgumentException>(() => new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start)).Save(Request(name), null));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void InvalidRetentionIsRejected(int days)
        => Assert.Throws<ArgumentException>(() => new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start)).Save(Request(retention: days), null));

    [Fact]
    public void InvalidSamplesAndSavedByAreRejected()
    {
        var service = new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start));
        Assert.Throws<ArgumentException>(() => service.Save(new("x", [], false), null));
        Assert.Throws<ArgumentException>(() => service.Save(new("x", [new("a", "e", "a"), new("A", "e", "a")]), null));
        Assert.Throws<ArgumentException>(() => service.Save(Request(savedBy: new string('x', 81)), null));
        Assert.Throws<ArgumentException>(() => service.Save(Request(savedBy: "bad\nname"), null));
    }

    [Fact]
    public void ScenarioCountAndSizeAreLimited()
    {
        var service = new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start));
        for (var index = 0; index < ComparisonScenarioService.MaxScenarios; index++) service.Save(Request("s" + index), null);
        Assert.Throws<InvalidOperationException>(() => service.Save(Request("one too many"), null));
        service.Save(Request("s0", actual: "<a>9</a>"), null);

        var big = new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start));
        var huge = new string('x', ComparisonScenarioService.MaxStoredBytes + 1);
        Assert.Throws<ArgumentException>(() => big.Save(Request(actual: huge), null));
    }

    [Fact]
    public void DeleteRemovesTheScenarioAndMissingOnesAreNotFound()
    {
        var service = new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start));
        var saved = service.Save(Request(), null);
        service.Delete(saved.Id);
        Assert.Empty(service.List());
        Assert.Throws<KeyNotFoundException>(() => service.Delete(saved.Id));
        Assert.Throws<KeyNotFoundException>(() => service.Get(saved.Id));
    }

    [Fact]
    public async Task RunKeepsTheResultWithoutAnyValues()
    {
        var time = new FakeTime(Start);
        var service = new ComparisonScenarioService(new MemoryStore(), time);
        var saved = service.Save(new("Run me", [new("s1", "<a><ssn>123-45-6789</ssn></a>", "<a><ssn>999-99-9999</ssn></a>")]), null);
        time.Now = Start.AddHours(1);

        var response = await service.RunAsync(saved.Id, null, default);

        Assert.Equal("Different", response.Results[0].Verdict);
        Assert.Contains(response.Results[0].Differences, difference => difference.Expected == "123-45-6789");

        var stored = service.Get(saved.Id).LastResult!;
        Assert.Equal(Start.AddHours(1), stored.RunAtUtc);
        Assert.Equal("Different", stored.Samples[0].Verdict);
        Assert.Equal("Value", stored.Samples[0].Differences[0].Class);
        var json = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("123-45-6789", json);
        Assert.DoesNotContain("999-99-9999", json);
        Assert.Equal(Start.AddHours(1), service.List()[0].LastRunAtUtc);
    }

    [Fact]
    public async Task RunOfAMissingScenarioIsNotFound()
        => await Assert.ThrowsAsync<KeyNotFoundException>(() => new ComparisonScenarioService(new MemoryStore(), new FakeTime(Start)).RunAsync("nope", null, default));

    [Fact]
    public async Task EndpointsSaveListGetRunAndDelete()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IComparisonScenarioStore, MemoryStore>();
        builder.Services.AddSingleton<TimeProvider>(new FakeTime(Start));
        builder.Services.AddSingleton<ComparisonScenarioService>();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapComparisonScenarioEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            const string root = "/api/flow-designer/native/compare/scenarios";

            using var saved = await client.PostAsJsonAsync(root, new { name = "Claims", pairs = new[] { new { name = "s", expected = "<a>1</a>", actual = "<a>2</a>" } }, retentionDays = 7, savedBy = "me" });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var summary = await saved.Content.ReadFromJsonAsync<JsonElement>();
            var id = summary.GetProperty("id").GetString()!;
            Assert.True(summary.GetProperty("containsRealData").GetBoolean());
            Assert.Equal("no-store", saved.Headers.CacheControl?.ToString());

            var list = await client.GetStringAsync(root);
            Assert.Contains("Claims", list);
            Assert.DoesNotContain("<a>1</a>", list);

            var full = await client.GetStringAsync($"{root}/{id}");
            Assert.Contains("<a>1</a>", full);

            using var run = await client.PostAsJsonAsync($"{root}/{id}/run", new { flowJson = (string?)null });
            Assert.True(run.IsSuccessStatusCode, await run.Content.ReadAsStringAsync());
            Assert.Equal("Different", (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0].GetProperty("verdict").GetString());

            using var bad = await client.PostAsJsonAsync(root, new { name = "../x", pairs = new[] { new { name = "s", expected = "e", actual = "a" } } });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);

            using var deleted = await client.DeleteAsync($"{root}/{id}");
            Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
            using var gone = await client.GetAsync($"{root}/{id}");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
