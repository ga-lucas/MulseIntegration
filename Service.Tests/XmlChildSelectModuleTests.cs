using Mulse.CompatibilityPack;
using Mulse.Modules;

namespace Service.Tests;

public sealed class XmlChildSelectModuleTests
{
    private const string Wrapper = "<HL7Multipart><MSH_Doc xmlns=\"urn:a\"><Id>1</Id></MSH_Doc><Body_Doc><Line>x</Line></Body_Doc></HL7Multipart>";

    private static IntegrationBatch Batch(string text, string name = "m.xml")
        => new([new IntegrationPayload(name, BinaryData.FromString(text), "application/xml", new Dictionary<string, string> { ["k"] = "v" })]);

    private static Task<IntegrationBatch> Run(string xml, Dictionary<string, string> settings)
        => new XmlChildSelectModule().RenderAsync(null!, Batch(xml), new ModuleStepDefinition { Module = "xml-child-select", Settings = settings }, CancellationToken.None);

    [Theory]
    [InlineData("1", "MSH_Doc")]
    [InlineData("2", "Body_Doc")]
    public async Task TakesTheNumberedChildAsAStandaloneMessageWithNoMetadataByDefault(string number, string expected)
    {
        var result = await Run(Wrapper, new() { ["partNumber"] = number, ["expectedRootName"] = expected });
        var payload = Assert.Single(result.Payloads);
        Assert.StartsWith("<" + expected, payload.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain("HL7Multipart", payload.GetText(), StringComparison.Ordinal);
        Assert.Empty(payload.Metadata);
        Assert.Equal("m.xml", payload.Name);
    }

    [Fact]
    public async Task KeepCopiesTheSourceMetadata()
    {
        var result = await Run(Wrapper, new() { ["partNumber"] = "2", ["metadata"] = "Keep" });
        Assert.Equal("v", Assert.Single(result.Payloads).Metadata["k"]);
    }

    [Fact]
    public async Task ABadPartNumberAWrongNameOrAMissingPartStopsTheFlow()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Wrapper, new() { ["partNumber"] = "0" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Wrapper, new() { ["partNumber"] = "x" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Wrapper, new() { ["partNumber"] = "1", ["metadata"] = "Maybe" }));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Wrapper, new() { ["partNumber"] = "1", ["expectedRootName"] = "Body_Doc" }));
        Assert.Contains("MSH_Doc", wrong.Message, StringComparison.Ordinal);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Wrapper, new() { ["partNumber"] = "3" }));
        Assert.Contains("2 child element", missing.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("not xml", new() { ["partNumber"] = "1" }));
    }

    [Fact]
    public async Task DoctypeDeclarationsAreRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("<!DOCTYPE a [<!ENTITY e \"x\">]><a><b/></a>", new() { ["partNumber"] = "1" }));
    }
}
