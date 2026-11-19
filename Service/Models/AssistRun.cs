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

/// <summary>Ask for a run to be put in plain words. The run is read on the server by the id in the route; the server rebuilds the redacted payload and refuses to send unless its hash matches what the user approved.</summary>
public sealed record RunWordingRequest(string? Question = null, bool IncludeErrorText = false, string? DisclosureSha256 = null);

/// <summary>Exactly what a run wording request would send. Nothing has been sent when this is returned.</summary>
public sealed record RunWordingDisclosure(
    bool AiAvailable,
    string? Unavailable,
    string Provider,
    string Model,
    string Payload,
    string PayloadSha256,
    bool IncludesErrorText,
    IReadOnlyList<string> Notes);
