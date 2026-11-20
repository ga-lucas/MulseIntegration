using System.Runtime.CompilerServices;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mulse.ServiceDefaults.Security;
using Service.Models;

namespace Service.Ai;

internal interface ICopilotAuthoringRuntime
{
    Task<string> CompleteAsync(AiAuthoringEffectiveSettings settings, IReadOnlyList<ChatMessage> messages,
        int maxOutputTokens, CancellationToken cancellationToken);

    Task<IReadOnlyList<AiAuthoringModelResponse>> ListModelsAsync(string userToken, CancellationToken cancellationToken);
}

internal sealed class AiAuthoringChatClientFactory(ICopilotAuthoringRuntime copilot) : IAiChatClientFactory
{
    public IChatClient Create(AiAuthoringEffectiveSettings settings) => settings.Provider switch
    {
        AiAuthoringProviders.GitHubCopilot => new GitHubCopilotChatClient(settings, copilot),
        AiAuthoringProviders.OpenAI or AiAuthoringProviders.OpenAICompatible => new OpenAiChatClientFactory().Create(settings),
        _ => throw new AiAuthoringException(StatusCodes.Status409Conflict, "AI authoring has an unsupported provider.")
    };
}

internal sealed class GitHubCopilotChatClient(AiAuthoringEffectiveSettings settings, ICopilotAuthoringRuntime runtime) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = await runtime.CompleteAsync(settings, messages.ToArray(), options?.MaxOutputTokens ?? settings.MaxOutputTokens,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text) { FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}

internal sealed class CopilotAuthoringRuntime(ILogger<CopilotAuthoringRuntime> logger, IOptions<AiAuthoringOptions> options) : ICopilotAuthoringRuntime
{
    private readonly SemaphoreSlim _modelRequests = new(Math.Clamp(options.Value.MaxConcurrentRequests, 1, 16));

    public async Task<IReadOnlyList<AiAuthoringModelResponse>> ListModelsAsync(string userToken, CancellationToken cancellationToken)
    {
        if (!CopilotUserCredential.IsValid(userToken))
            throw new AiAuthoringException(StatusCodes.Status409Conflict, "Connect your GitHub account under Configuration > My GitHub connection before loading Copilot models.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_modelRequests.Wait(0))
            throw new AiAuthoringException(StatusCodes.Status429TooManyRequests, "Too many model discovery requests are running; try again shortly.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 5, 300)));
        try
        {
            var settings = new AiAuthoringEffectiveSettings(false, AiAuthoringProviders.GitHubCopilot, "", null, 256, userToken);
            return await RunAsync(settings, async (client, _, token) =>
            {
                var result = await client.Rpc.Models.ListAsync(gitHubToken: userToken, cancellationToken: token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return AvailableModels(result.Models);
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (AiAuthoringException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("GitHub Copilot model discovery failed ({ExceptionType}).", exception.GetType().Name);
            throw AiAuthoringService.MapFailure(exception, timeout.IsCancellationRequested, AiAuthoringProviders.GitHubCopilot);
        }
        finally { _modelRequests.Release(); }
    }

    #pragma warning disable GHCP001 // The pinned SDK's explicit-user model catalog uses experimental RPC model metadata.
    internal static IReadOnlyList<AiAuthoringModelResponse> AvailableModels(IEnumerable<GitHub.Copilot.Rpc.Model> models)
    {
        var available = models.Where(model => model.Policy?.State != ModelPolicyState.Disabled)
            .Select(model =>
            {
                if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 128 || model.Id.Any(char.IsControl)
                    || string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 256 || model.Name.Any(char.IsControl))
                    throw new AiAuthoringException(StatusCodes.Status502BadGateway, "GitHub Copilot returned invalid model metadata.");
                return new AiAuthoringModelResponse(model.Id, model.Name);
            }).DistinctBy(model => model.Id, StringComparer.Ordinal).OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length == 0)
            throw new AiAuthoringException(StatusCodes.Status409Conflict, "No available Copilot models were returned for your connected account. Check your subscription and organization policies.");
        return available;
    }
#pragma warning restore GHCP001

    public async Task<string> CompleteAsync(AiAuthoringEffectiveSettings settings, IReadOnlyList<ChatMessage> messages,
        int maxOutputTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.Provider != AiAuthoringProviders.GitHubCopilot || settings.Endpoint is not null || settings.EndpointMalformed
            || string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new AiAuthoringException(StatusCodes.Status409Conflict, "GitHub Copilot requires an explicitly configured GitHub token and no custom endpoint.");

        return await RunAsync(settings, async (client, directory, token) =>
        {
            var session = await client.CreateSessionAsync(CreateSessionConfig(settings, messages, maxOutputTokens, directory), token)
                .ConfigureAwait(false);
            var prompt = JsonSerializer.Serialize(messages.Where(message => message.Role != ChatRole.System)
                .Select(message => new { role = message.Role.Value, content = message.Text }));
            var response = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt }, TimeSpan.FromMinutes(5), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return response?.Data.Content ?? "";
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(AiAuthoringEffectiveSettings settings, Func<CopilotClient, string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "mulse-copilot-" + Guid.NewGuid().ToString("N"));
        var stopped = false;
        await using var client = new CopilotClient(CreateClientOptions(settings, directory));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            return await operation(client, directory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Canceling the SDK wait alone does not abort inference. Retire this request's owned runtime too.
            try
            {
                await client.ForceStopAsync().ConfigureAwait(false);
                stopped = true;
            }
            catch (Exception exception)
            {
                logger.LogError("GitHub Copilot runtime cleanup failed ({ExceptionType}).", exception.GetType().Name);
            }
            if (stopped)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogError("GitHub Copilot temporary session cleanup failed ({ExceptionType}).", exception.GetType().Name);
                }
            }
        }
    }

    internal static CopilotClientOptions CreateClientOptions(AiAuthoringEffectiveSettings settings, string directory)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["HOME"] = environment["USERPROFILE"] = directory;
        environment["COPILOT_TELEMETRY_DISABLED"] = "true";
        return new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            Connection = RuntimeConnection.ForStdio(),
            BaseDirectory = directory,
            WorkingDirectory = directory,
            GitHubToken = settings.ApiKey,
            UseLoggedInUser = false,
            LogLevel = CopilotLogLevel.None,
            Environment = environment
        };
    }

    internal static SessionConfig CreateSessionConfig(AiAuthoringEffectiveSettings settings, IReadOnlyList<ChatMessage> messages,
        int maxOutputTokens, string directory)
    {
        return new SessionConfig
        {
            Model = settings.Model,
            GitHubToken = settings.ApiKey,
            WorkingDirectory = directory,
            AvailableTools = [],
            Tools = [],
            McpServers = new Dictionary<string, McpServerConfig>(),
            IncludedBuiltInAgents = [],
            CustomAgents = [],
            EnableConfigDiscovery = false,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableSkills = false,
            EnableSessionStore = false,
            EnableSessionTelemetry = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
#pragma warning disable GHCP001 // The pinned SDK's explicit fail-closed permission decision is experimental.
            OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("Flow authoring does not permit tool execution.")),
#pragma warning restore GHCP001
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = string.Join("\n\n", messages.Where(message => message.Role == ChatRole.System).Select(message => message.Text))
                    + "\nThe user message is a JSON array of conversation messages in chronological order. Answer the final user request using that conversation. Do not execute tools."
            },
#pragma warning disable GHCP001 // The pinned SDK exposes per-session model output bounds through this experimental API.
            ModelCapabilities = new ModelCapabilitiesOverride
            {
                Limits = new ModelCapabilitiesOverrideLimits { MaxOutputTokens = Math.Clamp(maxOutputTokens, 256, 16384) }
            }
#pragma warning restore GHCP001
        };
    }
}
