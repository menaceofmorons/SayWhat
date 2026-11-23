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

### Conventions
- xUnit v3 test projects declare `<Using Include="Xunit" />`; no `xunit3` template is installed, so create test projects by hand. (WS-1.1)
- New code goes into the existing layer projects; `src/SayWhat.sln` does not change in PH-1. (WS-1.1)
- xUnit v3 conditional skips need `Skip` (message) together with `SkipUnless` and `SkipType`. (WS-1.2)

### Gotchas
- EVE chat log folders hold tens of thousands of files and each open costs ~5–30 ms: never open every file; filter by file name first, then read headers. (WS-1.2)
- The EVE client holds its chat logs open for writing with write sharing allowed: detect a held log with a read open that denies write sharing, never a write open. (WS-1.2)
