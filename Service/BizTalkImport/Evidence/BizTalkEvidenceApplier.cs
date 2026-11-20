using Service.Models;

namespace Service.BizTalkImport.Evidence;

/// <summary>Adds materialized evidence to the path fields of an analysis request. The request's own paths are kept; evidence only adds to them or fills an empty field.</summary>
public static class BizTalkEvidenceApplier
{
    private const string Prefix = "evidence:";

    public static AnalyzeBizTalkSolutionRequest Apply(AnalyzeBizTalkSolutionRequest request, IReadOnlyList<BizTalkEvidenceItem> items,
        IReadOnlyDictionary<string, string> paths, List<string> warnings)
    {
        string Resolve(string path)
        {
            if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return path;
            return paths.TryGetValue(path[Prefix.Length..], out var resolved) ? resolved
                : throw new ArgumentException($"'{path}' names evidence that was not supplied.");
        }

        var sources = request.AdditionalSourcePaths.Select(Resolve).ToList();
        var bindings = request.AdditionalBindingPaths.Select(Resolve).ToList();
        var rules = request.AdditionalRulePaths.Select(Resolve).ToList();
        var helpers = request.NativeConfigurationHelperPaths.Select(Resolve).ToList();
        var hostConfig = request.NativeHostConfigurationPath is { } supplied ? Resolve(supplied) : null;
        var sourcePath = Resolve(request.SourcePath);

        foreach (var item in items)
        {
            var path = paths[item.Id];
            switch (item.Kind)
            {
                case BizTalkEvidenceKind.Source: sources.Add(path); break;
                case BizTalkEvidenceKind.Bindings: bindings.Add(path); break;
                case BizTalkEvidenceKind.RulePolicy: rules.AddRange(Files(path, "*.xml", "*.brl")); break;
                case BizTalkEvidenceKind.HelperSource: helpers.AddRange(Files(path, "*.cs")); break;
                case BizTalkEvidenceKind.HostConfig:
                    if (hostConfig is not null && !string.Equals(hostConfig, path, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"More than one host configuration was supplied; '{item.Name ?? item.Id}' (the last one) is used.");
                    hostConfig = Directory.Exists(path) ? Files(path, "*.config").FirstOrDefault() ?? path : path;
                    break;
                case BizTalkEvidenceKind.RealOutput: break;
            }
        }

        // The importer needs the host configuration and the helper source together; with only one it reads neither, so supplying them one at a time is safe.
        if (items.Any(item => item.Kind is BizTalkEvidenceKind.HostConfig or BizTalkEvidenceKind.HelperSource) && (hostConfig is null) != (helpers.Count == 0))
        {
            warnings.Add(hostConfig is null
                ? "Configuration helper source was supplied without the host configuration file, so neither is used yet. Supply the host configuration file too."
                : "The host configuration file was supplied without any configuration helper source, so neither is used yet. Supply the helper source too, or skip this step if no setting comes from a helper.");
            hostConfig = null;
            helpers.Clear();
        }

        return request with
        {
            SourcePath = sourcePath,
            AdditionalSourcePaths = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            AdditionalBindingPaths = bindings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            AdditionalRulePaths = rules.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            NativeConfigurationHelperPaths = helpers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            NativeHostConfigurationPath = hostConfig
        };
    }

    private static IEnumerable<string> Files(string path, params string[] patterns) =>
        Directory.Exists(path)
            ? patterns.SelectMany(pattern => Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase)
            : [path];
}
