using System.Text.Json;
using Mulse.Hl7;
using Mulse.Modules;

namespace Service.Tests;

public sealed class Hl7SchemaRenderTests
{
    private const string Datatypes = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:dt" xmlns="urn:dt">
          <xs:complexType name="CX">
            <xs:sequence>
              <xs:element name="CX_0_Id" type="xs:string" minOccurs="0"/>
              <xs:element name="CX_1_Check" type="xs:string" minOccurs="0"/>
              <xs:element name="CX_2_Scheme" type="xs:string" minOccurs="0"/>
              <xs:element name="CX_3_Authority" minOccurs="0">
                <xs:complexType>
                  <xs:sequence>
                    <xs:element name="CX_3_0_Namespace" type="xs:string" minOccurs="0"/>
                    <xs:element name="CX_3_1_Universal" type="xs:string" minOccurs="0"/>
                  </xs:sequence>
                </xs:complexType>
              </xs:element>
            </xs:sequence>
          </xs:complexType>
        </xs:schema>
        """;

    private const string Segments = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:seg" xmlns:dt="urn:dt">
          <xs:import namespace="urn:dt"/>
          <xs:complexType name="PID">
            <xs:sequence>
              <xs:element name="PID_1_SetId" type="xs:string" minOccurs="0"/>
              <xs:element name="PID_3_IdList" type="dt:CX" minOccurs="0" maxOccurs="unbounded"/>
              <xs:element name="PID_5_Name" type="xs:string" minOccurs="0"/>
            </xs:sequence>
          </xs:complexType>
          <xs:complexType name="OBX">
            <xs:sequence>
              <xs:element name="OBX_1_SetId" type="xs:string" minOccurs="0"/>
              <xs:element name="OBX_5_Value" type="xs:string" minOccurs="0"/>
            </xs:sequence>
          </xs:complexType>
        </xs:schema>
        """;

    private const string Header = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:msh" elementFormDefault="unqualified">
          <xs:element name="MSH_X">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="MSH">
                  <xs:complexType>
                    <xs:sequence>
                      <xs:element name="MSH.2_Encoding" type="xs:string" minOccurs="0"/>
                      <xs:element name="MSH.3_SendingApp" minOccurs="0">
                        <xs:complexType><xs:sequence><xs:element name="HD.0_Namespace" type="xs:string" minOccurs="0"/></xs:sequence></xs:complexType>
                      </xs:element>
                      <xs:element name="MSH.9_MessageType" minOccurs="0">
                        <xs:complexType><xs:sequence>
                          <xs:element name="CM_MSG.0_Type" type="xs:string" minOccurs="0"/>
                          <xs:element name="CM_MSG.1_Trigger" type="xs:string" minOccurs="0"/>
                        </xs:sequence></xs:complexType>
                      </xs:element>
                    </xs:sequence>
                  </xs:complexType>
                </xs:element>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string Body = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:body" xmlns:seg="urn:seg">
          <xs:import namespace="urn:seg"/>
          <xs:element name="DFT_P03_23_X">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="PID_Patient" type="seg:PID" minOccurs="0"/>
                <xs:sequence maxOccurs="unbounded">
                  <xs:element name="OBX_Observation" type="seg:OBX"/>
                </xs:sequence>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    internal static readonly string SchemaText = JsonSerializer.Serialize(new[] { Header, Body, Segments, Datatypes });

    private const string MshPart = "<MSH_X xmlns=\"urn:msh\"><MSH><MSH.2_Encoding>^~\\&amp;</MSH.2_Encoding><MSH.3_SendingApp><HD.0_Namespace>APP</HD.0_Namespace></MSH.3_SendingApp>"
        + "<MSH.9_MessageType><CM_MSG.0_Type>DFT</CM_MSG.0_Type><CM_MSG.1_Trigger>P03</CM_MSG.1_Trigger></MSH.9_MessageType></MSH></MSH_X>";

    private static string BodyPart(string pid = "<PID_1_SetId>1</PID_1_SetId><PID_3_IdList><CX_0_Id>123</CX_0_Id><CX_3_Authority><CX_3_0_Namespace>HOSP</CX_3_0_Namespace></CX_3_Authority></PID_3_IdList><PID_3_IdList><CX_0_Id>456</CX_0_Id></PID_3_IdList><PID_5_Name>DOE</PID_5_Name>")
        => $"<DFT_P03_23_X xmlns=\"urn:body\"><PID_Patient>{pid}</PID_Patient><OBX_Observation><OBX_1_SetId>1</OBX_1_SetId><OBX_5_Value>first</OBX_5_Value></OBX_Observation><OBX_Observation><OBX_1_SetId>2</OBX_1_SetId><OBX_5_Value>second</OBX_5_Value></OBX_Observation></DFT_P03_23_X>";

    private static async Task<string> RunAsync(string xml, Dictionary<string, string>? settings = null, string? schemas = null)
    {
        var module = new Hl7SchemaRenderModule();
        var all = new Dictionary<string, string> { ["schemaText"] = schemas ?? SchemaText };
        foreach (var (key, value) in settings ?? []) all[key] = value;
        var batch = new IntegrationBatch([new("a.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string>())]);
        var result = await module.RenderAsync(new("f", "e", DateTimeOffset.UnixEpoch), batch, new ModuleStepDefinition { Module = "hl7-schema-render", Settings = all }, default);
        return result.Payloads[0].Content.ToString();
    }

    [Fact]
    public async Task RendersAMultipartHeaderAndBodyUsingTheSchemaOrder()
    {
        var text = await RunAsync($"<HL7Multipart>{MshPart}{BodyPart()}</HL7Multipart>");
        Assert.Equal("MSH|^~\\&|APP||||||DFT^P03\rPID|1||123^^^HOSP~456||DOE\rOBX|1||||first\rOBX|2||||second\r", text);
    }

    [Fact]
    public async Task RendersASingleMessageDocumentAndTrimsTrailingFields()
    {
        var text = await RunAsync(MshPart, new() { ["trimTrailingDelimiters"] = "true", ["segmentTerminator"] = "LF" });
        Assert.Equal("MSH|^~\\&|APP||||||DFT^P03\n", text);
        var trimmed = await RunAsync("<MSH_X xmlns=\"urn:msh\"><MSH><MSH.2_Encoding>^~\\&amp;</MSH.2_Encoding><MSH.3_SendingApp><HD.0_Namespace>APP</HD.0_Namespace></MSH.3_SendingApp></MSH></MSH_X>", new() { ["trimTrailingDelimiters"] = "true" });
        Assert.Equal("MSH|^~\\&|APP\r", trimmed);
    }

    [Fact]
    public async Task EmptyComponentsAreKeptUnlessTrimmingIsEnabled()
    {
        var pid = "<PID_3_IdList><CX_0_Id>123</CX_0_Id><CX_1_Check/></PID_3_IdList>";
        var kept = await RunAsync($"<HL7Multipart>{MshPart}{BodyPart(pid)}</HL7Multipart>");
        Assert.Contains("PID|||123^\r", kept);
        var trimmed = await RunAsync($"<HL7Multipart>{MshPart}{BodyPart(pid)}</HL7Multipart>", new() { ["trimTrailingComponents"] = "true" });
        Assert.Contains("PID|||123\r", trimmed);
    }

    [Fact]
    public async Task DelimitersInValuesAreEscapedOnlyWhenConfigured()
    {
        var xml = $"<HL7Multipart>{MshPart}{BodyPart("<PID_5_Name>A|B^C~D&amp;E\\F</PID_5_Name>")}</HL7Multipart>";
        Assert.Contains("PID|||||A|B^C~D&E\\F\r", await RunAsync(xml));
        Assert.Contains("PID|||||A\\F\\B\\S\\C\\R\\D\\T\\E\\E\\F\r", await RunAsync(xml, new() { ["escapeSequences"] = "Encode" }));
    }

    [Fact]
    public async Task TheFieldSeparatorSettingAppliesWhenTheMessageHasNoMsh1()
    {
        var text = await RunAsync($"<HL7Multipart>{MshPart}{BodyPart("<PID_5_Name>X</PID_5_Name>")}</HL7Multipart>", new() { ["fieldSeparator"] = "#" });
        Assert.StartsWith("MSH#^~\\&#APP######DFT^P03\r", text);
    }

    [Fact]
    public async Task AcceptsOneSchemaDocumentInsteadOfAList()
    {
        Assert.Equal("MSH|^~\\&|APP\r", await RunAsync("<MSH_X xmlns=\"urn:msh\"><MSH><MSH.2_Encoding>^~\\&amp;</MSH.2_Encoding><MSH.3_SendingApp><HD.0_Namespace>APP</HD.0_Namespace></MSH.3_SendingApp></MSH></MSH_X>", null, Header));
    }

    [Fact]
    public async Task StopsWithAnErrorNamingTheProblem()
    {
        var unknownRoot = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("<Other xmlns=\"urn:q\"><MSH/></Other>"));
        Assert.Contains("no schema for the message element", unknownRoot.Message);
        var noMsh = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(BodyPart()));
        Assert.Contains("no leading MSH", noMsh.Message);
        var undeclared = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync($"<HL7Multipart>{MshPart}{BodyPart("<PID_9_Extra>x</PID_9_Extra>")}</HL7Multipart>"));
        Assert.Contains("PID_9_Extra", undeclared.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("not xml"));
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(MshPart, new() { ["segmentTerminator"] = "TAB" }));
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(MshPart, new() { ["escapeSequences"] = "Maybe" }));
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(MshPart, null, "<not-a-schema/>"));
    }

    [Fact]
    public void TheDescriptorListsEveryAssumptionAsASetting()
    {
        var keys = new Hl7SchemaRenderModule().Descriptor.Settings.Select(setting => setting.Key).ToArray();
        Assert.Contains("escapeSequences", keys);
        Assert.Contains("trimTrailingComponents", keys);
        Assert.Contains("fieldSeparator", keys);
        Assert.Contains("encodingCharacters", keys);
    }
}
