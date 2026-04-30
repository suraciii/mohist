## Why

Review stage only does AI code review before user approval — no build verification, no merge-readiness check. Users approve blind: they don't know if the code compiles, tests pass, or can merge without conflicts. Post-approval MergeQueue failures create frustrating surprises where approved work can't land. Meanwhile, AI review tokens are wasted on code that doesn't even build. The `pipeline-model` spec already defines a CHECK stage (not REVIEW); the codebase never caught up.

## What Changes

- **BREAKING**: Rename `Stage.Review = 'review'` → `Stage.Check = 'check'` in enum, `STAGE_ORDER`, `STAGE_TRANSITIONS`, and all references
- **BREAKING**: DB migration: `UPDATE issues SET stage = 'check' WHERE stage = 'review'`
- Replace single AI-review execution in `runPipelineReviewStage` with a sequential Check Suite: Build & Test → Merge Ready (dry-run) → AI Code Review
- Add build/test check with auto-fix loop (max 2 attempts) — agent fixes build/test failures before proceeding
- Add merge-readiness check: `git merge-base --is-ancestor` dry-run, informational only, non-blocking
- Migrate existing AI review logic (self-check → verdict → auto-fix loop) as the final check step
- Replace `ReviewApprovalPanel` UI with `CheckResultsPanel` showing structured check results (status per check, logs, actions)
- Gate approval actions on check results: build failure blocks approval entirely; AI review failure allows [退回去修] [添加指令] [强行批准]
- Replace direct `mergeBackFn` call on approval with `MergeQueue.enqueue()` for all post-approval merges
- Add `checks` configuration section to `workflow.yaml` (build-test command/timeout/autoFix, ff-merge enabled, ai-review enabled)

## Capabilities

### New Capabilities

- `check-suite` — Sequential multi-step check execution (Build & Test, Merge Ready, AI Review) with per-check status tracking, auto-fix loops, and structured CheckResult output
- `check-results-panel` — UI panel replacing ReviewApprovalPanel, showing per-check status (pending/running/passed/failed), logs, auto-fix indicators, and context-appropriate approval actions

### Modified Capabilities

- `pipeline-model` — Stage enum value changes from `review` to `check`; CHECK stage execution semantics expand from "AI review only" to "multi-step check suite"
- `workflow-config` — Add `checks` section to workflow.yaml for build-test command/timeout/autoFix, ff-merge enablement, and ai-review enablement
- `http-api` — Kanban/status endpoints reflect renamed stage; approval endpoint routes to MergeQueue instead of mergeBackFn
- `web-ui` — Kanban column label changes; approval panel replaced by check results panel

## Impact

- **Core types**: `packages/cli/src/types/index.ts` (Stage enum, transitions)
- **Workflow engine**: `packages/cli/src/workflow/workflow-controller.ts` (review stage → check suite execution)
- **Workflow config**: `packages/cli/src/workflow/workflow-loader.ts` (checks config parsing)
- **Server**: `packages/cli/src/server/index.ts` (mergeBackFn → MergeQueue integration)
- **Agent runner**: `packages/cli/src/services/agent-runner-service.ts` (mergeBackFn wiring)
- **API routes**: `packages/cli/src/api/issues.ts` (status counts, merge queue integration)
- **API status**: `packages/cli/src/api/status.ts` (stage filter)
- **DB migration**: New migration for `review` → `check` stage rename
- **Frontend**: Kanban columns, ReviewApprovalPanel → CheckResultsPanel, check status display
- **Git**: `packages/cli/src/git/merge-queue.ts` (becomes the sole merge path post-approval)
- **CLI**: `packages/cli/src/cli/commands/issue.ts` (--skip-to-review flag)
