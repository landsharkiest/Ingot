using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ingot.Diagnostics;
using Microsoft.Extensions.AI;

namespace Ingot.Evals;

/// <summary>
/// Runs the suite and scores it, subscribing to Ingot's own <see cref="IngotDiagnostics.Meter"/> by
/// name (dogfooding the observability layer) to total tokens independently of each result's usage.
/// With <c>verbose</c>, it also prints the <c>Ingot.extract</c> trace of each case — the trust claim
/// made visible.
/// </summary>
internal sealed class EvalRunner(Func<EvalCase, IChatClient> clientFactory, bool verbose)
{
    /// <summary>The default offline factory: a deterministic scripted client per case.</summary>
    public static IChatClient Offline(EvalCase @case) => new ScriptedChatClient(@case.Provider, @case.Responses);

    public async Task<(Scorecard Card, IReadOnlyList<CaseOutcome> Outcomes)> RunAsync(
        IReadOnlyList<EvalCase> cases, CancellationToken ct = default)
    {
        long meterTokens = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == IngotDiagnostics.SourceName && instrument.Name == "ingot.tokens")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref meterTokens, value));
        meterListener.Start();

        using var traceListener = verbose ? StartTracePrinter() : null;

        var outcomes = new List<CaseOutcome>(cases.Count);
        foreach (var @case in cases)
        {
            var client = clientFactory(@case);
            var options = new ExtractionOptions { Retry = @case.Retry };

            var started = Stopwatch.GetTimestamp();
            var result = await client.TryExtractAsync<Invoice>("Extract the invoice.", options, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            (client as IDisposable)?.Dispose();

            outcomes.Add(new CaseOutcome(
                @case.Name, result.IsSuccess, result.Attempts.Count,
                result.AggregateUsage.TotalTokenCount, elapsed.TotalMilliseconds, @case.ExpectSuccess));
        }

        meterListener.Dispose();
        return (Scorecard.From(outcomes, meterTokens), outcomes);
    }

    private static ActivityListener StartTracePrinter()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == IngotDiagnostics.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                Console.WriteLine($"  trace {activity.OperationName} " +
                    $"outcome={activity.GetTagItem("ingot.outcome")} " +
                    $"attempts={activity.GetTagItem("ingot.attempts")} " +
                    $"tokens={activity.GetTagItem("ingot.total_tokens")}");
                foreach (var e in activity.Events)
                {
                    Console.WriteLine($"    event {e.Name} " +
                        string.Join(" ", e.Tags.Select(t => $"{t.Key}={t.Value}")));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
