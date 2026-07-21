# Review - Issue #450 Pi Workflow Path

## Scope

Reviewed the final product change against issue 450's seven acceptance criteria, `openspec/changes/issue-450/design.md`, the three issue specs, `docs/actions/pi.md`, and `design/runtimes/pi.md`. Files under `openspec/changes/issue-450/` were treated as workflow artifacts.

## Findings

### F1. Unexpected `runTurn` failures lose terminal-reporting failure diagnostics

**What**: The catch around `runtime.runTurn()` in `packages/runner/src/actions/pi.ts:84-93` now attempts to report the buffered events plus `session.closed`, but catches any failure from `reportWithTerminalSignal()` with an empty handler (`catch { /* best effort */ }`). It then returns `fail("turn-failed", actionErrorMessage(error), ...)` without recording that terminal reporting failed.

**Impact**: If an unexpected runtime/SDK exception occurs after `session.input` was accepted and the terminal batch is rejected, times out, or returns malformed acceptance data, the Action result contains only the original `turn-failed` message. The Session may not be terminal, but the caller receives no sanitized `session-reporting-failed` diagnostic. This violates design D7 and the session spec requirement that terminal-reporting failure preserve the original runtime error while attaching an observable, sanitized reporting-failure diagnostic.

**Where to fix**: Preserve the original `turn-failed` code/message as the primary result, but include a stable sanitized indication that terminal reporting failed in the returned error/diagnostic contract. Keep the best-effort terminal attempt and do not claim that the Session became terminal.

**Severity**: Blocking — this is an explicitly specified failure semantic for a submitted turn.

## Coverage

- **AC 1**: `mohist/pi` is registered and task/check execution uses the shared Action path; final text reaches existing completion evaluation through the private turn fact.
- **AC 2**: Logical Session names are normalized and persisted Pi bindings are reused across same-name turns and model/variant changes.
- **AC 3**: Runner restart restoration uses the persisted absolute Pi session-file path.
- **AC 4**: Missing/corrupt bound files return `runtime-session-missing` with Reset guidance and no implicit replacement.
- **AC 5**: Fixed 60-minute deadlines, provider exhaustion policy, and invalid policy readiness gating are implemented; invalid policy configuration prevents polling/claiming.
- **AC 6**: Pi project trust is fixed false and repository-local `.pi/` execution resources are excluded.
- **AC 7**: Pi transcript, tool, retry, compaction, usage, cache-write, cost, and terminal facts flow through existing Session contracts and views.

## Structural Checks

- Runner typecheck and tests pass: 101 files, 1,177 tests.
- Full `npm test` passes for CLI, Server, Web, and Runner suites.
- Pi SDK imports remain confined to `packages/runner/src/runtime/pi/`.
- The process-local Workflow Session coordinator remains runtime-neutral and non-durable.

## Verdict

F1 is a remaining blocking failure-path gap.

<promise>FAIL</promise>
