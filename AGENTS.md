# AGENTS.md — DataBall

Guidance for coding agents working in this repository. The README is the user-facing reference; this file covers how to work on the code.

DataBall is the session engine of the squalor semiconductor suite: an Apache-2.0 .NET 10 library (`squalor.DataBall`), lab-format handlers (`squalor.DataBall.Handlers`) and the `databall` CLI (`squalor.DataBall.Cli`). One session sits on DuckDB behind the wide `"data"` relation plus `"meta"`. Product goals, non-goals and the queue live in slicer (`slicer goals`, `slicer list`).

## Layout

- `src/DataBall/`: the library. `DataBall*.cs` is the public session (partial class); `DuckDbStore*.cs` is the internal DuckDB store; `Config.cs` and friends are the schema/config model; `TableLayout.cs` resolves config-declared `tables`; `Import/` and `Export/` hold format paths.
- `src/DataBall.Handlers/`: `IFormatHandler` implementations for lab dialects.
- `src/DataBall.Cli/`: the `databall` tool (System.CommandLine).
- `tests/DataBall.Tests/`, `tests/DataBall.Cli.Tests/`: xUnit. `tests/DataBall.TrialProbe/` is a console probe used by the db-05 trial.
- `docs/`: `how-it-works.md` (architecture), `ball-v3-trial.md` (db-05 decision record), `conflux-spec.md` (design input being folded in; not shipped API), `archive/` (historical).
- `.slicer/`: roadmap and slice tracking. Managed only through the `slicer` CLI.

## Build, test, format

The SDK is pinned by `global.json` (10.0.400, `rollForward: latestFeature`).

```bash
dotnet build DataBall.sln -c Release
dotnet test DataBall.sln -c Release
dotnet format DataBall.sln --verify-no-changes
```

CI (`.github/workflows/ci.yml`) runs restore, build and test in Release on Windows, macOS and Linux. Run all three commands above before handing off a change.

## Code rules

- Every source file starts with `// SPDX-License-Identifier: Apache-2.0`. Namespace `squalor.DataBall` (`.Import`, `.Export`, `.Handlers`, `.Cli`).
- Nullable is on. Public members carry XML doc comments (documentation is generated for the library).
- Public failures surface as `DataBallException`; wrap lower-level exceptions instead of leaking DuckDB or IO types.
- One way in: `Open` detects the format and hands lab dialects to an `IFormatHandler`. Parsers never go in the store, and there is no second opener.
- Schema comes from config (default profile plus overlay), not from sniffing. A config-declared `tables` layout stays behind the `"data"` view, so readers never see the split.
- Every write is one transaction; a failed write leaves tables, view, config and metadata as they were. Tests cover the rollback.
- SQL identifiers and literals go through `DuckDbStore.QuoteIdent` / `QuoteString`; values bind as parameters.
- Prefer the BCL and in-repo code. Ask the owner before adding any package; current dependencies are DuckDB.NET.Data.Full (pinned 1.5.5), Microsoft.Extensions.Logging.Abstractions, SharpCompress and System.CommandLine.
- README and `docs/how-it-works.md` change in the same commit as the behavior they describe.

## Tests

- Test-first for slices: write the slice's named tests, watch each fail for the stated reason, then implement.
- Mutation-check new tests: break the product code, confirm the test goes red for the right reason, revert.
- No GB-scale fixtures in CI. Fixtures live under `tests/DataBall.Tests/fixtures/`.

## Tracking (slicer)

- Read and change roadmap, slice, goal and note state only through `slicer` (`slicer ai` explains the workflow). Never open, parse or hand-edit `.slicer/` files or the generated renders.
- `slicer next` gives the next ready slice; `slicer show <id>` prints its spec. Use `--render` on mutations and finish with `slicer check`.
- Agent instructions come from slicer itself: `slicer ai instructions` (quick start), `slicer ai skill` (print or install the agent skill for Claude Code, Codex and Grok), `slicer ai relay` (the slice relay).

## Slice relay

Slices are normally run as a slice relay: one orchestrating session drives an implementer, a read-only reviewer, a read-only architect and a final gate. Print the procedure with `slicer ai relay`; it covers the roles, order, gate criteria (first line exactly `VERDICT: PASS` or `VERDICT: FAIL`), what every brief contains, when to stop and how to close the slice.

What this repo adds:

- **Worktree:** `.worktrees/<id>` on branch `<id>`, created by the orchestrator before the implementer starts.
- **Briefs:** the slice from `slicer show <id> --json --lean`; for reviewers also the direction sources, `slicer goals --json --lean` and `slicer list --json --lean`, plus `docs/how-it-works.md`.
- **Checks:** the build, test and format commands above, plus the slice's own Check list. The orchestrator reruns the tests before review and runs the full check before committing.
- **Size S** slices run a lite relay without the architect; M and L run the full relay.
- **Models:** the owner picks the vendor, model and effort for each role.
- **Agents** never commit; the orchestrator commits and merges after the gate passes and the owner agrees. Findings that belong to other slices are filed with `slicer note <id>` by the orchestrator, not by reviewers.
- The owner's fuller playbook, with vendor fallback chains and a headless mailbox (`relay-call.sh`), lives in the squalor-xyz agents repo under `multi-agent-workflows/playbooks/slice-relay.md`.

## Git

- Work in a worktree under `.worktrees/<name>` on a feature branch (slices use the slice id, e.g. `.worktrees/db-07` on `db-07`). `.worktrees/` stays out of version control.
- Agents do not commit, push or tag unless the owner asks. The repository is public: never commit secrets, and keep committed content fit for public reading.

## Releases

A `v*` tag that is an ancestor of `main` runs `.github/workflows/release.yml`: test, pack the three packages, attach them to a GitHub Release, and push to nuget.org only if the `NUGET_API_KEY` secret is set. Package versions are in the three `src/*/*.csproj` files. Tagging is the owner's call (release slices are flagged `[OWNER]`). The packages are not listed on nuget.org (a non-goal).
