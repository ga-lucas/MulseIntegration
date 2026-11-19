using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mulse.Ui.Models;

/// <summary>What the user told the wizard about an evidence request. A skipped or unavailable request never blocks the import; the importer keeps failing closed.</summary>
public static class BizTalkRequestAnswers
{
    public const string Skipped = "Skipped";
    public const string NotAvailable = "NotAvailable";
}

public sealed class BizTalkSessionRequestNote
{
    public string Answer { get; set; } = BizTalkRequestAnswers.Skipped;
    public string? Note { get; set; }
}

/// <summary>Metadata about one evidence item. Pasted and uploaded content is kept apart (<see cref="BizTalkEvidenceContent"/>) because it can be large and sensitive.</summary>
public sealed class BizTalkSessionEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = BizTalkEvidenceKinds.Source;
    public string Origin { get; set; } = BizTalkEvidenceOrigins.Path;
    public string? Name { get; set; }
    public string? Path { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>The bytes or text of a pasted or uploaded evidence item. Never part of the stored session index.</summary>
public sealed class BizTalkEvidenceContent
{
    public string? Text { get; set; }
    public string? ContentBase64 { get; set; }
}

/// <summary>
/// One import in progress, kept in the browser. The service stays stateless: every analysis receives the whole session and returns the result.
/// This class holds no UI state, so it can be tested without a browser.
/// </summary>
public sealed class BizTalkImportSession
{
    public const int CurrentVersion = 1;
    public const int MaxEvidenceItems = 32;
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 80;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "import";
    public DateTimeOffset UpdatedUtc { get; set; }

    public string SourcePath { get; set; } = string.Empty;
    public List<string> AdditionalSourcePaths { get; set; } = [];
    public List<string> AdditionalBindingPaths { get; set; } = [];
    public List<string> AdditionalRulePaths { get; set; } = [];
    public bool GenerateNativeFlow { get; set; } = true;
    public bool DeriveNativeMapChoices { get; set; } = true;
    public Dictionary<string, string> NativePolicyVersions { get; set; } = new(StringComparer.Ordinal);
    public string? NativeHostConfigurationPath { get; set; }
    public List<string> NativeConfigurationHelperPaths { get; set; } = [];
    public string? NativeMapChoicesJson { get; set; }

    public List<BizTalkSessionEvidence> Evidence { get; set; } = [];

    /// <summary>Answers by evidence request id.</summary>
    public Dictionary<string, BizTalkSessionRequestNote> RequestNotes { get; set; } = new(StringComparer.Ordinal);

    public BizTalkAnalysisSummaryViewModel? LastSummary { get; set; }

    /// <summary>The flow created or updated from this session, so re-entry offers an update instead of a second import.</summary>
    public string? ImportedFlowId { get; set; }

    /// <summary>Content for pasted and uploaded items by evidence id. Not serialized with the session index.</summary>
    [JsonIgnore]
    public Dictionary<string, BizTalkEvidenceContent> Content { get; set; } = new(StringComparer.Ordinal);

    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Session names become storage keys, so they stay plain: letters, digits, spaces, dot, dash and underscore.</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && name == name.Trim() && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_');

    /// <summary>Derives a unique, valid id from the kind and name, for example <c>bindings-export-2</c>.</summary>
    public string NewEvidenceId(string kind, string? name)
    {
        var stem = new StringBuilder(kind.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(name))
        {
            stem.Append('-');
            foreach (var c in System.IO.Path.GetFileNameWithoutExtension(name.Trim()))
                stem.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-');
        }

        var baseId = stem.ToString().Trim('-', '.');
        if (baseId.Length > MaxIdLength - 4) baseId = baseId[..(MaxIdLength - 4)];
        var candidate = baseId;
        for (var i = 2; Evidence.Any(e => string.Equals(e.Id, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{baseId}-{i}";
        return candidate;
    }

    private BizTalkSessionEvidence Register(string kind, string origin, string? name, string? path, long size, TimeProvider time, string? replaceId)
    {
        if (!BizTalkEvidenceKinds.All.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException($"Unknown evidence kind '{kind}'.", nameof(kind));
        if (name is { Length: > MaxNameLength }) throw new ArgumentException($"Name is longer than {MaxNameLength} characters.", nameof(name));
        if (replaceId is null && Evidence.Count >= MaxEvidenceItems) throw new InvalidOperationException($"A session holds at most {MaxEvidenceItems} evidence items. Remove one first.");

        var id = replaceId ?? NewEvidenceId(kind, name ?? path);
        Evidence.RemoveAll(e => e.Id == replaceId);
        var item = new BizTalkSessionEvidence { Id = id, Kind = kind, Origin = origin, Name = name?.Trim(), Path = path, AddedUtc = time.GetUtcNow(), SizeBytes = size };
        Evidence.Add(item);
        UpdatedUtc = item.AddedUtc;
        return item;
    }

    /// <summary>Adds a file or folder that already exists on the service machine.</summary>
    public BizTalkSessionEvidence AddPath(string kind, string path, string? name, TimeProvider time, string? replaceId = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        Content.Remove(replaceId ?? string.Empty);
        return Register(kind, BizTalkEvidenceOrigins.Path, name ?? System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), path.Trim(), 0, time, replaceId);
    }

    public BizTalkSessionEvidence AddText(string kind, string name, string text, TimeProvider time, string? replaceId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Pasted text is empty.", nameof(text));
        var item = Register(kind, BizTalkEvidenceOrigins.Text, name, null, Encoding.UTF8.GetByteCount(text), time, replaceId);
        Content[item.Id] = new BizTalkEvidenceContent { Text = text };
        return item;
    }

    /// <summary>Adds a browser upload: a single file, or a .zip when several files or folders are expected.</summary>
    public BizTalkSessionEvidence AddUpload(string kind, string name, byte[] bytes, TimeProvider time, string? replaceId = null)
    {
        if (bytes.Length == 0) throw new ArgumentException("The uploaded file is empty.", nameof(bytes));
        var item = Register(kind, BizTalkEvidenceOrigins.Upload, name, null, bytes.Length, time, replaceId);
        Content[item.Id] = new BizTalkEvidenceContent { ContentBase64 = Convert.ToBase64String(bytes) };
        return item;
    }

    public bool RemoveEvidence(string id)
    {
        Content.Remove(id);
        return Evidence.RemoveAll(e => e.Id == id) > 0;
    }

    /// <summary>Evidence whose content is gone (for example after importing a session exported without content). It is left out of requests until the user adds it again.</summary>
    public IReadOnlyList<BizTalkSessionEvidence> EvidenceMissingContent()
        => [.. Evidence.Where(e => e.Origin != BizTalkEvidenceOrigins.Path && !Content.ContainsKey(e.Id))];

    public IReadOnlyList<BizTalkEvidenceItemViewModel> BuildEvidence()
        => [.. Evidence.Select(ToViewModel).OfType<BizTalkEvidenceItemViewModel>()];

    private BizTalkEvidenceItemViewModel? ToViewModel(BizTalkSessionEvidence e) => e.Origin switch
    {
        BizTalkEvidenceOrigins.Path => new BizTalkEvidenceItemViewModel(e.Id, e.Kind, e.Origin, e.Name, e.Path),
        BizTalkEvidenceOrigins.Text when Content.TryGetValue(e.Id, out var c) && c.Text is not null => new BizTalkEvidenceItemViewModel(e.Id, e.Kind, e.Origin, e.Name, Text: c.Text),
        BizTalkEvidenceOrigins.Upload when Content.TryGetValue(e.Id, out var c) && c.ContentBase64 is not null => new BizTalkEvidenceItemViewModel(e.Id, e.Kind, e.Origin, e.Name, ContentBase64: c.ContentBase64),
        _ => null
    };

    /// <summary>The source path to send: the chosen path, or the first Source evidence when none was entered.</summary>
    public string EffectiveSourcePath()
    {
        if (!string.IsNullOrWhiteSpace(SourcePath)) return SourcePath.Trim();
        var source = BuildEvidence().FirstOrDefault(e => e.Kind == BizTalkEvidenceKinds.Source);
        return source is null ? string.Empty : $"evidence:{source.Id}";
    }

    public AnalyzeBizTalkSolutionRequestViewModel BuildAnalyzeRequest(bool includePreviousSummary = true)
    {
        var evidence = BuildEvidence();
        return new(
            EffectiveSourcePath(), Paths(AdditionalBindingPaths), Paths(AdditionalSourcePaths), Paths(AdditionalRulePaths), GenerateNativeFlow,
            GenerateNativeFlow && NativePolicyVersions.Count > 0 ? NativePolicyVersions.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) : null,
            GenerateNativeFlow && !string.IsNullOrWhiteSpace(NativeHostConfigurationPath) ? NativeHostConfigurationPath.Trim() : null,
            GenerateNativeFlow ? Paths(NativeConfigurationHelperPaths) : null,
            GenerateNativeFlow && !string.IsNullOrWhiteSpace(NativeMapChoicesJson) ? NativeMapChoicesJson : null,
            GenerateNativeFlow && DeriveNativeMapChoices)
        {
            Evidence = evidence.Count > 0 ? evidence : null,
            PreviousSummary = includePreviousSummary ? LastSummary : null
        };
    }

    /// <summary>The same analysis inputs for importing, planning or updating one candidate. It must match what was analyzed so the same candidate is reproduced.</summary>
    public CreateImportedBizTalkFlowRequestViewModel BuildImportRequest(string suggestedFlowId, string? newFlowId = null)
    {
        var analyze = BuildAnalyzeRequest(includePreviousSummary: false);
        return new(
            analyze.SourcePath, suggestedFlowId, analyze.AdditionalSourcePaths, analyze.AdditionalBindingPaths, analyze.AdditionalRulePaths, analyze.GenerateNativeFlow,
            analyze.NativePolicyVersions, analyze.NativeHostConfigurationPath, analyze.NativeConfigurationHelperPaths, analyze.NativeMapChoicesJson, analyze.DeriveNativeMapChoices)
        {
            Evidence = analyze.Evidence,
            NewFlowId = string.IsNullOrWhiteSpace(newFlowId) ? null : newFlowId.Trim()
        };
    }

    private static IReadOnlyList<string>? Paths(IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length == 0 ? null : list;
    }

    /// <summary>Open, Skipped, NotAvailable, or Supplied (evidence of an accepted kind was added since). Supplied does not mean satisfied: only a later analysis that no longer reports the request clears it.</summary>
    public string StatusOf(BizTalkEvidenceRequestViewModel request)
    {
        if (RequestNotes.TryGetValue(request.Id, out var note)) return note.Answer;
        return request.Accepts.Count > 0 && Evidence.Any(e => request.Accepts.Contains(e.Kind, StringComparer.Ordinal)) ? "Supplied" : "Open";
    }

    public void Answer(string requestId, string answer, string? note)
    {
        if (answer is not (BizTalkRequestAnswers.Skipped or BizTalkRequestAnswers.NotAvailable)) throw new ArgumentException($"Unknown answer '{answer}'.", nameof(answer));
        RequestNotes[requestId] = new BizTalkSessionRequestNote { Answer = answer, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() };
    }

    public void Reopen(string requestId) => RequestNotes.Remove(requestId);

    /// <summary>Records an analysis: keeps its summary for the next delta, and forgets answers to requests that no longer exist.</summary>
    public void ApplyAnalysis(BizTalkSolutionAnalysisViewModel analysis, TimeProvider time)
    {
        LastSummary = analysis.Summary;
        var open = analysis.EvidenceRequests.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in RequestNotes.Keys.Where(id => !open.Contains(id)).ToArray()) RequestNotes.Remove(id);
        UpdatedUtc = time.GetUtcNow();
    }

    /// <summary>Requests the importer still reports that the user has not answered.</summary>
    public IReadOnlyList<BizTalkEvidenceRequestViewModel> StillOpen(BizTalkSolutionAnalysisViewModel analysis)
        => [.. analysis.EvidenceRequests.Where(r => !RequestNotes.ContainsKey(r.Id))];

    /// <summary>The index form kept in local storage: everything except content.</summary>
    public string ToIndexJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A file the user can keep. Content is left out unless asked for, because evidence can hold sensitive configuration.</summary>
    public string Export(bool includeContent)
    {
        var export = new ExportEnvelope { Session = this, Content = includeContent ? Content : null };
        return JsonSerializer.Serialize(export, Json);
    }

    public static BizTalkImportSession FromIndexJson(string json)
    {
        var session = JsonSerializer.Deserialize<BizTalkImportSession>(json, Json) ?? throw new InvalidDataException("The session is empty.");
        return Validate(session);
    }

    public static BizTalkImportSession Import(string json)
    {
        ExportEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, Json); }
        catch (JsonException exception) { throw new InvalidDataException($"The file is not a session export: {exception.Message}", exception); }
        var session = Validate(envelope?.Session ?? throw new InvalidDataException("The file holds no session."));
        session.Content = envelope.Content is null ? new(StringComparer.Ordinal) : new(envelope.Content, StringComparer.Ordinal);
        foreach (var id in session.Content.Keys.Where(id => session.Evidence.All(e => e.Id != id)).ToArray()) session.Content.Remove(id);
        return session;
    }

    private static BizTalkImportSession Validate(BizTalkImportSession session)
    {
        if (session.Version is < 1 or > CurrentVersion) throw new InvalidDataException($"Session version {session.Version} is not supported by this version of the wizard.");
        if (!IsValidName(session.Name)) throw new InvalidDataException("The session needs a name of up to 80 letters, digits, spaces, dots, dashes or underscores.");
        if (session.Evidence.Count > MaxEvidenceItems) throw new InvalidDataException($"A session holds at most {MaxEvidenceItems} evidence items.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in session.Evidence)
        {
            if (!IsValidId(e.Id) || !seen.Add(e.Id)) throw new InvalidDataException($"Evidence id '{e.Id}' is invalid or repeated.");
            if (!BizTalkEvidenceKinds.All.Contains(e.Kind, StringComparer.Ordinal)) throw new InvalidDataException($"Evidence '{e.Id}' has an unknown kind '{e.Kind}'.");
            if (e.Origin is not (BizTalkEvidenceOrigins.Path or BizTalkEvidenceOrigins.Text or BizTalkEvidenceOrigins.Upload)) throw new InvalidDataException($"Evidence '{e.Id}' has an unknown origin '{e.Origin}'.");
            if (e.Origin == BizTalkEvidenceOrigins.Path && string.IsNullOrWhiteSpace(e.Path)) throw new InvalidDataException($"Evidence '{e.Id}' has no path.");
        }

        session.AdditionalSourcePaths ??= [];
        session.AdditionalBindingPaths ??= [];
        session.AdditionalRulePaths ??= [];
        session.NativeConfigurationHelperPaths ??= [];
        session.NativePolicyVersions = new Dictionary<string, string>(session.NativePolicyVersions ?? [], StringComparer.Ordinal);
        session.RequestNotes = new Dictionary<string, BizTalkSessionRequestNote>(session.RequestNotes ?? [], StringComparer.Ordinal);
        session.Content = new(StringComparer.Ordinal);
        return session;
    }

    private sealed class ExportEnvelope
    {
        public BizTalkImportSession? Session { get; set; }
        public Dictionary<string, BizTalkEvidenceContent>? Content { get; set; }
    }
}
