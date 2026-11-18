using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Renders XML into flat-file text from a BizTalk flat-file schema (the schema-driven subset of the BizTalk flat-file assembler).
/// The schema and every behavior the schema leaves open are settings, so each can be reviewed and changed.
/// </summary>
public sealed class FlatFileSchemaRenderModule : IRenderModule
{
    private const int MaximumCachedSchemas = 64;
    private static readonly ConcurrentDictionary<string, FlatFileSchema> Compiled = new(StringComparer.Ordinal);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("schemaText", "Flat-file schema", "A BizTalk flat-file XSD (with b:recordInfo / b:fieldInfo annotations), or a JSON array of XSD texts whose first item is the root schema and the rest are the schemas it imports. Can be a {{config:name}} reference.", true, ModuleSettingInputKind.TextArea),
        new("wrapValues", "Wrap values", "Fields that have a wrap character: wrap every non-empty value (Always, assumed BizTalk behavior) or only values that contain a delimiter or line break (WhenNeeded). Empty values are never wrapped.", false, ModuleSettingInputKind.Select, "Always",
            [new("Always", "Always"), new("WhenNeeded", "Only when needed")]),
        new("tooLongValues", "Value longer than its position", "What a positional field does with a value that does not fit: stop with an error (assumed, nothing is lost silently) or cut the value.", false, ModuleSettingInputKind.Select, "Fail",
            [new("Fail", "Stop with an error"), new("Truncate", "Cut to fit")]),
        new("delimiterInValue", "Delimiter inside an unwrapped value", "A delimited value that contains its delimiter or a line break and has no wrap character: stop with an error (assumed) or write it, which makes the file ambiguous to read back.", false, ModuleSettingInputKind.Select, "Fail",
            [new("Fail", "Stop with an error"), new("Write", "Write as is")]),
        new("hourClock", "Meaning of hh in date/time formats", "Whether a lower-case 'hh' in a field's date/time format prints hours 00-23 (assumed) or a 12-hour clock like .NET. HH is always 00-23.", false, ModuleSettingInputKind.Select, "24",
            [new("24", "24-hour"), new("12", "12-hour")]),
        new("absentOptional", "Absent optional element", "An optional record or field that is missing from the XML: still write its place and delimiter (Keep, assumed because the schema preserves delimiters for empty data) or leave it out (Skip).", false, ModuleSettingInputKind.Select, "Keep",
            [new("Keep", "Keep its delimiter"), new("Skip", "Leave it out")])
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "flat-file-schema-render",
        "Flat-file schema render",
        ModuleKind.Render,
        "Renders an XML message as delimited or positional flat-file text using a BizTalk flat-file schema, the way the BizTalk flat-file assembler does for the supported schema features. Unsupported schema features and messages that do not fit the schema stop with an error naming the problem.",
        SettingDescriptors,
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var schemaText = ModuleSettings.GetRequired(step.Settings, "schemaText", Descriptor.Id);
        var options = ReadOptions(step.Settings);
        var schema = Schema(schemaText);
        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = schema.Render(payload.GetText(), options);
            return new IntegrationPayload(Path.ChangeExtension(payload.Name, ".txt"), BinaryData.FromString(rendered), "text/plain",
                new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase) { ["renderedBy"] = Descriptor.Id, ["renderedFormat"] = "FlatFile" });
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    internal static FlatFileRenderOptions ReadOptions(IReadOnlyDictionary<string, string> settings)
    {
        string Choice(string key, string fallback, params string[] allowed)
        {
            var value = settings.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;
            return allowed.FirstOrDefault(option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Setting '{key}' must be one of: {string.Join(", ", allowed)}.");
        }
        return new()
        {
            WrapAlways = Choice("wrapValues", "Always", "Always", "WhenNeeded") == "Always",
            TruncateTooLong = Choice("tooLongValues", "Fail", "Fail", "Truncate") == "Truncate",
            WriteDelimiterInValue = Choice("delimiterInValue", "Fail", "Fail", "Write") == "Write",
            TwelveHourClock = Choice("hourClock", "24", "24", "12") == "12",
            KeepAbsentPlaceholders = Choice("absentOptional", "Keep", "Keep", "Skip") == "Keep"
        };
    }

    private FlatFileSchema Schema(string schemaText)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schemaText)));
        if (Compiled.TryGetValue(key, out var cached)) return cached;
        FlatFileSchema compiled;
        try { compiled = FlatFileSchema.Compile(FlatFileSchema.SplitDocuments(schemaText)); }
        catch (NotSupportedException exception) { throw new InvalidOperationException("The flat-file schema uses a feature this renderer does not support: " + exception.Message, exception); }
        if (Compiled.Count >= MaximumCachedSchemas) Compiled.Clear();
        Compiled[key] = compiled;
        return compiled;
    }
}
