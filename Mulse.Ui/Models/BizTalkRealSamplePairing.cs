using System.IO.Compression;
using System.Text;

namespace Mulse.Ui.Models;

/// <summary>One recorded input with the real output BizTalk produced for it.</summary>
public sealed record BizTalkRealSample(string Name, string InputName, string Input, string Expected);

public sealed record BizTalkRealSampleResult(IReadOnlyList<BizTalkRealSample> Samples, IReadOnlyList<string> Problems);

/// <summary>
/// Turns recorded real-output evidence into input and expected-output pairs. Nothing is executed or sent anywhere.
/// A pair is two text files that share a name and differ only in their role, marked in either of two ways:
/// <c>claim1.in.xml</c> with <c>claim1.out.xml</c> (also <c>.input</c> and <c>.output</c>), or the same relative path under a folder named
/// <c>in</c>/<c>input</c> and one named <c>out</c>/<c>output</c>. Several files travel as a zip, or as separate uploads.
/// </summary>
public static class BizTalkRealSamplePairing
{
    public const int MaxSamples = 20;
    public const int MaxZipEntries = 200;
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    private static readonly string[] InputWords = ["in", "input", "inputs"];
    private static readonly string[] OutputWords = ["out", "output", "outputs"];

    private sealed record Candidate(string Source, string Path, byte[] Bytes);

    private enum Role { None, Input, Output }

    public static BizTalkRealSampleResult Pair(IReadOnlyList<BizTalkSessionEvidence> evidence, IReadOnlyDictionary<string, BizTalkEvidenceContent> content)
    {
        var problems = new List<string>();
        var files = new List<Candidate>();
        long total = 0;

        foreach (var item in evidence.Where(e => e.Kind == BizTalkEvidenceKinds.RealOutput))
        {
            var label = item.Name ?? item.Id;
            if (item.Origin == BizTalkEvidenceOrigins.Path) { problems.Add($"'{label}' is a path on the service machine and is not read here. Upload the files instead."); continue; }
            if (!content.TryGetValue(item.Id, out var stored)) { problems.Add($"'{label}' has no content in this session. Add it again."); continue; }

            if (item.Origin == BizTalkEvidenceOrigins.Text && stored.Text is { } text)
            {
                Add(files, ref total, problems, label, label, Encoding.UTF8.GetBytes(text));
            }
            else if (stored.ContentBase64 is { } base64)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(base64); }
                catch (FormatException) { problems.Add($"'{label}' could not be read."); continue; }
                if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4) ReadZip(files, ref total, problems, label, bytes);
                else Add(files, ref total, problems, label, label, bytes);
            }
        }

        var inputs = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        var outputs = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var (role, key) = Classify(file.Path);
            if (role == Role.None) { problems.Add($"'{file.Path}' is not marked as an input or an output (see the naming rules)."); continue; }
            var target = role == Role.Input ? inputs : outputs;
            if (!target.TryAdd(key, file)) problems.Add($"More than one {(role == Role.Input ? "input" : "output")} was found for '{key}'. Only the first is used.");
        }

        var samples = new List<BizTalkRealSample>();
        foreach (var key in inputs.Keys.Concat(outputs.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!inputs.TryGetValue(key, out var input)) { problems.Add($"There is an output for '{key}' but no input."); continue; }
            if (!outputs.TryGetValue(key, out var output)) { problems.Add($"There is an input for '{key}' but no output."); continue; }
            if (key.Length > 128) { problems.Add($"The sample name '{key[..40]}...' is longer than 128 characters."); continue; }
            var inputText = Decode(input.Bytes);
            var outputText = Decode(output.Bytes);
            if (inputText is null || outputText is null) { problems.Add($"'{key}' is not text, so it was left out."); continue; }
            if (samples.Count >= MaxSamples) { problems.Add($"Only the first {MaxSamples} samples are used."); break; }
            samples.Add(new(key, System.IO.Path.GetFileName(input.Path), inputText, outputText));
        }

        return new(samples, problems);
    }

    /// <summary>The guessed content type for an input payload.</summary>
    public static string ContentTypeOf(string text) => text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<') ? "application/xml" : "text/plain";

    private static void Add(List<Candidate> files, ref long total, List<string> problems, string source, string path, byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) { problems.Add($"'{path}' is larger than {MaxFileBytes / (1024 * 1024)} MB and was left out."); return; }
        total += bytes.Length;
        if (total > MaxTotalBytes) { problems.Add($"The samples are larger than {MaxTotalBytes / (1024 * 1024)} MB in total; '{path}' and the rest were left out."); return; }
        files.Add(new(source, path, bytes));
    }

    private static void ReadZip(List<Candidate> files, ref long total, List<string> problems, string source, byte[] bytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var seen = 0;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var path = entry.FullName.Replace('\\', '/');
                if (path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith('.')) continue;
                if (++seen > MaxZipEntries) { problems.Add($"'{source}' has more than {MaxZipEntries} files; the rest were left out."); return; }
                if (path.StartsWith('/') || path.Split('/').Contains("..")) { problems.Add($"'{path}' in '{source}' has an unsafe path and was left out."); continue; }
                if (entry.Length > MaxFileBytes) { problems.Add($"'{path}' is larger than {MaxFileBytes / (1024 * 1024)} MB and was left out."); continue; }
                if (total + entry.Length > MaxTotalBytes) { problems.Add($"The samples are larger than {MaxTotalBytes / (1024 * 1024)} MB in total; '{path}' and the rest were left out."); return; }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                // The declared length can lie, so the read itself is limited too.
                var chunk = new byte[16 * 1024];
                int read;
                var tooLarge = false;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > MaxFileBytes) { tooLarge = true; break; }
                    buffer.Write(chunk, 0, read);
                }
                if (tooLarge) { problems.Add($"'{path}' is larger than {MaxFileBytes / (1024 * 1024)} MB and was left out."); continue; }
                Add(files, ref total, problems, source, path, buffer.ToArray());
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException)
        {
            problems.Add($"'{source}' is not a readable zip file: {exception.Message}");
        }
    }

    private static (Role Role, string Key) Classify(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0) return (Role.None, "");
        var file = segments[^1];
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        var folders = segments.Take(segments.Count - 1).ToList();

        var dot = stem.LastIndexOf('.');
        if (dot > 0)
        {
            var marker = stem[(dot + 1)..];
            var role = RoleOf(marker);
            if (role != Role.None) return (role, string.Join('/', folders.Append(stem[..dot])));
        }

        for (var index = 0; index < folders.Count; index++)
        {
            var role = RoleOf(folders[index]);
            if (role == Role.None) continue;
            var rest = folders.Where((_, position) => position != index).Append(stem);
            return (role, string.Join('/', rest));
        }

        return (Role.None, "");
    }

    private static Role RoleOf(string word)
        => InputWords.Contains(word, StringComparer.OrdinalIgnoreCase) ? Role.Input : OutputWords.Contains(word, StringComparer.OrdinalIgnoreCase) ? Role.Output : Role.None;

    // Text only. The byte-order mark is kept (as U+FEFF) so the comparer can report it, and files with null bytes are not text.
    private static string? Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }
}
