using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Ingot.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ingot.Tests;

public sealed class DiagnosticsTests
{
    private sealed record Invoice(
        string VendorName,
        [property: Range(0, 1_000_000)] decimal Total);

    private const string ValidJson = """{"vendorName":"Acme Corp","total":450.00}""";
    private const string OverRangeJson = """{"vendorName":"Acme Corp","total":-450.00}""";

    [Fact]
    public async Task Extract_FirstTrySuccess_EmitsSpanWithAttemptEventAndMetrics()
    {
        using var trace = new TraceCapture();
        using var metrics = new MetricCapture();

        var result = await new FakeChatClient("openai", ValidJson)
            .TryExtractAsync<Invoice>("Extract.");

        Assert.True(result.IsSuccess);

        // One span, tagged with the outcome and zero repairs, carrying a single attempt event.
        var span = Assert.Single(trace.Stopped, a => a.OperationName == "Ingot.extract");
        Assert.Equal("success", span.GetTagItem("ingot.outcome"));
        Assert.Equal(1, span.GetTagItem("ingot.attempts"));
        Assert.Equal(0, span.GetTagItem("ingot.repair_rounds"));
        Assert.Single(span.Events, e => e.Name == "attempt");

        // Metrics: one successful extraction, zero repair rounds, tokens split by direction.
        Assert.Contains(metrics.Records, m =>
            m.Name == "ingot.extractions" && m.Value == 1 && m.Tag("ingot.outcome") == "success");
        Assert.Contains(metrics.Records, m => m.Name == "ingot.repair_rounds" && m.Value == 0);
        Assert.Contains(metrics.Records, m =>
            m.Name == "ingot.tokens" && m.Value == 100 && m.Tag("gen_ai.token.type") == "input");
        Assert.Contains(metrics.Records, m =>
            m.Name == "ingot.tokens" && m.Value == 50 && m.Tag("gen_ai.token.type") == "output");

        // Per-attempt usage is now populated on the attempt record (the "TokenLedger").
        Assert.Equal(150, result.Attempts[0].Usage!.TotalTokenCount);
    }

    [Fact]
    public async Task Extract_RepairThenSuccess_RecordsTwoAttemptsOneRepairRoundAndFailureMetric()
    {
        using var trace = new TraceCapture();
        using var metrics = new MetricCapture();

        var result = await new FakeChatClient("openai", OverRangeJson, ValidJson)
            .TryExtractAsync<Invoice>("Extract.");

        Assert.True(result.IsSuccess);

        var span = Assert.Single(trace.Stopped, a => a.OperationName == "Ingot.extract");
        Assert.Equal(2, span.GetTagItem("ingot.attempts"));
        Assert.Equal(1, span.GetTagItem("ingot.repair_rounds"));
        Assert.Equal(2, span.Events.Count(e => e.Name == "attempt"));

        Assert.Contains(metrics.Records, m => m.Name == "ingot.repair_rounds" && m.Value == 1);
        // The first attempt's Range violation is metered by category.
        Assert.Contains(metrics.Records, m =>
            m.Name == "ingot.failures" && m.Tag("ingot.failure_category") == "Annotations");
    }

    [Fact]
    public async Task Extract_WithoutListeners_DoesNotThrow_AndSpanIsNull()
    {
        // No ActivityListener/MeterListener attached: instrumentation must be a no-op.
        var result = await new FakeChatClient("openai", ValidJson).TryExtractAsync<Invoice>("Extract.");
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Logging_RedactPayloads_OmitsRawPayloadAndMessages()
    {
        var redacted = new CaptureLogger();
        var options = RedactionOptions(redacted, redact: true);

        var result = await new FakeChatClient("openai", OverRangeJson)
            .TryExtractAsync<Invoice>("Extract.", options);

        Assert.False(result.IsSuccess);
        // Structural detail is present; payload contents (vendor name) are not.
        Assert.Contains(redacted.Messages, m => m.Contains("$.total"));
        Assert.DoesNotContain(redacted.Messages, m => m.Contains("Acme Corp"));
        Assert.DoesNotContain(redacted.Messages, m => m.Contains("raw payload"));
    }

    [Fact]
    public async Task Logging_WithoutRedaction_IncludesRawPayload()
    {
        var verbose = new CaptureLogger();
        var options = RedactionOptions(verbose, redact: false);

        await new FakeChatClient("openai", OverRangeJson).TryExtractAsync<Invoice>("Extract.", options);

        Assert.Contains(verbose.Messages, m => m.Contains("Acme Corp"));
    }

    private static ExtractionOptions RedactionOptions(ILoggerFactory factory, bool redact)
    {
        var options = new ExtractionOptions { Retry = RetryPolicy.None };
        options.Diagnostics.LoggerFactory = factory;
        options.Diagnostics.RedactPayloads = redact;
        return options;
    }

    // ---------------------------------------------------------------- capture harnesses

    private sealed class TraceCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        public List<Activity> Stopped { get; } = [];

        public TraceCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == IngotDiagnostics.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Stopped.Add,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string key) =>
            Tags.FirstOrDefault(t => t.Key == key).Value?.ToString();
    }

    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public List<Measurement> Records { get; } = [];

        public MetricCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == IngotDiagnostics.SourceName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Records.Add(new(i.Name, v, t.ToArray())));
            _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Records.Add(new(i.Name, v, t.ToArray())));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Records.Add(new(i.Name, v, t.ToArray())));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CaptureLogger : ILoggerFactory, ILogger
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        public void Dispose() { }
    }
}
