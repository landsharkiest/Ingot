# Ingot — repository conventions

Ingot is an open-source (MIT) .NET library for structured, validated,
self-repairing LLM outputs: middleware over `Microsoft.Extensions.AI.IChatClient`.

## Build & test

```bash
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release
dotnet format                       # apply formatting/convention fixes
dotnet format --verify-no-changes   # what CI checks
```

Target framework: **net8.0**. Tests are **xUnit** behavior tests over a scripted
`FakeChatClient` (no live model calls in CI).

> **Status:** Published on NuGet as [`Ingot`](https://www.nuget.org/packages/Ingot) (0.1.0),
> reconciled against `Microsoft.Extensions.AI.Abstractions` 9.x. The build is clean
> (warnings-as-errors) and the full test suite passes. CI (build + test + format) gates every PR;
> CodeRabbit reviews automatically and can apply fixes via `@coderabbitai autofix`.

## Code conventions (enforced by `.editorconfig` + analyzers)

- **`TreatWarningsAsErrors` is on**, `Nullable` enabled, `AnalysisLevel=latest-recommended`. Warnings block the build.
- File-scoped namespaces; `using` directives outside the namespace, System first.
- `var` where the type is apparent; expression-bodied members where they fit on one line.
- **Public members require XML doc comments** (`GenerateDocumentationFile` is on).
- Library async code: accept a `CancellationToken`, propagate it, and use `ConfigureAwait(false)`.
- Private fields: `_camelCase`.
- Never swallow exceptions silently; validation failures carry JSON-cased paths (`$.lines[2].total`) because messages are addressed to the model.

## Contribution flow

1. Branch off `main` (`feat/…`, `fix/…`, `chore/…`).
2. PR titles follow **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, `test:`).
3. CI (build + test + format) must pass. CodeRabbit reviews automatically.
4. `main` is protected — merge via PR only.

## Architecture map

- `src/Ingot/` — public API (`ChatClientExtractionExtensions`, `ExtractionOptions`, `ExtractionResult<T>`, `ExtractionException`).
- `src/Ingot/Internal/ExtractionEngine.cs` — the parse → validate → repair loop.
- `src/Ingot/Internal/Schema/` — `ExtractionPlan` (cached) + `StrictSchemaTransformer`.
- `src/Ingot/Internal/Strategies/` — four provider strategies (NativeSchema / ToolCall / JsonMode / Prompted) + resolver.
- `src/Ingot/Internal/Validation/` — `ValidationPipeline` + recursive DataAnnotations validator.
- `src/Ingot/Internal/Json/LenientJson.cs` — fence-stripping tolerant reader (recovers, never "fixes").
- `tests/Ingot.Tests/` — behavior tests over `FakeChatClient`.
