---
name: codebase-lessons
description: Curated, reusable lessons about this codebase (build quirks, conventions, gotchas) accumulated across work streams. Preloaded into package agents; read-only for them, curated by the Team Lead.
user-invocable: false
metadata:
  source: codebase-lessons template v5.0
---

## Codebase Lessons
**Rules for agents:**
- Apply these when relevant.
- Never edit this file; report new lessons in your handover's Lessons section.

**Rules for the Team Lead (curator):**
- One line per lesson, tagged with the source work stream.
- Keep only reusable, codebase-level lessons; remove superseded or duplicate lines.
- Keep the file under 80 lines.
- This file merges with `merge=union`, so dedupe at release.

### Build & Tooling
- `dotnet test` with xunit.v3 on the .NET 10 SDK needs root `global.json` `{"test":{"runner":"Microsoft.Testing.Platform"}}`; VSTest mode fails. (WS-1.1)
- A fresh worktree needs `dotnet restore src/SayWhat.sln` before `dotnet test`; without it the test run fails with "using VSTest test runner" despite `global.json`. (WS-1.2)
- `dotnet format whitespace --verify-no-changes` on the solution can exceed 2 minutes in a OneDrive worktree; run it in the background. (WS-1.4)
- After writing files by hand, run `dotnet format whitespace` (not `--verify-no-changes`) to apply the repo line-ending style. (WS-1.4)
- Opt-in live tests use xunit.v3 `Assert.SkipUnless(condition, reason)` with the git-ignored `src/SayWhat.Tests/.model-cache/` via `ModelTestCache.cs`. (WS-1.4)

### Conventions
- xUnit v3 test projects declare `<Using Include="Xunit" />`; no `xunit3` template is installed, so create test projects by hand. (WS-1.1)
- New code goes into the existing layer projects; `src/SayWhat.sln` does not change in PH-1. (WS-1.1)
- xUnit v3 conditional skips need `Skip` (message) together with `SkipUnless` and `SkipType`. (WS-1.2)

### Gotchas
- EVE chat log folders hold tens of thousands of files and each open costs ~5–30 ms: never open every file; filter by file name first, then read headers. (WS-1.2)
- The EVE client holds its chat logs open for writing with write sharing allowed: detect a held log with a read open that denies write sharing, never a write open. (WS-1.2)
- Adding a package reference also needs `SolutionIntegrationTests` (pinned package set) and `LayerDependencyTests` (which projects may hold packages) updated. (WS-1.3)

### Dependencies
- Inspect a NuGet package without a build: download `https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg` and open it as a zip. (WS-1.3)
- Panlingo fastText: `Predict(text, k, 0f)` with k above the label count returns every label, which allows a script-restricted top-1. (WS-1.3)

### Gotchas
- xUnit v3 reuses pool threads: assert "runs off the calling thread" with a blocking fake and an incomplete task, not thread ids. (WS-1.4)
- Xenova Opus-MT `vocab.json` ids are per pair and differ from spm ids; map by piece string (`SentencePieceTokenizer.Vocabulary` gives piece → spm id). (WS-1.4)
- Xenova merged decoder: step one needs `use_cache_branch=false` with zero-length past tensors; later steps take only `present.*.decoder.*` and keep `*.encoder.*` from step one; start token `<pad>` is excluded from argmax. (WS-1.4)
- The Hugging Face API returns 401, not 404, for a repository that does not exist. (WS-1.4)
- Opus-MT timings vary 5-20x on a busy or OneDrive-synced disk; measure on a quiet machine. (WS-1.4)
- ONNX Runtime cache dims (heads, head size) come from `InferenceSession.InputMetadata` of `past_key_values.*`. (WS-1.4)
