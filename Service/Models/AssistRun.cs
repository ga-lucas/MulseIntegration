namespace Service.Models;

public sealed record RunDeliveryExplanation(string Stage, string State, string Meaning, bool NeedsPerson);

/// <summary>Facts about one run, worked out from its stored state, events and deliveries. It carries no message content and no error text.</summary>
public sealed record RunExplanation(
    string ExecutionId,
    string FlowId,
    string State,
    string Meaning,
    bool NeedsPerson,
    IReadOnlyList<string> WhatHappened,
    IReadOnlyList<RunDeliveryExplanation> Deliveries,
    IReadOnlyList<string> WhatToCheck,
    string Disclaimer);

/// <summary>A run as read from storage for the assistant. <paramref name="Run"/> may hold error text; it never leaves the server unless it is redacted first.</summary>
public sealed record AssistRunSource(string ExecutionId, string FlowId, AssistRunInput Run);
