using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// A checklist for the maps of an imported flow (M7): functoids and scripts that were not translated, schemas that could not be found, and links that look suspicious.
/// The rules are fixed and need no AI provider. It reads the saved migration inventory only and returns names and counts: map paths, script bodies and script hashes are never returned.
/// </summary>
public static class AssistMapReview
{
    public const int MaxFunctoidTypesListed = 12;
    public const int MaxScriptsListed = 10;
    public const int MaxApiNamesListed = 8;
    private const int MaxNameLength = 120;

    public const string Disclaimer = "These are rules applied to what the importer recorded about each map. A map with nothing to report is not shown to be correct: compare its output with output captured from the real system.";

    private static readonly string[] Checked =
    [
        "Whether the map could be read at all.",
        "Links that pass through functoids, and the functoid types involved.",
        "Script blocks: how many, in which language, and which BizTalk API names they mention.",
        "Whether the source and target schemas were found in the supplied projects, and are not the same.",
        "Maps with no links at all, and maps where most links need rework."
    ];

    private static readonly string[] NotChecked =
    [
        "What each functoid does (type ids are not translated to names).",
        "Whether a script is safe or portable; its body is never read here.",
        "Whether the links connect the right fields.",
        "Whether the output matches what the source system produced."
    ];

    public static MapReviewReport Review(PipelineDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var displayName = Clean(string.IsNullOrWhiteSpace(flow.Name) ? flow.Id : flow.Name);
        var remarks = new List<string>();
        var maps = new List<MapReviewMap>();

        if (flow.Migration is null)
            remarks.Add("This flow was not imported, so there are no recorded maps to check.");
        else
        {
            var mapPaths = flow.Migration.Artifacts.Where(artifact => string.Equals(artifact.Kind, "Map", StringComparison.OrdinalIgnoreCase))
                .Select(artifact => artifact.SourcePath).ToArray();
            var candidates = flow.Migration.MapInventory?.Candidates ?? [];
            if (mapPaths.Length == 0) remarks.Add("The import did not list any maps for this flow.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in mapPaths.Where(seen.Add))
            {
                var candidate = candidates.FirstOrDefault(map => string.Equals(map.SourcePath, path, StringComparison.OrdinalIgnoreCase));
                if (candidate is null)
                {
                    maps.Add(new(Clean(Path.GetFileNameWithoutExtension(path)), "NotRecorded", 0, 0, 0, 0,
                        [new("not-recorded", "Warning", "The map is listed but its details were not saved",
                            "The saved inventory does not include this map, so nothing could be checked.", [],
                            "Import the project again, or review the map in the source system.")]));
                    continue;
                }

                maps.Add(ReviewMap(candidate));
            }

            if (flow.Migration.MapInventory is { OmittedCount: > 0 } inventory)
                remarks.Add($"{inventory.OmittedCount} map(s) in the project were left out of the saved inventory.");
        }

        var ordered = maps.OrderByDescending(map => map.Items.Count(item => item.Severity == "Blocker")).ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var all = ordered.SelectMany(map => map.Items).ToArray();
        return new(flow.Id, displayName, ordered.Length, all.Count(item => item.Severity == "Blocker"), all.Count(item => item.Severity == "Warning"),
            all.Count(item => item.Severity == "Info"), ordered, remarks, Checked, NotChecked, Disclaimer);
    }

    private static MapReviewMap ReviewMap(MigrationMapCandidate map)
    {
        var items = new List<ReviewItem>();
        var scripts = map.ScriptInventory?.TotalScripts ?? 0;
        var notAnalyzed = string.Equals(map.SourceSchemaReference, "(not analyzed)", StringComparison.Ordinal)
            && string.Equals(map.TargetSchemaReference, "(not analyzed)", StringComparison.Ordinal);

        if (notAnalyzed)
        {
            items.Add(new("not-analyzed", "Blocker", "The map could not be read",
                "The importer could not open the map or did not recognise its format, so nothing is known about its links.", [],
                "Check the map file in the source project and import again; do not treat it as an empty map."));
            return Build(map, items, scripts);
        }

        if (map.UnsupportedLinkCount > 0)
            items.Add(new("functoid-links", "Blocker", $"{map.UnsupportedLinkCount} link(s) go through functoids that were not translated",
                "The starter transform leaves these links out, so the target would be missing those values.",
                FunctoidTypes(map.FunctoidFidCounts),
                "Open the map in the source tool, identify each functoid from its properties, and rebuild it as a mapping transform or formula."));
        else if (map.FunctoidFidCounts.Count > 0)
            items.Add(new("functoid-types", "Warning", $"{map.FunctoidFidCounts.Values.Sum()} functoid(s) of {map.FunctoidFidCounts.Count} type(s) were found",
                "None were recorded as sitting on a link, but they may still affect the output.",
                FunctoidTypes(map.FunctoidFidCounts), "Check in the source tool what each one does and whether the target needs it."));

        if (map.ScriptInventory is { TotalScripts: > 0 } inventory)
            items.Add(ScriptItem(inventory));

        foreach (var (schema, role) in new[] { (map.SourceSchema, "Source"), (map.TargetSchema, "Target") })
        {
            if (schema is null) continue;
            if (string.Equals(schema.Status, "Unresolved", StringComparison.OrdinalIgnoreCase))
                items.Add(new("schema-unresolved", "Warning", $"{role} schema was not found",
                    "The schema this map reads or writes is not in the supplied projects, so field names cannot be checked.", [$"{role.ToLowerInvariant()}: {Clean(schema.Reference)}"],
                    "Supply the project or assembly that holds the schema and import again."));
            else if (string.Equals(schema.Status, "Ambiguous", StringComparison.OrdinalIgnoreCase))
                items.Add(new("schema-ambiguous", "Warning", $"{role} schema matches more than one source",
                    "More than one schema file matches the reference, so it is not clear which one the map uses.", [$"{role.ToLowerInvariant()}: {Clean(schema.Reference)}"],
                    "Remove the duplicate from the import or choose the correct schema by hand."));
        }

        if (!string.IsNullOrWhiteSpace(map.SourceSchemaReference) && string.Equals(map.SourceSchemaReference, map.TargetSchemaReference, StringComparison.OrdinalIgnoreCase))
            items.Add(new("same-schema", "Warning", "The source and target schema are the same",
                "That is unusual: the map may be a pass-through, or one of the schema references may be wrong.", [Clean(map.SourceSchemaReference)],
                "Confirm in the source tool that the map is meant to read and write the same schema."));

        if (map.DirectLinkCount == 0 && map.ConstantValueCount == 0 && map.UnsupportedLinkCount == 0)
            items.Add(new("no-links", "Warning", "No links were found in the map",
                "An empty map would produce an empty or constant output.", [], "Open the map in the source tool and confirm that it really has no links."));
        else if (map.UnsupportedLinkCount > map.DirectLinkCount + map.ConstantValueCount)
            items.Add(new("mostly-reworked", "Info", "Most links need rework",
                $"{map.UnsupportedLinkCount} link(s) need rework against {map.DirectLinkCount + map.ConstantValueCount} that can be carried over directly.", [],
                "It may be easier to rebuild the map's logic in the designer than to patch the starter transform."));

        if (map.DirectLinkCount + map.ConstantValueCount > 0)
            items.Add(new("starter-links", "Info", $"{map.DirectLinkCount} direct link(s) and {map.ConstantValueCount} constant(s) can be carried over",
                "A starter transform covers these, but it is not installed or verified.", [],
                "Install it, run sample input through it and compare the output with the real system."));

        return Build(map, items, scripts);
    }

    private static MapReviewMap Build(MigrationMapCandidate map, List<ReviewItem> items, int scripts)
        => new(Clean(map.Name), map.Status.ToString(), map.DirectLinkCount, map.ConstantValueCount, map.UnsupportedLinkCount, scripts,
            [.. items.OrderBy(item => Rank(item.Severity)).ThenBy(item => item.Code, StringComparer.Ordinal)]);

    private static ReviewItem ScriptItem(MapScriptInventory inventory)
    {
        var languages = inventory.Scripts.GroupBy(script => Clean(script.Language), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count()}").ToArray();
        var where = new List<string>();
        foreach (var script in inventory.Scripts.OrderBy(script => script.SourceLine).Take(MaxScriptsListed))
            where.Add($"functoid {Clean(script.FunctoidId)} ({Clean(script.Language)}, {script.CharacterCount} characters)");
        var apis = inventory.Scripts.SelectMany(script => script.BizTalkApiReferences).Select(Clean).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        where.AddRange(apis.Take(MaxApiNamesListed).Select(name => $"mentions {name}"));
        if (apis.Length > MaxApiNamesListed) where.Add($"and {apis.Length - MaxApiNamesListed} more API name(s)");
        var hidden = Math.Max(inventory.TotalScripts - Math.Min(inventory.Scripts.Count, MaxScriptsListed), 0);
        if (hidden > 0) where.Add($"and {hidden} more script(s)");

        return new("scripts", "Blocker", $"{inventory.TotalScripts} script block(s) were not translated",
            languages.Length > 0 ? $"Languages: {string.Join(", ", languages)}. Script text is not copied into Mulse." : "Script text is not copied into Mulse.", where,
            "Read each script in the source tool and rewrite it as a formula, a custom function or native steps; test it with sample input. Mentions of the source system's own API do not carry over.");
    }

    private static IReadOnlyList<string> FunctoidTypes(IReadOnlyDictionary<int, int> counts)
    {
        var listed = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(MaxFunctoidTypesListed)
            .Select(pair => $"type {pair.Key}: {pair.Value} use(s)").ToList();
        if (counts.Count > MaxFunctoidTypesListed) listed.Add($"and {counts.Count - MaxFunctoidTypesListed} more type(s)");
        return listed;
    }

    private static int Rank(string severity) => severity switch { "Blocker" => 0, "Warning" => 1, _ => 2 };

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(character => char.IsControl(character) ? ' ' : character).ToArray();
        var cleaned = new string(chars);
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] + "..." : cleaned;
    }
}
