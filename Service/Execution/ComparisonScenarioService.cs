using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Service.Models;

namespace Service.Execution;

public interface IComparisonScenarioStore
{
    List<ComparisonScenario> Load();

    void Save(IReadOnlyList<ComparisonScenario> scenarios);
}

/// <summary>
/// Keeps every scenario in one DataProtection-encrypted file under its own directory, key ring and application name, so scenarios are
/// neither part of flow definitions, exports or workspace backups nor readable by the other protectors.
/// </summary>
public sealed class EncryptedComparisonScenarioStore(string directory, ILogger<EncryptedComparisonScenarioStore> logger) : IComparisonScenarioStore
{
    private const string Purpose = "Mulse.ComparisonScenarios.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDataProtector? _protector;

    private string FilePath => Path.Combine(directory, "scenarios.bin");

    public List<ComparisonScenario> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var plain = GetProtector().Unprotect(File.ReadAllBytes(FilePath));
            return JsonSerializer.Deserialize<List<ComparisonScenario>>(plain, Json) ?? throw new JsonException("Empty scenarios.");
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("The saved comparison scenarios could not be read ({ExceptionType}).", exception.GetType().Name);
            throw new InvalidOperationException("The saved comparison scenarios could not be read. Repair or remove the scenario file.");
        }
    }

    public void Save(IReadOnlyList<ComparisonScenario> scenarios)
    {
        Directory.CreateDirectory(directory);
        var bytes = GetProtector().Protect(JsonSerializer.SerializeToUtf8Bytes(scenarios, Json));
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, FilePath, overwrite: true);
    }

    private IDataProtector GetProtector()
    {
        if (_protector is not null) return _protector;
        Directory.CreateDirectory(directory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")), builder =>
        {
            builder.SetApplicationName("Mulse.ComparisonScenarios");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        return _protector = provider.CreateProtector(Purpose);
    }
}

/// <summary>Saves, lists, runs and deletes comparison scenarios. Expired scenarios are deleted whenever scenarios are read.</summary>
public sealed partial class ComparisonScenarioService(IComparisonScenarioStore store, TimeProvider time)
{
    public const int MaxScenarios = 50;
    public const int MaxStoredBytes = 20 * 1024 * 1024;
    public const int MaxRetentionDays = 365;
    private readonly Lock _gate = new();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,79}$")]
    private static partial Regex NamePattern();

    public IReadOnlyList<ComparisonScenarioSummary> List()
    {
        lock (_gate)
        {
            return LoadCurrent().OrderBy(scenario => scenario.Name, StringComparer.OrdinalIgnoreCase).Select(Summarize).ToList();
        }
    }

    public ComparisonScenario Get(string id)
    {
        lock (_gate)
        {
            return LoadCurrent().FirstOrDefault(scenario => scenario.Id == id) ?? throw new KeyNotFoundException("The scenario was not found. It may have been deleted or have expired.");
        }
    }

    public ComparisonScenarioSummary Save(SaveComparisonScenarioRequest request, string? authenticatedUser)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !NamePattern().IsMatch(name))
            throw new ArgumentException("The scenario name must be 1 to 80 characters: letters, digits, spaces, dots, dashes and underscores.");
        NativeFlowCompareRunner.ValidatePairs(request.Pairs);
        if (request.RetentionDays is { } days && (days < 1 || days > MaxRetentionDays))
            throw new ArgumentException($"Retention must be between 1 and {MaxRetentionDays} days, or empty to keep the scenario until it is deleted.");

        var savedBy = ChooseSavedBy(authenticatedUser, request.SavedBy);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var scenarios = LoadCurrent();
            var existing = scenarios.FindIndex(scenario => string.Equals(scenario.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing < 0 && scenarios.Count >= MaxScenarios)
                throw new InvalidOperationException($"At most {MaxScenarios} scenarios can be kept. Delete one first.");
            var saved = new ComparisonScenario(existing >= 0 ? scenarios[existing].Id : Guid.NewGuid().ToString("N"), name, request.Pairs, request.AdvanceWaits,
                request.ContainsRealData, savedBy, now, request.RetentionDays is { } keep ? now.AddDays(keep) : null, null);
            if (existing >= 0) scenarios[existing] = saved; else scenarios.Add(saved);
            Persist(scenarios);
            return Summarize(saved);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var scenarios = LoadCurrent();
            if (scenarios.RemoveAll(scenario => scenario.Id == id) == 0)
                throw new KeyNotFoundException("The scenario was not found. It may have been deleted or have expired.");
            Persist(scenarios);
        }
    }

    /// <summary>Runs the stored samples against the supplied definition and remembers the result without any sample values.</summary>
    public async Task<NativeFlowCompareResponse> RunAsync(string id, string? flowJson, CancellationToken cancellationToken)
    {
        var scenario = Get(id);
        var response = await NativeFlowCompareRunner.RunAsync(new(flowJson, scenario.Pairs, scenario.AdvanceWaits), cancellationToken).ConfigureAwait(false);
        var result = new ComparisonScenarioResult(time.GetUtcNow(), response.Summary, response.Results.Select(sample => new ComparisonScenarioSampleResult(
            sample.Name, sample.Verdict, sample.Source, sample.Truncated, sample.Error,
            sample.Differences.Select(difference => new ComparisonScenarioDifference(difference.Class, difference.Path, difference.Cosmetic)).ToList())).ToList());
        lock (_gate)
        {
            var scenarios = LoadCurrent();
            var index = scenarios.FindIndex(item => item.Id == id);
            // It may have been deleted or replaced while it ran; the result then belongs to nothing.
            if (index >= 0) { scenarios[index] = scenarios[index] with { LastResult = result }; Persist(scenarios); }
        }
        return response;
    }

    private static string ChooseSavedBy(string? authenticatedUser, string? typed)
    {
        var value = !string.IsNullOrWhiteSpace(authenticatedUser) ? authenticatedUser : typed;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return "unknown";
        if (value.Length > 80 || value.Any(char.IsControl)) throw new ArgumentException("The 'saved by' name must be at most 80 characters with no control characters.");
        return value;
    }

    private List<ComparisonScenario> LoadCurrent()
    {
        var scenarios = store.Load();
        var now = time.GetUtcNow();
        if (scenarios.RemoveAll(scenario => scenario.ExpiresAtUtc is { } expires && expires <= now) > 0) Persist(scenarios);
        return scenarios;
    }

    private void Persist(List<ComparisonScenario> scenarios)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(scenarios, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length > MaxStoredBytes)
            throw new ArgumentException($"The saved scenarios would be larger than {MaxStoredBytes / (1024 * 1024)} MB. Delete a scenario or save fewer or smaller samples.");
        store.Save(scenarios);
    }

    private static ComparisonScenarioSummary Summarize(ComparisonScenario scenario)
        => new(scenario.Id, scenario.Name, scenario.Pairs.Count, scenario.AdvanceWaits, scenario.ContainsRealData, scenario.SavedBy, scenario.SavedAtUtc,
            scenario.ExpiresAtUtc, scenario.LastResult?.RunAtUtc, scenario.LastResult?.Summary.Statement);
}
