using Microsoft.Extensions.AI;

namespace InvoiceExtractor;

/// <summary>
/// A stand-in <see cref="IChatClient"/> so the sample runs offline with no API key. The first
/// response is intentionally malformed (bad date) so you can watch Ingot repair it. Swap this for a
/// real client — e.g. <c>new OpenAIClient(key).AsChatClient("gpt-4o-mini")</c> — to hit a live model.
/// </summary>
internal sealed class ScriptedChatClient(string providerName, params string[] scriptedTexts) : IChatClient
{
    private readonly Queue<string> _script = new(scriptedTexts);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = _script.Count > 0 ? _script.Dequeue() : _script.LastOrDefault() ?? "";
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 90, OutputTokenCount = 40, TotalTokenCount = 130 },
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The sample is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(providerName) : null;

    public void Dispose()
    {
    }
}
