using System.Text.RegularExpressions;

namespace Service.Ai;

internal sealed record AiSlot(string Token, ModuleKind Kind, AiStepDoc Step);

/// <summary>What the server remembers about a step it showed the model: its module and the setting values it withheld.</summary>
internal sealed record AiOriginalSlot(ModuleKind Kind, string Module, Dictionary<string, string> Withheld);

/// <summary>Delivery protections present in the draft that a proposal may not lower.</summary>
internal sealed record AiActionProtection(bool RequireConfirmation, bool HasCompensation, string? AtomicScope);

internal sealed record AiRedactedDraft(
    AiFlowDoc Context,
    Dictionary<string, AiOriginalSlot> Originals,
    Dictionary<string, AiActionProtection> Protections);

internal static partial class AiAuthoringRedactor
{
    public const string Placeholder = "[WITHHELD]";

    [GeneratedRegex(@"^\{\{(?:config|secret):[^{}\r\n]+\}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"^-?\d{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex SmallNumberPattern();

    public static void Normalize(AiFlowDoc doc)
    {
        doc.Id ??= "";
        doc.Fetches ??= [];
        doc.Parses ??= [];
        doc.Augments ??= [];
        doc.Deliveries ??= [];
        doc.Fetches.RemoveAll(static fetch => fetch is null);
        doc.Parses.RemoveAll(static parse => parse is null);
        doc.Augments.RemoveAll(static step => step is null);
        doc.Deliveries.RemoveAll(static route => route is null);
        foreach (var fetch in doc.Fetches)
        {
            fetch.Id ??= "";
            fetch.InputParseIds ??= [];
            fetch.Fetch = NormalizeStep(fetch.Fetch);
        }
        foreach (var parse in doc.Parses)
        {
            parse.Id ??= "";
            parse.FetchNodeIds ??= [];
            parse.Parse = NormalizeStep(parse.Parse);
        }
        for (var index = 0; index < doc.Augments.Count; index++) doc.Augments[index] = NormalizeStep(doc.Augments[index])!;
        foreach (var route in doc.Deliveries)
        {
            route.Render = NormalizeStep(route.Render);
            route.Delivers ??= [];
            route.Delivers.RemoveAll(static action => action is null);
            foreach (var action in route.Delivers)
            {
                action.Deliver = NormalizeStep(action.Deliver);
                if (action.Compensation is not null) action.Compensation = NormalizeStep(action.Compensation);
            }
        }
    }

    private static AiStepDoc NormalizeStep(AiStepDoc? step)
    {
        step ??= new AiStepDoc();
        step.Module ??= "";
        step.Settings = step.Settings is null ? new(StringComparer.OrdinalIgnoreCase)
            : step.Settings.GroupBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.Last().Value ?? "", StringComparer.OrdinalIgnoreCase);
        return step;
    }

    /// <summary>Deterministic slot order: fetches, parses, augments, then per route the render, each deliver and its compensation.</summary>
    public static List<AiSlot> Slots(AiFlowDoc doc)
    {
        var slots = new List<AiSlot>();
        void Add(ModuleKind kind, AiStepDoc step) => slots.Add(new AiSlot($"s{slots.Count}", kind, step));
        foreach (var fetch in doc.Fetches!) Add(ModuleKind.Fetch, fetch.Fetch!);
        foreach (var parse in doc.Parses!) Add(ModuleKind.Parse, parse.Parse!);
        foreach (var augment in doc.Augments!) Add(ModuleKind.OrchestrationAugment, augment);
        foreach (var route in doc.Deliveries!)
        {
            Add(ModuleKind.Render, route.Render!);
            foreach (var action in route.Delivers!)
            {
                Add(ModuleKind.Deliver, action.Deliver!);
                if (action.Compensation is not null) Add(ModuleKind.Deliver, action.Compensation);
            }
        }
        return slots;
    }

    public static AiRedactedDraft Redact(AiFlowDoc draft, IReadOnlyList<ModuleDescriptor> catalog, bool includeEnvironmentSettings = false)
    {
        var context = System.Text.Json.JsonSerializer.Deserialize<AiFlowDoc>(
            System.Text.Json.JsonSerializer.Serialize(draft, AiAuthoringJson.Options), AiAuthoringJson.Options)!;
        Normalize(context);
        context.Migration = null;
        context.FetchIds = context.ParseIds = context.FetchModules = context.ParseModules = context.AugmentModules
            = context.RenderModules = context.DeliverModules = context.MissingModules = null;

        var originalSlots = Slots(draft);
        var contextSlots = Slots(context);
        var originals = new Dictionary<string, AiOriginalSlot>(StringComparer.Ordinal);
        for (var index = 0; index < contextSlots.Count; index++)
        {
            var slot = contextSlots[index];
            var descriptor = catalog.FirstOrDefault(item => item.Kind == slot.Kind && string.Equals(item.Id, slot.Step.Module, StringComparison.OrdinalIgnoreCase));
            var withheld = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in originalSlots[index].Step.Settings!)
            {
                var setting = descriptor?.Settings.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
                if (IsRevealable(key, value, setting, includeEnvironmentSettings)) continue;
                withheld[key] = value;
                slot.Step.Settings![key] = Placeholder;
            }
            slot.Step.Slot = slot.Token;
            originals[slot.Token] = new AiOriginalSlot(slot.Kind, slot.Step.Module!, withheld);
        }

        var protections = new Dictionary<string, AiActionProtection>(StringComparer.Ordinal);
        foreach (var route in draft.Deliveries!)
            foreach (var action in route.Delivers!)
            {
                var token = originalSlots.First(item => ReferenceEquals(item.Step, action.Deliver)).Token;
                protections[token] = new AiActionProtection(action.RequireConfirmation, action.Compensation is not null,
                    string.IsNullOrWhiteSpace(route.AtomicScope) ? null : route.AtomicScope);
            }
        return new AiRedactedDraft(context, originals, protections);
    }

    private static bool IsRevealable(string key, string value, ModuleSettingDescriptor? setting, bool includeEnvironmentSettings)
    {
        if (value.Length == 0 || ReferencePattern().IsMatch(value.Trim())) return true;
        var name = new string(key.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (setting?.InputKind == ModuleSettingInputKind.Password || CredentialNamePattern().IsMatch(name)
            || CredentialValuePattern().IsMatch(value) || value.Contains("{{secret:", StringComparison.OrdinalIgnoreCase)) return false;
        var environment = EnvironmentNamePattern().IsMatch(name);
        if (environment)
        {
            if (!includeEnvironmentSettings || value.Length > 2048 || value.Any(char.IsControl)
                || value.IndexOfAny(['?', '#', '=', '@']) >= 0) return false;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return (uri.IsFile || uri.Scheme is "https" or "http" or "sftp" or "ftp")
                    && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
            return !value.Contains("://", StringComparison.Ordinal);
        }
        if (name is "searchpattern" or "filepattern" or "pattern")
            return value.Length <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || "*?.-_ ".Contains(character));
        if (OperationalNumberNamePattern().IsMatch(name) && SmallNumberPattern().IsMatch(value)) return true;
        return setting?.InputKind switch
        {
            ModuleSettingInputKind.Boolean => value is "true" or "false" or "True" or "False",
            ModuleSettingInputKind.Select => setting.Options?.Any(option => option.Value == value) == true,
            ModuleSettingInputKind.Number => SmallNumberPattern().IsMatch(value),
            _ => false
        };
    }

    [GeneratedRegex(@"password|passwd|secret|token|apikey|credential|connectionstring|authorization|privatekey|accesskey|username|clientid|sas", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialNamePattern();

    [GeneratedRegex(@"(?:password|passwd|secret|token|api[_-]?key|sig|authorization)\s*=|\b(?:Bearer|Basic)\s+|-----BEGIN .*PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialValuePattern();

    [GeneratedRegex(@"path$|directory$|folder$|url$|uri$|endpoint$|hostname$|^host$|^server$|^address$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    [GeneratedRegex(@"(?:settle|delay|timeout|interval|count|limit|size)(?:ms|seconds|milliseconds|minutes|bytes)?$", RegexOptions.CultureInvariant)]
    private static partial Regex OperationalNumberNamePattern();
}
