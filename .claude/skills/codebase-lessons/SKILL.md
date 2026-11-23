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
- `dotnet format whitespace --verify-no-changes` on the solution can exceed 2 minutes in a OneDrive worktree; run it in the background. (WS-1.4)
- After writing files by hand, run `dotnet format whitespace` (not `--verify-no-changes`) to apply the repo line-ending style. (WS-1.4)
- Opt-in live tests use xunit.v3 `Assert.SkipUnless(condition, reason)` with the git-ignored `src/SayWhat.Tests/.model-cache/` via `ModelTestCache.cs`. (WS-1.4)

### Conventions
- xUnit v3 test projects declare `<Using Include="Xunit" />`; no `xunit3` template is installed, so create test projects by hand. (WS-1.1)
- New code goes into the existing layer projects; `src/SayWhat.sln` does not change in PH-1. (WS-1.1)

### Gotchas
- xUnit v3 reuses pool threads: assert "runs off the calling thread" with a blocking fake and an incomplete task, not thread ids. (WS-1.4)
- Xenova Opus-MT `vocab.json` ids are per pair and differ from spm ids; map by piece string (`SentencePieceTokenizer.Vocabulary` gives piece → spm id). (WS-1.4)
- Xenova merged decoder: step one needs `use_cache_branch=false` with zero-length past tensors; later steps take only `present.*.decoder.*` and keep `*.encoder.*` from step one; start token `<pad>` is excluded from argmax. (WS-1.4)
- ONNX Runtime cache dims (heads, head size) come from `InferenceSession.InputMetadata` of `past_key_values.*`. (WS-1.4)
