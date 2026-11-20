using Service.Models;

namespace Service.Assist;

/// <summary>What kinds of data an assistant request may carry. Each part of each surface belongs to exactly one category.</summary>
public enum AssistCategory
{
    /// <summary>Names of modules and node kinds, counts, setting keys, and difference classes. The default.</summary>
    Structure,
    /// <summary>Expressions, conditions, formulas and block reasons copied from the source system. Sent only on request.</summary>
    SourceExpressions,
    /// <summary>Plain (non-secret) setting values. Sent only on request.</summary>
    SettingValues,
    /// <summary>Message content and sample values. Sent only on request, with a payload preview.</summary>
    MessageData,
    /// <summary>Run records, error text and logs. Sent only on request, after redaction.</summary>
    RunLogs,
    /// <summary>Config values, secrets, keys and tokens. Never sent.</summary>
    ConfigAndSecrets
}

public sealed record AssistContextPart(string Name, AssistCategory Category, int BudgetChars);

public sealed record AssistAction(string Id, string Title, bool Built, bool NeedsAi);

public sealed record AssistSurface(string Id, string Title, IReadOnlyList<AssistContextPart> Context, IReadOnlyList<AssistAction> Actions);

/// <summary>
/// Every place an assistant can appear, what context it can draw on, and which disclosure category each part belongs to.
/// New features declare their context here first, so what can leave the organization is reviewable in one place and tested.
/// </summary>
public static class AssistSurfaces
{
    public static bool CanBeSent(AssistCategory category) => category != AssistCategory.ConfigAndSecrets;

    public static string Describe(AssistCategory category) => category switch
    {
        AssistCategory.Structure => "Names of modules and step kinds, counts, setting names and difference classes. Sent by default when you use an AI action.",
        AssistCategory.SourceExpressions => "Expressions, conditions, formulas and block reasons. Sent only when you choose to include them.",
        AssistCategory.SettingValues => "Setting values that are not secrets. Sent only when you choose to include them.",
        AssistCategory.MessageData => "Message content and sample values. Sent only when you choose to include them, after you see the exact payload.",
        AssistCategory.RunLogs => "Run records and error text. Sent only when you choose to include them, after redaction.",
        _ => "Configuration values, secrets, keys and tokens. Never sent."
    };

    private static AssistContextPart Part(string name, AssistCategory category, int budget) => new(name, category, budget);
    private static AssistAction Action(string id, string title, bool Built, bool NeedsAi) => new(id, title, Built, NeedsAi);

    private static readonly AssistSurface[] Entries =
    [
        new("designer", "Flow designer",
            [Part("Flow structure", AssistCategory.Structure, 150_000), Part("Module catalog", AssistCategory.Structure, 100_000), Part("Setting values", AssistCategory.SettingValues, 150_000)],
            [Action("draft", "Describe a flow and get a draft", Built: true, NeedsAi: true)]),
        new("native-editor", "Native flow editor",
            [Part("Step kinds and counts", AssistCategory.Structure, 20_000), Part("Module and setting names", AssistCategory.Structure, 40_000), Part("Conditions and expressions", AssistCategory.SourceExpressions, 40_000), Part("Setting values", AssistCategory.SettingValues, 40_000)],
            [Action("explain-node", "Explain a step", Built: true, NeedsAi: false), Action("explain-flow", "Explain the whole flow", Built: true, NeedsAi: false), Action("module-prompt", "Copy a prompt to write a missing module (manual copy and paste)", Built: true, NeedsAi: false), Action("explain-ai", "Put the explanation in plain words", Built: true, NeedsAi: true), Action("edit-node", "Change a selected step", Built: false, NeedsAi: true)]),
        new("mapping-designer", "Mapping designer",
            [Part("Field names and types", AssistCategory.Structure, 40_000), Part("Sample values", AssistCategory.MessageData, 40_000)],
            [Action("custom-function", "Copy a prompt for a formula stub (manual copy and paste)", Built: true, NeedsAi: false), Action("field-matches", "Suggest field matches", Built: false, NeedsAi: true)]),
        new("history", "History",
            [Part("Run outcome and failing step", AssistCategory.Structure, 10_000), Part("Error text", AssistCategory.RunLogs, 20_000), Part("Message content", AssistCategory.MessageData, 20_000)],
            [Action("explain-run", "Explain this run", Built: true, NeedsAi: false), Action("explain-run-ai", "Put the run explanation in plain words", Built: true, NeedsAi: true)]),
        new("config-values", "Config values",
            [Part("Config value names and where they are used", AssistCategory.Structure, 20_000), Part("Config values", AssistCategory.ConfigAndSecrets, 0)],
            [Action("explain-config", "Explain a config value", Built: false, NeedsAi: false)]),
        new("modules", "Modules",
            [Part("Module and setting descriptors", AssistCategory.Structure, 100_000)],
            [Action("explain-module", "Explain a module", Built: false, NeedsAi: false)]),
        new("import-report", "Import report",
            [Part("Blocker groups and counts", AssistCategory.Structure, 30_000), Part("Block reasons and source expressions", AssistCategory.SourceExpressions, 40_000)],
            [Action("glossary", "Look up a BizTalk term", Built: true, NeedsAi: false), Action("triage", "Group and rank blockers", Built: false, NeedsAi: false)]),
        new("biztalk-wizard", "BizTalk import wizard",
            [Part("Request text and evidence kinds", AssistCategory.Structure, 20_000), Part("Evidence excerpts", AssistCategory.SourceExpressions, 24_000)],
            [Action("explain-request", "Explain an evidence request", Built: true, NeedsAi: true), Action("check-evidence", "Check what I supplied", Built: true, NeedsAi: false)]),
        new("compare", "Compare with real output",
            [Part("Difference classes, counts and paths", AssistCategory.Structure, 150_000), Part("Expected and actual values", AssistCategory.MessageData, 0)],
            [Action("explain-differences", "Explain the differences", Built: true, NeedsAi: true)])
    ];

    public static IReadOnlyList<AssistSurface> All => Entries;

    public static AssistSurface? Find(string id) => Entries.FirstOrDefault(surface => string.Equals(surface.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AssistSurfacesResponse Describe() => new(
        [.. Enum.GetValues<AssistCategory>().Select(category => new AssistCategoryInfo(category.ToString(), Describe(category), CanBeSent(category)))],
        [.. Entries.Select(surface => new AssistSurfaceInfo(surface.Id, surface.Title,
            [.. surface.Context.Select(part => new AssistContextPartInfo(part.Name, part.Category.ToString(), part.BudgetChars))],
            [.. surface.Actions.Select(action => new AssistActionInfo(action.Id, action.Title, action.Built, action.NeedsAi))]))]);
}
