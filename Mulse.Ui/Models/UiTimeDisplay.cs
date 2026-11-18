namespace Mulse.Ui.Models;

/// <summary>The one display preference for date-times: server-local (default) or UTC. It only changes how values are shown and entered; stored and executed values stay UTC. The saved choice is keyed by workspace so workspaces never share it.</summary>
public sealed class UiTimeDisplay(string workspaceKey = UiTimeDisplay.DefaultWorkspace)
{
    public const string DefaultWorkspace = "default";

    public string WorkspaceKey { get; } = string.IsNullOrWhiteSpace(workspaceKey) ? DefaultWorkspace : workspaceKey;
    public bool ServerLocal { get; set; } = true;
    public string StorageKey => "mulse.timeDisplay." + WorkspaceKey;

    public static string ToStored(bool serverLocal) => serverLocal ? "local" : "utc";

    /// <summary>Anything other than an explicit "utc" keeps the server-local default.</summary>
    public static bool FromStored(string? value) => value != "utc";
}
