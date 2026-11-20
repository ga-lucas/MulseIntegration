namespace Service.Models;

/// <summary>One classified difference. Cosmetic means no data value changes (declaration, whitespace, namespace prefix, attribute or empty-element form).</summary>
public sealed record OutputDifference(string Class, string Path, string? Expected, string? Actual, bool Cosmetic, string Explanation);

public static class OutputVerdicts
{
    public const string Identical = "Identical";
    public const string FormattingOnly = "FormattingOnly";
    public const string Different = "Different";
    public const string NotCompared = "NotCompared";
}

public sealed record OutputComparison(string Verdict, List<OutputDifference> Differences, bool Truncated);

/// <summary>One real-output sample. Supply Actual to compare text you already have, or Input (and mocks) to run the isolated native test and take a delivery's input.</summary>
public sealed record NativeFlowComparePair(string Name, string Expected, string? Actual = null, NativeFlowTestPayload? Input = null,
    List<NativeFlowTestMock>? Mocks = null, string? DeliverModule = null, int PayloadIndex = 0);
public sealed record NativeFlowCompareRequest(string? FlowJson, List<NativeFlowComparePair> Pairs, bool AdvanceWaits = false);
public sealed record NativeFlowComparePairResult(string Name, string Verdict, string Source, List<OutputDifference> Differences, bool Truncated, string? Error);
public sealed record NativeFlowCompareSummary(int Total, int Identical, int FormattingOnly, int Different, int NotCompared, string Statement);
public sealed record NativeFlowCompareResponse(List<NativeFlowComparePairResult> Results, NativeFlowCompareSummary Summary);
