using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

public sealed record CreateImportedBizTalkFlowRequestViewModel(
    string SourcePath,
    string SuggestedFlowId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalSourcePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalBindingPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AdditionalRulePaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool GenerateNativeFlow = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? NativePolicyVersions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeHostConfigurationPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? NativeConfigurationHelperPaths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NativeMapChoicesJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DeriveNativeMapChoices = false)
{
    /// <summary>Must match the evidence sent to analyze so the same candidate is reproduced.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<BizTalkEvidenceItemViewModel>? Evidence { get; init; }

    /// <summary>Saves the import under this id instead of the suggested one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NewFlowId { get; init; }
}
