using System.Text.RegularExpressions;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    [GeneratedRegex("\\{\\{config:(?<reference>biztalk-map\\.[^{}\\r\\n\"\\\\]+)\\}\\}")]
    private static partial Regex StoredMapReferencePattern();

    /// <summary>
    /// Keeps, per candidate, only the stored map values its flow actually references, and tells the reviewer where they live.
    /// The flow itself holds small <c>{{config:...}}</c> references so dozens of large maps do not exceed the flow size limit.
    /// </summary>
    internal static BizTalkFlowCandidateResponse AttachStoredConfigValues(BizTalkFlowCandidateResponse candidate, IReadOnlyDictionary<string, string> stored)
    {
        if (stored.Count == 0) return candidate;
        var used = candidate.DraftFlow.Augments.SelectMany(step => step.Settings.Values)
            .SelectMany(value => StoredMapReferencePattern().Matches(value).Select(match => match.Groups["reference"].Value))
            .Distinct(StringComparer.Ordinal).Where(stored.ContainsKey).ToDictionary(name => name, name => stored[name], StringComparer.Ordinal);
        if (used.Count == 0) return candidate;
        return candidate with
        {
            StoredConfigValues = used,
            DraftFlow = candidate.DraftFlow with
            {
                Warnings = [.. candidate.DraftFlow.Warnings, $"{used.Count} converted map(s) are stored as config values named 'biztalk-map.*' and referenced by the flow's dispatch choices. They are created with the flow when they do not exist yet, an existing value is kept, and each can be reviewed and edited on the Config Values page."]
            }
        };
    }
}
