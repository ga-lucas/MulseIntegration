using Mulse.CompatibilityPack;
using Mulse.Modules;

namespace Service.Tests;

public sealed class FlatFileSchemaRenderTests
{
    private const string Header = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:b=\"http://schemas.microsoft.com/BizTalk/2003\" elementFormDefault=\"qualified\" targetNamespace=\"urn:t\" xmlns=\"urn:t\">";

    private static string Info(string root, string extra = "") =>
        $"<xs:annotation><xs:appinfo><b:schemaInfo standard=\"Flat File\" root_reference=\"{root}\" default_pad_char=\" \" pad_char_type=\"char\" {extra} /></xs:appinfo></xs:annotation>";

    private static string Record(string attributes) => $"<xs:annotation><xs:appinfo><b:recordInfo preserve_delimiter_for_empty_data=\"true\" {attributes} /></xs:appinfo></xs:annotation>";
    private static string FieldInfo(string attributes) => $"<xs:annotation><xs:appinfo><b:fieldInfo {attributes} /></xs:appinfo></xs:annotation>";

    private static string Field(string name, int sequence, string attributes = "", string type = "xs:string", string occurs = "")
        => $"<xs:element name=\"{name}\" type=\"{type}\" {occurs}>{FieldInfo($"sequence_number=\"{sequence}\" justification=\"left\" {attributes}")}</xs:element>";

    // Header line, then comma-separated detail lines, each line ended by CRLF.
    private static string DelimitedSchema(string fieldAttributes = "") =>
        Header + Info("File") +
        "<xs:element name=\"File\">" + Record("structure=\"delimited\" child_delimiter_type=\"hex\" child_delimiter=\"0x0D 0x0A\" child_order=\"postfix\" sequence_number=\"1\"") +
        "<xs:complexType><xs:sequence>" +
        Field("Title", 1, occurs: "minOccurs=\"0\"") +
        "<xs:element name=\"Line\" minOccurs=\"1\" maxOccurs=\"unbounded\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" child_order=\"infix\" sequence_number=\"2\"") +
        "<xs:complexType><xs:sequence>" + Field("Name", 1, fieldAttributes) + Field("Qty", 2) + Field("Note", 3, occurs: "minOccurs=\"0\"") + "</xs:sequence></xs:complexType></xs:element>" +
        "</xs:sequence></xs:complexType></xs:element></xs:schema>";

    private static string Xml(string body) => $"<File xmlns=\"urn:t\">{body}</File>";

    private static string Render(string schema, string xml, FlatFileRenderOptions? options = null)
        => FlatFileSchema.Compile([schema]).Render(xml, options);

    [Fact]
    public void DelimitedRecordsUseTheirChildOrderAndDelimiters()
    {
        var text = Render(DelimitedSchema(), Xml("<Title>T</Title><Line><Name>a</Name><Qty>1</Qty><Note>n</Note></Line><Line><Name>b</Name><Qty>2</Qty></Line>"));
        Assert.Equal("T\r\na,1,n\r\nb,2,\r\n", text);
    }

    [Fact]
    public void AnAbsentOptionalElementKeepsItsPlaceByDefaultAndCanBeSkipped()
    {
        var xml = Xml("<Line><Name>a</Name><Qty>1</Qty></Line>");
        Assert.Equal("\r\na,1,\r\n", Render(DelimitedSchema(), xml));
        Assert.Equal("a,1\r\n", Render(DelimitedSchema(), xml, new() { KeepAbsentPlaceholders = false }));
    }

    [Fact]
    public void ThePrefixAndInfixOrdersPlaceTheDelimiterDifferently()
    {
        string Schema(string order) => Header + Info("R") + "<xs:element name=\"R\">" + Record($"structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"|\" child_order=\"{order}\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + Field("B", 2) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        const string xml = "<R xmlns=\"urn:t\"><A>x</A><B>y</B></R>";
        Assert.Equal("x|y", Render(Schema("infix"), xml));
        Assert.Equal("x|y", Render(Schema("default"), xml));
        Assert.Equal("x|y|", Render(Schema("postfix"), xml));
        Assert.Equal("|x|y", Render(Schema("prefix"), xml));
    }

    [Fact]
    public void TrailingDelimitersAreSuppressedOnlyWhenTheRecordAsksForIt()
    {
        string Schema(string suppress) => Header + Info("R") + "<xs:element name=\"R\">" + Record($"structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" suppress_trailing_delimiters=\"{suppress}\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + Field("B", 2, occurs: "minOccurs=\"0\"") + Field("C", 3, occurs: "minOccurs=\"0\"") + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        const string xml = "<R xmlns=\"urn:t\"><A>x</A></R>";
        Assert.Equal("x,,", Render(Schema("false"), xml));
        Assert.Equal("x", Render(Schema("true"), xml));
    }

    [Fact]
    public void WrapCharactersApplyAlwaysOrOnlyWhenNeeded()
    {
        var schema = DelimitedSchema("wrap_char_type=\"char\" wrap_char=\"&quot;\"");
        var xml = Xml("<Line><Name>a,b</Name><Qty>1</Qty></Line><Line><Name>c</Name><Qty>2</Qty></Line>");
        Assert.Equal("\r\n\"a,b\",1,\r\n\"c\",2,\r\n", Render(schema, xml));
        Assert.Equal("\r\n\"a,b\",1,\r\nc,2,\r\n", Render(schema, xml, new() { WrapAlways = false }));
    }

    [Fact]
    public void AValueWithItsDelimiterAndNoWrapCharacterStopsUnlessAllowed()
    {
        var xml = Xml("<Line><Name>a,b</Name><Qty>1</Qty></Line>");
        var failure = Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), xml));
        Assert.Contains("Name", failure.Message, StringComparison.Ordinal);
        Assert.Equal("\r\na,b,1,\r\n", Render(DelimitedSchema(), xml, new() { WriteDelimiterInValue = true }));
    }

    [Fact]
    public void AValueContainingTheWrapCharacterFailsClosed()
    {
        var schema = DelimitedSchema("wrap_char_type=\"char\" wrap_char=\"&quot;\"");
        Assert.Throws<InvalidOperationException>(() => Render(schema, Xml("<Line><Name>say \"hi\"</Name><Qty>1</Qty></Line>")));
    }

    private static string PositionalSchema() =>
        Header + Info("Rows") + "<xs:element name=\"Rows\">" + Record("structure=\"delimited\" child_delimiter_type=\"hex\" child_delimiter=\"0x0D 0x0A\" sequence_number=\"1\"")
        + "<xs:complexType><xs:sequence><xs:element name=\"Row\" maxOccurs=\"unbounded\">" + Record("structure=\"positional\" sequence_number=\"1\"")
        + "<xs:complexType><xs:sequence>"
        + Field("Code", 1, "pos_offset=\"0\" pos_length=\"4\"")
        + Field("Amount", 2, "pos_offset=\"0\" pos_length=\"6\" pad_char=\"0\" pad_char_type=\"char\"").Replace("justification=\"left\"", "justification=\"right\"")
        + "</xs:sequence></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";

    [Fact]
    public void PositionalFieldsAreFixedWidthPaddedAndJustified()
    {
        var text = Render(PositionalSchema(), "<Rows xmlns=\"urn:t\"><Row><Code>AB</Code><Amount>12</Amount></Row><Row><Code>XYZW</Code><Amount>123456</Amount></Row></Rows>");
        Assert.Equal("AB  000012\r\nXYZW123456", text);
    }

    [Fact]
    public void AValueThatDoesNotFitAPositionStopsUnlessTruncationIsChosen()
    {
        const string xml = "<Rows xmlns=\"urn:t\"><Row><Code>ABCDE</Code><Amount>1</Amount></Row></Rows>";
        Assert.Throws<InvalidOperationException>(() => Render(PositionalSchema(), xml));
        Assert.Equal("ABCD000001", Render(PositionalSchema(), xml, new() { TruncateTooLong = true }));
    }

    [Fact]
    public void DateAndTimeFieldsUseTheirFormatWithTheChosenHourClock()
    {
        string Schema(string format, string type) => Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("D", 1, $"datetime_format=\"{format}\"", type) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("20240307", Render(Schema("yyyyMMdd", "xs:date"), "<R xmlns=\"urn:t\"><D>2024-03-07</D></R>"));
        Assert.Equal("240307-1530", Render(Schema("yyMMdd-HHmm", "xs:dateTime"), "<R xmlns=\"urn:t\"><D>2024-03-07T15:30:00</D></R>"));
        Assert.Equal("1530", Render(Schema("hhmm", "xs:time"), "<R xmlns=\"urn:t\"><D>15:30:00</D></R>"));
        Assert.Equal("0330", Render(Schema("hhmm", "xs:time"), "<R xmlns=\"urn:t\"><D>15:30:00</D></R>", new() { TwelveHourClock = true }));
    }

    [Fact]
    public void ATagIsTheFirstItemOfItsRecord()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"|\" tag_name=\"MSH\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("MSH|x", Render(schema, "<R xmlns=\"urn:t\"><A>x</A></R>"));
    }

    [Fact]
    public void ATagWithPrefixOrderIsNotPrecededByTheDelimiter()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"|\" child_order=\"prefix\" tag_name=\"MSH\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + Field("B", 2) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("MSH|x|y", Render(schema, "<R xmlns=\"urn:t\"><A>x</A><B>y</B></R>"));
    }

    [Fact]
    public void NillableFieldsRenderNormallyButANilValueStops()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence><xs:element name=\"A\" type=\"xs:string\" nillable=\"true\">" + FieldInfo("sequence_number=\"1\"") + "</xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("x", Render(schema, "<R xmlns=\"urn:t\"><A>x</A></R>"));
        Assert.Throws<InvalidOperationException>(() => Render(schema, "<R xmlns=\"urn:t\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><A i:nil=\"true\" /></R>"));
    }

    [Fact]
    public void ARecordWithOneChildNeedsNoChildDelimiter()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence><xs:element name=\"Body\" maxOccurs=\"unbounded\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + Field("B", 2) + "</xs:sequence></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("1,2", Render(schema, "<R xmlns=\"urn:t\"><Body><A>1</A><B>2</B></Body></R>"));
    }

    [Fact]
    public void SequenceGroupsAreTransparentAndRepeatAsAWhole()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1)
            + "<xs:sequence minOccurs=\"0\" maxOccurs=\"unbounded\"><xs:annotation><xs:appinfo><b:groupInfo sequence_number=\"2\" /></xs:appinfo></xs:annotation>" + Field("X", 1) + Field("Y", 2) + "</xs:sequence>"
            + Field("Z", 3) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("a,1,2,3,4,z", Render(schema, "<R xmlns=\"urn:t\"><A>a</A><X>1</X><Y>2</Y><X>3</X><Y>4</Y><Z>z</Z></R>"));
        Assert.Equal("a,z", Render(schema, "<R xmlns=\"urn:t\"><A>a</A><Z>z</Z></R>"));
        Assert.Throws<InvalidOperationException>(() => Render(schema, "<R xmlns=\"urn:t\"><A>a</A><X>1</X><Z>z</Z></R>"));
    }

    [Fact]
    public void AnAttributeOnlyRecordWritesItsAttributesAsFields()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"|\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence><xs:element name=\"Setting\" maxOccurs=\"unbounded\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" repeating_delimiter_type=\"char\" repeating_delimiter=\";\" sequence_number=\"1\"")
            + "<xs:complexType><xs:attribute name=\"Name\" type=\"xs:string\">" + FieldInfo("sequence_number=\"1\"") + "</xs:attribute><xs:attribute name=\"Value\" type=\"xs:string\">" + FieldInfo("sequence_number=\"2\"") + "</xs:attribute></xs:complexType></xs:element>"
            + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("a,1;b,", Render(schema, "<R xmlns=\"urn:t\"><Setting Name=\"a\" Value=\"1\" /><Setting Name=\"b\" /></R>"));
    }

    [Fact]
    public void AttributesComeBeforeChildElementsInOneSequenceNumberOrder()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("E", 2) + "</xs:sequence><xs:attribute name=\"A\" type=\"xs:string\">" + FieldInfo("sequence_number=\"1\"") + "</xs:attribute></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("a,e", Render(schema, "<R xmlns=\"urn:t\" A=\"a\"><E>e</E></R>"));
        var outOfOrder = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("E", 2) + "</xs:sequence><xs:attribute name=\"A\" type=\"xs:string\">" + FieldInfo("sequence_number=\"3\"") + "</xs:attribute></xs:complexType></xs:element></xs:schema>";
        Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([outOfOrder]));
    }

    [Fact]
    public void RepeatedRecordsCanUseTheirOwnRepeatingDelimiter()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"|\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1)
            + "<xs:element name=\"Item\" maxOccurs=\"unbounded\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\"^\" repeating_delimiter_type=\"char\" repeating_delimiter=\"~\" sequence_number=\"2\"")
            + "<xs:complexType><xs:sequence>" + Field("X", 1) + Field("Y", 2) + "</xs:sequence></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Equal("a|1^2~3^4", Render(schema, "<R xmlns=\"urn:t\"><A>a</A><Item><X>1</X><Y>2</Y></Item><Item><X>3</X><Y>4</Y></Item></R>"));
    }

    [Fact]
    public void ReferencedElementsAndImportsResolveAcrossDocuments()
    {
        var imported = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:b=\"http://schemas.microsoft.com/BizTalk/2003\" targetNamespace=\"urn:other\" xmlns=\"urn:other\">"
            + "<xs:element name=\"Row\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence><xs:element name=\"V\" type=\"xs:string\">" + FieldInfo("sequence_number=\"1\"") + "</xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        var root = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:b=\"http://schemas.microsoft.com/BizTalk/2003\" xmlns:o=\"urn:other\" targetNamespace=\"urn:t\" xmlns=\"urn:t\">"
            + Info("Rows") + "<xs:element name=\"Rows\">" + Record("structure=\"delimited\" child_delimiter_type=\"hex\" child_delimiter=\"0x0A\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence><xs:element ref=\"o:Row\" maxOccurs=\"unbounded\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"") + "</xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        var schema = FlatFileSchema.Compile([root, imported]);
        Assert.Equal("urn:t#Rows", schema.MessageType);
        Assert.Equal("a\nb", schema.Render("<Rows xmlns=\"urn:t\" xmlns:o=\"urn:other\"><o:Row><V xmlns=\"\">a</V></o:Row><o:Row><V xmlns=\"\">b</V></o:Row></Rows>"));
    }

    [Fact]
    public void InstancesThatDoNotFitTheSchemaFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), "<Other xmlns=\"urn:t\" />"));
        Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), Xml("<Line><Name>a</Name></Line>")));
        Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), Xml("<Title>T</Title><Line><Name>a</Name><Qty>1</Qty></Line><Extra/>")));
        Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), Xml("<Line><Qty>1</Qty><Name>a</Name></Line>")));
        Assert.Throws<InvalidOperationException>(() => Render(DelimitedSchema(), Xml("")));
    }

    [Theory]
    [InlineData("<xs:choice><xs:element name=\"A\" type=\"xs:string\"/></xs:choice>", "choice")]
    [InlineData("<xs:sequence><xs:sequence/></xs:sequence><xs:attribute name=\"x\"/>", "")]
    public void UnsupportedContentModelsAreNamed(string content, string expected)
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType>" + content + "</xs:complexType></xs:element></xs:schema>";
        var failure = Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([schema]));
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<b:schemaInfo standard=\"Flat File\" root_reference=\"R\" codepage=\"1252\"/>", "codepage")]
    [InlineData("<b:schemaInfo standard=\"Flat File\" root_reference=\"R\" count_positions_by_byte=\"true\"/>", "count_positions_by_byte")]
    [InlineData("<b:schemaInfo standard=\"Flat File\" root_reference=\"R\" surprise=\"1\"/>", "surprise")]
    public void UnsupportedSchemaSettingsAreNamed(string info, string expected)
    {
        var schema = Header + "<xs:annotation><xs:appinfo>" + info + "</xs:appinfo></xs:annotation><xs:element name=\"R\">"
            + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("A", 1) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Contains(expected, Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([schema])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedRecordAndFieldAnnotationsAreNamed()
    {
        string Wrap(string record, string field) => Header + Info("R") + "<xs:element name=\"R\">" + Record(record) + "<xs:complexType><xs:sequence>" + Field("A", 1, field) + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        const string good = "structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"";
        Assert.Contains("escape_char", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap(good + " escape_char=\"\\\"", "")])).Message, StringComparison.Ordinal);
        Assert.Contains("pos_offset", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap(good, "pos_offset=\"4\"")])).Message, StringComparison.Ordinal);
        Assert.Contains("datetime_format", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap(good, "datetime_format=\"yyyyMMdd\"")])).Message, StringComparison.Ordinal);
        Assert.Contains("surprise", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap(good, "surprise=\"1\"")])).Message, StringComparison.Ordinal);
        Assert.Contains("structure", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap("structure=\"other\" sequence_number=\"1\"", "")])).Message, StringComparison.Ordinal);
        Assert.Contains("preserve_delimiter_for_empty_data", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([Wrap(good, "").Replace("preserve_delimiter_for_empty_data=\"true\"", "preserve_delimiter_for_empty_data=\"false\"")])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatePatternWithAnUnknownLetterIsRejectedAtCompileTime()
    {
        var schema = Header + Info("R") + "<xs:element name=\"R\">" + Record("structure=\"delimited\" child_delimiter_type=\"char\" child_delimiter=\",\" sequence_number=\"1\"")
            + "<xs:complexType><xs:sequence>" + Field("D", 1, "datetime_format=\"yyyyQQ\"", "xs:date") + "</xs:sequence></xs:complexType></xs:element></xs:schema>";
        Assert.Contains("yyyyQQ", Assert.Throws<NotSupportedException>(() => FlatFileSchema.Compile([schema])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsFlatFileRecognisesTheAnnotationOnly()
    {
        Assert.True(FlatFileSchema.IsFlatFile(DelimitedSchema()));
        Assert.False(FlatFileSchema.IsFlatFile(Header + "<xs:element name=\"R\" type=\"xs:string\"/></xs:schema>"));
        Assert.False(FlatFileSchema.IsFlatFile("not xml"));
    }

    [Fact]
    public async Task TheModuleRendersWithEditableSettingsAndRejectsBadChoices()
    {
        var module = new FlatFileSchemaRenderModule();
        var batch = new IntegrationBatch([new IntegrationPayload("m.xml", BinaryData.FromString(Xml("<Line><Name>a</Name><Qty>1</Qty></Line>")), "application/xml", new Dictionary<string, string>())]);
        var result = await module.RenderAsync(null!, batch, new ModuleStepDefinition { Module = "flat-file-schema-render", Settings = new() { ["schemaText"] = DelimitedSchema(), ["absentOptional"] = "Skip" } }, CancellationToken.None);
        Assert.Equal("a,1\r\n", result.Payloads[0].GetText());
        Assert.Equal("m.txt", result.Payloads[0].Name);
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(null!, batch, new ModuleStepDefinition { Module = "flat-file-schema-render", Settings = new() { ["schemaText"] = DelimitedSchema(), ["hourClock"] = "13" } }, CancellationToken.None));
        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(() => module.RenderAsync(null!, batch, new ModuleStepDefinition { Module = "flat-file-schema-render", Settings = new() { ["schemaText"] = DelimitedSchema().Replace("standard=\"Flat File\"", "standard=\"Flat File\" codepage=\"1252\"") } }, CancellationToken.None));
        Assert.Contains("codepage", unsupported.Message, StringComparison.Ordinal);
        var withImports = await module.RenderAsync(null!, batch, new ModuleStepDefinition { Module = "flat-file-schema-render", Settings = new() { ["schemaText"] = System.Text.Json.JsonSerializer.Serialize(new[] { DelimitedSchema() }) } }, CancellationToken.None);
        Assert.Equal("\r\na,1,\r\n", withImports.Payloads[0].GetText());
    }
}
