using Microsoft.Extensions.AI;

namespace Ingot.Evals;

/// <summary>
/// Deterministic offline client: returns scripted assistant texts in order and reports a provider
/// so the engine resolves a real strategy. The offline suite uses this so the scorecard math is
/// reproducible without a network; a live run swaps in a real <see cref="IChatClient"/> instead.
/// </summary>
internal sealed class ScriptedChatClient(string providerName, params string[] scriptedTexts) : IChatClient
{
    private readonly Queue<string> _script = new(scriptedTexts);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var text = _script.Count > 0 ? _script.Dequeue() : "";
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50, TotalTokenCount = 150 },
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The offline eval suite is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(providerName) : null;

    public void Dispose()
    {
    }
}
