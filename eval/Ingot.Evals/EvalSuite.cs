using System.ComponentModel.DataAnnotations;

namespace Ingot.Evals;

/// <summary>The target the offline suite extracts.</summary>
internal sealed record Invoice(
    string VendorName,
    DateOnly IssuedOn,
    [property: Range(0, 1_000_000)] decimal Total);

/// <summary>
/// One scored task: a provider + scripted responses that drive a specific path (clean first try,
/// repair, exhaustion), plus whether it is expected to succeed. <see cref="ExpectSuccess"/> turns
/// the offline run into a self-test of the scoring math.
/// </summary>
internal sealed record EvalCase(
    string Name,
    string Provider,
    string[] Responses,
    RetryPolicy Retry,
    bool ExpectSuccess);

/// <summary>
/// A deterministic offline suite spanning first-try success, single- and double-repair recovery,
/// the prompted/lenient path, and unrecoverable failure — enough for the scorecard to be meaningful.
/// Swap the client factory in <see cref="EvalRunner"/> for a live one to score a real model.
/// </summary>
internal static class EvalSuite
{
    private const string Valid = """{"vendorName":"Acme Corp","issuedOn":"2026-06-01","total":450.00}""";
    private const string BadDate = """{"vendorName":"Acme Corp","issuedOn":"June 1st, 2026","total":450.00}""";
    private const string OverRange = """{"vendorName":"Acme Corp","issuedOn":"2026-06-01","total":-5.00}""";
    private const string Fenced =
        "Here you go:\n```json\n{\"vendorName\":\"Umbrella\",\"issuedOn\":\"2026-07-02\",\"total\":99.99}\n```";

    public static IReadOnlyList<EvalCase> Cases { get; } =
    [
        new("clean-first-try", "openai", [Valid], RetryPolicy.Default, ExpectSuccess: true),
        new("malformed-date-repair", "openai", [BadDate, Valid], RetryPolicy.Default, ExpectSuccess: true),
        new("range-violation-repair", "openai", [OverRange, Valid], RetryPolicy.Default, ExpectSuccess: true),
        new("double-repair", "openai", [BadDate, OverRange, Valid], RetryPolicy.Default, ExpectSuccess: true),
        new("prompted-fenced", "mystery-llm", [Fenced], RetryPolicy.Default, ExpectSuccess: true),
        new("unrecoverable", "openai", ["nope", "still nope", "no json here"], RetryPolicy.Default, ExpectSuccess: false),
    ];
}
