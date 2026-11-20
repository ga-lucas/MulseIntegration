using System.Xml.Linq;
using Mulse.Hl7;
using Mulse.Modules;

namespace Service.Tests;

public sealed class Hl7SchemaParseTests
{
    private const string Message = "MSH|^~\\&|APP||||||DFT^P03\rPID|1||123^^^HOSP~456||DOE\rOBX|1||||first\rOBX|2||||second\r";

    private static async Task<string> ParseAsync(string text, Dictionary<string, string>? settings = null, string? schemas = null)
    {
        var all = new Dictionary<string, string> { ["schemaText"] = schemas ?? Hl7SchemaRenderTests.SchemaText };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        var batch = new IntegrationBatch([new("a.hl7", BinaryData.FromString(text), "text/hl7-v2", new Dictionary<string, string>())]);
        var result = await new Hl7SchemaParseModule().ParseAsync(new("f", "e", DateTimeOffset.UnixEpoch), batch, new ModuleStepDefinition { Module = "hl7-schema-parse", Settings = all }, default);
        return result.Payloads[0].Content.ToString();
    }

    private static async Task<string> RenderAsync(string xml)
    {
        var batch = new IntegrationBatch([new("a.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string>())]);
        var result = await new Hl7SchemaRenderModule().RenderAsync(new("f", "e", DateTimeOffset.UnixEpoch), batch,
            new ModuleStepDefinition { Module = "hl7-schema-render", Settings = new() { ["schemaText"] = Hl7SchemaRenderTests.SchemaText } }, default);
        return result.Payloads[0].Content.ToString();
    }

    [Fact]
    public async Task ParsesIntoAMultipartWithTheHeaderAndBodyNamedBySchema()
    {
        var xml = XElement.Parse(await ParseAsync(Message));
        Assert.Equal("HL7Multipart", xml.Name.LocalName);
        var header = xml.Elements().First();
        var body = xml.Elements().Last();
        Assert.Equal("urn:msh", header.Name.NamespaceName);
        Assert.Equal("DFT_P03_23_X", body.Name.LocalName);
        Assert.Equal("DFT", header.Descendants("CM_MSG.0_Type").Single().Value);
        Assert.Equal("P03", header.Descendants("CM_MSG.1_Trigger").Single().Value);
        var ids = body.Elements("PID_Patient").Elements("PID_3_IdList").ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal("123", ids[0].Element("CX_0_Id")!.Value);
        Assert.Equal("HOSP", ids[0].Element("CX_3_Authority")!.Element("CX_3_0_Namespace")!.Value);
        Assert.Equal(["first", "second"], body.Elements("OBX_Observation").Select(observation => observation.Element("OBX_5_Value")!.Value));
        Assert.Null(body.Element("PID_Patient")!.Element("PID_2_Missing"));
    }

    [Fact]
    public async Task ParsedXmlRendersBackToTheSameText()
    {
        Assert.Equal(Message, await RenderAsync(await ParseAsync(Message)));
    }

    [Fact]
    public async Task EscapeSequencesAreDecodedOnlyWhenConfigured()
    {
        var text = "MSH|^~\\&|APP||||||DFT^P03\rPID|||||A\\F\\B\\S\\C\\E\\D\\.br\\\r";
        Assert.Contains("A\\F\\B\\S\\C\\E\\D\\.br\\", await ParseAsync(text));
        var decoded = XElement.Parse(await ParseAsync(text, new() { ["escapeSequences"] = "Decode" }));
        Assert.Equal("A|B^C\\D\\.br\\", decoded.Descendants("PID_5_Name").Single().Value);
    }

    [Fact]
    public async Task UnknownSegmentsAndFieldsStopUnlessConfiguredToBeDropped()
    {
        var withZ = Message + "ZXX|1\r";
        var segment = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync(withZ));
        Assert.Contains("ZXX", segment.Message);
        Assert.DoesNotContain("ZXX", await ParseAsync(withZ, new() { ["unknownSegments"] = "Skip" }));
        var withField = "MSH|^~\\&|APP||||||DFT^P03\rPID|1||||||extra\r";
        var field = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync(withField));
        Assert.Contains("Field 7", field.Message);
        Assert.DoesNotContain("extra", await ParseAsync(withField, new() { ["unknownFields"] = "Ignore" }));
    }

    [Fact]
    public async Task ChoosesTheBodySchemaFromMsh9OrTheSetting()
    {
        var unknownType = "MSH|^~\\&|APP||||||ADT^A01\rPID|1\r";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync(unknownType));
        Assert.Contains("Body schema root", error.Message);
        var forced = XElement.Parse(await ParseAsync(unknownType, new() { ["bodyRoot"] = "urn:body#DFT_P03_23_X" }));
        Assert.Equal("DFT_P03_23_X", forced.Elements().Last().Name.LocalName);
        await Assert.ThrowsAsync<ArgumentException>(() => ParseAsync(unknownType, new() { ["bodyRoot"] = "Nope" }));
    }

    [Fact]
    public async Task RejectsMessagesThatDoNotFitTheSchema()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync("PID|1"));
        var repeats = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync("MSH|^~\\&|APP||||||DFT^P03\rPID|1||||x~y\r"));
        Assert.Contains("repeats", repeats.Message);
        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync("MSH|^~\\&|APP||||||DFT^P03\rPID|||1^2^3^4^5\r"));
        Assert.Contains("components", tooMany.Message);
    }
}
