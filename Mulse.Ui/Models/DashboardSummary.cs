namespace Mulse.Ui.Models;

/// <summary>How a flow looks on the dashboard, worked out from its most recent run and its setup. One flow falls in exactly one group.</summary>
public enum FlowHealth
{
    Failing,
    NeedsAttention,
    NeedsSetup,
    Healthy,
    NotRunRecently,
    Other
}

public sealed record DashboardDay(DateOnly Date, int Completed, int Failed, int Other)
{
    public int Total => Completed + Failed + Other;
}

public sealed record DashboardAttention(string FlowId, string DisplayName, string Reason, string Href);

public sealed record DashboardRun(string ExecutionId, string FlowId, string State, DateTime? StartedUtc);

/// <summary>
/// Numbers for the dashboard, calculated from data the page already loads. Run figures only cover the runs that were supplied, so they describe recent activity, not all time.
/// </summary>
public sealed class DashboardSummary
{
    public const int MaxAttention = 8;
    public const int MaxRecentRuns = 8;
    public const int Days = 7;

    public int FlowCount { get; private init; }
    public int EnabledCount { get; private init; }
    public int DisabledCount { get; private init; }
    public int ModuleCount { get; private init; }
    public int UnresolvedConfig { get; private init; }
    public bool HasRunData { get; private init; }
    public int RunCount { get; private init; }
    public IReadOnlyDictionary<string, int> RunsByState { get; private init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<FlowHealth, int> FlowsByHealth { get; private init; } = new Dictionary<FlowHealth, int>();
    public IReadOnlyList<DashboardDay> Last7Days { get; private init; } = [];
    public IReadOnlyList<DashboardAttention> Attention { get; private init; } = [];
    public int AttentionTotal { get; private init; }
    public IReadOnlyList<DashboardRun> RecentRuns { get; private init; } = [];

    public int Completed => RunsByState.GetValueOrDefault("Completed");
    public int Failed => RunsByState.GetValueOrDefault("Failed");
    public int InProgress => RunsByState.GetValueOrDefault("Running") + RunsByState.GetValueOrDefault("Retrying");
    public int Held => RunsByState.GetValueOrDefault("Held");

    /// <summary>Share of finished runs (completed or failed) that completed, or null when none finished.</summary>
    public double? SuccessRate => Completed + Failed == 0 ? null : (double)Completed / (Completed + Failed);

    public static string Describe(FlowHealth health) => health switch
    {
        FlowHealth.Healthy => "Last run completed",
        FlowHealth.Failing => "Last run failed",
        FlowHealth.NeedsAttention => "Held or retrying",
        FlowHealth.NeedsSetup => "Needs setup",
        FlowHealth.NotRunRecently => "No recent run",
        _ => "Other"
    };

    public static DashboardSummary Build(
        IReadOnlyList<FlowViewModel> flows,
        IReadOnlyList<ModuleViewModel> modules,
        IReadOnlyList<ConfigReferenceUsageViewModel> usages,
        IReadOnlyList<HistoryExecutionViewModel>? executions,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(usages);

        var runs = (executions ?? []).Select(execution => (Execution: execution, Started: ToUtc(execution.StartedUtcTicks)))
            .OrderByDescending(item => item.Execution.StartedUtcTicks).ToArray();
        var latest = runs.GroupBy(item => item.Execution.FlowId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Execution, StringComparer.OrdinalIgnoreCase);

        var health = Enum.GetValues<FlowHealth>().ToDictionary(value => value, _ => 0);
        var attention = new List<(int Rank, DashboardAttention Item)>();
        foreach (var flow in flows)
        {
            var name = flow.DisplayName;
            var unresolved = usages.Count(usage => !usage.IsResolved && string.Equals(usage.FlowId, flow.Id, StringComparison.OrdinalIgnoreCase));
            var setupReason = SetupReason(flow, unresolved);
            latest.TryGetValue(flow.Id, out var last);

            var group = last?.State switch
            {
                "Failed" => FlowHealth.Failing,
                "Held" or "Retrying" => FlowHealth.NeedsAttention,
                _ when setupReason is not null => FlowHealth.NeedsSetup,
                "Completed" or "Running" => FlowHealth.Healthy,
                null => FlowHealth.NotRunRecently,
                _ => FlowHealth.Other
            };
            health[group]++;

            var history = "/history/" + Uri.EscapeDataString(flow.Id);
            if (group == FlowHealth.Failing)
                attention.Add((0, new(flow.Id, name, "The last run failed. It is not retried automatically.", history)));
            else if (group == FlowHealth.NeedsAttention)
                attention.Add((1, new(flow.Id, name, last!.State == "Held" ? "The last run is held and needs a person to look at it." : "The last run is retrying.", history)));
            if (setupReason is not null)
                attention.Add((2, new(flow.Id, name, setupReason, "/designer/" + Uri.EscapeDataString(flow.Id))));
        }

        var ordered = attention.OrderBy(item => item.Rank).ThenBy(item => item.Item.FlowId, StringComparer.OrdinalIgnoreCase).Select(item => item.Item).ToArray();
        var today = DateOnly.FromDateTime(nowUtc);
        var days = Enumerable.Range(0, Days).Select(offset => today.AddDays(offset - (Days - 1))).ToArray();
        var perDay = days.Select(day =>
        {
            var inDay = runs.Where(item => item.Started is { } started && DateOnly.FromDateTime(started) == day).Select(item => item.Execution.State).ToArray();
            return new DashboardDay(day, inDay.Count(state => state == "Completed"), inDay.Count(state => state == "Failed"),
                inDay.Count(state => state is not ("Completed" or "Failed")));
        }).ToArray();

        return new()
        {
            FlowCount = flows.Count,
            EnabledCount = flows.Count(flow => flow.Enabled),
            DisabledCount = flows.Count(flow => !flow.Enabled),
            ModuleCount = modules.Count,
            UnresolvedConfig = usages.Count(usage => !usage.IsResolved),
            HasRunData = executions is not null,
            RunCount = runs.Length,
            RunsByState = runs.GroupBy(item => item.Execution.State, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            FlowsByHealth = health,
            Last7Days = perDay,
            Attention = [.. ordered.Take(MaxAttention)],
            AttentionTotal = ordered.Length,
            RecentRuns = [.. runs.Take(MaxRecentRuns).Select(item => new DashboardRun(item.Execution.Id, item.Execution.FlowId, item.Execution.State, item.Started))]
        };
    }

    private static string? SetupReason(FlowViewModel flow, int unresolved)
    {
        if (flow.Migration is not null) return "Imported flow: execution is blocked until it has been reviewed.";
        if (flow.MissingModules is { Count: > 0 } missing) return "Unknown or unloaded modules: " + string.Join(", ", missing) + ".";
        return unresolved > 0 ? $"{unresolved} unresolved config or secret reference(s)." : null;
    }

    private static DateTime? ToUtc(long ticks)
        => ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
}
