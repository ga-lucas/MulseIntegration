using Service.Execution;
using Service.Models;

namespace Service.Endpoints;

public static class ComparisonScenarioEndpoints
{
    public static IServiceCollection AddComparisonScenarios(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IComparisonScenarioStore>(provider =>
        {
            var configured = configuration["ComparisonScenarios:Directory"];
            var directory = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(environment.ContentRootPath, "Data", "comparison-scenarios")
                : Path.GetFullPath(configured, environment.ContentRootPath);
            return new EncryptedComparisonScenarioStore(directory, provider.GetRequiredService<ILogger<EncryptedComparisonScenarioStore>>());
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ComparisonScenarioService>();
        return services;
    }

    public static WebApplication MapComparisonScenarioEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/flow-designer/native/compare/scenarios").WithTags("Designer");

        group.MapGet("/", (ComparisonScenarioService scenarios, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(scenarios.List());
            })
            .WithName("ListComparisonScenarios")
            .WithSummary("List saved comparison scenarios without any sample content");

        group.MapGet("/{id}", (string id, ComparisonScenarioService scenarios, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(scenarios.Get(id));
            })
            .WithName("GetComparisonScenario")
            .WithSummary("Read one saved scenario, including its samples (which may contain real data)")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", (SaveComparisonScenarioRequest request, ComparisonScenarioService scenarios, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(scenarios.Save(request, context.User.Identity?.Name));
            })
            .WithName("SaveComparisonScenario")
            .WithSummary("Save or replace a comparison scenario by name")
            .WithDescription("Stored encrypted and apart from flows, exports and backups. The flow definition is never stored. Scenarios with a retention period are deleted when it ends.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id}", (string id, ComparisonScenarioService scenarios) =>
            {
                scenarios.Delete(id);
                return TypedResults.NoContent();
            })
            .WithName("DeleteComparisonScenario")
            .WithSummary("Delete a saved scenario and everything in it")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/run", async (string id, RunComparisonScenarioRequest request, ComparisonScenarioService scenarios, HttpResponse response, CancellationToken cancellationToken) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(await scenarios.RunAsync(id, request?.FlowJson, cancellationToken).ConfigureAwait(false));
            })
            .WithName("RunComparisonScenario")
            .WithSummary("Run a saved scenario against the supplied native definition")
            .WithDescription("Only the result without sample values is remembered. No real module is invoked and no AI is called.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
