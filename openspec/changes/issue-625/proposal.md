## Why

The build stage currently runs the complete verification sequence as one `core/script` task with a shared 300000 ms deadline. Required lanes can finish successfully before the enclosing command times out, but the workflow records only a failed aggregate task, discards the completed evidence, and repeats work during recovery; this is blocking valid changes now, including recent Epic 67 runs.

## What Changes

- Replace the single full-suite verification execution in the built-in local and GitHub PR workflows with ordered, independently bounded verification lanes.
- Preserve the existing strict verification requirements and live command mapping: `npm ci`; `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`; Web typecheck; Web `test:run`; Runner typecheck; and Runner tests with `--no-file-parallelism`.
- Persist an observable result for every lane and allow the build stage to advance only after all required lanes pass.
- Remove the enclosing full-suite 300000 ms timeout. Each lane must have its own explicit execution budget, and a lane timeout must remain a recoverable lane outcome rather than invalidate earlier passing lanes.
- Resume recovery at the first unfinished lane while retaining prior passing results. Repeating the same recovery must not duplicate push, review, or merge side effects.
- Keep build, typecheck, and test thresholds unchanged. Do not add skips or allowlists, increase one global timeout, reintroduce resource containment, or change Runner slot policy.

## Capabilities

- `verification-lanes`: Ordered verification lanes with explicit per-lane budgets, durable pass/fail/timeout results, and a gate that advances only when every required lane passes.
- `verification-recovery`: Recovery from a timed-out or failed lane that preserves completed lane evidence, resumes at the first unfinished lane, and remains idempotent across retries without repeating downstream effects.

## Impact

- Built-in Workflow Profile definitions and their CI variable contract, especially the build-stage `verify` task in `mohist-local.workflow.yaml` and `mohist-github-pr.workflow.yaml`.
- Server Workflow dispatch, durable run/task or lane state, stage advancement, timeout/failure reporting, recovery, and status/event projections under `packages/server/src/Mohist.Server/Workflow`.
- Runner script execution and timeout/reporting behavior under `packages/runner`, including the independently bounded lane commands.
- Workflow definition/profile tests, Runner action tests, and end-to-end workflow recovery coverage. No new dependency, resource-profile mechanism, or Runner slot-policy change is required.
