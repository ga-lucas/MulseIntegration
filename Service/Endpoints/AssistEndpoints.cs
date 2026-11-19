using Service.Assist;
using Service.Models;

namespace Service.Endpoints;

/// <summary>The deterministic half of the assistant. Nothing here contacts an AI provider or needs one to be configured.</summary>
public static class AssistEndpoints
{
    public static IServiceCollection AddAssist(this IServiceCollection services)
    {
        services.AddSingleton<AssistExplainer>();
        services.AddSingleton<AssistModuleAuthoring>();
        services.AddSingleton<AssistFlowReviewer>();
        services.AddSingleton<AssistFlowRunbook>();
        return services;
    }

    public static WebApplication MapAssistEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/assist").WithTags("Assist");

        group.MapGet("/surfaces", () => TypedResults.Ok(AssistSurfaces.Describe()))
            .WithName("GetAssistSurfaces")
            .WithSummary("Where the assistant can appear, what each part may use, and which data categories are never sent");

        group.MapGet("/catalog/node-kinds", () => TypedResults.Ok(NativeNodeCatalog.All))
            .WithName("GetAssistNodeKinds")
            .WithSummary("Plain-language description of every kind of native step");

        group.MapGet("/glossary", (string? search) => TypedResults.Ok(AssistGlossary.Find(search)))
            .WithName("GetAssistGlossary")
            .WithSummary("BizTalk terms and what they become in Mulse");

        group.MapPost("/explain/flow", (ExplainFlowRequest request, AssistExplainer explainer, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(explainer.ExplainFlow(request.FlowJson));
            })
            .WithName("ExplainNativeFlow")
            .WithSummary("Facts about a whole native flow, read locally. Setting values are never returned")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/explain/node", (ExplainNodeRequest request, AssistExplainer explainer, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(explainer.ExplainNode(request.FlowJson, request.NodeId));
            })
            .WithName("ExplainNativeNode")
            .WithSummary("Facts about one native step, read locally. Setting values are never returned")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/module-prompt", (ModulePromptRequest request, AssistModuleAuthoring authoring, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                return TypedResults.Ok(authoring.Build(request.FlowJson, request.ModuleId, request.Description));
            })
            .WithName("BuildModuleAuthoringPrompt")
            .WithSummary("A prompt, built locally, that asks an AI tool to write a module the flow needs but this server lacks. Setting names only, never values")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/explain/run/{executionId}", async (string executionId, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
            {
                response.Headers.CacheControl = "no-store";
                var journal = services.GetService<Service.Execution.FlowJournal>()
                    ?? throw new InvalidOperationException("Execution history requires a database storage engine, so there is no run to explain.");
                return TypedResults.Ok(AssistRunExplainer.Explain(await journal.RunForAssistAsync(executionId, cancellationToken).ConfigureAwait(false)));
            })
            .WithName("ExplainRun")
            .WithSummary("Facts about one stored run: what its state means, what each delivery's state means and what a person should check. Never repeats error text or message content, and never offers a retry")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/explain/failures", async (int? take, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
            {
                response.Headers.CacheControl = "no-store";
                var journal = services.GetService<Service.Execution.FlowJournal>()
                    ?? throw new InvalidOperationException("Execution history requires a database storage engine, so there are no runs to group.");
                var (runs, total) = await journal.FailuresForAssistAsync(Math.Clamp(take ?? 100, 1, 200), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(AssistFailurePatterns.Analyze(runs, total));
            })
            .WithName("ExplainFailurePatterns")
            .WithSummary("Recent failed, held and retrying runs grouped by likely cause and stage across flows, with the one change that would clear the most. Never repeats error text or message content, and never offers a retry")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/digest", async (int? hours, IServiceProvider services, HttpResponse response, CancellationToken cancellationToken) =>
            {
                response.Headers.CacheControl = "no-store";
                var journal = services.GetService<Service.Execution.FlowJournal>()
                    ?? throw new InvalidOperationException("Execution history requires a database storage engine, so there is nothing to summarise.");
                const int FetchLimit = 200;
                var span = Math.Clamp(hours ?? 24, 1, 168);
                var rows = await journal.ListAsync(null, FetchLimit, cancellationToken).ConfigureAwait(false);
                var flows = services.GetService<Service.Flows.IFlowDefinitionService>()?.GetAll() ?? [];
                return TypedResults.Ok(AssistOperationalDigest.Build(
                    [.. rows.Select(row => new DigestRun(row.ExecutionId, row.FlowId, row.State, row.StartedUtcTicks, row.UpdatedUtcTicks))],
                    [.. flows.Select(flow => new DigestFlow(flow.Id, string.IsNullOrWhiteSpace(flow.Name) ? flow.Id : flow.Name!, flow.Enabled, flow.Trigger.Mode.ToString(), flow.Trigger.Interval))],
                    DateTimeOffset.UtcNow, span, FetchLimit));
            })
            .WithName("GetOperationalDigest")
            .WithSummary("A deterministic digest of a period (1 to 168 hours, default 24): runs by state, success rate, held runs, flows that ran slower than usual and interval flows that stopped running. Reads states, times and trigger settings only; never offers a retry")
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/review/{flowId}", (string flowId, IServiceProvider services, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                var flows = services.GetRequiredService<Service.Flows.IFlowDefinitionService>();
                var store = services.GetRequiredService<IRuntimeConfigurationStore>();
                return TypedResults.Ok(services.GetRequiredService<AssistFlowReviewer>().Review(flows.GetById(flowId), store.GetState().ConfigValues));
            })
            .WithName("ReviewFlowBeforeEnabling")
            .WithSummary("Rules applied to a stored flow before it is enabled: unverified imports, missing modules, unresolved references, typed-in credentials, placeholders, a source that is also a destination, missing retries and sources that are never marked as read. Names and counts only; never setting values")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/runbook/{flowId}", (string flowId, RunbookRequest? request, IServiceProvider services, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                var flows = services.GetRequiredService<Service.Flows.IFlowDefinitionService>();
                var store = services.GetRequiredService<IRuntimeConfigurationStore>();
                return TypedResults.Ok(services.GetRequiredService<AssistFlowRunbook>().Build(flows.GetById(flowId), store.GetState().ConfigValues, request, DateTimeOffset.UtcNow));
            })
            .WithName("BuildFlowRunbook")
            .WithSummary("A runbook for a stored flow, written from its definition: schedule, inputs, processing, outputs, dependencies, failure behavior and review state, as sections and as Markdown. Purpose, contact and notes are text the caller supplies. Names and numbers only; never setting values, config values or secrets. Nothing is saved")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/activity", (string? flowId, int? take, IServiceProvider services, HttpResponse response) =>
            {
                response.Headers.CacheControl = "no-store";
                var log = services.GetService<Service.Ai.IAiActivityLog>();
                return TypedResults.Ok(log is null ? [] : log.List(flowId, take ?? 50));
            })
            .WithName("GetAiActivity")
            .WithSummary("The record of AI requests, newest first: when, which surface, provider, model, data categories, and a hash and length of what was sent. Never the text sent or received")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }
}
