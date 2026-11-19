namespace Mulse.Ui.Models;

public sealed record NativeFlowTestPayloadViewModel(string Name, string Content, string ContentType, Dictionary<string, string> Metadata);
public sealed record NativeFlowTestMockViewModel(string Kind, string Module, NativeFlowTestPayloadViewModel? Output);
public sealed record NativeFlowTestRequestViewModel(string FlowJson, NativeFlowTestPayloadViewModel Input, List<NativeFlowTestMockViewModel> Mocks, bool AdvanceWaits = false);
public sealed record NativeFlowTestCallViewModel(string Kind, string Module, List<NativeFlowTestPayloadViewModel> Input);
public sealed record NativeFlowTestResponseViewModel(string Status, string? SuspensionReason, List<NativeFlowTestPayloadViewModel> Output,
    List<NativeFlowTestPayloadViewModel> Replies, List<NativeFlowTestCallViewModel> Calls, int WaitResumes = 0);

public sealed record OutputDifferenceViewModel(string Class, string Path, string? Expected, string? Actual, bool Cosmetic, string Explanation);
public sealed record NativeFlowComparePairViewModel(string Name, string Expected, NativeFlowTestPayloadViewModel Input, List<NativeFlowTestMockViewModel> Mocks, string? DeliverModule = null);
public sealed record NativeFlowCompareRequestViewModel(string FlowJson, List<NativeFlowComparePairViewModel> Pairs, bool AdvanceWaits = false);
public sealed record NativeFlowComparePairResultViewModel(string Name, string Verdict, string Source, List<OutputDifferenceViewModel> Differences, bool Truncated, string? Error);
public sealed record NativeFlowCompareSummaryViewModel(int Total, int Identical, int FormattingOnly, int Different, int NotCompared, string Statement);
public sealed record NativeFlowCompareResponseViewModel(List<NativeFlowComparePairResultViewModel> Results, NativeFlowCompareSummaryViewModel Summary);
