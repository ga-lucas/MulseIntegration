using Service.Models;

namespace Service.Assist;

/// <summary>
/// Groups recent failed, held and retrying runs by likely cause (O3). The grouping is deterministic and needs no AI provider.
/// It reads error text only to choose a category and never repeats it. It never offers to retry, resend or reconcile anything.
/// </summary>
public static class AssistFailurePatterns
{
    public const int MaxFlowsListed = 10;

    private sealed record Category(string Name, string[] Keywords, string WhatToCheck, string SuggestedChange);

    // Order matters: the first category with a matching keyword wins, so specific causes come before general ones.
    private static readonly Category[] Categories =
    [
        new("Timeout", ["timed out", "timeout", "time out", "deadline exceeded"],
            "Check whether the destination or source is slow or overloaded, and how long the step is allowed to wait.",
            "Raise the timeout on this step only if the other side is known to be slow; otherwise fix the slow side."),
        new("Unresolved configuration", ["unresolved", "config value", "secret reference", "placeholder", "is not configured", "missing setting"],
            "Open Config values and check that every name this flow refers to has a value.",
            "Set the missing config value or secret on the Config values page. No flow change is needed."),
        new("Authentication or permission", ["unauthorized", "forbidden", "access denied", "access to the path", "permission denied", "authentication", "login failed", "invalid credentials", "not authorized", "401", "403"],
            "Check that the account, password, key or token used by this step is still valid and has the rights it needs.",
            "Renew or correct the credential in Config values (or ask the owner of the destination to grant access)."),
        new("Cannot reach host", ["no such host", "could not resolve", "name or service not known", "connection refused", "actively refused", "host is unreachable", "network is unreachable", "connection reset", "no route to host", "name resolution"],
            "Check the host name, port and network path from the machine running Mulse, and that the destination is up.",
            "Correct the host or port setting, or ask the network owner to open the route."),
        new("File locked or in use", ["being used by another process", "sharing violation", "is locked", "file is in use"],
            "Check which other program holds the file open. Files that another process is still writing are a common cause.",
            "Have the writer finish before Mulse reads (for example a rename-when-done convention), or lengthen the file age check on the fetch."),
        new("Missing file or path", ["could not find file", "could not find a part of the path", "no such file", "does not exist", "path not found", "directory not found", "not found", "404"],
            "Check that the file, folder or resource still exists where the setting says, and that Mulse can see that location.",
            "Correct the path or resource setting on this step."),
        new("Disk or quota", ["no space left", "disk full", "not enough space", "quota"],
            "Check free space and quotas on the destination (and on the machine running Mulse).",
            "Free space or raise the quota. No flow change is needed."),
        new("Database error", ["sql", "deadlock", "constraint", "duplicate key", "foreign key", "database"],
            "Check the database's own log for this statement, and that the table and columns still match.",
            "Correct the statement or table settings on this step, or fix the data rule that was violated."),
        new("Invalid input data", ["parse", "parsing", "malformed", "unexpected token", "unexpected character", "schema", "validation", "invalid", "xml", "json", "cannot convert", "could not convert", "format", "encoding"],
            "Look at the input that arrived for this run and compare it with what the parse step expects.",
            "Correct the parse settings (or the schema) so they match the real input, or ask the sender to correct what they send.")
    ];

    private const string NoDetail = "No error detail recorded";
    private const string Unclassified = "Other (not recognised)";
    private const string Ambiguous = "Ambiguous delivery";
    private const string NoStep = "No failing step recorded";
    private const string RunLevelStage = "execution";

    public static FailurePatternReport Analyze(IReadOnlyList<AssistFailureRun> runs, int totalProblemRuns = -1)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var causes = runs.Select(Classify).ToArray();
        var total = Math.Max(runs.Count, causes.Length);

        var patterns = causes
            .GroupBy(item => (item.Category, item.Stage))
            .Select(group =>
            {
                var latest = group.OrderByDescending(item => item.Ticks).First();
                var guide = Guide(group.Key.Category);
                var flows = group.Select(item => item.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                return new FailurePattern(
                    group.Key.Category, group.Key.Stage, group.Count(), (double)group.Count() / total,
                    [.. flows.Take(MaxFlowsListed)], flows.Length,
                    group.GroupBy(item => item.State, StringComparer.Ordinal).OrderBy(state => state.Key, StringComparer.Ordinal).ToDictionary(state => state.Key, state => state.Count(), StringComparer.Ordinal),
                    latest.ExecutionId, latest.Ticks, guide.WhatToCheck, guide.SuggestedChange);
            })
            .OrderByDescending(pattern => pattern.Runs).ThenByDescending(pattern => pattern.MostRecentUtcTicks)
            .ThenBy(pattern => pattern.Category, StringComparer.Ordinal).ThenBy(pattern => pattern.Stage, StringComparer.Ordinal)
            .ToArray();

        string? headline = null;
        if (patterns.Length > 0)
        {
            var top = patterns[0];
            headline = patterns.Length == 1 || top.Runs > patterns[1].Runs
                ? $"One change would clear the most: '{top.Category}' at '{top.Stage}' accounts for {top.Runs} of {total} recent run(s) ({top.Share:P0}) across {top.FlowCount} flow(s). {top.SuggestedChange}"
                : $"No single cause stands out: the largest groups are tied at {top.Runs} of {total} recent run(s).";
        }

        var notes = new List<string>
        {
            "Causes are guessed from keywords in the recorded error text with fixed rules. They are a starting point, not a diagnosis. The text itself is not shown here; open the run in History to read it.",
            "This never retries or resends anything. Held runs and ambiguous deliveries need a person to check the destination."
        };
        if (totalProblemRuns > runs.Count) notes.Add($"Only the {runs.Count} most recent of {totalProblemRuns} problem runs were looked at.");

        return new(total, causes.Length, patterns, headline, notes);
    }

    private sealed record Cause(string ExecutionId, string FlowId, string State, string Category, string Stage, long Ticks);

    private static Cause Classify(AssistFailureRun item)
    {
        var source = item.Source;
        var run = source.Run;
        var events = run.Events ?? [];
        // The journal also writes a run-level 'execution' event that repeats the final state, so a real step's trouble is the better stage to name.
        var stageTrouble = events.LastOrDefault(item => IsTrouble(item) && item.Stage != RunLevelStage);
        var runTrouble = events.LastOrDefault(item => IsTrouble(item) && item.Stage == RunLevelStage);
        var uncertain = (run.Deliveries ?? []).FirstOrDefault(delivery => delivery.State is "Unknown" or "InFlight");
        var held = string.Equals(run.State, "Held", StringComparison.Ordinal);

        string category;
        string stage;
        if (uncertain is not null && (held || stageTrouble is null))
        {
            stage = uncertain.Stage;
            category = Ambiguous;
        }
        else if ((stageTrouble ?? runTrouble) is { } trouble)
        {
            stage = trouble.Stage;
            category = string.IsNullOrWhiteSpace(trouble.Detail) ? NoDetail : Match(trouble.Detail) ?? Unclassified;
        }
        else
        {
            stage = "(whole run)";
            category = NoStep;
        }

        return new(source.ExecutionId, source.FlowId, run.State, category, stage, item.StartedUtcTicks);
    }

    private static string? Match(string text)
        => Categories.FirstOrDefault(category => category.Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))?.Name;

    private static (string WhatToCheck, string SuggestedChange) Guide(string category)
    {
        var known = Categories.FirstOrDefault(item => item.Name == category);
        if (known is not null) return (known.WhatToCheck, known.SuggestedChange);
        return category switch
        {
            Ambiguous => ("Look at the destination yourself to find out whether each uncertain delivery arrived. Mulse will not resend it.",
                "Settle the held deliveries by hand first. Then consider whether the destination can report receipt, so the outcome is no longer uncertain."),
            NoDetail => ("The failing step recorded no error text. Check the service log around the time of the run.",
                "Nothing specific to change yet. Reproduce the failure with the step's own test or a manual run to get a message."),
            NoStep => ("No step reported trouble. The run may have been stopped by a service restart; read its events in History.",
                "Nothing specific to change."),
            _ => ("Read the error text of one of these runs in History to see what it says.",
                "Read one example first; the rules here do not recognise this error.")
        };
    }

    private static bool IsTrouble(AssistRunEventInput item)
        => item.State.Contains("fail", StringComparison.OrdinalIgnoreCase) || item.State.Contains("error", StringComparison.OrdinalIgnoreCase)
            || item.State is "Held" or "Unknown";
}
