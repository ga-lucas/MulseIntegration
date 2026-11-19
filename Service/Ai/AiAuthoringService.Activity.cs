using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Service.Ai;

internal sealed partial class AiAuthoringService
{
    private static class Categories
    {
        public static readonly string[] Structure = ["Structure"];
        public static readonly string[] StructureAndSettingValues = ["Structure", "SettingValues"];
        public static readonly string[] StructureAndMessageData = ["Structure", "MessageData"];
        public static readonly string[] StructureAndSourceExpressions = ["Structure", "SourceExpressions"];
        public static readonly string[] None = [];
    }

    private sealed record AiActivityContext(string Surface, string Action, IReadOnlyList<string> Categories, string? FlowId = null);

    /// <summary>Makes the provider call and records it in the AI activity log, whether it worked or not. Only a length and a hash of what was sent are recorded.</summary>
    private async Task<string> CallLoggedAsync(AiActivityContext context, AiAuthoringEffectiveSettings effective, List<ChatMessage> messages, int maxTokens,
        CancellationToken cancellationToken, Func<string, (bool Produced, int Errors)>? summarize = null)
    {
        try
        {
            var text = await CallProviderAsync(effective, messages, maxTokens, cancellationToken).ConfigureAwait(false);
            (bool Produced, int Errors)? proposal = null;
            if (summarize is not null)
            {
                try { proposal = summarize(text); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Record(context, effective, messages, "failed", "The reply could not be processed.");
                    throw;
                }
            }
            Record(context, effective, messages, "completed", null, proposal);
            return text;
        }
        catch (AiAuthoringException exception)
        {
            Record(context, effective, messages, "failed", exception.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            Record(context, effective, messages, "cancelled", null);
            throw;
        }
    }

    private void Record(AiActivityContext context, AiAuthoringEffectiveSettings effective, List<ChatMessage> messages, string outcome, string? detail, (bool Produced, int Errors)? proposal = null)
    {
        if (activityLog is null) return;
        var sent = string.Concat(messages.Where(message => message.Role != ChatRole.System).Select(message => message.Text + "\n"));
        try
        {
            activityLog.Record(new(context.Surface, context.Action, effective.Provider, effective.Model, context.Categories, context.FlowId, sent.Length,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sent))), outcome, detail, proposal?.Produced, proposal?.Errors));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("The AI activity log could not record a request ({ExceptionType}).", exception.GetType().Name);
        }
    }
}
