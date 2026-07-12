using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ingot.ProviderFixtures;

/// <summary>
/// Replays a captured provider exchange through the real extraction engine — no network, no SDK.
/// Each queued <see cref="FixtureTurn"/> becomes one <see cref="ChatResponse"/> shaped the way that
/// provider actually returns structured output: a <see cref="FunctionCallContent"/> for tool-call
/// providers (Anthropic), plain assistant text for JSON-mode / native-schema / prompted providers.
/// This is what lets the ToolCall and JsonMode strategies be exercised end-to-end.
/// </summary>
public sealed class RecordedChatClient(string providerName, IEnumerable<FixtureTurn> turns) : IChatClient
{
    private readonly Queue<FixtureTurn> _turns = new(turns);

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add((messages.ToList(), options));

        if (_turns.Count == 0)
        {
            throw new InvalidOperationException(
                $"RecordedChatClient exhausted after {CallCount - 1} response(s) — the engine made " +
                "more calls than the fixture recorded.");
        }

        var turn = _turns.Dequeue();
        var content = turn.ToolArguments is { } args
            ? new ChatMessage(ChatRole.Assistant, [BuildToolCall(args)])
            : new ChatMessage(ChatRole.Assistant, turn.Text ?? string.Empty);

        var response = new ChatResponse(content)
        {
            Usage = new UsageDetails { InputTokenCount = 120, OutputTokenCount = 60, TotalTokenCount = 180 },
        };
        return Task.FromResult(response);
    }

    private static FunctionCallContent BuildToolCall(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        // Mirror how an MEAI adapter surfaces tool arguments: a dictionary of JsonElement values.
        var boxed = arguments.ToDictionary(static kv => kv.Key, static kv => (object?)kv.Value);
        return new FunctionCallContent(callId: "call_fixture", name: "emit_extraction", arguments: boxed);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Fixtures replay non-streaming responses only.");

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(providerName) : null;

    public void Dispose()
    {
    }
}
