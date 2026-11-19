namespace Service.Models;

/// <summary>A stored run as the digest reads it: states and times only. No error text and no message content.</summary>
public sealed record DigestRun(string ExecutionId, string FlowId, string State, long StartedUtcTicks, long UpdatedUtcTicks);

/// <summary>A flow as the digest reads it: identity and trigger only. No settings.</summary>
public sealed record DigestFlow(string Id, string DisplayName, bool Enabled, string TriggerMode, TimeSpan? Interval);

public sealed record DigestHeldRun(string ExecutionId, string FlowId, long StartedUtcTicks);

public sealed record DigestSlowFlow(string FlowId, string DisplayName, int CompletedRuns, double TypicalSeconds, double LatestSeconds);

public sealed record DigestStoppedFlow(string FlowId, string DisplayName, double IntervalSeconds, long? LastRunUtcTicks, string Reason);

/// <summary>Counts and lists about a period of operation. Facts come first; the sentences are generated from them by fixed rules.</summary>
public sealed record OperationalDigest(
    long PeriodStartUtcTicks,
    long PeriodEndUtcTicks,
    int Hours,
    int Runs,
    IReadOnlyDictionary<string, int> RunsByState,
    double? SuccessRate,
    int FlowsThatRan,
    IReadOnlyList<DigestHeldRun> HeldRuns,
    int HeldTotal,
    IReadOnlyList<DigestSlowFlow> SlowFlows,
    IReadOnlyList<DigestStoppedFlow> StoppedFlows,
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Notes);
