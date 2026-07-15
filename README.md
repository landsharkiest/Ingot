# Ingot

**Structured, validated, self-repairing LLM outputs for .NET.** One call turns a prompt into a
strongly-typed, fully-validated object — over any
[`Microsoft.Extensions.AI.IChatClient`](https://learn.microsoft.com/dotnet/ai/).

[![NuGet](https://img.shields.io/nuget/v/Ingot.svg?logo=nuget)](https://www.nuget.org/packages/Ingot)
[![Downloads](https://img.shields.io/nuget/dt/Ingot.svg?logo=nuget)](https://www.nuget.org/packages/Ingot)
[![CI](https://github.com/landsharkiest/Ingot/actions/workflows/ci.yml/badge.svg)](https://github.com/landsharkiest/Ingot/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/landsharkiest/Ingot/blob/main/LICENSE)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com/)

---

Microsoft's `GetResponseAsync<T>` deserializes a model response into your type — then trusts it.
It does not validate business rules, it does not retry on malformed output, and it does not
guarantee the model honored your schema. Ingot is the trust layer that sits on top: it shapes the
request per provider, parses defensively, enforces your validation rules, and **repairs invalid
responses by feeding the errors back to the model** — with full token accounting and OpenTelemetry
instrumentation on every attempt.

```csharp
using Ingot;
using System.ComponentModel.DataAnnotations;

public record Invoice(
    string VendorName,
    DateOnly IssuedOn,
    [property: Range(0, 1_000_000)] decimal Total);

// One call: schema generation, provider-appropriate strategy, parsing,
// DateOnly conversion, [Range] enforcement, and repair-on-failure.
Invoice invoice = await chatClient.ExtractAsync<Invoice>(
    $"Extract the invoice from this email:\n{emailBody}");
```

If the model returns `"June 1st, 2026"` instead of an ISO date, Ingot catches the parse failure,
tells the model exactly what was wrong (`$.issuedOn: The JSON value could not be converted`), and
retries. If the total exceeds the `[Range]`, that is caught too. When every attempt is exhausted,
Ingot throws an `ExtractionException` carrying the complete attempt history — **never a silent
`null` or an out-of-range value.**

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Provider support](#provider-support)
- [Dependency injection](#dependency-injection)
- [Validation](#validation)
- [Observability](#observability)
- [Configuration](#configuration)
- [Reliability &amp; testing](#reliability--testing)
- [Design principles](#design-principles)
- [Roadmap](#roadmap)
- [Contributing](#contributing)
- [License](#license)

## Features

- **Typed extraction in one call** — `ExtractAsync<T>` / `TryExtractAsync<T>` over any
  `IChatClient`. Your POCOs and records are the schema; no DSL, no code generation.
- **Provider-aware request shaping** — OpenAI structured outputs, Anthropic forced tool calls,
  Ollama JSON mode, and a universal prompted fallback, selected automatically from client metadata.
- **Self-repair loop** — invalid responses are returned to the model with precise, JSON-cased
  error paths (`$.lines[2].total`), so the retry is corrective rather than a blind re-roll.
- **Real validation** — recursive `System.ComponentModel.DataAnnotations` across the whole object
  graph, plus your own async `ISemanticValidator<T>` for domain rules (catalog lookups, cross-field
  checks). All failures are collected and repaired in a single pass.
- **Honest cost accounting** — per-attempt and aggregate token usage on every result; retries are
  never hidden.
- **First-class observability** — an always-on `Ingot.extract` OpenTelemetry span and a `Meter`
  with extraction, duration, repair-round, token, and failure metrics. Opt-in, redactable logging.
- **Dependency-injection ready** — `AddIngotExtraction()` registers a configured `IExtractor`;
  a registered `ILoggerFactory` is auto-wired.
- **No silent failures** — unrecoverable extractions throw with the full attempt history attached.

## Installation

```bash
dotnet add package Ingot
```

Requires **.NET 8.0** or later. Ingot depends only on the `Microsoft.Extensions.AI.Abstractions`,
`Microsoft.Extensions.Logging.Abstractions`, and `Microsoft.Extensions.DependencyInjection.Abstractions`
surfaces — bring your own provider `IChatClient`.

## Quick start

```csharp
using Ingot;
using Microsoft.Extensions.AI;

// Any Microsoft.Extensions.AI client works — OpenAI, Azure OpenAI, Anthropic, Ollama, ...
IChatClient chatClient = /* your provider's IChatClient */;

// Throwing overload — returns T or throws ExtractionException.
Invoice invoice = await chatClient.ExtractAsync<Invoice>(
    $"Extract the invoice from this email:\n{emailBody}");

// Non-throwing overload — full attempt history and token usage, never throws on model failure.
ExtractionResult<Invoice> result = await chatClient.TryExtractAsync<Invoice>(prompt);
if (result.IsSuccess)
{
    Console.WriteLine($"{result.Value.VendorName} — {result.AggregateUsage.TotalTokenCount} tokens");
}
else
{
    foreach (var attempt in result.Attempts)
        Console.WriteLine($"attempt {attempt.Number}: {string.Join(", ", attempt.Failures)}");
}
```

A runnable tour — direct use, dependency injection, and a live OpenTelemetry trace of the repair
loop — ships in
[`samples/InvoiceExtractor`](https://github.com/landsharkiest/Ingot/tree/main/samples/InvoiceExtractor):

```bash
dotnet run --project samples/InvoiceExtractor
```

## How it works

Every extraction flows through a single engine that runs the same pipeline regardless of provider:

```
prompt ─▶ schema (from T) ─▶ strategy (per provider) ─▶ model call
                                                            │
                                          ┌── parse ◀───────┘
                                          ▼
                              DataAnnotations (recursive)
                                          ▼
                              semantic validators (async)
                                          │
                    ┌── all pass ─────────┴───────── any fail ──┐
                    ▼                                            ▼
              return typed T                    feed JSON-cased errors back,
                                                retry until the policy is exhausted
```

1. **Schema** is generated from the target type once and cached, keyed on the type and the
   schema-affecting serializer settings.
2. **Strategy** is resolved from `ChatClientMetadata.ProviderName` (or pinned via
   `ExtractionOptions.Mode`) and shapes the request so the model emits schema-conforming JSON.
3. **Validation** parses the payload into `T`, runs DataAnnotations across the whole graph, then
   runs any registered semantic validators — collecting *all* failures before deciding to repair.
4. **Repair** feeds the collected failures back to the model as a follow-up turn and retries, up to
   the configured `RetryPolicy`.

## Provider support

Ingot selects a strategy from the client's reported provider and degrades safely to a
universally-correct fallback for anything unrecognized. Any strategy can be forced per call with
`ExtractionOptions.Mode`.

| Provider | Reported name | Strategy | Mechanism |
| --- | --- | --- | --- |
| OpenAI / Azure OpenAI | `openai`, `azure.ai.openai`, `azureopenai` | `NativeSchema` | Structured outputs (`json_schema`, strict) |
| Anthropic | `anthropic` | `ToolCall` | Schema as a single required tool; arguments read back |
| Ollama | `ollama` | `JsonMode` | Provider JSON mode + schema in the system prompt |
| Any other / unknown | — | `Prompted` | Schema in the prompt + lenient payload recovery |

Provider-native schema modes improve the odds of valid JSON but do not prove *domain* correctness,
and providers may ignore response-format hints — so the validation and repair pipeline runs on top
of every strategy, including the native ones.

## Dependency injection

Register once and inject `IExtractor`. If an `ILoggerFactory` is registered and you did not set one
explicitly, it is auto-wired into diagnostics.

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<IChatClient>(/* your provider's IChatClient */);
services.AddIngotExtraction(options =>
{
    options.Retry = RetryPolicy.Default;              // 3 attempts (initial + 2 repairs)
    options.Validation.UseDataAnnotations = true;
});

public sealed class InvoiceService(IExtractor extractor)
{
    public Task<Invoice> ParseAsync(string email, CancellationToken ct = default) =>
        extractor.ExtractAsync<Invoice>($"Extract the invoice:\n{email}", ct);
}
```

## Validation

**DataAnnotations, recursively.** The BCL `Validator` only validates the top-level object. Ingot's
recursive validator walks the entire object graph — nested objects, collections, dictionaries —
with cycle and depth guards, and reports failures as **JSON-cased paths** (`$.lines[2].total`)
because those messages are addressed to the model, which wrote camelCase.

**Semantic validators** express domain rules that DataAnnotations cannot. They may be asynchronous
and may call external systems; their failures re-enter the repair loop exactly like structural ones,
so the message should describe what a *correct* value looks like:

```csharp
public sealed class SkuValidator(ICatalog catalog) : ISemanticValidator<Order>
{
    public async ValueTask<IReadOnlyList<ValidationFailure>> ValidateAsync(
        Order order, CancellationToken ct)
    {
        var failures = new List<ValidationFailure>();
        for (var i = 0; i < order.Lines.Count; i++)
        {
            var sku = order.Lines[i].Sku;
            if (!await catalog.ExistsAsync(sku, ct))
                failures.Add(new ValidationFailure(
                    $"$.lines[{i}].sku",
                    $"SKU '{sku}' does not exist; valid SKUs appear in the source document.",
                    FailureCategory.Semantic));
        }
        return failures;
    }
}

// Registration:
options.Validation.SemanticValidators.Add(new SkuValidator(catalog));
```

All validators run before any repair, so one corrective round-trip can fix several reported problems
at once — token cost is the currency, and three fixes in one retry beats three sequential retries.

## Observability

Instrumentation is a single always-on seam in the engine — near-free when nothing is listening.
Subscribe by name from OpenTelemetry (or any `ActivityListener` / `MeterListener`):

```csharp
builder.AddSource(IngotDiagnostics.SourceName)   // "Ingot" — one span per extraction
       .AddMeter(IngotDiagnostics.SourceName);    //           + one event per attempt
```

Each extraction emits one `Ingot.extract` span tagged with the target type, resolved strategy,
provider, outcome, attempt count, repair rounds, and aggregate tokens, carrying one structural event
per attempt (never the raw payload). The `Meter` exposes:

| Metric | Kind | Dimensions |
| --- | --- | --- |
| `ingot.extractions` | counter | outcome, provider, strategy |
| `ingot.extraction.duration` | histogram | outcome, provider, strategy |
| `ingot.repair_rounds` | histogram | provider, strategy |
| `ingot.tokens` | counter | direction (input/output), attempt |
| `ingot.failures` | counter | category |

This composes with the host's own `UseOpenTelemetry()` on the `IChatClient` — those spans cover the
model calls, these cover the extraction around them. Structured logging is opt-in via
`ExtractionOptions.Diagnostics` (an `ILoggerFactory` plus `RedactPayloads` and a payload-length cap
for bounded, redactable diagnostics); spans and metrics never carry raw payloads regardless.

## Configuration

All per-call behavior lives on `ExtractionOptions` (passed to `ExtractAsync`/`TryExtractAsync`, or
configured once via `AddIngotExtraction`):

| Option | Type | Default | Purpose |
| --- | --- | --- | --- |
| `Mode` | `ExtractionMode` | `Auto` | Force a strategy, or resolve from client metadata |
| `Retry` | `RetryPolicy` | `Default` (3 attempts) | Repair-loop budget; `RetryPolicy.None` disables repair |
| `Validation.UseDataAnnotations` | `bool` | `true` | Recursive DataAnnotations enforcement |
| `Validation.SemanticValidators` | `IList<object>` | empty | Registered `ISemanticValidator<T>` instances |
| `ChatOptions` | `ChatOptions?` | `null` | Pass-through to the underlying client (Ingot clones before mutating) |
| `SerializerOptions` | `JsonSerializerOptions` | web defaults | Deserialization and schema generation |
| `Diagnostics.LoggerFactory` | `ILoggerFactory?` | `null` | Opt-in structured logging |
| `Diagnostics.RedactPayloads` | `bool` | `false` | Omit raw payloads from logs |
| `Diagnostics.MaxLoggedPayloadLength` | `int` | `1024` | Truncation bound for logged payloads |

## Reliability &amp; testing

Correctness is enforced under warnings-as-errors and gated by CI (build + test + format) on every
change. Tests run in three rings:

- **Ring 1** (`tests/Ingot.Tests`) — deterministic behavior tests over a scripted `FakeChatClient`:
  repair, DataAnnotations, semantic validation, exhaustion, DI wiring, spans/metrics, and the schema
  cache. No network.
- **Ring 2** (`tests/Ingot.ProviderFixtures`) — recorded real provider responses (Anthropic
  tool-calls, Ollama/OpenAI/prompted text) replayed through the real engine, exercising the
  `ToolCall` and `JsonMode` strategies and cross-provider repair. No network.
- **Ring 3** (`eval/Ingot.Evals`) — an evaluation harness scoring first-attempt success, post-repair
  success, average repair rounds, tokens per success, and latency, emitting a markdown + JSON
  scorecard. Runs a deterministic offline suite (and a nightly workflow); point it at a live
  `IChatClient` to score a real model.

## Design principles

- **One error path.** STJ already round-trips `DateOnly`/`DateTime`/`Uri`/`Guid` as ISO/RFC strings;
  the real gap is that strict schema mode rejects the `format` keyword those types export. Ingot
  folds `format` into the description and lets malformed values become ordinary parse failures — no
  parallel converter machinery.
- **Constraints go in twice, on purpose.** `[Range]`, `pattern`, and `minLength` are folded into the
  schema description (model guidance, fewer retries) *and* enforced post-parse (real correctness).
- **Recover, never guess.** `LenientJson` strips code fences and extracts balanced JSON, but it never
  heuristically "fixes" a payload — that would hide model misbehavior from the eval data that drives
  tuning.
- **Cost is always visible.** `AggregateUsage` sums tokens across every attempt and each
  `ExtractionAttempt.Usage` meters its own; the `ingot.tokens` metric and the result object reconcile
  by design.
- **Public API is forever.** The `IExtractionStrategy` seam is designed to go public for community
  provider strategies, but stays internal until the shape survives contact with a third provider.

## Roadmap

Ingot's engine, provider strategies, validation pipeline, observability, and evaluation harness are
in place. On the horizon:

- Source-generated schemas + `JsonSerializerContext` for trimming / native AOT.
- A `Ingot.FluentValidation` adapter bridging `IValidator<T>` into the validation pipeline.
- Streaming partial extraction (`IAsyncEnumerable<Partial<T>>`).
- Built-in LLM-judge semantic validators over the existing `ISemanticValidator<T>` seam.
- A public strategy-registration hook for community providers.
- A documentation site with the full provider capability matrix.

## Contributing

Contributions are welcome. Please read [`CONTRIBUTING.md`](https://github.com/landsharkiest/Ingot/blob/main/CONTRIBUTING.md)
for the branch, commit, and CI conventions, and [`CLAUDE.md`](https://github.com/landsharkiest/Ingot/blob/main/CLAUDE.md)
for the code style enforced by `.editorconfig` and analyzers. CI (build + test + format) must pass on
every pull request.

## License

Ingot is released under the [MIT License](https://github.com/landsharkiest/Ingot/blob/main/LICENSE).
