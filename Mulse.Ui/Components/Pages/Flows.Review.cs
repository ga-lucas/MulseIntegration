using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// The pre-enable review (R1). It informs the person who clicks Enable; it never enables anything on its own and never blocks a flow the server would allow.
public partial class Flows
{
    private string? _reviewFlowId;
    private FlowReviewViewModel? _review;
    private bool _reviewLoading;
    private bool _reviewForEnable;

    private async Task ShowReviewAsync(FlowViewModel flow) => await LoadReviewAsync(flow, forEnable: false);

    // Enabling is the moment the review matters: a flow with nothing to report is enabled straight away, anything else is shown first.
    private async Task EnableOrDisableAsync(FlowViewModel flow)
    {
        if (flow.Enabled)
        {
            CloseReview();
            await ToggleFlowEnabledAsync(flow);
            return;
        }

        await LoadReviewAsync(flow, forEnable: true);
        if (_reviewFlowId is null) await ToggleFlowEnabledAsync(flow);
    }

    private async Task LoadReviewAsync(FlowViewModel flow, bool forEnable)
    {
        _reviewFlowId = flow.Id;
        _reviewForEnable = forEnable;
        _review = null;
        _reviewLoading = true;
        _errorMessage = null;
        try
        {
            _review = await ApiClient.GetFlowReviewAsync(flow.Id, CancellationToken.None);
            if (forEnable && _review.Items.Count == 0) _reviewFlowId = null;
        }
        catch (Exception)
        {
            // An unavailable review is shown as such; the person decides whether to enable without it.
            _review = null;
        }
        finally
        {
            _reviewLoading = false;
        }
    }

    private async Task EnableAfterReviewAsync(FlowViewModel flow)
    {
        CloseReview();
        await ToggleFlowEnabledAsync(flow);
    }

    private void CloseReview()
    {
        _reviewFlowId = null;
        _review = null;
        _reviewLoading = false;
        _reviewForEnable = false;
    }
}
