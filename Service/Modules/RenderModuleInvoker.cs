using Mulse.Modules.Abstractions;
using Service.ConfigValues;
using Service.Execution;

namespace Service.Modules;

internal sealed class RenderModuleInvoker(IServiceProvider services) : IRenderModuleInvoker
{
    public async Task<IntegrationBatch> RenderAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        // Render dispatch choices keep large maps as {{config:...}} references so a flow can name many of them; only the selected one is loaded.
        if (ConfigValueResolver.HasStoredDefinitionReference(settings))
        {
            var store = services.GetService<IRuntimeConfigurationStore>();
            settings = ConfigValueResolver.ResolveStoredDefinitions(settings, reference => store?.TryGetConfigValue(reference));
        }
        await using var lease = await services.GetRequiredService<IModuleCatalog>().LeaseRenderAsync(moduleId, cancellationToken).ConfigureAwait(false);
        return await lease.Module.RenderAsync(context, batch, new ModuleStepDefinition
        {
            Module = moduleId, Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        }, cancellationToken).ConfigureAwait(false);
    }
}
