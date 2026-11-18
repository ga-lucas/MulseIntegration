using Microsoft.Extensions.Options;

namespace Service.Ai;

public static class AiAuthoringServiceCollectionExtensions
{
    /// <summary>Registers the AI authoring assistant (settings, credential store, provider client factory and chat service).</summary>
    public static IServiceCollection AddAiAuthoring(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<AiAuthoringOptions>(configuration.GetSection("AiAuthoring"));
        services.AddSingleton<IAiAuthoringSettingsStore>(provider =>
        {
            var configured = provider.GetRequiredService<IOptions<AiAuthoringOptions>>().Value.SettingsDirectory;
            var directory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(environment.ContentRootPath, "Data", "ai-authoring")
                : Path.GetFullPath(configured, environment.ContentRootPath);
            return new AiAuthoringSettingsStore(directory, provider.GetRequiredService<ILogger<AiAuthoringSettingsStore>>());
        });
        services.AddSingleton(provider => new AiAuthoringSettingsService(
            provider.GetRequiredService<IOptions<AiAuthoringOptions>>(), provider.GetRequiredService<IAiAuthoringSettingsStore>(), environment));
        services.AddSingleton<ICopilotAuthoringRuntime, CopilotAuthoringRuntime>();
        services.AddSingleton<IAiChatClientFactory, AiAuthoringChatClientFactory>();
        services.AddSingleton<AiAuthoringService>();
        return services;
    }
}
