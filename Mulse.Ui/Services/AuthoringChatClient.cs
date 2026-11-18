using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public sealed class AuthoringChatClient(
    IMulseApiClient apiClient,
    Func<Task<string>> captureDraft,
    Func<FlowAuthoringProposal?, IReadOnlyList<string>, Task> receiveProposal,
    bool includeEnvironmentSettings = false) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = messages.Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant).ToArray();
        if (conversation.LastOrDefault() is not { Role: var role } last || role != ChatRole.User)
            throw new InvalidOperationException("An authoring request must end with a user message.");
        var draft = await captureDraft().ConfigureAwait(false);
        await receiveProposal(null, []).ConfigureAwait(false);
        var history = conversation.SkipLast(1).TakeLast(12)
            .Select(message => new AiAuthoringMessageViewModel(message.Role.Value, message.Text)).ToArray();
        var result = await apiClient.ChatAuthoringAsync(new(last.Text, draft, history, includeEnvironmentSettings), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await receiveProposal(result.ProposalJson is null ? null : new(draft, result.ProposalJson), result.ValidationErrors).ConfigureAwait(false);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, result.Reply));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Publish only the validated response; partial model JSON must never become an editable proposal.
        var result = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, result.Text)
        {
            MessageId = Guid.NewGuid().ToString("N"), FinishReason = ChatFinishReason.Stop
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
