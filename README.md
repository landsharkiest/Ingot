# Ingot — working draft

Structured, validated, self-repairing LLM outputs for .NET, built as middleware over
`Microsoft.Extensions.AI.IChatClient`.

```csharp
public record Invoice(
    string VendorName,
    DateOnly IssuedOn,                              // the type SK's schema mode can't handle
    [property: Range(0, 1_000_000)] decimal Total);

Invoice invoice = await chatClient.ExtractAsync<Invoice>(
    $"Extract the invoice from this email:\n{emailBody}");
```

One call: schema generated from the type, provider-appropriate strategy selected from client
metadata, response parsed, `DateOnly` converted, `[Range]` enforced, and invalid responses
repaired via retry-with-error-feedback. Unrecoverable failure throws `ExtractionException`
carrying the full attempt history — never a silent null.

## Install

```bash
dotnet add package Ingot
```

## Dependency injection

Register once and inject `IExtractor` — a registered `ILoggerFactory` is auto-wired for
extraction logging:

```csharp
services.AddSingleton<IChatClient>(/* your provider's IChatClient */);
services.AddIngotExtraction(options => options.Retry = RetryPolicy.Default);

public sealed class InvoiceService(IExtractor extractor)
{
    public Task<Invoice> ParseAsync(string email, CancellationToken ct = default) =>
        extractor.ExtractAsync<Invoice>($"Extract the invoice:\n{email}", ct);
}
```

A runnable tour — direct use, DI, and a live OpenTelemetry trace of the repair loop — is in
[`samples/InvoiceExtractor`](samples/InvoiceExtractor): `dotnet run --project samples/InvoiceExtractor`.

## Repository layout

```text
src/Ingot/
  ExtractionOptions.cs                  Options, RetryPolicy, ExtractionMode
  ExtractionResult.cs                   Result/Attempt/Failure/Exception diagnostics
  ChatClientExtractionExtensions.cs     Public API + ISemanticValidator<T>
  DependencyInjection/                  IExtractor + AddIngotExtraction (DI wiring)
  Diagnostics/IngotDiagnostics.cs       ActivitySource + Meter ("Ingot"), instruments
  Diagnostics/ExtractionLog.cs          Source-generated [LoggerMessage] delegates
  Internal/
    ExtractionEngine.cs                 Repair-loop orchestration + usage accumulation + telemetry
    Schema/ExtractionPlan.cs            Cached per-type plan (schema + names)
    Schema/StrictSchemaTransformer.cs   format-keyword folding, strict-mode compliance ← core IP
    Strategies/Strategies.cs            NativeSchema / ToolCall / JsonMode / Prompted + resolver
    Json/LenientJson.cs                 Fence stripping, balanced-JSON extraction
    Validation/ValidationPipeline.cs    Parse → recursive DataAnnotations → semantic
tests/Ingot.Tests/
  FakeChatClient.cs                     Scripted, request-recording IChatClient (ring 1)
  ExtractionEngineTests.cs              Engine behavior: repair, annotations, semantic, exhaustion
  DiagnosticsTests.cs                   Spans, metrics, per-attempt usage, redaction
tests/Ingot.ProviderFixtures/           Ring 2: recorded provider responses replayed (no network)
eval/Ingot.Evals/                       Ring 3: eval harness scoring success/cost + scorecard
samples/InvoiceExtractor/               Runnable tour: direct use, DI, OpenTelemetry trace
```

## Engineering status — read before building

This slice was authored offline against the `Microsoft.Extensions.AI.Abstractions` surface as
of early 2026. That surface has churned across preview waves. **First task on a connected
machine:** restore the current stable package and reconcile the following call sites (each is
also marked in-source):

1. `ChatResponseFormat.ForJsonSchema(JsonElement, string?, string?)` — NativeSchemaStrategy.
2. `AIFunction` override set: `Name`, `Description`, `JsonSchema`, `InvokeCoreAsync(AIFunctionArguments, CancellationToken)` — ToolCallStrategy.DeclaredExtractionTool.
3. `ChatToolMode.RequireSpecific(string)` — ToolCallStrategy.
4. `AIJsonUtilities.CreateJsonSchema(Type, serializerOptions:)` — ExtractionPlan. Newer versions
   accept an `AIJsonSchemaCreateOptions` with transform hooks; if the built-in transform can
   express our strict-mode rules, prefer it and shrink `StrictSchemaTransformer` accordingly.
5. `ChatClientMetadata.ProviderName` casing/values per adapter — StrategyResolver's table.
6. `ChatResponse.Messages` / `.Text` / `.Usage` shapes — Engine + strategies.
7. `FunctionCallContent.Arguments` type (currently `IDictionary<string, object?>`) — ToolCallStrategy payload serialization.

## Deliberate decisions (the "why" behind the diffs a reviewer would question)

**No converter registry.** STJ already round-trips `DateOnly`/`DateTime`/`TimeSpan`/`Uri`/`Guid`
as ISO/RFC strings. The actual gap is that strict schema mode rejects the `format` keyword those
types export. So the fix lives entirely in `StrictSchemaTransformer` (fold `format` into
`description`), and malformed values become ordinary parse failures feeding the ordinary repair
loop. One error path, no parallel machinery.

**Constraints go in twice, on purpose.** `[Range]`, `pattern`, `minLength` are folded into
schema `description` (model guidance) *and* enforced by DataAnnotations post-parse (real
enforcement). Guidance reduces retries; enforcement guarantees correctness.

**Recursive DataAnnotations.** The BCL `Validator` only validates the top object — a
long-standing footgun. `RecursiveAnnotationValidator` walks the graph with cycle and depth
guards and reports **JSON-cased paths** (`$.lines[2].total`), because the repair prompt is
addressed to the model, which wrote camelCase.

**All semantic validators run before repairing.** One repair pass fixing three reported
problems beats three sequential repair round-trips. Token cost is the currency here.

**`LenientJson` recovers, never repairs.** Fence stripping and balanced-extraction only.
Heuristic JSON "fixing" would hide model misbehavior from the eval data that drives prompt and
strategy tuning.

**Strategies are internal for now.** The `IExtractionStrategy` seam is designed to go public
(community provider strategies) but stays internal until the shape survives a third provider.
Public API is forever; internal is Tuesday.

**Usage is never hidden.** `ExtractionResult<T>.AggregateUsage` sums tokens across every
attempt, and each `ExtractionAttempt.Usage` meters that attempt independently — the two views
reconcile by design. Retries cost money, and both the metric (`ingot.tokens`) and the result
object say so.

## Observability

Instrumentation is a single seam in the engine and always on — near-free when nothing listens.
Subscribe by name from OpenTelemetry or a plain listener:

```csharp
builder.AddSource(IngotDiagnostics.SourceName)   // "Ingot" — one span per extraction
       .AddMeter(IngotDiagnostics.SourceName);    //           + event per attempt
```

One `Ingot.extract` span (target type, strategy, provider, outcome, attempts, repair rounds,
tokens) carries one event per attempt (structural only — never raw payload). Metrics:
`ingot.extractions`, `ingot.extraction.duration`, `ingot.repair_rounds`, `ingot.tokens`,
`ingot.failures` (by category). This composes with the host's own `UseOpenTelemetry()` on the
`IChatClient` — those spans cover the model calls, these cover the extraction around them.
Logging is opt-in via `ExtractionOptions.Diagnostics` (an `ILoggerFactory`, plus `RedactPayloads`
and a length cap for bounded, redactable diagnostics).

## Test rings

Ring 1 (`tests/Ingot.Tests`): deterministic engine tests over `FakeChatClient`.
Ring 2 (`tests/Ingot.ProviderFixtures`): recorded provider responses (tool-call for Anthropic,
text for Ollama/OpenAI/prompted) replayed through the real engine — no network. Covers the
ToolCall and JsonMode strategies and cross-provider repair; the on-disk format is what live
captures drop into.
Ring 3 (`eval/Ingot.Evals`): an eval harness scoring first-attempt success, post-repair success,
avg repair rounds, tokens/success, and latency, emitting a markdown + JSON scorecard. Runs a
deterministic offline suite today (and as a nightly workflow); swap in a live `IChatClient` to
score a real model. It subscribes to Ingot's own meter — the observability layer feeds the eval.

## Product direction

Ingot is the trust layer after structured output. Provider-native schemas improve the chance of
valid JSON, but providers may ignore response-format hints and schema conformance does not prove
domain correctness. Ingot keeps those concerns portable: request shaping, local validation,
self-repair, diagnostics, and retry cost accounting all sit over `IChatClient`.

## Phase 1 remaining work

Delivered: `ILogger`/`Activity` instrumentation (span per extraction, event per attempt, metrics),
bounded and redactable diagnostics, recorded provider fixtures (ring 2), and the eval harness
(ring 3, offline suite with a live-client seam).

Still open: source-generated schemas + `JsonSerializerContext` for AOT (`Ingot.SourceGenerators`),
FluentValidation adapter package, capability-registry overrides in options, the docs site skeleton
with the provider capability matrix, post-deserialization structural validation for prompted/JSON
modes, live-model eval fixtures, and a pinned `Microsoft.Extensions.AI` compatibility baseline
before a first NuGet release.
