using System.Text.Json;
using System.Text.RegularExpressions;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.BizTalkImport.Evidence;

/// <summary>
/// The catalog of evidence rules. Each rule reads structured results of an analysis (diagnostic codes, derived choice statuses, reference resolution, blocked flow nodes) and, when it finds a gap,
/// asks for the evidence that could close it. A rule never reads evidence content and never decides that a gap is closed: a request disappears only when a later analysis no longer reports the gap.
/// </summary>
public static partial class BizTalkEvidenceRules
{
    private const int MaxListed = 10;

    [GeneratedRegex(@"called policy '(?<name>.+)'\.$")]
    private static partial Regex MissingPolicyMessage();

    [GeneratedRegex(@"component '(?<name>[^']+)'")]
    private static partial Regex ComponentReason();

    private static readonly HashSet<string> AssumptionRenderModules = new(StringComparer.Ordinal)
        { "flat-file-schema-render", "hl7-schema-render", "hl7-xml-render" };

    /// <summary>Runs every rule over an analysis. <paramref name="request"/> is the request after evidence was applied, so supplied evidence is already reflected.</summary>
    public static IReadOnlyList<BizTalkEvidenceRequest> Build(AnalyzeBizTalkSolutionRequest request, BizTalkSolutionAnalysisResponse analysis)
    {
        var flows = analysis.FlowCandidates.Select(Inspect).ToArray();
        var requests = new List<BizTalkEvidenceRequest>();
        void Add(BizTalkEvidenceRequest? item) { if (item is not null) requests.Add(item); }
        Add(Bindings(analysis, flows));
        Add(RulePolicies(flows));
        Add(PolicyVersions(request, analysis));
        Add(UnresolvedProjects(analysis));
        Add(MapOwners(analysis));
        Add(MapStubs(analysis));
        Add(UnknownComponents(flows));
        Add(RealOutput(analysis, flows));
        Add(HostConfiguration(request, flows));
        return requests;
    }

    public static BizTalkAnalysisSummary Summarize(IReadOnlyList<BizTalkEvidenceRequest> requests, BizTalkSolutionAnalysisResponse analysis) => new()
    {
        Requests = requests.Select(item => new BizTalkSummaryRequest(item.Id, item.Title)).ToArray(),
        OpenBlockersByFlow = analysis.FlowCandidates.Select(Inspect).GroupBy(flow => flow.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(flow => flow.Blockers), StringComparer.OrdinalIgnoreCase),
        DerivedChoiceStatuses = analysis.DerivedMapChoices.GroupBy(choice => choice.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Status, StringComparer.Ordinal)
    };

    /// <summary>Compares the previous summary with this analysis. Requests are matched by id.</summary>
    public static BizTalkAnalysisDelta Compare(BizTalkAnalysisSummary previous, BizTalkAnalysisSummary current)
    {
        var before = previous.Requests.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var after = current.Requests.GroupBy(item => item.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var flowIds = previous.OpenBlockersByFlow.Keys.Concat(current.OpenBlockersByFlow.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var values = previous.DerivedChoiceStatuses.Keys.Concat(current.DerivedChoiceStatuses.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new(
            before.Values.Where(item => !after.ContainsKey(item.Id)).ToArray(),
            after.Values.Where(item => !before.ContainsKey(item.Id)).ToArray(),
            after.Values.Where(item => before.ContainsKey(item.Id)).ToArray(),
            flowIds.Select(id => new BizTalkFlowBlockerChange(id, previous.OpenBlockersByFlow.GetValueOrDefault(id), current.OpenBlockersByFlow.GetValueOrDefault(id)))
                .Where(change => change.Before != change.After).ToArray(),
            values.Select(value => new BizTalkDerivedChoiceChange(value, previous.DerivedChoiceStatuses.GetValueOrDefault(value), current.DerivedChoiceStatuses.GetValueOrDefault(value)))
                .Where(change => change.Before != change.After).ToArray());
    }

    private sealed record FlowFacts(string Id, IReadOnlyList<MigrationDiagnostic> Diagnostics, int Blockers, IReadOnlyList<string> Components, int AssumptionRenders);

    private static FlowFacts Inspect(BizTalkFlowCandidateResponse candidate)
    {
        var diagnostics = candidate.DraftFlow.Migration?.Diagnostics ?? [];
        var blocked = 0;
        var assumptions = 0;
        var components = new SortedSet<string>(StringComparer.Ordinal);
        var json = candidate.DraftFlow.Augments.FirstOrDefault(step => step.Module == NativeFlowDefinition.ModuleId)?.Settings.GetValueOrDefault("flowJson");
        var native = false;
        if (json is not null)
        {
            try
            {
                foreach (var node in NativeFlowDefinition.Read(json, allowBlocked: true).AllNodes())
                {
                    native = true;
                    if (node.Kind == NativeFlowNodeKind.Blocked)
                    {
                        blocked++;
                        foreach (Match match in ComponentReason().Matches(node.BlockReason)) components.Add(match.Groups["name"].Value);
                    }
                    else if (node.Kind == NativeFlowNodeKind.Render && AssumptionRenderModules.Contains(node.Module)) assumptions++;
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            {
                // A flow JSON the reader rejects is reported by the importer itself; it just contributes no facts here.
            }
        }

        if (!native) blocked = diagnostics.Count(diagnostic => diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        return new(candidate.SuggestedFlowId, diagnostics, blocked, [.. components], assumptions);
    }

    private static string List(IEnumerable<string> values)
    {
        var all = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return all.Length <= MaxListed ? string.Join(", ", all) : $"{string.Join(", ", all.Take(MaxListed))} and {all.Length - MaxListed} more";
    }

    private static IReadOnlyList<string> Listed(IEnumerable<string> values)
    {
        var all = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return all.Length <= MaxListed ? all : [.. all.Take(MaxListed), $"and {all.Length - MaxListed} more"];
    }

    private static BizTalkEvidenceRequest? Bindings(BizTalkSolutionAnalysisResponse analysis, FlowFacts[] flows)
    {
        var missing = flows.Where(flow => flow.Diagnostics.Any(diagnostic => diagnostic.Code == "BT-BINDING-MISSING")).Select(flow => flow.Id).ToArray();
        if (analysis.BindingFiles.Count > 0 && missing.Length == 0) return null;
        var why = analysis.BindingFiles.Count == 0
            ? $"No binding files were found. Bindings hold each port's real address, transport and pipeline settings, so without them {flows.Length} flow(s) use placeholders."
            : $"{missing.Length} of {flows.Length} flow(s) match no supplied binding, so they use placeholders instead of the real port settings.";
        return new("bindings", "bindings", "Export the deployed bindings", why,
        [
            "In the BizTalk Administration Console, right-click the application and choose Export > Bindings, or run BTSTask ExportBindings /Destination:bindings.xml /ApplicationName:<application> on the BizTalk server.",
            "Export from the environment you are migrating from. Bindings are per environment.",
            "Upload the file, or a zip if there are several."
        ], [BizTalkEvidenceKind.Bindings], "Certain", Listed(missing),
        "Bindings can hold passwords or connection strings. Remove them first if you prefer; the importer keeps secrets as references to be filled in.");
    }

    private static BizTalkEvidenceRequest? RulePolicies(FlowFacts[] flows)
    {
        var names = flows.SelectMany(flow => flow.Diagnostics).Where(diagnostic => diagnostic.Code == "BT-RULE-POLICY-MISSING")
            .Select(diagnostic => MissingPolicyMessage().Match(diagnostic.Message)).Where(match => match.Success).Select(match => match.Groups["name"].Value).ToArray();
        if (names.Length == 0) return null;
        return new("rule-policies", "rule-policies", "Export the business rule policies",
            $"{names.Distinct().Count()} polic{(names.Distinct().Count() == 1 ? "y is" : "ies are")} called by orchestrations, but no export was supplied: {List(names)}. The call site does not contain the rules.",
        [
            "On the BizTalk server open the Business Rule Engine Deployment Wizard and choose Export Policy/Vocabulary to File.",
            "Select the policy (and its vocabularies), then save the XML file. Export each version you might use.",
            "Upload the file, or a zip if there are several."
        ], [BizTalkEvidenceKind.RulePolicy], "Certain", Listed(names), null);
    }

    private static BizTalkEvidenceRequest? PolicyVersions(AnalyzeBizTalkSolutionRequest request, BizTalkSolutionAnalysisResponse analysis)
    {
        if (!request.GenerateNativeFlow) return null;
        var open = analysis.RulePolicies.GroupBy(policy => policy.Name, StringComparer.Ordinal)
            .Where(group => group.Select(policy => (policy.MajorVersion, policy.MinorVersion)).Distinct().Count() > 1
                && request.NativePolicyVersions?.ContainsKey(group.Key) != true)
            .Select(group => group.Key).ToArray();
        if (open.Length == 0) return null;
        return new("policy-versions", "policy-versions", "Confirm which policy version is deployed",
            $"{open.Length} polic{(open.Length == 1 ? "y has" : "ies have")} more than one exported version and none is chosen: {List(open)}. The importer never guesses the deployed version.",
        [
            "In the Business Rule Composer, open the policy and look at the Deployment column of its versions, or ask whoever deploys it.",
            "Choose the version in the Environment step. There is nothing to upload."
        ], [], "Certain", Listed(open), null);
    }

    private static BizTalkEvidenceRequest? UnresolvedProjects(BizTalkSolutionAnalysisResponse analysis)
    {
        var names = analysis.BizTalkProjects.SelectMany(project => project.References)
            .Where(reference => reference.Resolution is BizTalkReferenceResolution.Unresolved or BizTalkReferenceResolution.Ambiguous)
            .Select(reference => reference.Name).ToArray();
        if (names.Length == 0) return null;
        return new("unresolved-projects", "unresolved-projects", "Add the referenced projects",
            $"{names.Distinct().Count()} referenced project(s) are not in the source you supplied, so schemas, maps or pipelines they own cannot be read: {List(names)}.",
        [
            "Find the projects in source control; they are often in another solution of the same application.",
            "Upload the solution or project folder, as a zip if it has several files."
        ], [BizTalkEvidenceKind.Source], "Certain", Listed(names), "Source can contain secrets in config files. Leave those out; the importer does not need them.");
    }

    private static BizTalkEvidenceRequest? MapOwners(BizTalkSolutionAnalysisResponse analysis)
    {
        var values = analysis.DerivedMapChoices.Where(choice => choice.Status is "NoMatchingMap" or "SchemaUnresolved").Select(choice => choice.Value).ToArray();
        if (values.Length == 0) return null;
        return new("map-owners", "map-owners", "Add the projects that own these maps",
            $"{values.Length} map name(s) named in the rules match no map, or their schemas were not found, so they have no native conversion and stay stopped: {List(values.Select(ShortName))}.",
        [
            "A map name looks like Namespace.MapClass, AssemblyName, Version=..., so the assembly name is the project that owns it.",
            "Find that project in source control and upload it, as a zip if it has several files."
        ], [BizTalkEvidenceKind.Source], "Certain", Listed(values.Select(ShortName)), null);
    }

    private static string ShortName(string value) => value.Split(',')[0].Trim();

    private static BizTalkEvidenceRequest? MapStubs(BizTalkSolutionAnalysisResponse analysis)
    {
        var values = analysis.DerivedMapChoices.Where(choice => choice.Status == "ConvertedWithStubs").Select(choice => choice.Value).ToArray();
        if (values.Length == 0) return null;
        return new("map-stubs", "map-stubs", "Fill in the scripting formulas",
            $"{values.Length} converted map(s) have a scripting step the importer cannot translate, so the send fails closed until each has a formula: {List(values.Select(ShortName))}.",
        [
            "Open the map in the editor and write each formula, or use AI assist if it is enabled.",
            "Optional evidence: the map's compiled stylesheet (in Visual Studio, right-click the .btm and choose Validate Map; it prints the XSLT path). It shows what each script does."
        ], [], "Certain", Listed(values.Select(ShortName)), "A compiled stylesheet can contain business data in literals. Check it before sharing.");
    }

    private static BizTalkEvidenceRequest? UnknownComponents(FlowFacts[] flows)
    {
        var names = flows.SelectMany(flow => flow.Components).ToArray();
        if (names.Length == 0) return null;
        return new("unknown-components", "unknown-components", "Identify the pipeline components that have no native equivalent",
            $"{names.Distinct().Count()} pipeline component(s) are not translated, so every send that uses one stays stopped: {List(names)}.",
        [
            "Check the pipeline in the Administration Console (Send Ports > Configure > Send pipeline) to see whether the component is actually used and how it is configured.",
            "If it is your organization's own component, upload its project source, as a zip if it has several files.",
            "Otherwise replace it with a native step in the flow editor."
        ], [BizTalkEvidenceKind.Source], "Certain", Listed(names), null);
    }

    private static BizTalkEvidenceRequest? RealOutput(BizTalkSolutionAnalysisResponse analysis, FlowFacts[] flows)
    {
        var assumptionFlows = flows.Where(flow => flow.AssumptionRenders > 0).Select(flow => flow.Id).ToArray();
        var choices = CountAssumptionChoices(analysis.EffectiveMapChoicesJson);
        if (assumptionFlows.Length == 0 && choices == 0) return null;
        return new("real-output", "real-output", "Provide samples of what BizTalk really produced",
            $"{assumptionFlows.Length} flow(s) and {choices} shared choice(s) write flat-file or HL7 output whose exact BizTalk format was not verified (escaping, trailing delimiters, line endings). Samples are recorded for later comparison; the importer does not read them yet.",
        [
            "Pick a few real outbound messages that were delivered to the partner, ideally the ones with optional or empty fields.",
            "Take them from the partner drop folder or a send port archive, and note the input message that produced each.",
            "Upload the pair, as a zip if there are several."
        ], [BizTalkEvidenceKind.RealOutput], "Possible", Listed(assumptionFlows), "Samples can contain patient or customer data. Use test or scrambled data.");
    }

    private static int CountAssumptionChoices(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try { return Mulse.Modules.Mapping.RenderDispatchDefinition.Read(json, allowEmpty: true).Count(choice => AssumptionRenderModules.Contains(choice.Module)); }
        catch (ArgumentException) { return 0; }
    }

    private static BizTalkEvidenceRequest? HostConfiguration(AnalyzeBizTalkSolutionRequest request, FlowFacts[] flows)
    {
        if (!request.GenerateNativeFlow || request.NativeHostConfigurationPath is not null) return null;
        var affected = flows.Where(flow => flow.Blockers > 0).Select(flow => flow.Id).ToArray();
        if (affected.Length == 0) return null;
        return new("host-config", "host-config", "Check whether settings come from the host configuration",
            $"{affected.Length} flow(s) still have blocked steps. If a blocked expression reads a setting such as a path, address or limit through a configuration helper, the value lives in the BizTalk host's configuration file, not in the source. The importer cannot tell which blocked steps this applies to.",
        [
            "Open the host instance's configuration file on the BizTalk server: BTSNTSvc64.exe.config for a 64-bit host, in the BizTalk installation folder.",
            "Find the appSettings keys the code reads, and upload that file together with the C# source of the helper class that reads them."
        ], [BizTalkEvidenceKind.HostConfig, BizTalkEvidenceKind.HelperSource], "Possible", Listed(affected),
            "The configuration file can hold connection strings and passwords. Blank them first; the importer pins only the keys it needs.");
    }
}
