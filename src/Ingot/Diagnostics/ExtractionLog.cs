using Microsoft.Extensions.Logging;

namespace Ingot.Diagnostics;

/// <summary>
/// Source-generated log messages for the extraction loop. Kept as compile-time delegates
/// (<see cref="LoggerMessageAttribute"/>) so logging is allocation-free when the level is disabled
/// and analyzer-clean under warnings-as-errors.
/// </summary>
internal static partial class ExtractionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Extraction of {TargetType} succeeded on attempt {Attempt} ({TotalTokens} total tokens).")]
    public static partial void Succeeded(ILogger logger, string targetType, int attempt, long? totalTokens);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Extraction of {TargetType} attempt {Attempt} failed validation ({FailureCount}): {Failures}")]
    public static partial void AttemptFailed(ILogger logger, string targetType, int attempt, int failureCount, string failures);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "Extraction of {TargetType} attempt {Attempt} raw payload: {Payload}")]
    public static partial void AttemptRawPayload(ILogger logger, string targetType, int attempt, string payload);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Extraction of {TargetType} exhausted after {Attempts} attempt(s): {Failures}")]
    public static partial void Exhausted(ILogger logger, string targetType, int attempts, string failures);
}
