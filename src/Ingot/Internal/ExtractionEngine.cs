using System.Text;
using Ingot.Internal.Schema;
using Ingot.Internal.Strategies;
using Ingot.Internal.Validation;
using Microsoft.Extensions.AI;

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

        // Clone: callers reuse ChatOptions instances across requests; we mutate ResponseFormat/Tools.
        var chatOptions = options.ChatOptions?.Clone() ?? new ChatOptions();
        var conversation = new List<ChatMessage>(messages);
        strategy.Prepare(conversation, chatOptions, plan);

        var attempts = new List<ExtractionAttempt>(options.Retry.MaxAttempts);
        var usage = new UsageAccumulator();

        for (var attemptNumber = 1; attemptNumber <= options.Retry.MaxAttempts; attemptNumber++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await client.GetResponseAsync(conversation, chatOptions, ct).ConfigureAwait(false);
            usage.Add(response.Usage);

            var payload = strategy.TryExtractPayload(response);
            if (payload is null)
            {
                // Nothing that even resembles JSON. Repair by restating the contract rather than
                // echoing a null payload back at the model.
                var noPayload = new ValidationFailure(
                    "$", "The response contained no JSON payload. Respond with only the JSON object.",
                    FailureCategory.NoPayload);
                attempts.Add(new ExtractionAttempt(attemptNumber, RawPayload: null, [noPayload]));

                if (attemptNumber == options.Retry.MaxAttempts) break;
                conversation.Add(new ChatMessage(ChatRole.Assistant, response.Text ?? string.Empty));
                conversation.Add(new ChatMessage(ChatRole.User, BuildRepairMessage([noPayload])));
                continue;
            }

            var (value, failures) = await ValidationPipeline
                .RunAsync<T>(payload, options, ct)
                .ConfigureAwait(false);

            attempts.Add(new ExtractionAttempt(attemptNumber, payload, failures));

            if (failures.Count == 0)
            {
                return new ExtractionResult<T>(value, isSuccess: true, attempts, usage.ToUsageDetails());
            }

            if (attemptNumber == options.Retry.MaxAttempts) break;

            conversation.Add(new ChatMessage(ChatRole.Assistant, payload));
            conversation.Add(new ChatMessage(ChatRole.User, BuildRepairMessage(failures)));
        }

        return new ExtractionResult<T>(default, isSuccess: false, attempts, usage.ToUsageDetails());
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
