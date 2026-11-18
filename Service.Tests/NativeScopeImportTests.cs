using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeScopeImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-scope-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
    private static XElement Assign(string name, string expression) => N("VariableAssignment", name, P("Expression", expression));
    private static XElement Catch(string expression, string type = "System.Exception") =>
        N("Catch", "Handle", P("ExceptionName", "ex"), P("ExceptionType", type), P("IsFaultMessage", "False"), Assign("Gather", expression));

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(params XElement[] body)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"),
            N("VariableDeclaration", "result", P("Type", "System.String"), P("InitialValue", "\"\"")),
            N("VariableDeclaration", "failed", P("Type", "System.Boolean"), P("InitialValue", "false")),
            N("ServiceBody", "Body", new object[] { N("Receive", "input", P("MessageName", "request"), P("PortName", "input")) }.Concat(body).ToArray())));
        var project = Path.Combine(_directory, "Scopes.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Scopes</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Scopes.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Scopes.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Fact]
    public async Task CatchScopeBecomesNativeTryWithExceptionVariablesAndHonestWarnings()
    {
        var (candidate, definition) = await Import(N("Scope", "Work",
            N("LongRunningTransaction", "Tx"), N("TransactionAttribute", "Attr", P("Retry", "True"), P("Timeout", "60")),
            Assign("Run", "result = \"ok\";"),
            Catch("failed = true; result = \"Exception occurred: \" + ex.Message + \", \" + ex.StackTrace;")));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind == NativeFlowNodeKind.Blocked);
        var attempt = Assert.Single(all, node => node.Kind == NativeFlowNodeKind.Try);
        Assert.Equal("ex_Message", attempt.ExceptionMessageVariable); Assert.Equal("ex_StackTrace", attempt.ExceptionDetailsVariable);
        Assert.Contains(attempt.Children, node => node.Name.StartsWith("LongRunningTransaction (not enforced)", StringComparison.Ordinal));
        Assert.Contains(attempt.Children, node => node.Name.Contains("Retry=True; Timeout=60", StringComparison.Ordinal));
        var handler = Assert.Single(attempt.Otherwise);
        var sets = handler.Children.SelectMany(child => child.Children.Prepend(child)).Where(node => node.Kind == NativeFlowNodeKind.SetVariable).ToArray();
        Assert.Equal(2, sets.Length);
        Assert.Contains(sets, node => node.Expression!.ToString()!.Length > 0 && JsonContains(node, "ex_Message") && JsonContains(node, "ex_StackTrace"));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("transaction declarations", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not a CLR stack trace", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("Terminate, failed Deliver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TimeSpanAndTypeVariablesStayBlockedButNameTheMulseReplacement()
    {
        var (_, definition) = await Import(N("VariableDeclaration", "window", P("Type", "System.TimeSpan")), N("VariableDeclaration", "mapType", P("Type", "System.Type")),
            N("VariableDeclaration", "label", P("Type", "System.Decimal")));
        var reasons = definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Blocked).Select(node => node.BlockReason).ToArray();
        Assert.Contains(reasons, reason => reason.Contains("durable-batch-augment", StringComparison.Ordinal));
        Assert.Contains(reasons, reason => reason.Contains("Derive native map choices", StringComparison.Ordinal));
        Assert.Single(reasons, reason => !reason.Contains("durable-batch-augment", StringComparison.Ordinal) && !reason.Contains("Derive native map choices", StringComparison.Ordinal)
            && reason.StartsWith("Only unique service-level", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatementsUsingTimeSpanStayBlockedWithADateTimeReasonNotASchemaFieldReason()
    {
        var (_, definition) = await Import(N("VariableDeclaration", "windowEnd", P("Type", "System.TimeSpan")),
            Assign("Fresh", "windowEnd = System.TimeSpan.Zero;"), Assign("Hour", "result = windowEnd.Hours;"),
            N("While", "Window", P("Expression", "windowEnd.TotalSeconds > 0")));
        var reasons = definition.AllNodes().Where(node => node.Kind == NativeFlowNodeKind.Blocked && node.Name is "Fresh" or "Hour" or "Window").Select(node => node.BlockReason).ToArray();
        Assert.Equal(3, reasons.Length);
        Assert.All(reasons, reason => { Assert.Contains("date and time", reason, StringComparison.Ordinal); Assert.Contains("durable-batch-augment", reason, StringComparison.Ordinal); });
        Assert.DoesNotContain(reasons, reason => reason.Contains("distinguished", StringComparison.Ordinal));
    }

    private static bool Fail(string message) => throw new Xunit.Sdk.XunitException(message);

    [Fact]
    public async Task BatchWindowStyleDateTimeStatementsImportNativelyAsUtcWithWarnings()
    {
        var (candidate, definition) = await Import(N("Scope", "Window",
            N("VariableDeclaration", "dtEnd", P("Type", "System.DateTime")), N("VariableDeclaration", "dtStart", P("Type", "System.DateTime")),
            N("VariableDeclaration", "count", P("Type", "System.Int32")),
            Assign("Parse", "dtEnd = System.DateTime.Parse(result);"),
            Assign("Build", "dtEnd = new System.DateTime(dtEnd.Year, dtEnd.Month, dtEnd.Day, dtEnd.Hour, dtEnd.Minute, 0);"),
            Assign("Shift", "dtEnd = dtEnd.AddDays(count); dtStart = System.DateTime.Now; dtStart = dtEnd;"),
            Assign("Check", "if (!System.Int32.TryParse(result, out count)) { count = 1; } if (System.DateTime.Compare(dtEnd, dtStart) >= 0 && dtEnd > dtStart) { failed = true; }"),
            N("Delay", "Pause", P("Timeout", "dtEnd.Subtract(System.DateTime.Now)"))));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind == NativeFlowNodeKind.Blocked && Fail(node.Name + ": " + node.BlockReason));
        Assert.Equal(2, all.Count(node => node.Kind == NativeFlowNodeKind.DeclareVariable && node.VariableType == NativeScalarType.DateTime));
        var minimum = all.First(node => node.Kind == NativeFlowNodeKind.DeclareVariable && node.Variable == "dtEnd").Expression!.Literal!;
        Assert.Equal(NativeScalarType.DateTime, minimum.Type); Assert.Equal(DateTime.MinValue, minimum.Read());
        var parse = Assert.Single(all, node => node.Expression?.Kind == NativeExpressionKind.ParseInt32OrZero);
        Assert.Equal("count", parse.Variable);
        var check = Assert.Single(all, node => node.Kind == NativeFlowNodeKind.If && node.Expression!.Kind == NativeExpressionKind.Not);
        Assert.Equal(NativeExpressionKind.IsInt32, check.Expression!.Operands[0].Kind);
        Assert.Equal(NativeExpressionKind.DateTimeFromParts, all.Single(node => node.Name == "Build").Children.Single().Expression!.Kind);
        var wait = Assert.Single(all, node => node.Kind == NativeFlowNodeKind.Wait);
        Assert.Equal(0, wait.DurationSeconds); Assert.Equal(NativeExpressionKind.SecondsBetween, wait.DurationExpression!.Kind);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("imported as UTC", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("truncated toward zero", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dtEnd = dtEnd + 1;")]
    [InlineData("dtEnd = dtEnd - dtStart;")]
    [InlineData("result = dtEnd.ToString();")]
    [InlineData("dtEnd = System.DateTime.Parse(result, null);")]
    [InlineData("dtEnd = new System.DateTime(2000, 1, 1);")]
    [InlineData("dtEnd = dtEnd.AddDays(result);")]
    [InlineData("if (System.Int32.TryParse(result, out count) && count > 0) { failed = true; }")]
    [InlineData("failed = dtEnd == 5;")]
    public async Task UnprovenDateTimeAndTryParseFormsStayBlocked(string statement)
    {
        var (_, definition) = await Import(N("Scope", "Window", N("VariableDeclaration", "dtEnd", P("Type", "System.DateTime")), N("VariableDeclaration", "dtStart", P("Type", "System.DateTime")),
            N("VariableDeclaration", "count", P("Type", "System.Int32")), Assign("Use", statement)));
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name != "Use");
        Assert.Contains(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name == "Use");
    }

    [Fact]
    public async Task ScopeWithoutCatchIsAPlainSequenceAndScopeLocalScalarsAreDeclared()
    {
        var (candidate, definition) = await Import(N("Scope", "Work", N("VariableDeclaration", "local", P("Type", "System.Int32"), P("InitialValue", "3")),
            Assign("Run", "local = local + 1;")));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind is NativeFlowNodeKind.Blocked or NativeFlowNodeKind.Try);
        Assert.Contains(all, node => node.Kind == NativeFlowNodeKind.DeclareVariable && node.Variable == "local" && node.VariableType == NativeScalarType.Int32);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not enforced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnprovenCatchesStayVisibleBlockers()
    {
        var (_, typed) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("failed = true;", "System.ApplicationException")));
        Assert.Contains(typed.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("untyped System.Exception", StringComparison.Ordinal));
        Assert.DoesNotContain(typed.AllNodes(), node => node.Kind == NativeFlowNodeKind.Try);
        Directory.Delete(_directory, recursive: true);

        var (_, twice) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("failed = true;"), Catch("failed = false;")));
        Assert.Contains(twice.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("several catch handlers", StringComparison.Ordinal));
        Directory.Delete(_directory, recursive: true);

        var (_, assignment) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("ex.Message = \"x\";")));
        Assert.Contains(assignment.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("read-only", StringComparison.Ordinal));
        Directory.Delete(_directory, recursive: true);

        var (_, outside) = await Import(N("Scope", "Work", Assign("Run", "result = ex.Message;"), Catch("failed = true;")));
        Assert.Contains(outside.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
    }

    private static bool JsonContains(NativeFlowNode node, string text) => System.Text.Json.JsonSerializer.Serialize(node.Expression).Contains(text, StringComparison.Ordinal);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
