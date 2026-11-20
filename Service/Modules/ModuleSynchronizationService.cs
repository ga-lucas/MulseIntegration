namespace Service.Modules;

public sealed class ModuleSynchronizationService(IModuleCatalog moduleCatalog, ILogger<ModuleSynchronizationService> logger) : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => moduleCatalog.SynchronizeAsync(cancellationToken);
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                await moduleCatalog.SynchronizeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Runtime module synchronization failed.");
            }
        }
    }
}
