using Ingot.Internal;
using Microsoft.Extensions.AI;

namespace Ingot;

/// <summary>
/// Domain validation that runs after structural validation succeeds. Failures re-enter the
/// repair loop, so messages should tell the model what a correct value looks like
/// ("SKU 'A-9931' does not exist; valid SKUs appear in the source document"), not just that
/// the value is wrong.
/// </summary>
public interface ISemanticValidator<in T>
{
    /// <summary>Validates <paramref name="value"/>, returning any failures to feed back into the
    /// repair loop. Return an empty list to accept the value.</summary>
    /// <param name="value">The deserialized, structurally-valid object to check.</param>
    /// <param name="cancellationToken">Cancels any external work the validator performs.</param>
    ValueTask<IReadOnlyList<ValidationFailure>> ValidateAsync(T value, CancellationToken cancellationToken);
}

/// <summary>Entry point: typed, validated, self-repairing extraction over any <see cref="IChatClient"/>.</summary>
public static class ChatClientExtractionExtensions
{
    /// <summary>
    /// Extracts a <typeparamref name="T"/> from the model's response to <paramref name="prompt"/>.
    /// Schema generation, provider strategy selection, deserialization, validation, and
    /// repair-on-failure are all handled. Throws <see cref="ExtractionException"/> if the model
    /// cannot produce a valid instance within the retry policy.
    /// </summary>
    public static Task<T> ExtractAsync<T>(
        this IChatClient client,
        string prompt,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => client.ExtractAsync<T>([new ChatMessage(ChatRole.User, prompt)], options, cancellationToken);

    /// <summary>Multi-turn / multimodal overload: extract from a full conversation
    /// (which may include images or documents the provider supports).</summary>
    public static async Task<T> ExtractAsync<T>(
        this IChatClient client,
        IEnumerable<ChatMessage> messages,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await client.TryExtractAsync<T>(messages, options, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? result.Value
            : throw new ExtractionException(typeof(T), result.Attempts);
    }

    /// <summary>Non-throwing variant exposing the full attempt history and aggregate token usage.</summary>
    public static Task<ExtractionResult<T>> TryExtractAsync<T>(
        this IChatClient client,
        string prompt,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => client.TryExtractAsync<T>([new ChatMessage(ChatRole.User, prompt)], options, cancellationToken);

    /// <summary>Non-throwing variant exposing the full attempt history and aggregate token usage.</summary>
    public static Task<ExtractionResult<T>> TryExtractAsync<T>(
        this IChatClient client,
        IEnumerable<ChatMessage> messages,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(messages);
        return ExtractionEngine.RunAsync<T>(client, messages, options ?? new ExtractionOptions(), cancellationToken);
    }

    // Phase 2: ExtractStreamAsync<T> returning IAsyncEnumerable<Partial<T>> — incremental
    // assembly over GetStreamingResponseAsync with a tolerant reader. Kept off the MVP surface;
    // streaming validates late and we want the docs story ready before the API ships.
}
