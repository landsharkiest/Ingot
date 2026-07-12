using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ingot;

/// <summary>How Ingot asks the model for schema-conforming output.</summary>
public enum ExtractionMode
{
    /// <summary>Resolve the best strategy from the client's <see cref="ChatClientMetadata"/>. Default.</summary>
    Auto = 0,

    /// <summary>Provider-native structured outputs (e.g. OpenAI <c>json_schema</c> with <c>strict: true</c>).</summary>
    NativeSchema,

    /// <summary>Expose the schema as a single required tool and read the arguments back.
    /// Most reliable path on Anthropic models.</summary>
    ToolCall,

    /// <summary>Provider JSON mode (<c>json_object</c>) plus the schema embedded in the system prompt.</summary>
    JsonMode,

    /// <summary>Schema embedded in the prompt, lenient parsing (fence stripping, first-JSON extraction).
    /// Works against any model; last resort in <see cref="Auto"/> resolution.</summary>
    Prompted,
}

/// <summary>Per-call configuration for <c>ExtractAsync</c>.</summary>
public sealed class ExtractionOptions
{
    /// <summary>Which extraction strategy to use, or <see cref="ExtractionMode.Auto"/> to resolve
    /// one from the client's metadata. Default: <see cref="ExtractionMode.Auto"/>.</summary>
    public ExtractionMode Mode { get; set; } = ExtractionMode.Auto;

    /// <summary>The repair-loop policy (how many attempts before giving up). Default:
    /// <see cref="RetryPolicy.Default"/> (3 attempts).</summary>
    public RetryPolicy Retry { get; set; } = RetryPolicy.Default;

    /// <summary>The validation stages that run after every model attempt.</summary>
    public ValidationOptions Validation { get; } = new();

    /// <summary>
    /// Options passed through to the underlying <see cref="IChatClient"/>. Ingot clones this
    /// before mutating (it owns <see cref="ChatOptions.ResponseFormat"/>, and in
    /// <see cref="ExtractionMode.ToolCall"/> also <see cref="ChatOptions.Tools"/>/<see cref="ChatOptions.ToolMode"/>).
    /// </summary>
    public ChatOptions? ChatOptions { get; set; }

    /// <summary>
    /// Serializer used for deserialization and schema generation. Defaults to web defaults
    /// (camelCase, case-insensitive read) which matches what models most naturally emit.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Controls the validation stages that run after every model attempt.</summary>
public sealed class ValidationOptions
{
    /// <summary>Run <see cref="System.ComponentModel.DataAnnotations"/> attributes recursively
    /// over the deserialized graph. Default: true.</summary>
    public bool UseDataAnnotations { get; set; } = true;

    /// <summary>
    /// Domain validators (may be async, may hit external systems). Failures are fed back to the
    /// model in the repair loop exactly like structural failures. Register instances of
    /// <see cref="ISemanticValidator{T}"/> for the extraction target type.
    /// </summary>
    public IList<object> SemanticValidators { get; } = [];
}

/// <summary>Repair-loop policy. Attempt 1 is the initial call; each subsequent attempt feeds the
/// previous failure back to the model.</summary>
public sealed class RetryPolicy
{
    /// <summary>3 total attempts (initial + 2 repairs). In our benchmarking of comparable tools,
    /// the large majority of repairable failures resolve on the first repair.</summary>
    public static RetryPolicy Default { get; } = new() { MaxAttempts = 3 };

    /// <summary>No repair loop: one attempt, fail fast.</summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    private int _maxAttempts = 3;

    /// <summary>Total attempts allowed, including the initial call (so 3 = initial + 2 repairs).
    /// Must be at least 1.</summary>
    public int MaxAttempts
    {
        get => _maxAttempts;
        init => _maxAttempts = value >= 1 ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "At least one attempt is required.");
    }

    // Phase 2: model escalation — ThenEscalateTo(IChatClient) so early attempts run on a
    // cheap model and only failures pay for the frontier model. Deliberately omitted from
    // the MVP surface so we don't lock the shape before the eval benchmark exists.
}
