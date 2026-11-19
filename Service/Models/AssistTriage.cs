namespace Service.Models;

/// <summary>One kind of finding across the imported flows, with how many flows it touches and which of them it alone would unblock.</summary>
public sealed record BlockerGroup(
    string Code,
    string Tag,
    string Severity,
    string Title,
    string WhatItMeans,
    string WhatToDo,
    int Flows,
    int Occurrences,
    int ClearsFlowsAlone,
    IReadOnlyList<string> FlowIds);

public sealed record TagTotal(string Tag, string Label, int Groups, int Flows);

public sealed record BlockerTriageReport(
    int ImportedFlows,
    int FlowsWithBlockers,
    IReadOnlyList<BlockerGroup> Groups,
    IReadOnlyList<TagTotal> Tags,
    string? Headline,
    IReadOnlyList<string> Notes);
