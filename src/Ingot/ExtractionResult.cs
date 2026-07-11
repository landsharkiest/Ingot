using Microsoft.Extensions.AI;

namespace Ingot;

/// <summary>Which stage of the pipeline rejected an attempt.</summary>
public enum FailureCategory
{
    /// <summary>The response contained no extractable JSON payload at all.</summary>
    NoPayload,
    /// <summary>The payload was not valid JSON, or could not be bound to the target type
    /// (includes transport conversions: malformed dates, URIs, GUIDs, enum values).</summary>
    Parse,
    /// <summary>A <see cref="System.ComponentModel.DataAnnotations"/> attribute rejected a value.</summary>
    Annotations,
    /// <summary>A user-supplied <see cref="ISemanticValidator{T}"/> rejected the object.</summary>
    Semantic,
}

/// <summary>A single validation failure, precise enough to be fed back to the model verbatim.</summary>
/// <param name="Path">JSON path of the offending value (e.g. <c>$.lines[2].sku</c>), or <c>$</c> for the root.</param>
/// <param name="Message">Human/model-readable description of what was wrong and what is expected.</param>
/// <param name="Category">The pipeline stage that produced the failure.</param>
public sealed record ValidationFailure(string Path, string Message, FailureCategory Category)
{
    /// <summary>Renders the failure as <c>path: message</c> for logs and repair prompts.</summary>
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>One model round-trip inside an extraction, kept for diagnostics and telemetry.</summary>
public sealed record ExtractionAttempt(
    int Number,
    string? RawPayload,
    IReadOnlyList<ValidationFailure> Failures)
{
    /// <summary>True when this attempt produced no validation failures.</summary>
    public bool Succeeded => Failures.Count == 0;
}

/// <summary>
/// The full outcome of an extraction.
/// <see cref="ChatClientExtractionExtensions.TryExtractAsync{T}(IChatClient, string, ExtractionOptions, CancellationToken)"/>
/// returns this directly;
/// <see cref="ChatClientExtractionExtensions.ExtractAsync{T}(IChatClient, string, ExtractionOptions, CancellationToken)"/>
/// unwraps <see cref="Value"/> or throws <see cref="ExtractionException"/>.
/// </summary>
public sealed class ExtractionResult<T>
{
    internal ExtractionResult(T? value, bool isSuccess, IReadOnlyList<ExtractionAttempt> attempts, UsageDetails usage)
    {
        _value = value;
        IsSuccess = isSuccess;
        Attempts = attempts;
        AggregateUsage = usage;
    }

    private readonly T? _value;

    /// <summary>True when an attempt produced a value that passed every validation stage.</summary>
    public bool IsSuccess { get; }

    /// <summary>The extracted value. Throws if <see cref="IsSuccess"/> is false — check first,
    /// or use <see cref="ValueOrDefault"/>.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            "Extraction did not succeed; inspect Attempts for the failure detail.");

    /// <summary>The extracted value, or <c>default</c> when the extraction failed. Never throws.</summary>
    public T? ValueOrDefault => _value;

    /// <summary>Every model round-trip, in order, including the failures that drove each repair.</summary>
    public IReadOnlyList<ExtractionAttempt> Attempts { get; }

    /// <summary>Token usage summed across all attempts. Retries cost money; we never hide that.
    /// (When TokenLedger is installed outboard of Ingot it meters each attempt independently.)</summary>
    public UsageDetails AggregateUsage { get; }
}

/// <summary>Thrown by <c>ExtractAsync</c> when all attempts are exhausted. Carries the complete
/// attempt history so callers can log exactly what the model returned and why it was rejected.</summary>
public sealed class ExtractionException : Exception
{
    internal ExtractionException(
        Type targetType,
        IReadOnlyList<ExtractionAttempt> attempts,
        UsageDetails aggregateUsage)
        : base(BuildMessage(targetType, attempts))
    {
        TargetType = targetType;
        Attempts = attempts;
        AggregateUsage = aggregateUsage;
    }

    /// <summary>The type the extraction was targeting.</summary>
    public Type TargetType { get; }

    /// <summary>Every model round-trip that was attempted, with the failures that rejected each.</summary>
    public IReadOnlyList<ExtractionAttempt> Attempts { get; }

    /// <summary>Token usage summed across every failed attempt.</summary>
    public UsageDetails AggregateUsage { get; }

    private static string BuildMessage(Type targetType, IReadOnlyList<ExtractionAttempt> attempts)
    {
        var last = attempts.Count > 0 ? attempts[^1].Failures : [];
        var detail = last.Count > 0
            ? string.Join("; ", last.Select(f => f.ToString()))
            : "no payload was produced";
        return $"Failed to extract '{targetType.Name}' after {attempts.Count} attempt(s). Last failures: {detail}";
    }
}
