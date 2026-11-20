namespace Mulse.Ui.Models;

/// <summary>Evidence the user supplied in an evidence card. Path origin carries a path, Text carries text, Upload carries bytes.</summary>
public sealed record BizTalkEvidenceAdd(string Kind, string Origin, string? Name, string? Path, string? Text, byte[]? Bytes);

public sealed record BizTalkRequestAnswer(string Answer, string? Note);

public enum BizTalkWizardStage
{
    Source,
    Environment,
    RulesAndMaps,
    Behavior,
    Review
}

/// <summary>Groups the importer's evidence requests into the wizard's stages. A request id this build does not know is shown in Review so it is never hidden.</summary>
public static class BizTalkWizardStages
{
    public static IReadOnlyList<BizTalkWizardStage> All { get; } = [.. Enum.GetValues<BizTalkWizardStage>()];

    public static string Title(BizTalkWizardStage stage) => stage switch
    {
        BizTalkWizardStage.Source => "1. Source",
        BizTalkWizardStage.Environment => "2. Environment",
        BizTalkWizardStage.RulesAndMaps => "3. Rules and maps",
        BizTalkWizardStage.Behavior => "4. Evidence of behavior",
        _ => "5. Review and import"
    };

    public static string Purpose(BizTalkWizardStage stage) => stage switch
    {
        BizTalkWizardStage.Source => "Tell the importer where the BizTalk source is. A solution, project or folder on the service machine, or a zip you upload.",
        BizTalkWizardStage.Environment => "Deployment facts that are not in source: port bindings and host configuration values.",
        BizTalkWizardStage.RulesAndMaps => "Business rule exports, the policy versions to follow, and the maps that rules or orchestrations select at run time.",
        BizTalkWizardStage.Behavior => "Components the importer cannot read, and samples of what the real system produced. Samples are recorded with the session and compared in the flow's native test panel (\"Compare with real output\").",
        _ => "What changed since the last analysis, what is still open, and the flows to create or update."
    };

    public static BizTalkWizardStage StageOf(string requestId) => requestId switch
    {
        "bindings" or "host-config" => BizTalkWizardStage.Environment,
        "rule-policies" or "policy-versions" or "unresolved-projects" or "map-owners" or "map-stubs" => BizTalkWizardStage.RulesAndMaps,
        "unknown-components" or "real-output" => BizTalkWizardStage.Behavior,
        _ => BizTalkWizardStage.Review
    };
}
