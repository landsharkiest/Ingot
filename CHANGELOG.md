# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

_No unreleased changes yet._

## [0.1.0] - 2026-07-13

Initial public release.

### Added

- `ExtractAsync<T>` / `TryExtractAsync<T>` extension methods over any `IChatClient` — typed,
  validated, self-repairing extraction from a prompt or a full conversation.
- Provider-aware strategy resolution: `NativeSchema` (OpenAI / Azure OpenAI), `ToolCall`
  (Anthropic), `JsonMode` (Ollama), and a universal `Prompted` fallback, with per-call override via
  `ExtractionOptions.Mode`.
- Repair loop that feeds JSON-cased validation failures back to the model, bounded by a configurable
  `RetryPolicy`.
- Recursive `System.ComponentModel.DataAnnotations` validation across the full object graph, plus
  async `ISemanticValidator<T>` for domain rules.
- Per-attempt and aggregate token accounting on `ExtractionResult<T>` and `ExtractionException`.
- OpenTelemetry instrumentation: an always-on `Ingot.extract` span and a `Meter` exposing
  `ingot.extractions`, `ingot.extraction.duration`, `ingot.repair_rounds`, `ingot.tokens`, and
  `ingot.failures`. Opt-in, redactable structured logging via `ExtractionOptions.Diagnostics`.
- Dependency-injection integration: `AddIngotExtraction()` registers a configured `IExtractor` and
  auto-wires a registered `ILoggerFactory`.
- Runnable `samples/InvoiceExtractor` tour and a three-ring test/eval suite
  (`Ingot.Tests`, `Ingot.ProviderFixtures`, `Ingot.Evals`).

[Unreleased]: https://github.com/landsharkiest/Ingot/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/landsharkiest/Ingot/releases/tag/v0.1.0
