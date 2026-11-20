using Mulse.Ui.Models;

namespace Mulse.Ui.Components.Pages;

// The map checklist for an imported flow (M7). It reads the saved migration inventory on the server and changes nothing.
public partial class Flows
{
    private string? _mapReviewFlowId;
    private MapReviewViewModel? _mapReview;
    private bool _mapReviewLoading;
    private bool _mapReviewFailed;

    private static bool HasMaps(FlowViewModel flow)
        => flow.Migration is { } migration && migration.Artifacts.Any(artifact => string.Equals(artifact.Kind, "Map", StringComparison.OrdinalIgnoreCase));

    private async Task ShowMapReviewAsync(FlowViewModel flow)
    {
        _mapReviewFlowId = flow.Id;
        _mapReviewLoading = true;
        _mapReviewFailed = false;
        _mapReview = null;
        try
        {
            _mapReview = await ApiClient.GetMapReviewAsync(flow.Id, CancellationToken.None);
        }
        catch (Exception)
        {
            _mapReviewFailed = true;
        }
        finally
        {
            _mapReviewLoading = false;
        }
    }

    private void CloseMapReview()
    {
        _mapReviewFlowId = null;
        _mapReview = null;
        _mapReviewLoading = false;
        _mapReviewFailed = false;
    }
}
