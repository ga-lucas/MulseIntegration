using System.Text.Json;
using Service.Ai;
using Service.Models;

namespace Service.Endpoints;

public static class AiAuthoringEndpoints
{
    private const int MaxSettingsBodyBytes = 16 * 1024;
    private const int MaxBizTalkAssistBodyBytes = 8 * 1024 * 1024;

    public static WebApplication MapAiAuthoringEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/authoring").WithTags("AI Authoring");

        group.MapGet("/settings", (AiAuthoringSettingsService settings, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return Guarded(() => Task.FromResult<IResult>(Results.Ok(settings.Get())));
        }).WithName("GetAiAuthoringSettings").WithSummary("Read AI authoring settings; the API key is never returned");

        group.MapPut("/settings", async (HttpRequest request, AiAuthoringSettingsService settings, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var body = await ReadBodyAsync<AiAuthoringSettingsRequest>(request, MaxSettingsBodyBytes, cancellationToken).ConfigureAwait(false);
            return await Guarded(() => Task.FromResult<IResult>(Results.Ok(settings.Update(body)))).ConfigureAwait(false);
        }).WithName("UpdateAiAuthoringSettings").WithSummary("Replace AI authoring settings; apiKey is write-only");

        group.MapPost("/copilot/models", async (HttpRequest request, ICopilotAuthoringRuntime runtime, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            return await Guarded(async () =>
            {
                var token = UserToken(request) ?? throw new AiAuthoringException(StatusCodes.Status409Conflict,
                    "Connect your GitHub account under Configuration > My GitHub connection before loading Copilot models.");
                return Results.Ok(await runtime.ListModelsAsync(token, cancellationToken).ConfigureAwait(false));
            }).ConfigureAwait(false);
        }).WithName("GetCopilotAuthoringModels").WithSummary("Load Copilot models available to the requesting user's connected GitHub account");

        group.MapPost("/test", async (HttpRequest request, AiAuthoringService service, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            return await Guarded(async () => Results.Ok(await service.TestAsync(cancellationToken, UserToken(request)).ConfigureAwait(false))).ConfigureAwait(false);
        }).WithName("TestAiAuthoring").WithSummary("Check connectivity to the configured AI provider");

        group.MapPost("/chat", async (HttpRequest request, AiAuthoringService service, AiAuthoringSettingsService settings, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var limit = Math.Clamp(settings.Options.MaxDraftChars, 1024, 2_000_000) * 4 + 256 * 1024;
            var body = await ReadBodyAsync<AiAuthoringChatRequest>(request, limit, cancellationToken).ConfigureAwait(false);
            return await Guarded(async () => Results.Ok(await service.ChatAsync(body, cancellationToken, UserToken(request)).ConfigureAwait(false))).ConfigureAwait(false);
        }).WithName("AiAuthoringChat").WithSummary("Ask the assistant to propose a validated, disabled flow draft");

        group.MapPost("/connection/analyze", async (HttpRequest request, AiAuthoringService service, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var body = await ReadBodyAsync<ConnectionAssistanceRequest>(request, 2_000_000, cancellationToken).ConfigureAwait(false);
            return Results.Ok(service.AnalyzeConnection(body));
        }).WithName("AnalyzeAuthoringConnection").WithSummary("Infer sample shapes locally without contacting an AI provider");

        group.MapPost("/connection", async (HttpRequest request, AiAuthoringService service, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var body = await ReadBodyAsync<ConnectionAssistanceRequest>(request, 2_000_000, cancellationToken).ConfigureAwait(false);
            return await Guarded(async () => Results.Ok(await service.AssistConnectionAsync(body, cancellationToken, UserToken(request)).ConfigureAwait(false))).ConfigureAwait(false);
        }).WithName("AssistAuthoringConnection").WithSummary("Propose a scoped mapping and compare every sample without running flows or plugins");

        group.MapPost("/biztalk-assist/preview", async (HttpRequest request, AiAuthoringService service, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var body = await ReadBodyAsync<BizTalkAssistRequest>(request, MaxBizTalkAssistBodyBytes, cancellationToken).ConfigureAwait(false);
            return Results.Ok(service.PreviewBizTalkAssist(body));
        }).WithName("PreviewBizTalkAssist").WithSummary("Show exactly what a BizTalk import assist would send, and check supplied evidence locally. Contacts no AI provider");

        group.MapPost("/biztalk-assist", async (HttpRequest request, AiAuthoringService service, HttpResponse response, CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            var body = await ReadBodyAsync<BizTalkAssistRequest>(request, MaxBizTalkAssistBodyBytes, cancellationToken).ConfigureAwait(false);
            return await Guarded(async () => Results.Ok(await service.BizTalkAssistAsync(body, cancellationToken, UserToken(request)).ConfigureAwait(false))).ConfigureAwait(false);
        }).WithName("BizTalkAssist").WithSummary("Explain an evidence request or review supplied evidence. Sends only what the preview showed");

        return app;
    }

    private static string? UserToken(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(Mulse.ServiceDefaults.Security.CopilotUserCredential.HeaderName, out var values)) return null;
        if (values.Count != 1 || values[0] is not { } token || !Mulse.ServiceDefaults.Security.CopilotUserCredential.IsValid(token))
            throw new AiAuthoringException(StatusCodes.Status400BadRequest, "The per-user GitHub credential is invalid. Reconnect your account.");
        return token;
    }

    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (AiAuthoringException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: "AI authoring unavailable", detail: exception.Message);
        }
    }

    private static async Task<T> ReadBodyAsync<T>(HttpRequest request, int limit, CancellationToken cancellationToken) where T : class
    {
        if (request.ContentLength > limit) throw new ArgumentException("The request body is too large.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new ArgumentException("The request body is too large.");
        }
        try { return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonSerializerOptions.Web) ?? throw new ArgumentException("A request body is required."); }
        catch (JsonException) { throw new ArgumentException("The request body is not valid JSON."); }
    }
}
