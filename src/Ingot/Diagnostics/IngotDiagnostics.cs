using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Ingot.Diagnostics;

/// <summary>
/// The observability surface for Ingot: one <see cref="System.Diagnostics.ActivitySource"/> and one
/// <see cref="System.Diagnostics.Metrics.Meter"/>, both named <see cref="SourceName"/>. Subscribe to
/// them by name from OpenTelemetry (<c>.AddSource(IngotDiagnostics.SourceName)</c> /
/// <c>.AddMeter(IngotDiagnostics.SourceName)</c>) or a plain <see cref="ActivityListener"/> /
/// <see cref="MeterListener"/>.
/// </summary>
/// <remarks>
/// Instrumentation is always on and near-free when nothing is listening — no configuration is
/// required to get traces and metrics. This composes with (does not replace) the host's own
/// <c>UseOpenTelemetry()</c> on the underlying <c>IChatClient</c>: those spans cover the model
/// calls; these cover the extraction that wraps them (repairs, validation outcomes, total cost).
/// </remarks>
public static class IngotDiagnostics
{
    /// <summary>The name shared by Ingot's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string SourceName = "Ingot";

    private static readonly string Version =
        typeof(IngotDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(IngotDiagnostics).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    internal static readonly ActivitySource ActivitySource = new(SourceName, Version);

    internal static readonly Meter Meter = new(SourceName, Version);

    /// <summary>Count of completed extractions, tagged by outcome/strategy/provider.</summary>
    internal static readonly Counter<long> Extractions =
        Meter.CreateCounter<long>("ingot.extractions", unit: "{extraction}",
            description: "Completed extractions, by outcome.");

    /// <summary>Wall-clock duration of an extraction (all attempts), in seconds.</summary>
    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("ingot.extraction.duration", unit: "s",
            description: "End-to-end extraction duration including repairs.");

    /// <summary>Number of repair rounds an extraction needed (0 = succeeded first try).</summary>
    internal static readonly Histogram<int> RepairRounds =
        Meter.CreateHistogram<int>("ingot.repair_rounds", unit: "{round}",
            description: "Repair rounds per extraction (attempts beyond the first).");

    /// <summary>Tokens consumed, tagged by direction (<c>input</c>/<c>output</c>). Retries cost money.</summary>
    internal static readonly Counter<long> Tokens =
        Meter.CreateCounter<long>("ingot.tokens", unit: "{token}",
            description: "Tokens consumed across all attempts, by direction.");

    /// <summary>Validation failures, tagged by <see cref="FailureCategory"/>.</summary>
    internal static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("ingot.failures", unit: "{failure}",
            description: "Per-attempt validation failures, by category.");
}
