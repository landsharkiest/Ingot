namespace Ingot.Evals;

/// <summary>The scored result of one eval case.</summary>
internal sealed record CaseOutcome(
    string Name,
    bool Success,
    int Attempts,
    long? TotalTokens,
    double LatencyMs,
    bool ExpectSuccess)
{
    public int RepairRounds => Math.Max(0, Attempts - 1);

    /// <summary>True when the observed outcome matched the case's expectation (offline self-test).</summary>
    public bool SelfCheckOk => Success == ExpectSuccess;
}

/// <summary>
/// The numbers that substantiate the trust claim: how often extraction works on the first try, how
/// often repair rescues it, what that costs in tokens, and how long it takes.
/// </summary>
internal sealed record Scorecard(
    int Total,
    int FirstAttemptSuccess,
    int PostRepairSuccess,
    int Failed,
    double FirstAttemptSuccessRate,
    double OverallSuccessRate,
    double AvgRepairRounds,
    long TotalTokens,
    long MeterTokens,
    double TokensPerSuccess,
    double P50LatencyMs,
    double P95LatencyMs,
    bool AllSelfChecksPassed)
{
    public static Scorecard From(IReadOnlyList<CaseOutcome> outcomes, long meterTokens)
    {
        var total = outcomes.Count;
        var firstAttempt = outcomes.Count(o => o.Success && o.Attempts == 1);
        var postRepair = outcomes.Count(o => o.Success && o.Attempts > 1);
        var failed = outcomes.Count(o => !o.Success);
        var successes = firstAttempt + postRepair;
        var totalTokens = outcomes.Sum(o => o.TotalTokens ?? 0);
        var latencies = outcomes.Select(o => o.LatencyMs).OrderBy(static x => x).ToArray();

        return new Scorecard(
            Total: total,
            FirstAttemptSuccess: firstAttempt,
            PostRepairSuccess: postRepair,
            Failed: failed,
            FirstAttemptSuccessRate: Rate(firstAttempt, total),
            OverallSuccessRate: Rate(successes, total),
            AvgRepairRounds: total == 0 ? 0 : outcomes.Average(o => o.RepairRounds),
            TotalTokens: totalTokens,
            MeterTokens: meterTokens,
            TokensPerSuccess: successes == 0 ? 0 : Math.Round((double)totalTokens / successes, 1),
            P50LatencyMs: Percentile(latencies, 0.50),
            P95LatencyMs: Percentile(latencies, 0.95),
            AllSelfChecksPassed: outcomes.All(o => o.SelfCheckOk));
    }

    private static double Rate(int n, int total) => total == 0 ? 0 : Math.Round((double)n / total, 3);

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(rank, 0, sorted.Length - 1)], 2);
    }
}
