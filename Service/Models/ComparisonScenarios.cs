namespace Service.Models;

/// <summary>One difference remembered from a run. It deliberately has no expected or actual values, so keeping a result never copies data.</summary>
public sealed record ComparisonScenarioDifference(string Class, string Path, bool Cosmetic);

public sealed record ComparisonScenarioSampleResult(string Name, string Verdict, string Source, bool Truncated, string? Error, List<ComparisonScenarioDifference> Differences);

/// <summary>The outcome of the latest run of a scenario, without any sample content.</summary>
public sealed record ComparisonScenarioResult(DateTimeOffset RunAtUtc, NativeFlowCompareSummary Summary, List<ComparisonScenarioSampleResult> Samples);

/// <summary>
/// A saved set of samples that can be run again against the current native definition. It is stored encrypted, apart from flows,
/// exports and backups, and may contain real data. The flow definition itself is never part of a scenario.
/// </summary>
public sealed record ComparisonScenario(
    string Id,
    string Name,
    List<NativeFlowComparePair> Pairs,
    bool AdvanceWaits,
    bool ContainsRealData,
    string SavedBy,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    ComparisonScenarioResult? LastResult);

/// <summary>What a scenario list shows. No sample content.</summary>
public sealed record ComparisonScenarioSummary(
    string Id,
    string Name,
    int SampleCount,
    bool AdvanceWaits,
    bool ContainsRealData,
    string SavedBy,
    DateTimeOffset SavedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? LastRunAtUtc,
    string? LastRunStatement);

/// <summary>Saving with a name that already exists replaces that scenario. <c>RetentionDays</c> of null keeps it until it is deleted.</summary>
public sealed record SaveComparisonScenarioRequest(
    string Name,
    List<NativeFlowComparePair> Pairs,
    bool AdvanceWaits = false,
    bool ContainsRealData = true,
    int? RetentionDays = null,
    string? SavedBy = null);

public sealed record RunComparisonScenarioRequest(string FlowJson);
