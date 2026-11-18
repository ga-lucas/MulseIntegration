using System.Text;
using System.Xml.Linq;
using Mulse.Modules.Decisions;
using Service.BizTalkImport;

namespace Service.Tests;

/// <summary>
/// Opt-in comparison of a converted BizTalk rule policy with results recorded from the real BizTalk rule engine. Skipped unless the variables are set:
/// <code>
/// $env:POLICY_RULES = 'C:\path\policy-export.xml'; $env:POLICY_NAME = 'Policy Name'; $env:POLICY_VERSION = '1.2'
/// $env:POLICY_RESULTS = 'C:\path\policy-results.tsv'; $env:POLICY_OUT = 'C:\path\comparison.tsv'
/// </code>
/// The results file is tab-separated with a header row. Columns ending in <c>_in</c> are the input values for the field of the same name without the suffix
/// (a column named <c>GroupID_in</c> sets <c>GroupID</c>); every other column is a field and holds the value BizTalk produced. Fields the policy assigns as boolean start as false and all others start empty.
/// A row where no native rule matches is reported as NO-MATCH; BizTalk leaves the fact unchanged in that case, so compare its columns with the initial values.
/// The files name real partners and are supplied by the caller, so do not commit them.
/// </summary>
public sealed class PolicyResultsComparisonReportTests
{
    [Fact]
    public void Compare()
    {
        var rulesPath = Environment.GetEnvironmentVariable("POLICY_RULES");
        var results = Environment.GetEnvironmentVariable("POLICY_RESULTS");
        var output = Environment.GetEnvironmentVariable("POLICY_OUT");
        var name = Environment.GetEnvironmentVariable("POLICY_NAME");
        var version = Environment.GetEnvironmentVariable("POLICY_VERSION");
        if (new[] { rulesPath, results, output, name, version }.Any(string.IsNullOrWhiteSpace)) return;
        var response = BizTalkRulePolicyAnalyzer.Analyze(rulesPath!).Where(policy => policy.Name == name && $"{policy.MajorVersion}.{policy.MinorVersion}" == version).ToArray();
        var proposal = Assert.IsType<string>(Assert.Single(response).ProposedPolicyJson);
        var policy = XmlRuleRuntime.ReadPolicy(proposal);
        var booleanFields = policy.Rules.SelectMany(rule => rule.Assignments).Where(field => field.Type == "boolean").Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        var lines = File.ReadAllLines(results!).Where(line => line.Length > 0).ToArray();
        var header = lines[0].Split('\t');
        var report = new StringBuilder("row\tstatus\tdetail\n");
        var counts = new Dictionary<string, int>();
        for (var index = 1; index < lines.Length; index++)
        {
            var cells = lines[index].Split('\t');
            var root = new XElement(XName.Get(policy.RootName, policy.RootNamespace));
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputs = header.Where(column => column.EndsWith("_in", StringComparison.Ordinal)).ToDictionary(column => column[..^3], column => cells[Array.IndexOf(header, column)], StringComparer.Ordinal);
            foreach (var (column, cell) in header.Zip(cells))
            {
                if (column.EndsWith("_in", StringComparison.Ordinal)) continue;
                expected[column] = cell;
                root.Add(new XElement(column, inputs.GetValueOrDefault(column, booleanFields.Contains(column) ? "false" : "")));
            }
            string status, detail;
            try
            {
                var evaluated = XDocument.Parse(XmlRuleRuntime.Evaluate(policy, root.ToString()).OutputXml).Root!;
                var differences = expected.Where(pair => (evaluated.Element(pair.Key)?.Value ?? "") != pair.Value).Select(pair => pair.Key).ToArray();
                (status, detail) = differences.Length == 0 ? ("MATCH", "") : ("MISMATCH", string.Join(",", differences));
            }
            catch (ArgumentException exception) when (exception.Message.StartsWith("No XML rule matched", StringComparison.Ordinal))
            {
                var unchanged = expected.All(pair => (root.Element(pair.Key)?.Value ?? "") == pair.Value);
                (status, detail) = ("NO-MATCH", unchanged ? "BizTalk left the fact unchanged" : "BizTalk changed the fact");
            }
            counts[status] = counts.GetValueOrDefault(status) + 1;
            report.Append(string.Join("|", inputs.Values)).Append('\t').Append(status).Append('\t').Append(detail).Append('\n');
        }
        report.Insert(0, string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}")) + "\n");
        File.WriteAllText(output!, report.ToString());
    }
}
