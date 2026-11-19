using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mulse.Modules.ControlFlow;
using Service.Endpoints;
using Service.Execution;
using Service.Models;

namespace Service.Tests;

public sealed class OutputComparerTests
{
    private static OutputDifference Single(OutputComparison comparison) => Assert.Single(comparison.Differences);

    [Fact]
    public void IdenticalTextIsIdenticalWithNoDifferences()
    {
        var result = OutputComparer.Compare("<a><b>1</b></a>", "<a><b>1</b></a>");
        Assert.Equal(OutputVerdicts.Identical, result.Verdict);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void DeclarationBomAndIndentationAreFormattingOnly()
    {
        var expected = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<a>\r\n  <b>1</b>\r\n</a>";
        var actual = "<a><b>1</b></a>";
        var result = OutputComparer.Compare(expected, actual);

        Assert.Equal(OutputVerdicts.FormattingOnly, result.Verdict);
        Assert.Equal(new[] { "ByteOrderMark", "Whitespace", "XmlDeclaration" }, result.Differences.Select(item => item.Class).Order(StringComparer.Ordinal));
        Assert.All(result.Differences, item => Assert.True(item.Cosmetic));
    }

    [Fact]
    public void NamespacePrefixAndAttributeOrderAreCosmeticAndReportedOnce()
    {
        var expected = "<ns0:r xmlns:ns0=\"urn:x\" a=\"1\" b=\"2\"><ns0:i>1</ns0:i><ns0:i>2</ns0:i></ns0:r>";
        var actual = "<q:r xmlns:q=\"urn:x\" b=\"2\" a=\"1\"><q:i>1</q:i><q:i>2</q:i></q:r>";
        var result = OutputComparer.Compare(expected, actual);

        Assert.Equal(OutputVerdicts.FormattingOnly, result.Verdict);
        Assert.Equal(1, result.Differences.Count(item => item.Class == "NamespacePrefix"));
        Assert.Contains(result.Differences, item => item.Class == "AttributeOrder");
        Assert.Equal(2, result.Differences.Count);
    }

    [Fact]
    public void EmptyElementFormIsCosmetic()
    {
        var item = Single(OutputComparer.Compare("<a><b/></a>", "<a><b></b></a>"));
        Assert.Equal("EmptyElementForm", item.Class);
        Assert.True(item.Cosmetic);
    }

    [Fact]
    public void ChangedValueIsClassifiedWithItsPath()
    {
        var result = OutputComparer.Compare("<a><b>1</b><c x=\"p\">z</c></a>", "<a><b>2</b><c x=\"q\">z</c></a>");
        Assert.Equal(OutputVerdicts.Different, result.Verdict);
        Assert.Contains(result.Differences, item => item is { Class: "Value", Path: "/a/b", Expected: "1", Actual: "2" });
        Assert.Contains(result.Differences, item => item is { Class: "Value", Path: "/a/c/@x" });
    }

    [Theory]
    [InlineData("2024-01-05", "20240105", "DateTimeFormat")]
    [InlineData("2024-01-05T10:00:00", "20240105100000", "DateTimeFormat")]
    [InlineData("1/5/2024", "2024-01-05", "DateTimeFormat")]
    [InlineData("2024-01-05", "2024-01-06", "DateTimeValue")]
    [InlineData("1.0", "1", "NumberFormat")]
    [InlineData("Yes", "YES", "ValueCase")]
    [InlineData("a ", "a", "ValueWhitespace")]
    public void ValuesAreClassifiedByTheKindOfDifference(string expected, string actual, string expectedClass)
    {
        var item = Single(OutputComparer.Compare($"<a><b>{expected}</b></a>", $"<a><b>{actual}</b></a>"));
        Assert.Equal(expectedClass, item.Class);
        Assert.False(item.Cosmetic);
    }

    [Fact]
    public void MissingExtraAndReorderedElementsAreDistinguished()
    {
        var missing = OutputComparer.Compare("<a><b/><c/></a>", "<a><b/></a>");
        Assert.Equal("MissingElement", Single(missing).Class);
        Assert.Equal("/a/c", Single(missing).Path);

        var extra = OutputComparer.Compare("<a><b/></a>", "<a><b/><c/></a>");
        Assert.Equal("ExtraElement", Single(extra).Class);

        var reordered = OutputComparer.Compare("<a><b>1</b><c>2</c></a>", "<a><c>2</c><b>1</b></a>");
        Assert.Equal("ElementOrder", Single(reordered).Class);
        Assert.False(Single(reordered).Cosmetic);
    }

    [Fact]
    public void RepeatedElementsArePairedByOccurrence()
    {
        var result = OutputComparer.Compare("<a><i>1</i><i>2</i><i>3</i></a>", "<a><i>1</i><i>9</i></a>");
        Assert.Contains(result.Differences, item => item is { Class: "Value", Path: "/a/i[2]" });
        Assert.Contains(result.Differences, item => item is { Class: "MissingElement", Path: "/a/i[3]" });
    }

    [Fact]
    public void AttributesMissingOrExtraAreReported()
    {
        var result = OutputComparer.Compare("<a x=\"1\"/>", "<a y=\"1\"/>");
        Assert.Contains(result.Differences, item => item.Class == "MissingAttribute" && item.Path == "/a/@x");
        Assert.Contains(result.Differences, item => item.Class == "ExtraAttribute" && item.Path == "/a/@y");
    }

    [Fact]
    public void DifferentRootsOrNamespacesStopTheComparison()
    {
        Assert.Equal("ElementName", Single(OutputComparer.Compare("<a/>", "<b/>")).Class);
        Assert.Equal("Namespace", Single(OutputComparer.Compare("<a xmlns=\"urn:1\"/>", "<a xmlns=\"urn:2\"/>")).Class);
    }

    [Fact]
    public void EncodingDifferenceIsNotCosmetic()
    {
        var result = OutputComparer.Compare("<?xml version=\"1.0\" encoding=\"utf-8\"?><a/>", "<?xml version=\"1.0\" encoding=\"utf-16\"?><a/>");
        var item = Single(result);
        Assert.Equal("Encoding", item.Class);
        Assert.False(item.Cosmetic);
        Assert.Equal(OutputVerdicts.Different, result.Verdict);
    }

    [Fact]
    public void XmlOnOneSideOnlyIsAFormatDifference()
    {
        Assert.Equal("Format", Single(OutputComparer.Compare("<a/>", "plain text")).Class);
    }

    [Fact]
    public void PlainTextReportsLineEndingsTrailingNewlineAndLineDifferences()
    {
        Assert.Equal("LineEndings", Single(OutputComparer.Compare("MSH|1\rPID|2\r", "MSH|1\nPID|2\n")).Class);
        Assert.Equal("TrailingNewline", Single(OutputComparer.Compare("a\nb\n", "a\nb")).Class);

        var lines = OutputComparer.Compare("a\nb\nc", "a\nB\nc\nd");
        Assert.Contains(lines.Differences, item => item is { Class: "Value", Path: "line 2" });
        Assert.Contains(lines.Differences, item => item is { Class: "ExtraLine", Path: "line 4" });
        Assert.Equal(OutputVerdicts.Different, lines.Verdict);
    }

    [Fact]
    public void LineEndingsAreNotCalledCosmeticForPlainText()
    {
        Assert.False(Single(OutputComparer.Compare("a\r\nb", "a\nb")).Cosmetic);
    }

    [Fact]
    public void DifferenceListIsBoundedAndMarkedTruncated()
    {
        var left = "<a>" + string.Concat(Enumerable.Range(0, 400).Select(index => $"<i>{index}</i>")) + "</a>";
        var right = "<a>" + string.Concat(Enumerable.Range(0, 400).Select(index => $"<i>x{index}</i>")) + "</a>";
        var result = OutputComparer.Compare(left, right);
        Assert.Equal(OutputComparer.MaxDifferences, result.Differences.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void LongValuesAreClippedAndDoctypesAreNotProcessed()
    {
        var item = Single(OutputComparer.Compare($"<a>{new string('x', 5000)}</a>", $"<a>{new string('y', 5000)}</a>"));
        Assert.True(item.Expected!.Length <= 201);

        var withDtd = OutputComparer.Compare("<!DOCTYPE a [<!ENTITY e \"boom\">]><a>&e;</a>", "<a>boom</a>");
        Assert.Equal("Format", Single(withDtd).Class);
    }

    [Fact]
    public void OversizedOrMissingInputIsRefused()
    {
        Assert.Throws<ArgumentException>(() => OutputComparer.Compare(new string('a', OutputComparer.MaxChars + 1), "a"));
        Assert.Throws<ArgumentException>(() => OutputComparer.Compare(null!, "a"));
    }
}

public sealed class NativeFlowCompareRunnerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static NativeFlowTestPayload Payload(string text) => new("sample.xml", text, "application/xml", new() { ["group"] = "test", ["messageId"] = "1", ["channel"] = "input" });

    private static string DeliverFlow() => JsonSerializer.Serialize(new NativeFlowDefinition
    {
        Durable = true,
        Nodes =
        [
            new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "source" },
            new() { Kind = NativeFlowNodeKind.Augment, Module = "map", InputMessages = ["source"], MessageName = "output" },
            new() { Kind = NativeFlowNodeKind.Deliver, Module = "billing-sink", MessageName = "output" }
        ]
    }, Json);

    [Fact]
    public async Task SuppliedSamplesAreSummarizedWithoutClaimingEquivalence()
    {
        var response = await NativeFlowCompareRunner.RunAsync(new(null,
        [
            new("exact", "<a>1</a>", "<a>1</a>"),
            new("format", "<?xml version=\"1.0\"?><a>1</a>", "<a>1</a>"),
            new("wrong", "<a>1</a>", "<a>2</a>")
        ]), default);

        Assert.Equal([OutputVerdicts.Identical, OutputVerdicts.FormattingOnly, OutputVerdicts.Different], response.Results.Select(result => result.Verdict));
        Assert.Equal(new NativeFlowCompareSummary(3, 1, 1, 1, 0, response.Summary.Statement), response.Summary);
        Assert.StartsWith("Matches 2 of 3 compared samples (1 exactly, 1 apart from formatting); 1 differs.", response.Summary.Statement);
        Assert.Contains("not proof that the flows are equivalent", response.Summary.Statement);
        Assert.DoesNotContain("are equivalent.", response.Summary.Statement.Replace("not proof that the flows are equivalent", ""));
    }

    [Fact]
    public async Task RunSamplesCompareTheLastDeliveryInputAndNeverCallRealModules()
    {
        var mocks = new List<NativeFlowTestMock> { new("Augment", "map", Payload("<mapped>7</mapped>")), new("Deliver", "billing-sink", null) };
        var response = await NativeFlowCompareRunner.RunAsync(new(DeliverFlow(),
        [
            new("match", "<mapped>7</mapped>", Input: Payload("<in/>"), Mocks: mocks, DeliverModule: "billing-sink"),
            new("differs", "<mapped>8</mapped>", Input: Payload("<in/>"), Mocks: mocks)
        ]), default);

        Assert.Equal(OutputVerdicts.Identical, response.Results[0].Verdict);
        Assert.Equal("Run", response.Results[0].Source);
        Assert.Equal(OutputVerdicts.Different, response.Results[1].Verdict);
        Assert.Equal("/mapped", Assert.Single(response.Results[1].Differences).Path);
    }

    [Fact]
    public async Task RunProblemsMakeASampleNotComparedWithAReasonInsteadOfFailingTheBatch()
    {
        var mocks = new List<NativeFlowTestMock> { new("Augment", "map", Payload("<mapped/>")), new("Deliver", "billing-sink", null) };
        var response = await NativeFlowCompareRunner.RunAsync(new(DeliverFlow(),
        [
            new("noMock", "<a/>", Input: Payload("<in/>"), Mocks: []),
            new("wrongModule", "<a/>", Input: Payload("<in/>"), Mocks: mocks, DeliverModule: "other-sink"),
            new("badIndex", "<a/>", Input: Payload("<in/>"), Mocks: mocks, PayloadIndex: 5),
            new("tooLarge", new string('a', OutputComparer.MaxChars + 1), "x"),
            new("ok", "<mapped/>", Input: Payload("<in/>"), Mocks: mocks)
        ]), default);

        Assert.Equal(4, response.Summary.NotCompared);
        Assert.Contains("No explicit Augment mock", response.Results[0].Error);
        Assert.Contains("never delivered through 'other-sink'", response.Results[1].Error);
        Assert.Contains("index 5 does not exist", response.Results[2].Error);
        Assert.Contains("at most", response.Results[3].Error);
        Assert.Equal(OutputVerdicts.Identical, response.Results[4].Verdict);
        Assert.Contains("4 could not be compared", response.Summary.Statement);
    }

    [Fact]
    public async Task NothingComparedSaysSo()
    {
        var response = await NativeFlowCompareRunner.RunAsync(new(DeliverFlow(), [new("a", "<a/>", Input: Payload("<in/>"), Mocks: [])]), default);
        Assert.Equal("No sample was compared.", response.Summary.Statement);
    }

    [Fact]
    public async Task InvalidRequestsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowCompareRunner.RunAsync(new(null, []), default));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowCompareRunner.RunAsync(new(null, [new("a", "x", "x"), new("A", "x", "x")]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowCompareRunner.RunAsync(new(null, [new("a", "x")]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowCompareRunner.RunAsync(new(null, [new("a", "x", Input: Payload("<in/>"))]), default));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowCompareRunner.RunAsync(new(null, Enumerable.Range(0, NativeFlowCompareRunner.MaxPairs + 1).Select(index => new NativeFlowComparePair("p" + index, "x", "x")).ToList()), default));
    }

    [Fact]
    public async Task CompareEndpointReturnsClassifiedDifferencesAndBadRequestsForInvalidInput()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<Service.Middleware.ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<Service.Flows.IFlowDesignService>(_ => throw new InvalidOperationException("Comparison must not use flow design services."));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapDesignerEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var ok = await client.PostAsJsonAsync("/api/flow-designer/native/compare", new { pairs = new[] { new { name = "s", expected = "<a>1</a>", actual = "<a>2</a>" } } });
            Assert.True(ok.StatusCode == System.Net.HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
            var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Different", body.GetProperty("results")[0].GetProperty("verdict").GetString());
            Assert.Equal("Value", body.GetProperty("results")[0].GetProperty("differences")[0].GetProperty("class").GetString());

            using var bad = await client.PostAsJsonAsync("/api/flow-designer/native/compare", new { pairs = Array.Empty<object>() });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
}
