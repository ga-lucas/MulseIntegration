using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Service.Ai;

internal interface IAiChatClientFactory
{
    /// <summary>Creates a disposable client for one request. The application must not automatically replay authoring requests.</summary>
    IChatClient Create(AiAuthoringEffectiveSettings settings);
}

internal sealed class OpenAiChatClientFactory : IAiChatClientFactory
{
    public IChatClient Create(AiAuthoringEffectiveSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http),
            RetryPolicy = new ClientRetryPolicy(0)
        };
        if (settings.Endpoint is not null) options.Endpoint = settings.Endpoint;
        var client = new OpenAIClient(new ApiKeyCredential(settings.ApiKey ?? "keyless"), options);
        return new OwnedChatClient(client.GetChatClient(settings.Model).AsIChatClient(), http);
    }

    private sealed class OwnedChatClient(IChatClient inner, IDisposable owned) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) owned.Dispose();
        }
    }
}
