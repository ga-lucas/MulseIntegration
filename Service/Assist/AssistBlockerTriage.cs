using Mulse.Modules;
using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// Groups the blockers and warnings of imported flows by cause (M1). The rules are fixed and need no AI provider.
/// It reads only diagnostic codes and severities; diagnostic messages, paths and remediation text are never returned.
/// </summary>
public static class AssistBlockerTriage
{
    public const int MaxFlowsListed = 10;

    public const string NeedsDecision = "needs-decision";
    public const string NeedsSourceInfo = "needs-source-info";
    public const string NeedsStub = "needs-stub";
    public const string NeedsModule = "needs-module";
    public const string Other = "other";

    private sealed record Rule(string Tag, string Title, string WhatItMeans, string WhatToDo);

    private static readonly (string Tag, string Label)[] TagOrder =
    [
        (NeedsSourceInfo, "Needs information from the source system"),
        (NeedsDecision, "Needs a decision"),
        (NeedsStub, "Needs a stub or formula implemented"),
        (NeedsModule, "Needs a module that does not exist yet"),
        (Other, "Not recognised")
    ];

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["BT-ARTIFACT-MISSING"] = new(NeedsSourceInfo, "A listed artifact was not found", "The project lists a file that was not on disk when it was imported.", "Restore the original file and import again."),
        ["BT-BINDING-MISSING"] = new(NeedsSourceInfo, "No binding export supplied", "Without the deployed binding export, transport settings are placeholders.", "Export the bindings from the source system and add them to the import."),
        ["BT-CONFIGURATION-REQUIRED"] = new(NeedsSourceInfo, "Values needed from the source system", "Settings refer to config values or secrets that someone has to supply.", "Enter the values on the Config values page. Secrets are never imported."),
        ["BT-DEPENDENCY-UNRESOLVED"] = new(NeedsSourceInfo, "Referenced project or assembly not supplied", "The project depends on something the import could not find.", "Supply the missing projects or reference paths and import again."),
        ["BT-MAP-NOT-ANALYZED"] = new(NeedsSourceInfo, "A map could not be read", "The map file could not be opened or its format was not recognised.", "Check the map file in the source project and import again."),
        ["BT-ODX-NOT-ANALYZED"] = new(NeedsSourceInfo, "An orchestration could not be read", "Its designer data was missing or unreadable, so nothing is known about its behavior.", "Restore valid designer metadata and import again."),
        ["BT-PIPELINE-NOT-ANALYZED"] = new(NeedsSourceInfo, "A pipeline could not be read", "The pipeline file could not be analysed.", "Check the pipeline file and import again."),
        ["BT-PIPELINE-COMPONENT-METADATA"] = new(NeedsSourceInfo, "Pipeline component details unclear", "A component's identity or properties were missing or ambiguous.", "Look at the component in the source system and decide what it does."),
        ["BT-PIPELINE-INVENTORY-LIMIT"] = new(NeedsSourceInfo, "Pipeline details were shortened", "A pipeline exceeded the saved-report limits.", "Review the original pipeline for what was left out."),
        ["BT-PIPELINE-METADATA-LIMIT"] = new(NeedsSourceInfo, "A pipeline name was shortened", "A component name or property name was truncated.", "Check the original pipeline source."),
        ["BT-RULE-POLICY-MISSING"] = new(NeedsSourceInfo, "Business rule policy export missing", "A called policy's definition was not in any supplied export.", "Export the policy and its vocabularies from the source system and add them to the import."),

        ["BT-BINDING-REVIEW"] = new(NeedsDecision, "Transport settings were inferred", "Ports and settings come from a related binding; ownership and behavior are unverified.", "Review ports, filters, retries and the chosen Mulse modules."),
        ["BT-MAP-NOT-WIRED"] = new(NeedsDecision, "Starter transform not installed", "A starter stylesheet was produced but is not wired into the flow.", "Review it, install it, set up the render step and compare the output."),
        ["BT-NATIVE-TRANSLATION-FAILED"] = new(NeedsDecision, "Native draft could not be generated", "The importer could not produce native steps for this flow.", "Correct the source or rebuild the flow by hand in the designer."),
        ["BT-NATIVE-UNRESOLVED"] = new(NeedsDecision, "Orchestration steps left blocked", "Some orchestration shapes have no native equivalent yet and stay blocked.", "Open each blocked step in the native editor and choose how it should behave."),
        ["BT-ODX-FAILURE-SCOPES"] = new(NeedsDecision, "Transactions and failure handling", "Scope, catch, compensate and terminate behavior was not translated.", "Decide what must succeed together, what is undone and how failures are recovered."),
        ["BT-ODX-LOOPS"] = new(NeedsDecision, "Loops", "Loop bodies and their ordering were not translated.", "Decide the iteration, empty-case, ordering and per-item failure behavior."),
        ["BT-ODX-PORTS"] = new(NeedsDecision, "Sends, receives and calls", "Port operations are listed but not wired to native steps.", "Decide the destination, order and request/reply behavior for each."),
        ["BT-ODX-WAITS"] = new(NeedsDecision, "Waits, parallel branches and correlation", "These need orchestration-specific review.", "Decide timing, durable state and correlation, and test restart and competing arrivals."),
        ["BT-ORCHESTRATION-REVIEW"] = new(NeedsDecision, "Orchestration not fully verified", "Only some decisions were translated; the rest was not verified.", "Compare the draft with the original orchestration step by step."),
        ["BT-PIPELINE-COMPONENT-HINT"] = new(NeedsDecision, "Pipeline component has a likely equivalent", "A component may map to an existing Mulse step.", "Check the suggestion and apply it if it matches."),
        ["BT-PIPELINE-DIRECTION"] = new(NeedsDecision, "Pipeline direction unclear", "Mixed or unknown stages hide whether it receives or sends.", "Decide which side it belongs to."),
        ["BT-PIPELINE-STRUCTURE"] = new(NeedsDecision, "Pipeline has unrecognised content", "The inventory does not show every construct.", "Compare with the original pipeline."),
        ["BT-RULE-POLICY-REVIEW"] = new(NeedsDecision, "Rule policy proposal needs review", "A rule policy was translated and needs a person to confirm it.", "Review the proposal and compare with sample inputs."),
        ["BT-SCHEMA-REVIEW"] = new(NeedsDecision, "Schemas need checking", "Namespaces, references and annotations were not verified.", "Check them with representative sample messages."),
        ["BT-SEMANTICS-UNVERIFIED"] = new(NeedsDecision, "Not yet compared with the original", "Nothing proves the draft behaves like the source.", "Run sample input and compare with output captured from the real system."),

        ["BT-CUSTOM-CODE"] = new(NeedsStub, "Custom code", "Custom assemblies were listed, not translated.", "Implement equivalent behavior as a module or native steps, and test it."),
        ["BT-MAP-SCRIPTS-UNTRANSLATED"] = new(NeedsStub, "Script functoids", "Script blocks in maps were counted, not translated.", "Rewrite each script as a formula or custom function and test it."),
        ["BT-MAP-UNTRANSLATED"] = new(NeedsStub, "Map functoids", "Some map links use functoids with no translation.", "Implement the remaining transformations and check every required mapping."),
        ["BT-ODX-ASSIGNMENTS"] = new(NeedsStub, "Message and variable assignments", "Expressions that build messages were not translated.", "Rewrite each as native message steps or formulas."),
        ["BT-ODX-RULES"] = new(NeedsStub, "Rule policy calls", "Calls to business rule policies were not translated.", "Provide the policies and implement the equivalent rules."),
        ["BT-ORCHESTRATION-STUB"] = new(NeedsStub, "Generated stub module", "A starter module was generated for control flow that was not translated.", "Implement its behavior; a pass-through stub is not an implementation."),
        ["BT-RULE-POLICY-UNSUPPORTED"] = new(NeedsStub, "Rule policy cannot be translated", "No proposal could be produced for the policy.", "Implement the rules by hand."),

        ["BT-ARTIFACT-UNSUPPORTED"] = new(NeedsModule, "Artifact type with no translator", "This kind of artifact has no importer.", "Build a module or native steps for it, or leave it out."),
        ["BT-PIPELINE-UNTRANSLATED"] = new(NeedsModule, "Pipeline components not translated", "Component behavior and property values were not translated.", "Use native steps or a new module for the decoding, parsing or promotion it did.")
    };

    public static BlockerTriageReport Analyze(IEnumerable<PipelineDefinition> flows)
    {
        ArgumentNullException.ThrowIfNull(flows);
        var imported = flows.Where(flow => flow.Migration is not null).ToArray();

        var findings = new List<(string FlowId, string Code, MigrationDiagnosticSeverity Severity)>();
        foreach (var flow in imported)
            foreach (var diagnostic in AllDiagnostics(flow.Migration!).Where(item => item.Severity is MigrationDiagnosticSeverity.Blocking or MigrationDiagnosticSeverity.Review))
                findings.Add((flow.Id, SafeCode(diagnostic.Code), diagnostic.Severity));

        var blockingByFlow = findings.Where(item => item.Severity == MigrationDiagnosticSeverity.Blocking)
            .GroupBy(item => item.FlowId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Code).ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);

        var groups = findings
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .Select(group =>
            {
                var rule = Rules.GetValueOrDefault(group.Key) ?? new Rule(Other, $"Finding {group.Key}", "This kind of finding is not one the triage knows about.", "Open an affected flow and read its migration report.");
                var flowIds = group.Select(item => item.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var blocking = group.Any(item => item.Severity == MigrationDiagnosticSeverity.Blocking);
                var alone = blocking ? flowIds.Count(id => blockingByFlow.TryGetValue(id, out var codes) && codes.Count == 1 && codes.Contains(group.Key)) : 0;
                return new BlockerGroup(group.Key, rule.Tag, blocking ? "Blocker" : "Warning", rule.Title, rule.WhatItMeans, rule.WhatToDo,
                    flowIds.Length, group.Count(), alone, [.. flowIds.Take(MaxFlowsListed)]);
            })
            .OrderByDescending(group => group.Severity == "Blocker")
            .ThenByDescending(group => group.Flows)
            .ThenByDescending(group => group.ClearsFlowsAlone)
            .ThenBy(group => group.Code, StringComparer.Ordinal)
            .ToArray();

        var tags = TagOrder
            .Select(entry => new TagTotal(entry.Tag, entry.Label, groups.Count(group => group.Tag == entry.Tag),
                findings.Where(item => (Rules.GetValueOrDefault(item.Code)?.Tag ?? Other) == entry.Tag).Select(item => item.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .Where(total => total.Groups > 0)
            .ToArray();

        var notes = new List<string>
        {
            "Groups come from the importer's finding codes with fixed rules. Only counts and flow ids are shown, never messages, paths or values.",
            "'Would unblock alone' counts flows whose only blocker is that group. Clearing a group does not make a flow correct, and imported flows still need to be rebuilt and saved before they can run."
        };
        if (imported.Length == 0) notes.Insert(0, "There are no imported flows.");

        return new(imported.Length, blockingByFlow.Count, groups, tags, Headline(groups, blockingByFlow.Count), notes);
    }

    private static string? Headline(BlockerGroup[] groups, int flowsWithBlockers)
    {
        if (flowsWithBlockers == 0) return null;
        var best = groups.Where(group => group.Severity == "Blocker").OrderByDescending(group => group.ClearsFlowsAlone).ThenByDescending(group => group.Flows).First();
        return best.ClearsFlowsAlone > 0
            ? $"{flowsWithBlockers} imported flow(s) have blockers. The largest single step is \"{best.Title}\": {best.ClearsFlowsAlone} flow(s) have no other blocker."
            : $"{flowsWithBlockers} imported flow(s) have blockers, and each has more than one kind. The most common is \"{best.Title}\" ({best.Flows} flow(s)).";
    }

    private static IEnumerable<MigrationDiagnostic> AllDiagnostics(FlowMigrationReport report)
        => report.Diagnostics.Concat(report.Artifacts.SelectMany(artifact => artifact.Diagnostics)).DistinctBy(item => (item.Code, item.SourcePath, item.Message));

    // Unknown codes are shown only when they look like a code, so free text can never reach the response.
    private static string SafeCode(string? code)
        => !string.IsNullOrEmpty(code) && code.Length <= 60 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') ? code : "UNKNOWN";
}
