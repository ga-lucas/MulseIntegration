using Service.Models;

namespace Service.Assist;

/// <summary>
/// Plain-language facts about a stored run. It reads only states, stage names and counts, so it needs no AI provider and never repeats error text or message content.
/// It explains what Mulse did and did not do; it never offers to retry, resend or reconcile anything.
/// </summary>
public static class AssistRunExplainer
{
    private const int MaxTimeline = 12;

    public const string Disclaimer = "These are facts read from the stored run. Mulse does not resend or retry on your behalf from here. Only a person who has checked the destination can confirm a delivery or close a held run.";

    public static RunExplanation Explain(AssistRunSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var run = source.Run;
        var events = run.Events ?? [];
        var deliveries = (run.Deliveries ?? []).Select(ExplainDelivery).ToArray();
        var (meaning, needsPerson) = StateMeaning(run.State);
        needsPerson |= deliveries.Any(delivery => delivery.NeedsPerson);

        var happened = new List<string>();
        if (events.Count == 0) happened.Add("No events were recorded for this run.");
        else
        {
            happened.Add($"{events.Count} event(s) were recorded. The last was '{events[^1].Stage}' with state '{events[^1].State}'.");
            var trouble = events.Where(IsTrouble).ToArray();
            if (trouble.Length > 0)
                happened.Add($"{trouble.Length} event(s) look like trouble: " + string.Join(", ", trouble.Take(MaxTimeline).Select(item => $"'{item.Stage}' ({item.State})")) + (trouble.Length > MaxTimeline ? ", and more." : "."));
            var withText = events.Count(item => !string.IsNullOrWhiteSpace(item.Detail));
            if (withText > 0) happened.Add($"{withText} event(s) carry detail text. It is not repeated here; read it in the Events table.");
        }
        if (deliveries.Length > 0)
            happened.Add($"{deliveries.Length} delivery record(s): " + string.Join(", ", deliveries.GroupBy(delivery => delivery.State).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Count()} {group.Key}")) + ".");

        var check = new List<string>();
        var unknown = deliveries.Count(delivery => delivery.State == "Unknown");
        var inFlight = deliveries.Count(delivery => delivery.State == "InFlight");
        if (unknown + inFlight > 0)
            check.Add($"Start with the {unknown + inFlight} delivery(ies) in state Unknown or InFlight. Look at the destination yourself (for example the folder, queue, endpoint or database it sends to) and find out whether each one arrived.");
        if (unknown > 0)
            check.Add("If it did arrive, use 'Confirm independently verified delivery' on that delivery. That only records your finding; it does not send anything. If it did not arrive, Mulse does not resend it from here: decide with the owner of the destination how to get it sent.");
        if (string.Equals(run.State, "Held", StringComparison.Ordinal))
            check.Add("When every uncertain delivery is settled, either let the run continue (after you have confirmed what arrived) or use 'Close held execution without replay' if the input should be abandoned. Closing does not undo anything that already happened.");
        if (string.Equals(run.State, "Failed", StringComparison.Ordinal))
            check.Add("Read the failing event's detail in the Events table. Fix the cause in the flow or its settings, then start a new run; this run is not retried automatically.");
        if (string.Equals(run.State, "Retrying", StringComparison.Ordinal))
            check.Add("Nothing is needed unless it keeps retrying. If it does, check that the destination is reachable and that the credentials still work.");
        if (check.Count == 0 && !needsPerson) check.Add("Nothing to do for this run.");

        return new(source.ExecutionId, source.FlowId, run.State, meaning, needsPerson, happened, deliveries, check, Disclaimer);
    }

    private static (string Meaning, bool NeedsPerson) StateMeaning(string state) => state switch
    {
        "Running" => ("The run is in progress, or it was when the service stopped. When the service starts again, Mulse moves such runs to Held or Retrying and does not repeat anything uncertain.", false),
        "Retrying" => ("A step failed and the flow's retry settings call for another attempt. Mulse is waiting to make it. It only repeats work that cannot duplicate an effect.", false),
        "Held" => ("Mulse stopped this run on purpose. It cannot tell whether repeating a step would duplicate an effect (for example a send whose result was never recorded), so it is waiting for a person.", true),
        "Completed" => ("The run finished and every step succeeded.", false),
        "Failed" => ("The run ended with an error and is not retried automatically.", false),
        "Closed" => ("A person closed this held run without replaying it. Its input was abandoned. Effects that had already happened were not undone.", false),
        _ => ("This state is not one the assistant recognises, so it cannot say what it means.", false)
    };

    private static RunDeliveryExplanation ExplainDelivery(AssistRunDeliveryInput delivery) => delivery.State switch
    {
        "Prepared" => new(delivery.Stage, delivery.State, "Recorded as about to be sent, but the send had not started. Nothing is recorded as having left Mulse.", false),
        "InFlight" => new(delivery.Stage, delivery.State, "The send had started and no result was recorded. If the service stopped meanwhile, Mulse treats it as Unknown.", true),
        "Unknown" => new(delivery.Stage, delivery.State, "It may or may not have reached its destination. Mulse will not resend it by itself, because that could duplicate it. Check the destination by hand.", true),
        "Succeeded" => new(delivery.Stage, delivery.State, "The destination accepted it and Mulse recorded the success.", false),
        _ => new(delivery.Stage, delivery.State, "This delivery state is not one the assistant recognises.", false)
    };

    private static bool IsTrouble(AssistRunEventInput item)
        => item.State.Contains("fail", StringComparison.OrdinalIgnoreCase) || item.State.Contains("error", StringComparison.OrdinalIgnoreCase)
            || item.State is "Held" or "Unknown";
}
