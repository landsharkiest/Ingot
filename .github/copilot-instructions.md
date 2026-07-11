# Copilot instructions — Ingot

Guidance for GitHub Copilot (any backing model, including **GPT‑5.6 Sol**) and
other coding agents when reading, reviewing, or making changes in this repo.
These are the same conventions in [`CLAUDE.md`](../CLAUDE.md), phrased for an
agent editing code.

## What this project is

**Ingot** — an open-source (MIT) .NET library for structured, validated,
self-repairing LLM outputs. It is middleware over
`Microsoft.Extensions.AI.IChatClient`: one call turns a prompt into a typed,
validated object, repairing invalid model output via a retry-with-feedback loop.

## Before you propose a change — validate it

Every change must leave these three green (this is exactly what CI checks):

```bash
dotnet build --configuration Release   # warnings are ERRORS — a warning fails the build
dotnet test  --configuration Release   # 8 xUnit tests over a scripted FakeChatClient
dotnet format --verify-no-changes      # whitespace, import order, style
```

Run `dotnet format` (no flags) to auto-apply formatting before committing.
Target framework is **net8.0**.

## Hard rules (these break the build if violated)

- **`TreatWarningsAsErrors` is on.** No unused usings, no unreachable code, no nullable warnings.
- **Public members require XML doc comments** (`GenerateDocumentationFile` is on). This is a published library — the public API is forever, so document it and keep it minimal.
- **Nullable reference types are enabled.** Honor nullability; don't suppress with `!` unless truly warranted.

## Style conventions (match the existing code)

- File-scoped namespaces; `using` directives outside the namespace, **System first**.
- `var` where the type is apparent; expression-bodied members where they fit on one line.
- Library async code: accept a `CancellationToken`, propagate it, and use `ConfigureAwait(false)`.
- Private **instance** fields are `_camelCase`; `const`/`static` fields are `PascalCase`.
- Never swallow exceptions silently. Validation failures carry JSON-cased paths (`$.lines[2].total`) because the messages are addressed to the model — keep that convention.

## Change scope

- Keep diffs minimal and focused; match the surrounding code's style and comment density.
- Don't add NuGet dependencies without a clear reason — this is a lean middleware library.
- Internal seams (`IExtractionStrategy`, the strategies) are intentionally `internal` until the shape is proven; don't make them public without discussion.
- Tests are behavior tests over `FakeChatClient` — no live model calls. New behavior needs a test in the same style (Arrange/Act/Assert, descriptive names, underscores allowed).

## Pull requests

- PR titles follow **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, `test:`) — enforced by CI.
- `main` is protected; land changes via PR. Fixes proposed by an agent should come as their own PR (or a stacked PR) for human review before merge.
