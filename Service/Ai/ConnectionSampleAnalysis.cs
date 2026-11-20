using System.Text.Json;
using System.Text.Json.Nodes;
using Service.Models;

namespace Service.Ai;

internal static class ConnectionSampleAnalysis
{
    public const int MaxSamples = 8;
    public const int MaxSampleChars = 32_768;
    private const int MaxNodes = 20_000;

    public static ConnectionAnalysis Analyze(IReadOnlyList<ConnectionSample>? samples)
    {
        if (samples is null || samples.Count is < 1 or > MaxSamples)
            throw new ArgumentException($"Supply 1 to {MaxSamples} named input/expected-output scenarios.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<JsonNode?>();
        var outputs = new List<JsonNode?>();
        var errors = new List<string>();
        var shared = new List<object>();
        var nodes = 0;
        foreach (var sample in samples)
        {
            if (sample is null || string.IsNullOrWhiteSpace(sample.Name) || sample.Name.Length > 80
                || sample.Name.Any(char.IsControl) || !names.Add(sample.Name.Trim()))
                throw new ArgumentException("Each scenario needs a unique name of 1 to 80 characters without control characters.");
            var input = Read(sample.InputJson, "Input", false);
            var output = Read(sample.ExpectedJson, "Expected output", true);
            inputs.AddRange(Records(input));
            outputs.AddRange(Records(output));
            shared.Add(new
            {
                scenario = shared.Count + 1,
                input = sample.ShareInputValues ? input : null,
                expected = sample.ShareExpectedValues ? output : null
            });
        }
        var inputShape = Shape(inputs, "Input");
        var outputShape = Shape(outputs, "Output");
        var disclosure = JsonSerializer.Serialize(new { inputShape, outputShape, examples = shared }, JsonSerializerOptions.Web);
        if (disclosure.Length > 150_000) throw new ArgumentException("The combined disclosure is too large; use fewer or smaller shared samples.");
        return new(inputShape, outputShape, errors, disclosure);

        JsonNode Read(string? text, string label, bool requireArray)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSampleChars)
                throw new ArgumentException($"{label} samples must contain JSON of at most {MaxSampleChars} characters.");
            JsonNode? node;
            try { node = JsonNode.Parse(text, documentOptions: new() { MaxDepth = 16 }); }
            catch (JsonException) { throw new ArgumentException($"{label} sample is invalid JSON or exceeds 16 nesting levels."); }
            if (node is not (JsonObject or JsonArray) || requireArray && node is not JsonArray)
                throw new ArgumentException($"{label} must be {(requireArray ? "an array of objects" : "an object or array of objects")}.");
            if (node is JsonArray array && array.Any(item => item is not JsonObject))
                throw new ArgumentException($"{label} arrays must contain only objects.");
            Check(node);
            return node;
        }

        void Check(JsonNode? node)
        {
            if (++nodes > MaxNodes) throw new ArgumentException($"Samples exceed the combined {MaxNodes}-value limit.");
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (pair.Key.Length > 128 || pair.Key.Any(char.IsControl))
                        throw new ArgumentException("Sample field names must be at most 128 characters without control characters.");
                    Check(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var item in array) Check(item);
        }

        List<ConnectionShapeField> Shape(List<JsonNode?> records, string label)
        {
            var fields = new List<ConnectionShapeField>();
            if (records.Count == 0) errors.Add($"{label}: supply at least one populated record across the scenarios.");
            Visit(records, "", false);
            return fields;

            void Visit(List<JsonNode?> values, string path, bool optional)
            {
                if (fields.Count >= 256) throw new ArgumentException("Inferred shapes support at most 256 paths per side.");
                var types = values.Where(value => value is not null).Select(TypeOf).Distinct().Order(StringComparer.Ordinal).ToArray();
                fields.Add(new(path, types, optional, values.Any(value => value is null)));
                if (types.Length > 1) errors.Add($"{label} {Display(path)}: conflicting types ({string.Join(", ", types)}). Normalize the samples or clarify the contract before requesting a mapping.");
                if (types.Length == 0) errors.Add($"{label} {Display(path)}: only null observed; provide a representative non-null value.");
                var objects = values.OfType<JsonObject>().ToList();
                foreach (var key in objects.SelectMany(obj => obj.Select(pair => pair.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    var present = objects.Where(obj => obj.ContainsKey(key)).Select(obj => obj[key]).ToList();
                    Visit(present, path + "/" + key.Replace("~", "~0").Replace("/", "~1"), optional || present.Count < objects.Count);
                }
                var arrays = values.OfType<JsonArray>().ToList();
                if (arrays.Count > 0)
                {
                    var items = arrays.SelectMany(array => array).ToList();
                    if (items.Count == 0) errors.Add($"{label} {Display(path)}: array item shape is unknown; add a sample with an item.");
                    else Visit(items, path + "/*", optional);
                }
            }
        }
    }

    private static IEnumerable<JsonNode?> Records(JsonNode node) => node is JsonArray array ? array : [node];
    private static string Display(string path) => path.Length == 0 ? "(record)" : path;
    private static string TypeOf(JsonNode? node) => node switch
    {
        JsonObject => "object",
        JsonArray => "array",
        _ => node!.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null"
        }
    };
}
