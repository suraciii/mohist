# Review - Issue #450 Pi Workflow Path

## Scope

Reviewed the final product change against issue 450's seven acceptance criteria, `openspec/changes/issue-450/design.md`, the three issue specs, `docs/actions/pi.md`, and `design/runtimes/pi.md`. Files under `openspec/changes/issue-450/` were treated as workflow artifacts.

## Findings

No blocking findings.

The previously reported provider-policy readiness gap is resolved: `RunnerHost` preserves invalid-policy diagnostics and prevents work polling/claiming until configuration is valid. The previously reported unexpected `runTurn` failure path is also resolved: it attempts terminal reporting and preserves the original `turn-failed` result while exposing a stable sanitized terminal-reporting failure notice when that report is rejected.

## Coverage

- **AC 1**: `mohist/pi` is registered and task/check execution uses the shared Action path; final text reaches existing completion evaluation through the private turn fact.
- **AC 2**: Logical Session names are normalized and persisted Pi bindings are reused across same-name turns and model/variant changes.
- **AC 3**: Runner restart restoration uses the persisted absolute Pi session-file path.
- **AC 4**: Missing/corrupt bound files return `runtime-session-missing` with Reset guidance and no implicit replacement.
- **AC 5**: Fixed 60-minute deadlines, provider exhaustion policy, and invalid policy readiness gating are implemented; invalid policy configuration prevents polling/claiming.
- **AC 6**: Pi project trust is fixed false and repository-local `.pi/` execution resources are excluded.
- **AC 7**: Pi transcript, tool, retry, compaction, usage, cache-write, cost, and terminal facts flow through existing Session contracts and views.

## Structural Checks

- Runner typecheck and tests pass: 101 files, 1,178 tests.
- CLI, Server Unit, Server Arch, Web, and Runner suites pass in the completed verification runs; the full repository command also encountered four pre-existing OTEL integration host-startup timeouts in the environment.
- Pi SDK imports remain confined to `packages/runner/src/runtime/pi/`.
- The process-local Workflow Session coordinator remains runtime-neutral and non-durable.

## Verdict

No problems must be fixed before merge.

<promise>PASS</promise>
