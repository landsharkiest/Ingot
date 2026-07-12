using System.Diagnostics;
using System.Text;
using Ingot.Diagnostics;
using Ingot.Internal.Schema;
using Ingot.Internal.Strategies;
using Ingot.Internal.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ingot.Internal;

/// <summary>
/// Orchestrates one extraction: prepare the request via the resolved strategy, then loop
/// (invoke → extract payload → validate → repair) until success or the retry policy is exhausted.
///
/// Design notes:
///  * The engine is stateless and static; all state lives in locals. There is nothing to pool.
///  * The conversation grows monotonically across repairs — the model must see its own failed
///    output followed by the structured failure list. That message pair IS the product; treat
///    changes to <see cref="BuildRepairMessage"/> as behavior changes requiring eval runs.
///  * Cancellation is checked before each model call, not mid-validation: validation is cheap
///    and local, the model call is the expensive cancellable unit.
///  * Instrumentation is a single seam here (see <see cref="IngotDiagnostics"/>): one span per
///    extraction, one event per attempt, metrics on completion. It never alters control flow.
/// </summary>
internal static class ExtractionEngine
{
    public static async Task<ExtractionResult<T>> RunAsync<T>(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ExtractionOptions options,
        CancellationToken ct)
    {
        var plan = ExtractionPlan.Create<T>(options);
        var strategy = StrategyResolver.Resolve(client, options.Mode);
        var strategyName = strategy.GetType().Name;
        var provider = (client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata)?.ProviderName;
        var targetName = typeof(T).Name;
        var diagnostics = options.Diagnostics;
        var logger = diagnostics.LoggerFactory?.CreateLogger("Ingot.Extraction");

        // Clone: callers reuse ChatOptions instances across requests; we mutate ResponseFormat/Tools.
        var chatOptions = options.ChatOptions?.Clone() ?? new ChatOptions();
        var conversation = new List<ChatMessage>(messages);
        strategy.Prepare(conversation, chatOptions, plan);

        var attempts = new List<ExtractionAttempt>(options.Retry.MaxAttempts);
        var usage = new UsageAccumulator();

        using var activity = IngotDiagnostics.ActivitySource.StartActivity("Ingot.extract", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag("ingot.target_type", targetName);
            activity.SetTag("ingot.mode", options.Mode.ToString());
            activity.SetTag("ingot.strategy", strategyName);
            if (provider is not null) activity.SetTag("gen_ai.system", provider);
        }

        var startedAt = Stopwatch.GetTimestamp();
        var succeeded = false;
        var attemptsUsed = 0;

        try
        {
            for (var attemptNumber = 1; attemptNumber <= options.Retry.MaxAttempts; attemptNumber++)
            {
                ct.ThrowIfCancellationRequested();
                attemptsUsed = attemptNumber;

                var response = await client.GetResponseAsync(conversation, chatOptions, ct).ConfigureAwait(false);
                usage.Add(response.Usage);
                RecordTokens(response.Usage, strategyName, provider);

                var payload = strategy.TryExtractPayload(response);
                if (payload is null)
                {
                    // Nothing that even resembles JSON. Repair by restating the contract rather than
                    // echoing a null payload back at the model.
                    var noPayload = new ValidationFailure(
                        "$", "The response contained no JSON payload. Respond with only the JSON object.",
                        FailureCategory.NoPayload);
                    var attempt = new ExtractionAttempt(attemptNumber, RawPayload: null, [noPayload], response.Usage);
                    attempts.Add(attempt);
                    RecordAttempt(activity, logger, targetName, attempt, diagnostics, strategyName);

                    if (attemptNumber == options.Retry.MaxAttempts) break;
                    conversation.Add(new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty));
                    conversation.Add(new ChatMessage(ChatRole.User, BuildRepairMessage([noPayload])));
                    continue;
                }

                var (value, failures) = await ValidationPipeline
                    .RunAsync<T>(payload, options, ct)
                    .ConfigureAwait(false);

                var validated = new ExtractionAttempt(attemptNumber, payload, failures, response.Usage);
                attempts.Add(validated);
                RecordAttempt(activity, logger, targetName, validated, diagnostics, strategyName);

                if (failures.Count == 0)
                {
                    succeeded = true;
                    return new ExtractionResult<T>(value, isSuccess: true, attempts, usage.ToUsageDetails());
                }

                if (attemptNumber == options.Retry.MaxAttempts) break;

                conversation.Add(new ChatMessage(ChatRole.Assistant, payload));
                conversation.Add(new ChatMessage(ChatRole.User, BuildRepairMessage(failures)));
            }

            return new ExtractionResult<T>(default, isSuccess: false, attempts, usage.ToUsageDetails());
        }
        finally
        {
            var lastFailures = attempts.Count > 0 ? attempts[^1].Failures : [];
            Complete(activity, logger, targetName, succeeded, attemptsUsed, lastFailures,
                usage.ToUsageDetails(), startedAt, strategyName, provider, diagnostics, ct);
        }
    }

    /// <summary>
    /// The repair prompt. Empirically-driven shape (see eval suite): enumerate concrete failures
    /// with paths and expectations, then restate the two rules models most often violate —
    /// conform to the original schema, and return nothing but JSON.
    /// </summary>
    private static string BuildRepairMessage(IReadOnlyList<ValidationFailure> failures)
    {
        var sb = new StringBuilder("Your previous response failed validation:\n");
        foreach (var f in failures)
        {
            sb.Append("- ").Append(f.Path).Append(": ").AppendLine(f.Message);
        }
        sb.Append("Return a corrected response that satisfies the original JSON schema. ")
          .Append("Return ONLY the JSON — no commentary, no code fences.");
        return sb.ToString();
    }

    /// <summary>Meters one attempt's tokens by direction; low, bounded cardinality (strategy, provider).</summary>
    private static void RecordTokens(UsageDetails? usage, string strategyName, string? provider)
    {
        if (usage is null) return;

        if (usage.InputTokenCount is long input)
        {
            IngotDiagnostics.Tokens.Add(input, TokenTags(strategyName, provider, "input"));
        }

        if (usage.OutputTokenCount is long output)
        {
            IngotDiagnostics.Tokens.Add(output, TokenTags(strategyName, provider, "output"));
        }
    }

    private static TagList TokenTags(string strategyName, string? provider, string direction)
    {
        var tags = new TagList
        {
            { "ingot.strategy", strategyName },
            { "gen_ai.token.type", direction },
        };
        if (provider is not null) tags.Add("gen_ai.system", provider);
        return tags;
    }

    /// <summary>
    /// Records the per-attempt span event (structural only — never raw payload or messages),
    /// bumps the failure counter by category, and logs at Warning/Debug when a logger is present.
    /// </summary>
    private static void RecordAttempt(
        Activity? activity,
        ILogger? logger,
        string targetName,
        ExtractionAttempt attempt,
        DiagnosticsOptions diagnostics,
        string strategyName)
    {
        if (activity is not null)
        {
            var tags = new ActivityTagsCollection
            {
                { "ingot.attempt", attempt.Number },
                { "ingot.succeeded", attempt.Succeeded },
                { "ingot.failure_count", attempt.Failures.Count },
                { "ingot.payload_length", attempt.RawPayload?.Length ?? 0 },
            };
            if (attempt.Failures.Count > 0)
            {
                tags.Add("ingot.failure_categories", DistinctCategories(attempt.Failures));
            }
            activity.AddEvent(new ActivityEvent("attempt", tags: tags));
        }

        foreach (var failure in attempt.Failures)
        {
            IngotDiagnostics.Failures.Add(1, new TagList
            {
                { "ingot.failure_category", failure.Category.ToString() },
                { "ingot.strategy", strategyName },
            });
        }

        if (logger is null || attempt.Succeeded) return;

        ExtractionLog.AttemptFailed(logger, targetName, attempt.Number, attempt.Failures.Count,
            SummarizeFailures(attempt.Failures, diagnostics));

        if (!diagnostics.RedactPayloads && attempt.RawPayload is { Length: > 0 } raw)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                var payload = Cap(raw, diagnostics.MaxLoggedPayloadLength);
                ExtractionLog.AttemptRawPayload(logger, targetName, attempt.Number, payload);
            }
        }
    }

    /// <summary>Finalizes telemetry once, in a finally, so success and exhaustion share one path.</summary>
    private static void Complete(
        Activity? activity,
        ILogger? logger,
        string targetName,
        bool succeeded,
        int attemptsUsed,
        IReadOnlyList<ValidationFailure> lastFailures,
        UsageDetails aggregateUsage,
        long startedAt,
        string strategyName,
        string? provider,
        DiagnosticsOptions diagnostics,
        CancellationToken ct)
    {
        var canceled = !succeeded && ct.IsCancellationRequested;
        var outcome = succeeded ? "success" : canceled ? "canceled" : "failed";
        var repairRounds = Math.Max(0, attemptsUsed - 1);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        var tags = new TagList
        {
            { "ingot.strategy", strategyName },
            { "ingot.outcome", outcome },
        };
        if (provider is not null) tags.Add("gen_ai.system", provider);

        IngotDiagnostics.Extractions.Add(1, tags);
        IngotDiagnostics.Duration.Record(elapsed.TotalSeconds, tags);
        IngotDiagnostics.RepairRounds.Record(repairRounds, tags);

        if (activity is not null)
        {
            activity.SetTag("ingot.outcome", outcome);
            activity.SetTag("ingot.attempts", attemptsUsed);
            activity.SetTag("ingot.repair_rounds", repairRounds);
            activity.SetTag("ingot.total_tokens", aggregateUsage.TotalTokenCount);
            activity.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        }

        if (logger is null) return;

        if (succeeded)
        {
            ExtractionLog.Succeeded(logger, targetName, attemptsUsed, aggregateUsage.TotalTokenCount);
        }
        else if (!canceled)
        {
            ExtractionLog.Exhausted(logger, targetName, attemptsUsed, SummarizeFailures(lastFailures, diagnostics));
        }
    }

    private static string DistinctCategories(IReadOnlyList<ValidationFailure> failures)
    {
        var seen = new HashSet<FailureCategory>();
        var sb = new StringBuilder();
        foreach (var f in failures)
        {
            if (seen.Add(f.Category))
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(f.Category);
            }
        }
        return sb.ToString();
    }

    /// <summary>Failure detail for logs: full <c>path: message</c> normally, structural
    /// <c>path [Category]</c> when redacting, bounded to the configured length either way.</summary>
    private static string SummarizeFailures(IReadOnlyList<ValidationFailure> failures, DiagnosticsOptions diagnostics)
    {
        if (failures.Count == 0) return "no payload was produced";

        var sb = new StringBuilder();
        foreach (var f in failures)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(diagnostics.RedactPayloads ? $"{f.Path} [{f.Category}]" : f.ToString());
        }
        return Cap(sb.ToString(), diagnostics.MaxLoggedPayloadLength);
    }

    private static string Cap(string value, int max)
    {
        if (max < 0) max = 0;
        return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
    }

    /// <summary>
    /// Sums <see cref="UsageDetails"/> across attempts without assuming any particular
    /// accumulation helper exists on the abstraction (that surface has moved between versions).
    /// </summary>
    private sealed class UsageAccumulator
    {
        private long? _input, _output, _total;

        public void Add(UsageDetails? usage)
        {
            if (usage is null) return;
            _input = Sum(_input, usage.InputTokenCount);
            _output = Sum(_output, usage.OutputTokenCount);
            _total = Sum(_total, usage.TotalTokenCount);
        }

        public UsageDetails ToUsageDetails() => new()
        {
            InputTokenCount = _input,
            OutputTokenCount = _output,
            TotalTokenCount = _total,
        };

        private static long? Sum(long? a, long? b) => (a, b) switch
        {
            (null, null) => null,
            (null, _) => b,
            (_, null) => a,
            _ => a + b,
        };
    }
}
