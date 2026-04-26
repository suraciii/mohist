## Why

`recoverIssues()` blindly marks all non-awaiting orphan issues as `blocked`, ignoring actual build progress in tasks.json. When the ACP process crashes mid-prompt (e.g. Issue #12: all 3 tasks done, but T-003's process crash triggered a false blocked), completed work is lost and requires manual `mo issue reopen`. Additionally, `proc.on("exit")` in acp-session.ts only logs when `initialized === false`, so crashes during the running phase leave no audit trail.

## What Changes

- **acp-session.ts**: Both `proc.on("exit")` handlers (runAcpSession at ~L139, createAcpConnection at ~L515) now unconditionally write `acp_session_process_exit` log with `phase` field (init/running), `exitCode`, and `mode`. The existing init-phase rejection logic is preserved unchanged.
- **agent-runner-service.ts**: `recoverIssues()` adds stage-aware recovery for build-stage orphans — reads tasks.json from the issue's worktree, auto-advances to review if all tasks pass, or marks blocked with a progress summary (e.g. "2/3 tasks completed, T-003 pending") if partial.
- **tests/recover-issues.test.ts**: New test file covering all recovery branches (all-pass, partial, no tasks.json, plan stage, awaiting approval).

## Capabilities

### New Capabilities

- **orphan-recovery**: Stage-aware orphan issue recovery that inspects tasks.json to determine actual build progress instead of blindly blocking.

### Modified Capabilities

- **error-resilience**: Add requirement that ACP session process exits are logged unconditionally with phase information, not just during init failures.
- **reopen-resume**: Clarify that reopen-after-recovery for build-stage orphans may find the issue already advanced to review (no stage reset needed).

## Impact

- `packages/cli/src/agent-runtime/acp-session.ts` — Two exit handlers modified to always log
- `packages/cli/src/services/agent-runner-service.ts` — `recoverIssues()` gains tasks.json inspection
- `packages/cli/src/api/issues.ts` — Reopen handler message for auto-advanced review stage
- `packages/cli/tests/recover-issues.test.ts` — New test file
- No API or schema changes
- Depends on tasks.json format defined in `ralph-task-execution` spec
