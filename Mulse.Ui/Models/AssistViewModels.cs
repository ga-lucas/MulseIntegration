namespace Mulse.Ui.Models;

public sealed record AssistNodeKindViewModel(string Kind, string Title, string Summary, string Effect, string SideEffects, List<string> KeySettings);
public sealed record AssistGlossaryEntryViewModel(string Term, string Meaning, string InMulse, List<string> Aliases);
public sealed record AssistStepViewModel(int Depth, string Id, string Kind, string Title, string? Module, string? ModuleName, string Summary, List<string> Notes);
public sealed record AssistFindingViewModel(string Severity, string Text, string? NodeId);
public sealed record FlowExplanationViewModel(bool Durable, int NodeCount, Dictionary<string, int> KindCounts, List<AssistStepViewModel> Steps,
    List<string> Effects, List<AssistFindingViewModel> Findings, string Disclaimer, List<AssistMissingModuleViewModel>? MissingModules = null);
public sealed record AssistMissingModuleViewModel(string Module, string Kind, List<string> NodeIds);
public sealed record ModulePromptViewModel(string Module, string Kind, List<string> NodeIds, List<string> SettingKeys, List<string> UnloadedPackages, string Prompt, string Disclaimer);
public sealed record AssistSettingStateViewModel(string Key, string Label, string Description, bool Required, string State);
public sealed record NodeExplanationViewModel(AssistStepViewModel Step, AssistNodeKindViewModel KindInfo, string? ModuleDescription,
    List<AssistSettingStateViewModel> Settings, List<string> Facts, List<AssistFindingViewModel> Findings);
