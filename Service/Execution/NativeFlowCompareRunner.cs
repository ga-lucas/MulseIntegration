using Service.Models;

namespace Service.Execution;

/// <summary>Compares supplied real-output samples with Mulse's output, either supplied as text or produced by the isolated native test. Nothing real is invoked.</summary>
public static class NativeFlowCompareRunner
{
    public const int MaxPairs = 20;

    public static async Task<NativeFlowCompareResponse> RunAsync(NativeFlowCompareRequest request, CancellationToken cancellationToken)
    {
        if (request?.Pairs is not { Count: > 0 and <= MaxPairs })
            throw new ArgumentException($"Supply between 1 and {MaxPairs} samples to compare.");
        if (request.Pairs.Any(pair => pair is null || string.IsNullOrWhiteSpace(pair.Name) || pair.Name.Length > 128 || pair.Expected is null
            || pair.PayloadIndex is < 0 or > 63 || pair.DeliverModule is { Length: > 256 }))
            throw new ArgumentException("Each sample needs a name (up to 128 characters), the expected output and a valid payload index.");
        if (request.Pairs.Select(pair => pair.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Pairs.Count)
            throw new ArgumentException("Sample names must be unique.");
        if (request.Pairs.Any(pair => pair.Actual is null && pair.Input is null))
            throw new ArgumentException("Each sample needs either Mulse's output (Actual) or an input to run.");
        if (request.Pairs.Any(pair => pair.Actual is null) && string.IsNullOrWhiteSpace(request.FlowJson))
            throw new ArgumentException("A native definition is required to run a sample.");

        var results = new List<NativeFlowComparePairResult>();
        foreach (var pair in request.Pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = pair.Actual is null ? "Run" : "Supplied";
            string? actual = pair.Actual;
            string? error = null;
            if (actual is null)
            {
                try { actual = await RunPairAsync(request, pair, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
                { error = exception.Message; }
            }
            if (actual is null)
            {
                results.Add(new(pair.Name, OutputVerdicts.NotCompared, source, [], false, error ?? "Mulse produced no output to compare."));
                continue;
            }
            try
            {
                var comparison = OutputComparer.Compare(pair.Expected, actual);
                results.Add(new(pair.Name, comparison.Verdict, source, comparison.Differences, comparison.Truncated, null));
            }
            catch (ArgumentException exception)
            {
                results.Add(new(pair.Name, OutputVerdicts.NotCompared, source, [], false, exception.Message));
            }
        }
        return new(results, Summarize(results));
    }

    private static async Task<string?> RunPairAsync(NativeFlowCompareRequest request, NativeFlowComparePair pair, CancellationToken cancellationToken)
    {
        var response = await NativeFlowTestRunner.RunAsync(new(request.FlowJson!, pair.Input!, pair.Mocks ?? [], request.AdvanceWaits), cancellationToken).ConfigureAwait(false);
        var deliveries = response.Calls.Where(call => call.Kind == "Deliver" && (pair.DeliverModule is null || call.Module == pair.DeliverModule)).ToList();
        if (pair.DeliverModule is not null && deliveries.Count == 0)
            throw new InvalidOperationException($"The test run never delivered through '{pair.DeliverModule}'. Status: {response.Status}.");
        var payloads = deliveries.Count > 0 ? deliveries[^1].Input : response.Output;
        if (payloads.Count == 0) throw new InvalidOperationException($"The test run produced nothing to compare. Status: {response.Status}.");
        if (pair.PayloadIndex >= payloads.Count) throw new InvalidOperationException($"The test run produced {payloads.Count} payload(s); index {pair.PayloadIndex} does not exist.");
        return payloads[pair.PayloadIndex].Content;
    }

    internal static NativeFlowCompareSummary Summarize(IReadOnlyList<NativeFlowComparePairResult> results)
    {
        int Count(string verdict) => results.Count(result => result.Verdict == verdict);
        var identical = Count(OutputVerdicts.Identical);
        var formatting = Count(OutputVerdicts.FormattingOnly);
        var different = Count(OutputVerdicts.Different);
        var notCompared = Count(OutputVerdicts.NotCompared);
        var compared = results.Count - notCompared;
        var statement = compared == 0
            ? "No sample was compared."
            : $"Matches {identical + formatting} of {compared} compared sample{(compared == 1 ? "" : "s")} ({identical} exactly, {formatting} apart from formatting); {different} differ{(different == 1 ? "s" : "")}."
              + (notCompared > 0 ? $" {notCompared} could not be compared." : "")
              + " This is evidence from the supplied samples, not proof that the flows are equivalent.";
        return new(results.Count, identical, formatting, different, notCompared, statement);
    }
}
