namespace Mulse.Ui.Models;

public sealed record AiAuthoringSettingsViewModel(
    bool Enabled, string Provider, string Model, string Endpoint, bool CredentialConfigured, int MaxOutputTokens);

public sealed record UpdateAiAuthoringSettingsViewModel(
    bool Enabled, string Provider, string Model, string Endpoint, int MaxOutputTokens,
    string? ApiKey = null, bool ClearApiKey = false);

public sealed record AiAuthoringMessageViewModel(string Role, string Text);

public sealed record AiAuthoringRequestViewModel(
    string Message, string DraftJson, IReadOnlyList<AiAuthoringMessageViewModel> History, bool IncludeEnvironmentSettings = false);

public sealed record AiAuthoringResponseViewModel(
    string Reply, string? ProposalJson, IReadOnlyList<string> ValidationErrors);

public sealed record AiAuthoringTestViewModel(string Message);

public sealed record AiAuthoringModelViewModel(string Id, string Name);

public sealed record FlowAuthoringProposal(string BaseDraftJson, string ProposalJson);
