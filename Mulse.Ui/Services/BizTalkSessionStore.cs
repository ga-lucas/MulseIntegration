using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using Mulse.Ui.Models;

namespace Mulse.Ui.Services;

public sealed record BizTalkSessionListing(string Name, DateTimeOffset? UpdatedUtc);

/// <summary>Keeps import sessions in the browser. Sessions are per browser and per user; they are not shared or backed up, so export them to keep a copy.</summary>
public interface IBizTalkSessionStore
{
    Task<IReadOnlyList<BizTalkSessionListing>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The saved session with its pasted and uploaded content, or null when none has that name.</summary>
    Task<BizTalkImportSession?> LoadAsync(string name, CancellationToken cancellationToken);

    Task SaveAsync(BizTalkImportSession session, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Browser storage failures surface as InvalidOperationException so callers handle one type.</summary>
public sealed class BizTalkSessionStore(IJSRuntime js) : IBizTalkSessionStore, IAsyncDisposable
{
    // The circuit accepts only about 32 KB per message from the browser, so stored content is read back in slices.
    private const int SliceLength = 16 * 1024;
    private IJSObjectReference? _module;

    private async Task<T> CallAsync<T>(string function, CancellationToken cancellationToken, params object?[] arguments)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./js/biztalk-session-store.js");
            return await _module.InvokeAsync<T>(function, cancellationToken, arguments);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException)
        {
            throw new InvalidOperationException($"Browser storage failed in '{function}': {exception.Message}", exception);
        }
    }

    private async Task CallAsync(string function, CancellationToken cancellationToken, params object?[] arguments)
        => _ = await CallAsync<object?>(function, cancellationToken, arguments);

    public async Task<IReadOnlyList<BizTalkSessionListing>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await CallAsync<List<Listing>>("listSessions", cancellationToken);
        return [.. items.Select(item => new BizTalkSessionListing(item.Name, item.UpdatedUtc)).OrderByDescending(item => item.UpdatedUtc).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<BizTalkImportSession?> LoadAsync(string name, CancellationToken cancellationToken)
    {
        var json = await CallAsync<string?>("loadIndex", cancellationToken, name);
        if (json is null) return null;
        var session = BizTalkImportSession.FromIndexJson(json);
        foreach (var item in session.Evidence.Where(e => e.Origin != BizTalkEvidenceOrigins.Path))
            if (await ReadContentAsync(name, item.Id, cancellationToken) is { } content)
                session.Content[item.Id] = content;
        return session;
    }

    private async Task<BizTalkEvidenceContent?> ReadContentAsync(string session, string id, CancellationToken cancellationToken)
    {
        var length = await CallAsync<int?>("openContent", cancellationToken, session, id);
        if (length is null) return null;
        try
        {
            var json = new StringBuilder(length.Value);
            for (var offset = 0; offset < length; offset += SliceLength)
                json.Append(await CallAsync<string>("readContentSlice", cancellationToken, session, id, offset, Math.Min(SliceLength, length.Value - offset)));
            return JsonSerializer.Deserialize<BizTalkEvidenceContent>(json.ToString());
        }
        finally
        {
            await CallAsync("releaseContent", CancellationToken.None, session, id);
        }
    }

    public async Task SaveAsync(BizTalkImportSession session, CancellationToken cancellationToken)
    {
        if (!BizTalkImportSession.IsValidName(session.Name)) throw new InvalidOperationException($"'{session.Name}' is not a valid session name.");
        var previous = await CallAsync<string?>("loadIndex", cancellationToken, session.Name);
        foreach (var (id, content) in session.Content.Where(pair => session.Evidence.Any(e => e.Id == pair.Key)))
            await CallAsync("putContent", cancellationToken, session.Name, id, JsonSerializer.Serialize(content));
        if (previous is not null)
            foreach (var old in BizTalkImportSession.FromIndexJson(previous).Evidence.Where(e => session.Evidence.All(current => current.Id != e.Id)))
                await CallAsync("deleteContent", cancellationToken, session.Name, old.Id);
        await CallAsync("saveIndex", cancellationToken, session.Name, session.ToIndexJson());
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await CallAsync("deleteSessionContent", cancellationToken, name);
        await CallAsync("deleteIndex", cancellationToken, name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }

    private sealed record Listing(string Name, DateTimeOffset? UpdatedUtc);
}
