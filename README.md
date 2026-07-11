# Ingot — Phase 1 vertical slice

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

## Repository layout

```
src/Ingot/
  ExtractionOptions.cs                  Options, RetryPolicy, ExtractionMode
  ExtractionResult.cs                   Result/Attempt/Failure/Exception diagnostics
  ChatClientExtractionExtensions.cs     Public API + ISemanticValidator<T>
  Internal/
    ExtractionEngine.cs                 Repair-loop orchestration + usage accumulation
    Schema/ExtractionPlan.cs            Cached per-type plan (schema + names)
    Schema/StrictSchemaTransformer.cs   format-keyword folding, strict-mode compliance ← core IP
    Strategies/Strategies.cs            NativeSchema / ToolCall / JsonMode / Prompted + resolver
    Json/LenientJson.cs                 Fence stripping, balanced-JSON extraction
    Validation/ValidationPipeline.cs    Parse → recursive DataAnnotations → semantic
tests/Ingot.Tests/
  FakeChatClient.cs                     Scripted, request-recording IChatClient (ring 1)
  ExtractionEngineTests.cs              Engine behavior: repair, annotations, semantic, exhaustion
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
attempt. Retries cost money; when TokenLedger sits outboard it meters each attempt
independently — the two views reconcile by design.

## Test rings

Ring 1 (this repo): deterministic engine tests over `FakeChatClient`.
Ring 2 (Phase 1 exit): recorded provider fixtures replaying real schema-mode quirks per version.
Ring 3 (nightly): live-model eval benchmark (~50 extraction tasks) scoring first-attempt
success, post-repair success, and cost — the numbers we publish.

## Phase 1 remaining work

Source-generated schemas + `JsonSerializerContext` for AOT (`Ingot.SourceGenerators`),
FluentValidation adapter package, capability-registry overrides in options, `ILogger`/`Activity`
instrumentation (span per extraction, event per attempt), and the docs site skeleton with the
provider capability matrix.
