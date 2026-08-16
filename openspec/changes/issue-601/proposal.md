## Why

Workflow task completion currently makes a valid Action result depend on same-session worktree cleanup converging within a fixed number of attempts. When cleanup remains dirty, the runner reports a business task failure even when the action produced the intended artifacts and the workspace contains task commits; seven recent Epic 67 runs exhibited this exact outcome. A durable completion and commit boundary is needed now so cleanup uncertainty cannot erase valid work or create false terminal failures.

## What Changes

- Persist an immutable `ActionCompletion` together with an exact branch/HEAD/tree/status `CommitReceipt` before the Workflow task is settled.
- Define explicit committed-clean, dirty, and unconfirmed outcomes. Dirty or unconfirmed workspace state remains recoverable and is not converted into a business task failure.
- Replace same-session cleanup retries with an idempotent, bounded cleanup lease/fence tied to the task and workspace generation. Cleanup may remove only explicitly scoped generated artifacts and must not use broad reset/clean operations to discard task output.
- On cleanup timeout or unverifiable workspace state, preserve the task and workspace for recovery or allocate a fresh workspace; do not blindly retry against an unverified dirty workspace.
- Make receipt persistence, replay, report delivery, and settlement idempotent for the exact task/workspace identity, and reject conflicting receipts or completions.
- **BREAKING**: Change workflow task settlement and status projections so cleanup-induced dirty or unconfirmed results are represented as recoverable outcomes rather than terminal business failures. Cover Pi, OpenCode, and generic Action execution paths with deterministic filesystem and time tests.

## Capabilities

- `workflow-task-commit-boundary`: Durable ActionCompletion and CommitReceipt persistence, outcome arbitration before task settlement, fenced and scoped cleanup recovery, idempotent replay/conflict handling, and the resulting Workflow/Runner contract.

## Impact

- Runner execution and settlement pipeline: Action result projection, branch/worktree probes, cleanup attempts, workspace-generation ownership, Pi/OpenCode runtime paths, task-log/artifact sequencing, and durable result reporting under `packages/runner/src/runtime` and `packages/runner/src/actions`.
- Server Workflow boundary: task report translation, Workflow task/run state transitions, dispatch/workspace identity, durable events and receipt storage, report acknowledgements, and recovery/status projections under `packages/server/src/Mohist.Server/Workflow` and `packages/server/src/Mohist.Server/Runner`.
- Runner-to-server contracts and consumers: task result/status payloads, replay and conflict semantics, CLI/Web workflow status handling, and deterministic test doubles for filesystem and time. No new external dependency is expected; persistence and API changes must preserve exact identity and atomicity across retries.
