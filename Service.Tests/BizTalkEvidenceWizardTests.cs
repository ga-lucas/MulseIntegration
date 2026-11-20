using System.IO.Compression;
using System.Text;
using Service.BizTalkImport;
using Service.BizTalkImport.Evidence;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkEvidenceWizardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-wizard-" + Guid.NewGuid().ToString("N"));

    private const string Project = "<Project><PropertyGroup><AssemblyName>Generic</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Process.odx'/></ItemGroup><ItemGroup><ProjectReference Include='Missing.btproj'/></ItemGroup></Project>";
    private const string Process = "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='CallRules'><om:Property Name='PolicyName' Value='Any Policy'/></om:Element></om:MetaModel>";

    private string WriteProject()
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Generic.btproj");
        File.WriteAllText(project, Project);
        File.WriteAllText(Path.Combine(_directory, "Process.odx"), Process);
        return project;
    }

    private static string ZipBase64(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(content);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static BizTalkEvidenceItem Upload(string id, BizTalkEvidenceKind kind, string name, string base64) =>
        new() { Id = id, Kind = kind, Origin = BizTalkEvidenceOrigin.Upload, Name = name, ContentBase64 = base64 };

    private static BizTalkEvidenceItem Text(string id, BizTalkEvidenceKind kind, string text, string? name = null) =>
        new() { Id = id, Kind = kind, Origin = BizTalkEvidenceOrigin.Text, Name = name, Text = text };

    [Fact]
    public async Task AnalysisAsksForBindingsPoliciesAndMissingProjectsWithReasonsAndSteps()
    {
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = WriteProject() }, default);
        Assert.Equal(["bindings", "rule-policies", "unresolved-projects"], result.EvidenceRequests.Select(request => request.Id));
        Assert.All(result.EvidenceRequests, request =>
        {
            Assert.NotEmpty(request.Why);
            Assert.NotEmpty(request.HowToGet);
            Assert.NotEmpty(request.Impact);
        });
        Assert.Contains("Any Policy", Assert.Single(result.EvidenceRequests, request => request.Id == "rule-policies").Impact);
        Assert.Contains("Missing", Assert.Single(result.EvidenceRequests, request => request.Id == "unresolved-projects").Impact);
        Assert.Null(result.Delta);
        Assert.Equal(result.EvidenceRequests.Select(request => request.Id), result.Summary.Requests.Select(request => request.Id));
    }

    [Fact]
    public async Task SuppliedPastedPolicyClearsItsRequestOnlyBecauseTheAnalysisNoLongerReportsTheGap()
    {
        var service = new BizTalkImportService();
        var project = WriteProject();
        var first = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project }, default);
        var second = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = project,
            PreviousSummary = first.Summary,
            Evidence = [Text("policy", BizTalkEvidenceKind.RulePolicy, BizTalkRulePolicyTests.Export(BizTalkRulePolicyTests.Policy(1, 2)), "rules.xml")]
        }, default);
        Assert.DoesNotContain(second.EvidenceRequests, request => request.Id == "rule-policies");
        Assert.Equal(["rule-policies"], second.Delta!.ClearedRequests.Select(request => request.Id));
        Assert.Empty(second.Delta.NewRequests);
        Assert.Equal(["bindings", "unresolved-projects"], second.Delta.StillOpenRequests.Select(request => request.Id));
        Assert.Single(second.RulePolicies);
    }

    [Fact]
    public async Task SuppliedEvidenceThatDoesNotCloseTheGapLeavesTheRequestOpen()
    {
        var service = new BizTalkImportService();
        var project = WriteProject();
        var first = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project }, default);
        var second = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = project,
            PreviousSummary = first.Summary,
            Evidence = [Text("unrelated", BizTalkEvidenceKind.RulePolicy, BizTalkRulePolicyTests.Export(BizTalkRulePolicyTests.Policy(1, 2)).Replace("Any Policy", "Other Policy"), "other.xml")]
        }, default);
        Assert.Contains(second.EvidenceRequests, request => request.Id == "rule-policies");
        Assert.Empty(second.Delta!.ClearedRequests);
    }

    [Fact]
    public async Task SeveralPolicyVersionsAskForTheDeployedOneUntilItIsChosen()
    {
        var service = new BizTalkImportService();
        var project = WriteProject();
        var export = BizTalkRulePolicyTests.Export(BizTalkRulePolicyTests.Policy(1, 2), BizTalkRulePolicyTests.Policy(1, 3));
        var open = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true, Evidence = [Text("policy", BizTalkEvidenceKind.RulePolicy, export, "rules.xml")] }, default);
        var versions = Assert.Single(open.EvidenceRequests, request => request.Id == "policy-versions");
        Assert.Contains("Any Policy", versions.Impact);
        Assert.Empty(versions.Accepts);

        var chosen = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = project, GenerateNativeFlow = true, NativePolicyVersions = new Dictionary<string, string> { ["Any Policy"] = "1.3" },
            Evidence = [Text("policy", BizTalkEvidenceKind.RulePolicy, export, "rules.xml")]
        }, default);
        Assert.DoesNotContain(chosen.EvidenceRequests, request => request.Id == "policy-versions");
    }

    [Fact]
    public async Task UploadedZipOfASourceTreeIsAnalyzedByEvidenceIdAndRemovedAfterwards()
    {
        var before = TemporaryFolders();
        var zip = ZipBase64(("Generic.btproj", Project), ("Process.odx", Process));
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = "evidence:tree", Evidence = [Upload("tree", BizTalkEvidenceKind.Source, "tree.zip", zip)]
        }, default);
        Assert.Single(result.FlowCandidates);
        Assert.Contains(result.EvidenceRequests, request => request.Id == "rule-policies");
        Assert.Equal(before, TemporaryFolders());
    }

    [Fact]
    public async Task NamingEvidenceThatWasNotSuppliedIsANamedError()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => new BizTalkImportService().AnalyzeAsync(
            new AnalyzeBizTalkSolutionRequest { SourcePath = "evidence:missing", Evidence = [Text("other", BizTalkEvidenceKind.Source, "x")] }, default));
        Assert.Contains("evidence:missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeltaReportsClearedNewAndStillOpenRequestsAndChangedBlockersAndChoices()
    {
        var before = new BizTalkAnalysisSummary
        {
            Requests = [new("a", "A"), new("b", "B")],
            OpenBlockersByFlow = new Dictionary<string, int> { ["f1"] = 3, ["f2"] = 1, ["f3"] = 2 },
            DerivedChoiceStatuses = new Dictionary<string, string> { ["m1"] = "NoMatchingMap", ["m2"] = "Converted" }
        };
        var after = new BizTalkAnalysisSummary
        {
            Requests = [new("b", "B"), new("c", "C")],
            OpenBlockersByFlow = new Dictionary<string, int> { ["f1"] = 0, ["f2"] = 1, ["f4"] = 5 },
            DerivedChoiceStatuses = new Dictionary<string, string> { ["m1"] = "Converted", ["m2"] = "Converted", ["m3"] = "AmbiguousMap" }
        };
        var delta = BizTalkEvidenceRules.Compare(before, after);
        Assert.Equal(["a"], delta.ClearedRequests.Select(request => request.Id));
        Assert.Equal(["c"], delta.NewRequests.Select(request => request.Id));
        Assert.Equal(["b"], delta.StillOpenRequests.Select(request => request.Id));
        Assert.Equal([("f1", 3, 0), ("f3", 2, 0), ("f4", 0, 5)], delta.FlowChanges.Select(change => (change.FlowId, change.Before, change.After)));
        Assert.Equal([("m1", "NoMatchingMap", "Converted"), ("m3", (string?)null, "AmbiguousMap")], delta.DerivedChoiceChanges.Select(change => (change.Value, change.Before, change.After)));
    }

    [Fact]
    public void HostConfigurationAndHelperSourceAreUsedOnlyTogether()
    {
        var items = new[] { Text("cfg", BizTalkEvidenceKind.HostConfig, "<configuration />", "host.config") };
        using var materialized = BizTalkEvidenceMaterializer.Create(items);
        var warnings = new List<string>();
        var request = BizTalkEvidenceApplier.Apply(new AnalyzeBizTalkSolutionRequest { SourcePath = "x", GenerateNativeFlow = true }, items, materialized.Paths, warnings);
        Assert.Null(request.NativeHostConfigurationPath);
        Assert.Empty(request.NativeConfigurationHelperPaths);
        Assert.Contains("without any configuration helper source", Assert.Single(warnings), StringComparison.Ordinal);

        var both = new[] { items[0], Text("helper", BizTalkEvidenceKind.HelperSource, "class Helper {}", "Helper.cs") };
        using var together = BizTalkEvidenceMaterializer.Create(both);
        var applied = BizTalkEvidenceApplier.Apply(new AnalyzeBizTalkSolutionRequest { SourcePath = "x", GenerateNativeFlow = true }, both, together.Paths, []);
        Assert.Equal(together.Paths["cfg"], applied.NativeHostConfigurationPath);
        Assert.Equal([together.Paths["helper"]], applied.NativeConfigurationHelperPaths);
    }

    [Fact]
    public void PastedTextAndSingleUploadsAreWrittenUnderSafeNamesAndDeletedOnDispose()
    {
        string root;
        using (var materialized = BizTalkEvidenceMaterializer.Create([
            Text("pasted", BizTalkEvidenceKind.Bindings, "<BindingInfo />", "..\\..\\escape.xml"),
            Upload("single", BizTalkEvidenceKind.HostConfig, "host.config", Convert.ToBase64String(Encoding.UTF8.GetBytes("<configuration />")))]))
        {
            root = materialized.TemporaryRoot!;
            Assert.Equal("escape.xml", Path.GetFileName(materialized.Paths["pasted"]));
            Assert.StartsWith(root, materialized.Paths["pasted"], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("<configuration />", File.ReadAllText(materialized.Paths["single"]));
        }

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ZipExtractionKeepsTheFolderTreeAndIsDeletedOnDispose()
    {
        string root;
        using (var materialized = BizTalkEvidenceMaterializer.Create([Upload("tree", BizTalkEvidenceKind.Source, "tree.zip", ZipBase64(("a/b/one.txt", "1"), ("two.txt", "2")))]))
        {
            root = materialized.TemporaryRoot!;
            Assert.Equal("1", File.ReadAllText(Path.Combine(materialized.Paths["tree"], "a", "b", "one.txt")));
            Assert.Equal("2", File.ReadAllText(Path.Combine(materialized.Paths["tree"], "two.txt")));
        }

        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("../escape.txt", "points outside|parent-relative")]
    [InlineData("a/../../escape.txt", "points outside|parent-relative")]
    [InlineData("/rooted.txt", "absolute")]
    [InlineData("C:/drive.txt", "absolute")]
    [InlineData("CON.txt", "invalid path")]
    [InlineData("inner.zip", "nested archive")]
    [InlineData("deep/inner.nupkg", "nested archive")]
    public void UnsafeZipEntriesAreRejectedByNameAndLeaveNothingBehind(string entry, string expected)
    {
        var before = TemporaryFolders();
        var exception = Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Upload("bad", BizTalkEvidenceKind.Source, "bad.zip", ZipBase64(("fine.txt", "ok"), (entry, "x")))]));
        Assert.Contains(entry, exception.Message, StringComparison.Ordinal);
        Assert.Matches(expected, exception.Message);
        Assert.Equal(before, TemporaryFolders());
    }

    [Fact]
    public void ZipLinkEntriesAreRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var link = archive.CreateEntry("link.txt");
            link.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(link.Open());
            writer.Write("target");
        }

        var exception = Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Upload("link", BizTalkEvidenceKind.Source, "l.zip", Convert.ToBase64String(stream.ToArray()))]));
        Assert.Contains("is a link", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyZipEntriesTooManyItemsBadIdsDuplicateIdsAndBrokenUploadsAreNamedErrors()
    {
        var many = ZipBase64(Enumerable.Range(0, BizTalkEvidenceMaterializer.MaxZipEntries + 1).Select(index => ($"f{index}.txt", "")).ToArray());
        Assert.Contains("more than", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Upload("many", BizTalkEvidenceKind.Source, "m.zip", many)])).Message, StringComparison.Ordinal);
        Assert.Contains("at most", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create(
            Enumerable.Range(0, BizTalkEvidenceMaterializer.MaxItems + 1).Select(index => Text($"t{index}", BizTalkEvidenceKind.Source, "x")).ToArray())).Message, StringComparison.Ordinal);
        foreach (var id in new[] { "", "..", ".hidden", "a/b", "a b", new string('a', 65) })
            Assert.Contains("needs an id", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Text(id, BizTalkEvidenceKind.Source, "x")])).Message, StringComparison.Ordinal);
        Assert.Contains("more than once", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Text("same", BizTalkEvidenceKind.Source, "x"), Text("same", BizTalkEvidenceKind.Source, "y")])).Message, StringComparison.Ordinal);
        Assert.Contains("not valid base64", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Upload("b64", BizTalkEvidenceKind.Source, "x.xml", "***")])).Message, StringComparison.Ordinal);
        Assert.Contains("not a readable zip", Assert.Throws<ArgumentException>(() => BizTalkEvidenceMaterializer.Create([Upload("zip", BizTalkEvidenceKind.Source, "x.zip", Convert.ToBase64String("not a zip"u8.ToArray()))])).Message, StringComparison.Ordinal);
    }

    private static string[] TemporaryFolders() => Directory.GetDirectories(Path.GetTempPath(), "mulse-evidence-*").Order().ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
