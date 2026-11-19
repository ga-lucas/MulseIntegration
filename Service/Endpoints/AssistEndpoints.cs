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
