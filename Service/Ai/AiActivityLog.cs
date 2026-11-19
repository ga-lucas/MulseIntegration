using System.Text;
using System.Text.Json;

namespace Service.Ai;

/// <summary>What the service reports about one AI request. It never carries the prompt, the reply, sample values, secrets or config values: only a length and a hash of what was sent.</summary>
public sealed record AiActivityRecord(
    string Surface,
    string Action,
    string Provider,
    string Model,
    IReadOnlyList<string> Categories,
    string? FlowId,
    int PayloadChars,
    string PayloadSha256,
    string Outcome,
    string? Detail = null,
    bool? ProposalProduced = null,
    int? ProposalErrors = null);

public sealed record AiActivityEntry(
    Guid Id,
    DateTimeOffset At,
    string? Actor,
    string Surface,
    string Action,
    string Provider,
    string Model,
    IReadOnlyList<string> Categories,
    string? FlowId,
    int PayloadChars,
    string PayloadSha256,
    string Outcome,
    string? Detail,
    bool? ProposalProduced,
    int? ProposalErrors);

/// <summary>An append-only record of AI requests, separate from run history so it is not pruned with it.</summary>
public interface IAiActivityLog
{
    void Record(AiActivityRecord record);

    /// <summary>Newest first. <paramref name="flowId"/> limits the list to one flow.</summary>
    IReadOnlyList<AiActivityEntry> List(string? flowId, int take);
}

/// <summary>
/// One JSON object per line in <c>activity.jsonl</c>. When the file passes about 5 MB it is renamed and a new one starts; renamed files are kept and never deleted by Mulse.
/// A write problem is logged and never stops the user's request, because the request has already been decided by the time it is recorded.
/// </summary>
internal sealed class FileAiActivityLog(string directory, TimeProvider time, IHttpContextAccessor? accessor, ILogger<FileAiActivityLog> logger) : IAiActivityLog
{
    public const int MaxTake = 200;
    private const long RotateBytes = 5 * 1024 * 1024;
    private const int MaxDetailChars = 300;
    private const int MaxFlowIdChars = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Lock _gate = new();

    private string CurrentPath => Path.Combine(directory, "activity.jsonl");

    public void Record(AiActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var now = time.GetUtcNow();
        var entry = new AiActivityEntry(Guid.NewGuid(), now, Clip(accessor?.HttpContext?.User.Identity?.Name, 128), Clip(record.Surface, 64) ?? "", Clip(record.Action, 64) ?? "",
            Clip(record.Provider, 64) ?? "", Clip(record.Model, 128) ?? "", [.. record.Categories.Select(category => Clip(category, 40) ?? "")], Clip(record.FlowId, MaxFlowIdChars),
            record.PayloadChars, record.PayloadSha256, record.Outcome, Clip(record.Detail, MaxDetailChars), record.ProposalProduced, record.ProposalErrors);
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (new FileInfo(CurrentPath) is { Exists: true, Length: > RotateBytes })
                    File.Move(CurrentPath, Path.Combine(directory, $"activity-{now:yyyyMMddHHmmssfff}.jsonl"));
                File.AppendAllText(CurrentPath, line, Utf8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The AI activity log could not be written ({ExceptionType}).", exception.GetType().Name);
            }
        }
    }

    public IReadOnlyList<AiActivityEntry> List(string? flowId, int take)
    {
        if (take is < 1 or > MaxTake) throw new ArgumentException($"take must be between 1 and {MaxTake}.");
        var found = new List<AiActivityEntry>();
        lock (_gate)
        {
            if (!Directory.Exists(directory)) return found;
            var files = Directory.EnumerateFiles(directory, "activity*.jsonl").OrderByDescending(path => Path.GetFileName(path) == "activity.jsonl" ? "z" : Path.GetFileName(path), StringComparer.Ordinal);
            foreach (var file in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(file, Utf8); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("An AI activity file could not be read ({ExceptionType}).", exception.GetType().Name);
                    continue;
                }
                for (var index = lines.Length - 1; index >= 0 && found.Count < take; index--)
                {
                    AiActivityEntry? entry;
                    try { entry = JsonSerializer.Deserialize<AiActivityEntry>(lines[index], Json); }
                    catch (JsonException) { continue; }
                    if (entry is null) continue;
                    if (!string.IsNullOrWhiteSpace(flowId) && !string.Equals(entry.FlowId, flowId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    found.Add(entry);
                }
                if (found.Count >= take) break;
            }
        }
        return found;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new StringBuilder(Math.Min(value.Length, max));
        foreach (var character in value)
        {
            if (builder.Length >= max) break;
            builder.Append(char.IsControl(character) ? ' ' : character);
        }
        return builder.ToString().Trim();
    }
}
