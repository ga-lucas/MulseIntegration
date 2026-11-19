using System.Globalization;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// A short digest of a period (O4): runs, failures, held runs, slow flows and flows that stopped running.
/// It is deterministic, reads only states, times and trigger settings, and never offers to retry, resend or reconcile anything.
/// </summary>
public static class AssistOperationalDigest
{
    public const int MaxListed = 10;
    private const int MinRunsForSlow = 4;
    private const double SlowFactor = 2;
    private const double SlowMinimumExtraSeconds = 5;
    private const double StoppedFactor = 3;
    private static readonly TimeSpan StoppedGrace = TimeSpan.FromMinutes(1);

    public static OperationalDigest Build(IReadOnlyList<DigestRun> runs, IReadOnlyList<DigestFlow> flows, DateTimeOffset now, int hours, int fetchLimit)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(flows);
        var end = now.UtcDateTime;
        var start = end.AddHours(-hours);
        var inPeriod = runs.Where(run => run.StartedUtcTicks >= start.Ticks && run.StartedUtcTicks <= end.Ticks).ToArray();

        var byState = inPeriod.GroupBy(run => run.State, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var completed = byState.GetValueOrDefault("Completed");
        var failed = byState.GetValueOrDefault("Failed");
        double? rate = completed + failed == 0 ? null : (double)completed / (completed + failed);

        // A held run needs a person whenever it started, so it is listed even when it began before the period.
        var held = runs.Where(run => run.State == "Held").OrderByDescending(run => run.StartedUtcTicks).ToArray();

        var names = flows.ToDictionary(flow => flow.Id, flow => flow.DisplayName, StringComparer.OrdinalIgnoreCase);
        var slow = FindSlow(inPeriod, names);
        var stopped = FindStopped(runs, flows, now, TimeSpan.FromHours(hours));

        var facts = new List<string>
        {
            inPeriod.Length == 0
                ? $"No runs started in the last {hours} hour(s)."
                : $"{inPeriod.Length} run(s) started in the last {hours} hour(s) across {inPeriod.Select(run => run.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} flow(s): "
                    + string.Join(", ", byState.Select(pair => $"{pair.Value} {pair.Key.ToLowerInvariant()}")) + "."
        };
        if (rate is { } value) facts.Add(string.Create(CultureInfo.InvariantCulture, $"{completed} of {completed + failed} finished run(s) completed ({Math.Round(value * 100):0}%)."));
        if (held.Length > 0) facts.Add($"{held.Length} run(s) are held and need a person to check the destination first.");
        if (slow.Length > 0) facts.Add($"{slow.Length} flow(s) ran slower than usual: " + string.Join(", ", slow.Take(MaxListed).Select(item => item.DisplayName)) + ".");
        if (stopped.Length > 0) facts.Add($"{stopped.Length} interval flow(s) look like they stopped running: " + string.Join(", ", stopped.Take(MaxListed).Select(item => item.DisplayName)) + ".");
        if (held.Length == 0 && slow.Length == 0 && stopped.Length == 0 && failed == 0) facts.Add("Nothing needs attention.");

        var notes = new List<string>
        {
            "Times are UTC. Only states and times were read; no error text or message content.",
            $"Slow means the latest completed run took more than {SlowFactor:0}x the flow's usual time (and at least {SlowMinimumExtraSeconds:0} seconds longer), judged from at least {MinRunsForSlow} completed runs in the period.",
            $"Stopped means an enabled Interval flow has not started a run for more than {StoppedFactor:0}x its interval. On-demand and push flows are not checked.",
            "This digest never retries or resends anything."
        };
        if (runs.Count >= fetchLimit) notes.Add($"Only the {fetchLimit} most recent runs were read, so a busy system may have more than shown here.");

        return new(start.Ticks, end.Ticks, hours, inPeriod.Length, byState, rate, inPeriod.Select(run => run.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            [.. held.Take(MaxListed).Select(run => new DigestHeldRun(run.ExecutionId, run.FlowId, run.StartedUtcTicks))], held.Length,
            [.. slow.Take(MaxListed)], [.. stopped.Take(MaxListed)], facts, notes);
    }

    private static DigestSlowFlow[] FindSlow(IReadOnlyList<DigestRun> inPeriod, Dictionary<string, string> names)
        => [.. inPeriod.Where(run => run.State == "Completed" && run.UpdatedUtcTicks >= run.StartedUtcTicks)
            .GroupBy(run => run.FlowId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(run => run.StartedUtcTicks).ToArray();
                if (ordered.Length < MinRunsForSlow) return null;
                var latest = Seconds(ordered[^1]);
                var earlier = ordered[..^1].Select(Seconds).Order().ToArray();
                var typical = earlier[earlier.Length / 2];
                return latest > typical * SlowFactor && latest - typical >= SlowMinimumExtraSeconds
                    ? new DigestSlowFlow(group.Key, names.GetValueOrDefault(group.Key, group.Key), ordered.Length, Math.Round(typical, 1), Math.Round(latest, 1))
                    : null;
            })
            .OfType<DigestSlowFlow>().OrderByDescending(item => item.LatestSeconds / Math.Max(item.TypicalSeconds, 0.1)).ThenBy(item => item.FlowId, StringComparer.OrdinalIgnoreCase)];

    private static double Seconds(DigestRun run) => TimeSpan.FromTicks(run.UpdatedUtcTicks - run.StartedUtcTicks).TotalSeconds;

    private static DigestStoppedFlow[] FindStopped(IReadOnlyList<DigestRun> runs, IReadOnlyList<DigestFlow> flows, DateTimeOffset now, TimeSpan period)
    {
        var lastRun = runs.GroupBy(run => run.FlowId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Max(run => run.StartedUtcTicks), StringComparer.OrdinalIgnoreCase);
        var result = new List<DigestStoppedFlow>();
        foreach (var flow in flows.Where(flow => flow.Enabled && flow.TriggerMode == "Interval" && flow.Interval is { } interval && interval > TimeSpan.Zero && interval < period).OrderBy(flow => flow.Id, StringComparer.OrdinalIgnoreCase))
        {
            var interval = flow.Interval!.Value;
            var limit = TimeSpan.FromTicks((long)(interval.Ticks * StoppedFactor)) + StoppedGrace;
            if (lastRun.TryGetValue(flow.Id, out var ticks))
            {
                var since = now.UtcDateTime - new DateTime(ticks, DateTimeKind.Utc);
                if (since > limit)
                    result.Add(new(flow.Id, flow.DisplayName, interval.TotalSeconds, ticks, $"The last run started {Describe(since)} ago; it should run every {Describe(interval)}."));
            }
            else
                result.Add(new(flow.Id, flow.DisplayName, interval.TotalSeconds, null, $"No run was found among the runs read, but it should run every {Describe(interval)}."));
        }
        return [.. result];
    }

    private static string Describe(TimeSpan span)
        => span.TotalDays >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalDays:0.#} day(s)")
            : span.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0.#} hour(s)")
            : span.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0.#} minute(s)")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0} second(s)");
}
