namespace Service.Models;

/// <summary>A problem run as read for pattern grouping, with when it started.</summary>
public sealed record AssistFailureRun(AssistRunSource Source, long StartedUtcTicks);

/// <summary>One recurring cause of trouble. It names a category and a stage; it never carries error text, message content or setting values.</summary>
public sealed record FailurePattern(
    string Category,
    string Stage,
    int Runs,
    double Share,
    IReadOnlyList<string> FlowIds,
    int FlowCount,
    IReadOnlyDictionary<string, int> States,
    string MostRecentExecutionId,
    long MostRecentUtcTicks,
    string WhatToCheck,
    string SuggestedChange);

/// <summary>Recent runs that failed, are held or are retrying, grouped by likely cause. The grouping is rules in code and best effort; the list is never a diagnosis.</summary>
public sealed record FailurePatternReport(
    int RunsConsidered,
    int RunsGrouped,
    IReadOnlyList<FailurePattern> Patterns,
    string? Headline,
    IReadOnlyList<string> Notes);
