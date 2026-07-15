# Contributing to Ingot

Thanks for your interest in improving Ingot. This document covers how to build the project, the
conventions we follow, and how to get a change merged.

## Getting started

Ingot targets **.NET 8.0**. With the SDK installed:

```bash
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release
```

Tests are xUnit behavior tests over a scripted `FakeChatClient` — no live model calls are made in
CI, so the suite is fully deterministic and offline.

## Before you open a pull request

Run the same checks CI enforces:

```bash
dotnet build --configuration Release      # warnings are errors
dotnet test  --configuration Release
dotnet format --verify-no-changes          # formatting / convention gate
```

`dotnet format` (without `--verify-no-changes`) applies the fixes automatically.

## Code conventions

Style is enforced by `.editorconfig` and analyzers; see [`CLAUDE.md`](CLAUDE.md) for the full list.
The essentials:

- **Warnings are errors**, nullable reference types are enabled, and public members require XML doc
  comments (documentation generation is on).
- File-scoped namespaces; `using` directives outside the namespace, `System` first.
- `var` where the type is apparent; expression-bodied members where they fit on one line.
- Library async code accepts a `CancellationToken`, propagates it, and uses `ConfigureAwait(false)`.
- Private fields are `_camelCase`.
- Never swallow exceptions silently. Validation failures carry JSON-cased paths (`$.lines[2].total`)
  because those messages are read by the model.

## Branching and commits

1. Branch off `main` using a conventional prefix: `feat/…`, `fix/…`, `chore/…`, `docs/…`, `test/…`.
2. Pull request titles follow [Conventional Commits](https://www.conventionalcommits.org/)
   (`feat:`, `fix:`, `chore:`, `docs:`, `test:`) — this is linted in CI.
3. `main` is protected; changes land via pull request only.
4. CI (build + test + format) must pass. Automated review runs on every PR.

## Architecture map

- `src/Ingot/` — public API (`ChatClientExtractionExtensions`, `ExtractionOptions`,
  `ExtractionResult<T>`, `ExtractionException`, `IExtractor`).
- `src/Ingot/Internal/ExtractionEngine.cs` — the parse → validate → repair loop.
- `src/Ingot/Internal/Schema/` — cached `ExtractionPlan` and the strict-schema transformer.
- `src/Ingot/Internal/Strategies/` — the four provider strategies and the resolver.
- `src/Ingot/Internal/Validation/` — the validation pipeline and recursive DataAnnotations validator.
- `tests/Ingot.Tests/` — Ring 1 behavior tests. `tests/Ingot.ProviderFixtures/` — Ring 2 recorded
  provider fixtures. `eval/Ingot.Evals/` — Ring 3 evaluation harness.

## Reporting bugs and requesting features

Open a GitHub issue with a minimal reproduction (the target type, the prompt, and the observed vs
expected result). For extraction failures, the `Attempts` collection on `ExtractionResult<T>` or
`ExtractionException` contains the full per-attempt history and is the most useful thing to include.
