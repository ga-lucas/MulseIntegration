using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// The flow runbook (R3). It is written from the stored definition, needs no AI, and changes nothing.
public partial class Flows
{
    private string? _runbookFlowId;
    private FlowRunbookViewModel? _runbook;
    private bool _runbookLoading;
    private bool _runbookFailed;

    private void ShowRunbook(FlowViewModel flow)
    {
        _runbookFlowId = flow.Id;
        _runbook = null;
        _runbookLoading = false;
        _runbookFailed = false;
    }

    private async Task GenerateRunbookAsync(FlowViewModel flow, RunbookRequestViewModel request)
    {
        _runbookLoading = true;
        _runbookFailed = false;
        _runbook = null;
        try
        {
            _runbook = await ApiClient.BuildFlowRunbookAsync(flow.Id, request, CancellationToken.None);
        }
        catch (Exception)
        {
            _runbookFailed = true;
        }
        finally
        {
            _runbookLoading = false;
        }
    }

    private void CloseRunbook()
    {
        _runbookFlowId = null;
        _runbook = null;
        _runbookLoading = false;
        _runbookFailed = false;
    }
}
