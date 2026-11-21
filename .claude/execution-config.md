# Execution Config

Execution settings for the agent team. It lives **in the repo** at `.claude/execution-config.md`, so every worktree has it at the same relative path, and changes to commands are versioned and released with the code. It is read by the Team Lead (`#T-TLO`), the package agents and `health-check`.

Project variables (`#PN`, `#DP`, `#CP`) are **not** defined here; they come from `#NP`.

Every command row names in `Requires` the repo-relative file whose presence makes the command valid (e.g. `Cargo.toml`, `ui/package.json`), or `—` if it always applies. How a row whose file is absent is treated: `project-rules` (Execution Config rows).

One check per row. A row has one `Requires` path, so a command spanning two parts of the stack is split into two rows. Chain with `&&` only where a later step depends on an earlier one (e.g. generate, then diff), so one failure never hides another check's result.

Provisional: .NET stack defaults (`dotnet restore`, `build`, `format`, `test`) with solution `src/SayWhat.sln`.

## Settings
| Var | Value | Notes |
| --- | --- | --- |
| `#GR` | `develop` | Integration target branch |
| `#REMOTE` | `true` | `true` to fetch, pull and push `origin` |
| `#MAX_PARALLEL` | `2` | Max package agents running at once per Team Lead (throttle for plan usage limits) |

## Tracker Columns (`py "{#TOOLS}/trkr_update.py"`)
| Arg | Header in `#TRKR` |
| --- | --- |
| `--id-col` | `ID` |
| `--status-col` | `Status` |
| `--completed-col` | `Completed` |
| `--notes-col` | `Notes` |
| `--title-col` | `Title` (optional: the default is `Title`) |

These are the standard `#TRKR` headers; change one only if the project's tracker differs. The tab is always `--sheet #WS_ID`.

## Worktree Setup
Run by every agent in a fresh worktree, and by `health-check` before checks. Commands must be idempotent and quick when nothing has changed (prefer `npm install` to `npm ci`). Missing local files belong in `.worktreeinclude`.

| Step | Command | Requires |
| --- | --- | --- |
| deps-backend | `dotnet restore src/SayWhat.sln` | `src/SayWhat.sln` |

## Build Cache (health-check, pkg-integrator)
Every worktree builds in its own checkout, so a toolchain that keeps its build output or cache per checkout starts cold in each one. One row per compiled toolchain. `Redirect` is what the Developer sets once per machine (`Machine-Setup.md`, Build caches): a machine-wide folder outside any synced folder. `Check` is a command that exits 0 when the redirect is in effect. Rules: `project-rules` (Build cache).

No row: the only compiled toolchain is .NET, which `Machine-Setup.md` (Build caches) marks `none`.

## Package Test Commands (package agents / test-runner)
| Purpose | Command | Requires |
| --- | --- | --- |
| Build (backend) | `dotnet build src/SayWhat.sln --no-restore` | `src/SayWhat.sln` |
| Unit tests (all) | `dotnet test src/SayWhat.sln --no-restore` | `src/SayWhat.sln` |
| Unit tests (filtered) | `dotnet test src/SayWhat.sln --no-restore --filter "FullyQualifiedName~{module}"` | `src/SayWhat.sln` |

## Health Checks (health-check)
Scopes: `wave` runs only after a merge conflict resolved by an agent or a change to a shared file (T-TLO §2.6), so keep `wave` rows to fast static checks; `full` runs once per work stream (§2.7) and at release (§3), and an unchanged tree is reused. A check must pass on a clean checkout. When a `wave` lint or type-check row already compiles the code a build row compiles, the build row's Scope is `full` only.

`Paths` (optional) lists the repo paths the check reads, comma-separated, or `—` if it always runs; a run given `Base` skips a row whose paths the change did not touch (`project-rules`, Execution Config rows).

| Name | Command | Requires | Scope | Paths |
| --- | --- | --- | --- | --- |
| build | `dotnet build src/SayWhat.sln --no-restore` | `src/SayWhat.sln` | full | — |
| format | `dotnet format whitespace src/SayWhat.sln --verify-no-changes` | `src/SayWhat.sln` | wave, full | `src/` |
| unit-all | `dotnet test src/SayWhat.sln --no-restore` | `src/SayWhat.sln` | full | — |


## Release (`#T-REL`, `#T-RS`)
| Var | Value | Notes |
| --- | --- | --- |
| `#BUILD` | `{actions / local}` | `actions`: `#RWF` builds and publishes on tag push (public GitHub repositories); `local`: `#T-REL` builds and runs the publish steps |

### Version Files
| File | Pattern |
| --- | --- |
| `{e.g. web/package.json}` | `{e.g. "version": "{v}"}` |
| `{e.g. src/App/App.csproj}` | `{e.g. <Version>{v}</Version>}` |

### Release Build (`local` only)
| Step | Command |
| --- | --- |
| `{e.g. package}` | `{e.g. fw-package}` |

Artefacts: `{e.g. Packaging/out/}`

### Publish Steps
| Step | Command or `manual` |
| --- | --- |
| `{e.g. GitHub Release}` | `{done by #RWF when #BUILD = actions}` |
| `{e.g. deploy}` | `{command}` |
| `{e.g. announce}` | `manual` |

