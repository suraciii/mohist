## Why

Mohist currently mixes issue lifecycle, workflow position, execution failure, session liveness, and user recovery intent into `blocked`/`interrupted` states and overloaded verbs like `reopen` and `restart`, which makes failure recovery hard to understand and easy to misuse. This change is needed now because `rewind` from #176 will otherwise add another overlapping recovery verb on top of an already inconsistent model.

## What Changes

- Refine user-visible issue lifecycle semantics around `open`, `paused`, `closed`, and `completed`, while keeping failure and interruption as recoverable execution conditions instead of long-lived lifecycle decisions.
- Narrow `reopen` so it is only used for reopening closed issues, and route paused or interrupted recovery through `resume` instead of overloading `reopen`.
- Keep `retry`, `rerun`, and future `rewind` as distinct recovery actions with clear intent boundaries: retry from checkpoint, rerun the current stage, and rewind to an earlier stage.
- **BREAKING** Remove or deprecate `POST /api/issues/:number/restart`; callers must use `retry`, `rerun`, or `rewind` instead.
- **BREAKING** Remove Restart entry points and restart-oriented guidance from CLI and Web UI, and replace blocked-oriented labels with user-facing failure language such as `Failed` or `Needs action`.
- Update API, CLI, and Web recovery copy so suggested actions depend on the issue's actual condition: `resume` for paused/interrupted, `retry` or `rerun` for failed attempts, and `reopen` only for closed issues.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `reopen-resume`
- `http-api`
- `cli-interface`
- `web-ui`

## Impact

- Affected backend workflow domain types and lifecycle logic in `packages/cli/src/types/index.ts`, `packages/cli/src/services/issue-service.ts`, and `packages/cli/src/workflow/issue-lifecycle.ts`.
- Affected issue action routes in `packages/cli/src/api/issues.ts`, especially `reopen`, `retry`, `rerun`, and `restart`, plus any error messages that currently recommend restart.
- Affected CLI issue commands and help text in `packages/cli/src/cli/commands/issue.ts`, where `reopen` and `resume` are currently overlapping and user guidance still references restart semantics.
- Affected Web issue action surfaces and status presentation in `packages/cli/web/src/components/IssueCard.tsx`, `packages/cli/web/src/lib/api.ts`, and `packages/cli/web/src/lib/types.ts`, where interrupted recovery currently uses `reopen` and blocked is shown as a primary user status.
- Affected tests and specs that currently encode `blocked` or `reopen` as the main recovery path, including the existing OpenSpec capability `reopen-resume`.
