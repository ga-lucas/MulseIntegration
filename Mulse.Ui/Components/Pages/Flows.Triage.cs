using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// Blocker triage for imported flows (M1). It reads nothing but finding codes on the server and changes nothing.
public partial class Flows
{
    private bool _triageOpen;
    private bool _triageLoading;
    private bool _triageFailed;
    private BlockerTriageViewModel? _triage;

    private bool HasImportedFlows => _flows.Any(flow => flow.Migration is not null);

    private async Task ShowTriageAsync()
    {
        _triageOpen = true;
        _triageLoading = true;
        _triageFailed = false;
        _triage = null;
        try
        {
            _triage = await ApiClient.GetBlockerTriageAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            _triageFailed = true;
        }
        finally
        {
            _triageLoading = false;
        }
    }

    private void CloseTriage()
    {
        _triageOpen = false;
        _triage = null;
        _triageLoading = false;
        _triageFailed = false;
    }
}
