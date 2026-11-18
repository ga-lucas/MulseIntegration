using System.Text;
using System.Text.RegularExpressions;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;
using Xunit;

namespace Service.Tests;

/// <summary>
/// Opt-in tool, not a regression test: it imports a .sln/.btproj (ORCH_ROOT may also be a folder: every solution below it, else every project) with native flow generation and lists, per orchestration,
/// how many native nodes were produced and which source shapes stayed blocked and why. It does nothing unless both variables are set.
/// <code>
/// $env:ORCH_ROOT = 'C:\path\to\biztalk\projects'; $env:ORCH_OUT = 'C:\path\to\report.tsv'
/// dotnet test Service.Tests --filter "FullyQualifiedName~ImportOrchestrationReport"
/// </code>
/// The report names real orchestrations and is written outside the repository by the caller, so do not commit it.
/// A grouped summary of block reasons is written next to it as .summary.txt.
/// Optional, semicolon-separated: ORCH_EXTRA (additional source paths such as schema projects), ORCH_BINDINGS, ORCH_RULES (policy exports); ORCH_DERIVE=1 derives native map choices.
/// ORCH_HELPERS (configuration-helper .cs files) and ORCH_HOSTCONFIG (one host .config with the appSettings values) must be supplied together so helper calls resolve to their deployed values.
/// </summary>
public sealed class ImportOrchestrationReportTests
{
    private static string[] List(string variable)
        => (Environment.GetEnvironmentVariable(variable) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public async Task Report()
    {
        var root = Environment.GetEnvironmentVariable("ORCH_ROOT");
        var output = Environment.GetEnvironmentVariable("ORCH_OUT");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(output)) return;
        var rows = new List<(string Project, string Flow, string Status, string Detail)>();
        // A solution lets the importer resolve schema projects that the orchestration project references; a lone project often cannot.
        var sources = File.Exists(root) ? [root] : Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.AllDirectories)).ToArray();
        if (sources.Length == 0) sources = Directory.EnumerateFiles(root, "*.btproj", SearchOption.AllDirectories).ToArray();
        foreach (var project in sources.Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var response = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
                {
                    SourcePath = project, GenerateNativeFlow = true, AdditionalSourcePaths = List("ORCH_EXTRA"), AdditionalBindingPaths = List("ORCH_BINDINGS"),
                    AdditionalRulePaths = List("ORCH_RULES"), DeriveNativeMapChoices = Environment.GetEnvironmentVariable("ORCH_DERIVE") == "1",
                    NativeConfigurationHelperPaths = List("ORCH_HELPERS"), NativeHostConfigurationPath = List("ORCH_HOSTCONFIG").FirstOrDefault()
                }, default);
                foreach (var candidate in response.FlowCandidates)
                {
                    var json = candidate.DraftFlow.Augments.FirstOrDefault(step => step.Settings.ContainsKey("flowJson"))?.Settings["flowJson"];
                    if (json is null) { rows.Add((project, candidate.DraftFlow.Id, "NONE", "no native flow generated")); continue; }
                    var nodes = NativeFlowDefinition.Read(json, allowBlocked: true).AllNodes().ToArray();
                    var blocked = nodes.Where(node => node.Kind == NativeFlowNodeKind.Blocked && !string.IsNullOrEmpty(node.BlockReason)).ToArray();
                    if (blocked.Length == 0) rows.Add((project, candidate.DraftFlow.Id, "CLEAN", nodes.Length + " nodes"));
                    foreach (var node in blocked) rows.Add((project, candidate.DraftFlow.Id, "BLOCKED", (node.Name + ": " + node.BlockReason).ReplaceLineEndings(" ")));
                }
            }
            catch (Exception exception)
            {
                rows.Add((project, "", "ERROR", exception.Message.ReplaceLineEndings(" ")));
            }
        }
        var report = new StringBuilder();
        foreach (var (project, flow, status, detail) in rows) report.Append(project).Append('\t').Append(flow).Append('\t').Append(status).Append('\t').Append(detail).Append('\n');
        File.WriteAllText(output, report.ToString());
        var summary = new StringBuilder();
        summary.Append(string.Join(", ", rows.GroupBy(row => row.Status).OrderBy(group => group.Key).Select(group => $"{group.Key} {group.Count()}"))).Append('\n');
        // Source excerpts after "Source expression:" and quoted names are dropped so equal causes group together.
        foreach (var group in rows.Where(row => row.Status == "BLOCKED")
            .GroupBy(row => Regex.Replace(Regex.Replace(Regex.Replace(row.Detail, @"^[^:]*: ", ""), @"Source expression:.*$", ""), @"'[^']*'|\d+", "X"))
            .OrderByDescending(group => group.Count()))
            summary.Append(group.Count()).Append('\t').Append(group.Key.Length > 240 ? group.Key[..240] : group.Key).Append('\n');
        File.WriteAllText(Path.ChangeExtension(output, ".summary.txt"), summary.ToString());
    }
}
