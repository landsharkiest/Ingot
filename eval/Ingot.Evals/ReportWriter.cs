using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ingot.Evals;

/// <summary>Writes the scorecard as machine-readable JSON and a human-readable markdown report.</summary>
internal static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Write(string outputDirectory, Scorecard card, IReadOnlyList<CaseOutcome> outcomes)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "eval-report.json"),
            JsonSerializer.Serialize(new { scorecard = card, cases = outcomes }, Json));
        File.WriteAllText(Path.Combine(outputDirectory, "eval-report.md"), Markdown(card, outcomes));
    }

    public static string Markdown(Scorecard card, IReadOnlyList<CaseOutcome> outcomes)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Ingot eval scorecard").AppendLine();
        sb.AppendLine(inv, $"- **Cases:** {card.Total}");
        sb.AppendLine(inv, $"- **First-attempt success:** {card.FirstAttemptSuccess} ({card.FirstAttemptSuccessRate:P1})");
        sb.AppendLine(inv, $"- **Post-repair success:** {card.PostRepairSuccess}");
        sb.AppendLine(inv, $"- **Overall success:** {card.OverallSuccessRate:P1}");
        sb.AppendLine(inv, $"- **Failed:** {card.Failed}");
        sb.AppendLine(inv, $"- **Avg repair rounds:** {card.AvgRepairRounds:F2}");
        sb.AppendLine(inv, $"- **Total tokens:** {card.TotalTokens} (meter-observed: {card.MeterTokens})");
        sb.AppendLine(inv, $"- **Tokens / success:** {card.TokensPerSuccess}");
        sb.AppendLine(inv, $"- **Latency p50 / p95:** {card.P50LatencyMs:F1} ms / {card.P95LatencyMs:F1} ms");
        sb.AppendLine(inv, $"- **Self-checks passed:** {(card.AllSelfChecksPassed ? "yes" : "NO")}");
        sb.AppendLine();

        sb.AppendLine("| Case | Outcome | Attempts | Repairs | Tokens | Latency (ms) | Self-check |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var o in outcomes)
        {
            var outcome = o.Success ? "success" : "failed";
            sb.AppendLine(inv,
                $"| {o.Name} | {outcome} | {o.Attempts} | {o.RepairRounds} | {o.TotalTokens} | " +
                $"{o.LatencyMs:F1} | {(o.SelfCheckOk ? "ok" : "MISMATCH")} |");
        }
        return sb.ToString();
    }
}
