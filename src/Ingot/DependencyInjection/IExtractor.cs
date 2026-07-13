using Microsoft.Extensions.AI;

namespace Ingot;

/// <summary>
/// A DI-friendly extraction service: the same typed, validated, self-repairing extraction as the
/// <see cref="ChatClientExtractionExtensions"/> methods, but with the <see cref="IChatClient"/> and
/// <see cref="ExtractionOptions"/> already bound. Register it with
/// <c>services.AddIngotExtraction(...)</c> and inject <see cref="IExtractor"/> where you need it.
/// </summary>
public interface IExtractor
{
    /// <summary>Extracts a <typeparamref name="T"/> from the model's response to <paramref name="prompt"/>,
    /// throwing <see cref="ExtractionException"/> if no valid instance is produced within the retry policy.</summary>
    Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default);

    /// <summary>Extracts a <typeparamref name="T"/> from a full conversation, throwing on unrecoverable failure.</summary>
    Task<T> ExtractAsync<T>(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Non-throwing variant exposing the full attempt history and aggregate token usage.</summary>
    Task<ExtractionResult<T>> TryExtractAsync<T>(string prompt, CancellationToken cancellationToken = default);

    /// <summary>Non-throwing variant over a full conversation.</summary>
    Task<ExtractionResult<T>> TryExtractAsync<T>(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IExtractor"/>: a thin binding of an <see cref="IChatClient"/> and
/// <see cref="ExtractionOptions"/> that delegates to the extension-method surface — no engine logic
/// lives here. Construct it directly, or let <c>AddIngotExtraction</c> wire it up.
/// </summary>
public sealed class ChatClientExtractor(IChatClient client, ExtractionOptions options) : IExtractor
{
    private readonly IChatClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ExtractionOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default)
        => _client.ExtractAsync<T>(prompt, _options, cancellationToken);

    /// <inheritdoc />
    public Task<T> ExtractAsync<T>(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        => _client.ExtractAsync<T>(messages, _options, cancellationToken);

    /// <inheritdoc />
    public Task<ExtractionResult<T>> TryExtractAsync<T>(string prompt, CancellationToken cancellationToken = default)
        => _client.TryExtractAsync<T>(prompt, _options, cancellationToken);

    /// <inheritdoc />
    public Task<ExtractionResult<T>> TryExtractAsync<T>(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
        => _client.TryExtractAsync<T>(messages, _options, cancellationToken);
}
