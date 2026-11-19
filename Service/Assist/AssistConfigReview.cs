using Service.ConfigValues;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// Reviews the stored config values and secrets against what the saved flows refer to (C3): missing, unused, near-duplicate names, repeated and empty values.
/// It is deterministic, needs no AI provider and reports names, counts and flow ids only. A value is compared but never returned. It never changes or deletes anything.
/// </summary>
public static class AssistConfigReview
{
    public const int MaxFlowsListed = 10;
    public const int MaxRelated = 5;
    private const int MaxFuzzyMissing = 200;
    private const int MaxNameLength = 200;

    public static ConfigReviewReport Analyze(IReadOnlyDictionary<string, string> stored, IReadOnlyList<ConfigReferenceUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(usages);

        var storedNames = stored.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var byReference = usages
            .GroupBy(usage => usage.Reference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var items = new List<ConfigReviewItem>();
        var missing = byReference.Where(pair => pair.Value.Any(usage => !usage.IsResolved)).Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var unused = storedNames.Where(name => !byReference.ContainsKey(name)).ToArray();
        var normalizedStored = storedNames.Select(name => (Name: name, Key: Normalize(name))).ToArray();

        foreach (var name in missing)
        {
            var flows = FlowIds(byReference[name]);
            var similar = missing.Length > MaxFuzzyMissing ? [] : Similar(name, normalizedStored, unused);
            items.Add(new("missing", "Blocker", Safe(name),
                $"'{Safe(name)}' is used by {flows.Total} flow(s) but has no stored value",
                "Flows that use it cannot be enabled or run until it has a value.",
                similar, flows.Ids,
                similar.Count > 0
                    ? "A stored name looks similar. If one of them is the one meant, correct the name in the flow, or store the value under this name."
                    : "Enter a value for it on this page."));
        }

        foreach (var name in storedNames.Where(name => string.IsNullOrWhiteSpace(stored[name])))
        {
            var used = byReference.TryGetValue(name, out var found);
            items.Add(new("empty-value", used ? "Warning" : "Info", Safe(name),
                $"'{Safe(name)}' is stored with an empty value",
                used ? "A flow uses it, so it will be filled in with nothing." : "It is empty and no saved flow uses it.",
                [], used ? FlowIds(found!).Ids : [],
                "Enter the real value, or delete it if it is not needed."));
        }

        foreach (var group in normalizedStored.GroupBy(entry => entry.Key).Where(group => group.Key.Length > 0 && group.Count() > 1))
        {
            var names = group.Select(entry => entry.Name).ToArray();
            items.Add(new("similar-names", "Info", Safe(names[0]),
                $"{names.Length} stored names differ only by case or separators",
                "They look like the same setting stored more than once, which makes it easy to update one and forget the other.",
                [.. names.Skip(1).Take(MaxRelated).Select(Safe)], [],
                "Pick one name, point the flows at it and delete the others."));
        }

        foreach (var group in storedNames.Where(name => !string.IsNullOrWhiteSpace(stored[name])).GroupBy(name => stored[name], StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var names = group.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            // Names that differ only by case or separators are already reported as similar names.
            if (names.Select(Normalize).Distinct(StringComparer.Ordinal).Count() == 1) continue;
            items.Add(new("same-value", "Info", Safe(names[0]),
                $"{names.Length} stored names hold the same value",
                "This can be correct (one server used by several flows). It can also mean a value was copied that should differ per environment. The value is not shown here.",
                [.. names.Skip(1).Take(MaxRelated).Select(Safe)], [],
                "If they are meant to be one setting, use a single name."));
        }

        foreach (var name in unused)
            items.Add(new("unused", "Info", Safe(name),
                $"'{Safe(name)}' is not used by any saved flow",
                "It may belong to a flow that has not been saved yet, or to one that was deleted or changed.",
                [], [],
                "Keep it if a flow still needs it; otherwise delete it so old credentials do not linger."));

        var ordered = items
            .OrderBy(item => item.Severity switch { "Blocker" => 0, "Warning" => 1, _ => 2 })
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Reference, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new(stored.Count, byReference.Count, missing.Length, unused.Length, ordered,
        [
            "Only names, counts and flow ids are shown here, never values. Matching is by name, so a reference built at run time is not seen.",
            "Nothing is changed or deleted by this review. 'Unused' means no saved flow refers to it, not that it is safe to delete."
        ]);
    }

    private static (int Total, IReadOnlyList<string> Ids) FlowIds(ConfigReferenceUsage[] usages)
    {
        var ids = usages.Select(usage => usage.FlowId).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return (ids.Length, [.. ids.Take(MaxFlowsListed)]);
    }

    // Prefer names nothing uses: a stored name that is already in use is rarely a typo of the missing one.
    private static IReadOnlyList<string> Similar(string name, (string Name, string Key)[] stored, string[] unused)
    {
        var key = Normalize(name);
        var unusedSet = unused.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. stored
            .Where(entry => !string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (entry.Name, Distance: entry.Key == key ? 0 : key.Length >= 5 && entry.Key.Length >= 5 ? Levenshtein(key, entry.Key, 2) : 3))
            .Where(entry => entry.Distance <= 2)
            .OrderBy(entry => entry.Distance)
            .ThenByDescending(entry => unusedSet.Contains(entry.Name))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(entry => Safe(entry.Name))];
    }

    private static string Normalize(string name)
        => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Safe(string name)
    {
        var cleaned = string.Concat(name.Select(character => char.IsControl(character) ? ' ' : character)).Trim();
        return cleaned.Length <= MaxNameLength ? cleaned : cleaned[..MaxNameLength] + "...";
    }

    // Returns limit + 1 as soon as the distance is known to exceed the limit.
    private static int Levenshtein(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, current[j]);
            }

            if (best > limit) return limit + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
