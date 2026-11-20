using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Service.Models;

namespace Service.BizTalkImport.Evidence;

/// <summary>
/// Turns supplied evidence into files the analysis can read. Pasted text and uploads are written to a request-scoped temporary folder that is deleted when this object is disposed; a path is used as it is.
/// Uploaded content is untrusted: a zip is extracted under strict limits and anything that could escape the folder is rejected by name.
/// </summary>
public sealed partial class BizTalkEvidenceMaterializer : IDisposable
{
    public const int MaxItems = 32;
    public const int MaxTextCharacters = 16_000_000;
    public const long MaxUploadBytes = 64L * 1024 * 1024;
    public const int MaxZipEntries = 5_000;
    public const long MaxZipEntryBytes = 128L * 1024 * 1024;
    public const long MaxZipTotalBytes = 512L * 1024 * 1024;

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".cab", ".jar", ".nupkg", ".msi" };

    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private string? _root;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDeviceName();

    /// <summary>The file or folder each evidence id resolves to.</summary>
    public IReadOnlyDictionary<string, string> Paths => _paths;

    public static BizTalkEvidenceMaterializer Create(IReadOnlyList<BizTalkEvidenceItem> items)
    {
        var materializer = new BizTalkEvidenceMaterializer();
        try
        {
            materializer.Materialize(items);
            return materializer;
        }
        catch
        {
            materializer.Dispose();
            throw;
        }
    }

    private void Materialize(IReadOnlyList<BizTalkEvidenceItem> items)
    {
        if (items.Count > MaxItems) throw new ArgumentException($"Supply at most {MaxItems} evidence items.");
        foreach (var item in items)
        {
            if (item is null || !IdPattern().IsMatch(item.Id ?? "")) throw new ArgumentException("Each evidence item needs an id of 1-64 letters, digits, dot, dash or underscore.");
            if (_paths.ContainsKey(item.Id)) throw new ArgumentException($"Evidence id '{item.Id}' is used more than once.");
            _paths[item.Id] = item.Origin switch
            {
                BizTalkEvidenceOrigin.Path => ResolvePath(item),
                BizTalkEvidenceOrigin.Text => WriteText(item),
                BizTalkEvidenceOrigin.Upload => WriteUpload(item),
                _ => throw new ArgumentException($"Evidence '{item.Id}' has an unknown origin.")
            };
        }
    }

    private static string ResolvePath(BizTalkEvidenceItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path) || item.Path.Length > 4096)
            throw new ArgumentException($"Evidence '{item.Id}' needs a path of at most 4096 characters.");
        if (item.Text is not null || item.ContentBase64 is not null)
            throw new ArgumentException($"Evidence '{item.Id}' is a path, so it must not also carry text or uploaded content.");
        return Path.GetFullPath(item.Path);
    }

    private string WriteText(BizTalkEvidenceItem item)
    {
        if (item.Text is null) throw new ArgumentException($"Evidence '{item.Id}' is pasted text but has no text.");
        if (item.Text.Length > MaxTextCharacters) throw new ArgumentException($"Evidence '{item.Id}' text is longer than {MaxTextCharacters} characters.");
        var directory = ItemDirectory(item);
        var target = Path.Combine(directory, SafeFileName(item));
        File.WriteAllText(target, item.Text, new UTF8Encoding(false));
        return target;
    }

    private string WriteUpload(BizTalkEvidenceItem item)
    {
        if (string.IsNullOrEmpty(item.ContentBase64)) throw new ArgumentException($"Evidence '{item.Id}' is an upload but has no content.");
        if (item.ContentBase64.Length / 4 * 3 > MaxUploadBytes + 3) throw new ArgumentException($"Evidence '{item.Id}' is larger than {MaxUploadBytes / 1024 / 1024} MB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(item.ContentBase64); }
        catch (FormatException) { throw new ArgumentException($"Evidence '{item.Id}' content is not valid base64."); }
        if (bytes.Length > MaxUploadBytes) throw new ArgumentException($"Evidence '{item.Id}' is larger than {MaxUploadBytes / 1024 / 1024} MB.");

        var directory = ItemDirectory(item);
        if (!string.Equals(Path.GetExtension(item.Name ?? ""), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.Combine(directory, SafeFileName(item));
            File.WriteAllBytes(target, bytes);
            return target;
        }

        Extract(item.Id, bytes, directory);
        return directory;
    }

    private static void Extract(string id, byte[] bytes, string directory)
    {
        ZipArchive archive;
        try { archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new ArgumentException($"Evidence '{id}' is not a readable zip file."); }

        using (archive)
        {
            if (archive.Entries.Count > MaxZipEntries) throw new ArgumentException($"Evidence '{id}' zip has more than {MaxZipEntries} entries.");
            var rootPrefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) { ValidateEntryName(id, name); continue; }
                ValidateEntryName(id, name);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new ArgumentException($"Evidence '{id}' zip entry '{name}' is a link, which is not allowed.");
                if (ArchiveExtensions.Contains(Path.GetExtension(name)))
                    throw new ArgumentException($"Evidence '{id}' zip entry '{name}' is a nested archive, which is not allowed. Zip the files directly.");
                if (entry.Length > MaxZipEntryBytes) throw new ArgumentException($"Evidence '{id}' zip entry '{name}' is larger than {MaxZipEntryBytes / 1024 / 1024} MB.");

                var target = Path.GetFullPath(Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Evidence '{id}' zip entry '{name}' points outside the extraction folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // The declared size is not trusted: the bytes actually written are counted.
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    total += read;
                    if (written > MaxZipEntryBytes) throw new ArgumentException($"Evidence '{id}' zip entry '{name}' is larger than {MaxZipEntryBytes / 1024 / 1024} MB.");
                    if (total > MaxZipTotalBytes) throw new ArgumentException($"Evidence '{id}' zip expands to more than {MaxZipTotalBytes / 1024 / 1024} MB.");
                    output.Write(buffer, 0, read);
                }
            }
        }
    }

    private static void ValidateEntryName(string id, string name)
    {
        if (name.Length == 0 || name.Length > 512 || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl)
            || name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is ".." or "." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.EndsWith('.') || segment.EndsWith(' ') || ReservedDeviceName().IsMatch(segment)))
            throw new ArgumentException($"Evidence '{id}' zip entry '{name}' has an absolute, parent-relative or invalid path, which is not allowed.");
    }

    private static string SafeFileName(BizTalkEvidenceItem item)
    {
        var name = Path.GetFileName((item.Name ?? "").Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || ReservedDeviceName().IsMatch(name))
            name = item.Kind switch
            {
                BizTalkEvidenceKind.HostConfig => "host.config",
                BizTalkEvidenceKind.HelperSource => "helper.cs",
                BizTalkEvidenceKind.RealOutput => "sample.txt",
                _ => "evidence.xml"
            };
        return name.Length > 128 ? name[^128..] : name;
    }

    private string ItemDirectory(BizTalkEvidenceItem item)
    {
        _root ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mulse-evidence-" + Guid.NewGuid().ToString("N"))).FullName;
        return Directory.CreateDirectory(Path.Combine(_root, item.Id)).FullName;
    }

    /// <summary>The temporary folder, or null when nothing was written. Exposed for tests that check cleanup.</summary>
    internal string? TemporaryRoot => _root;

    public void Dispose()
    {
        if (_root is null) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _root = null;
    }
}
