using System.Text.Json;
using System.Text.Json.Serialization;

namespace Service.Ai;

internal static class AiAuthoringJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 64,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new LenientStringConverter());
        return options;
    }
}

/// <summary>Reads numbers and booleans as their text so model output such as <c>"recursive": false</c> still maps to string settings.</summary>
internal sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => throw new JsonException("Expected a string value.")
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>The UI <c>FlowViewModel</c> JSON shape (camelCase), plus the server-issued <c>slot</c> token on steps used only inside a chat exchange.</summary>
internal sealed class AiFlowDoc
{
    public string? Id { get; set; } = "";
    public string? Name { get; set; }
    public bool Enabled { get; set; }
    public string? TriggerMode { get; set; }
    public TimeSpan? Interval { get; set; }
    public bool RunOnStartup { get; set; }
    public List<string>? FetchIds { get; set; }
    public List<string>? ParseIds { get; set; }
    public List<string>? FetchModules { get; set; }
    public List<string>? ParseModules { get; set; }
    public List<string>? AugmentModules { get; set; }
    public List<string>? RenderModules { get; set; }
    public List<string>? DeliverModules { get; set; }
    public List<AiFetchDoc>? Fetches { get; set; }
    public List<AiParseDoc>? Parses { get; set; }
    public List<AiStepDoc>? Augments { get; set; }
    public List<AiRouteDoc>? Deliveries { get; set; }
    public AiRetryDoc? Retry { get; set; }
    public string? Category { get; set; }
    public List<string>? MissingModules { get; set; }
    public JsonElement? Migration { get; set; }
}

internal sealed class AiFetchDoc
{
    public string? Id { get; set; } = "";
    public AiStepDoc? Fetch { get; set; }
    public List<string>? InputParseIds { get; set; }
}

internal sealed class AiParseDoc
{
    public string? Id { get; set; } = "";
    public AiStepDoc? Parse { get; set; }
    public List<string>? FetchNodeIds { get; set; }
}

internal sealed class AiRouteDoc
{
    public AiStepDoc? Render { get; set; }
    public List<AiActionDoc>? Delivers { get; set; }
    public string? AtomicScope { get; set; }
}

internal sealed class AiActionDoc
{
    public AiStepDoc? Deliver { get; set; }
    public AiStepDoc? Compensation { get; set; }
    public bool RequireConfirmation { get; set; }
}

internal sealed class AiStepDoc
{
    public string? Slot { get; set; }
    public string? Module { get; set; } = "";
    public Dictionary<string, string>? Settings { get; set; }
    public AiRetryDoc? Retry { get; set; }
    public string? Name { get; set; }
}

internal sealed class AiRetryDoc
{
    public int? MaxAttempts { get; set; }
    public double DelaySeconds { get; set; } = 5;
    public string? Backoff { get; set; }
    public double? MaxDelaySeconds { get; set; }
}
