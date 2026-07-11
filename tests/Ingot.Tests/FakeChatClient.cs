using Microsoft.Extensions.AI;

namespace Ingot.Tests;

/// <summary>
/// Deterministic <see cref="IChatClient"/> for ring-1 tests: returns scripted responses in
/// order and records every request (messages + options) for assertions. Ring 2 (recorded
/// provider fixtures) and ring 3 (nightly live evals) live in separate projects — this fake
/// exists so engine behavior (repair loops, message shapes, option mutation) is testable
/// without a network or a provider SDK.
/// </summary>
public sealed class FakeChatClient(string providerName, params string[] scriptedTexts) : IChatClient
{
    private readonly Queue<string> _script = new(scriptedTexts);

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add((messages.ToList(), options));

        if (_script.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeChatClient script exhausted after {CallCount - 1} response(s) — " +
                "the code under test made more calls than the test scripted.");
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _script.Dequeue()))
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50, TotalTokenCount = 150 },
        };
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Streaming lands in Phase 2.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(providerName) : null;

    public void Dispose()
    {
    }
}
