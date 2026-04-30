## Context

The current pipeline has 5 stages: `explore → plan → build → review → done`. The `review` stage only runs AI code review via an ACP connection. After review, the user approves and the code calls `mergeBackFn` (a thin wrapper around `WorktreeManager.mergeBack`) directly. The `pipeline-model` spec already defines the stage as `check` but the code uses `Review = 'review'`.

Key files:
- `packages/cli/src/types/index.ts` — Stage enum, STAGE_ORDER, STAGE_TRANSITIONS
- `packages/cli/src/workflow/workflow-controller.ts` — `run()` dispatches stage execution; `runPipelineReviewStage()` runs the review loop (review → self-check → verdict → auto-fix)
- `packages/cli/src/workflow/workflow-loader.ts` — `WorkflowConfig` interface, `DEFAULT_WORKFLOW`, `loadWorkflow()`
- `packages/cli/src/git/merge-queue.ts` — `MergeQueue` class with `enqueue()`, serial processing, build verification with auto-fix, conflict resolution
- `packages/cli/src/api/issues.ts` — `POST /:number/approve` handler: sets approval, calls `agentRunner.resumePipeline()` which re-enters `WorkflowController.run()` where Review case calls `mergeBackFn`
- `packages/cli/src/services/agent-runner-service.ts` — wires `mergeBackFn` and `onMergeConflictFn` into WorkflowController
- `packages/cli/src/db/migrations.ts` — schema version 15, migration pattern: `if (currentVersion < N) migrateToVersionN(db)`

## Goals / Non-Goals

**Goals:**
- Rename `Stage.Review` → `Stage.Check` across codebase with DB migration
- Implement 3-step check suite (Build & Test → Merge Ready → AI Review) inside WorkflowController
- Replace `mergeBackFn` direct call with `MergeQueue.enqueue()` on approval
- Add `checks` config section to workflow.yaml
- Build CheckResultsPanel UI replacing the inline review report display

**Non-Goals:**
- Custom check plugins (future)
- Parallel check execution within the suite (sequential for now)
- Changing AI review prompt templates or review quality logic (preserved as-is)
- Changing MergeQueue's internal logic (reused as-is)

## Decisions

### D1: Rename Stage enum in-place, not alias

Change `Stage.Review = 'review'` directly to `Stage.Check = 'check'` in `types/index.ts`. Update `STAGE_ORDER`, `STAGE_TRANSITIONS`, and every string literal `'review'` across the codebase. Do not create a backwards-compat alias.

**Why:** The `pipeline-model` spec already defines `check`. An alias would perpetuate the mismatch. A clean break with a DB migration is simpler — every `review` reference is either code (find-and-replace) or DB (one UPDATE).

**Alternatives considered:**
- Keep `Stage.Review` and add `Stage.Check = Stage.Review` — perpetuates wrong name, two names for same thing
- Runtime mapping layer — unnecessary indirection for a one-time rename

### D2: Check suite runs inside WorkflowController, not as a new service

Add `runPipelineCheckStage()` method to `WorkflowController` (replacing `runPipelineReviewStage()`). It orchestrates the three checks sequentially. Each check is a private method: `runBuildTestCheck()`, `runMergeReadyCheck()`, `runAiReviewCheck()`.

**Why:** The controller already owns stage dispatch and the review loop. The checks are sequential and need to share the same `AcpConnection` lifecycle (for AI review). Keeping it in the controller avoids introducing a new abstraction layer for what is essentially a refactored method.

**Alternatives considered:**
- New `CheckSuite` service class — over-engineering for 3 sequential steps that share controller context
- Each check as a separate agent session — would lose the ACP connection reuse for AI review, and complicate status tracking

### D3: Build & Test auto-fix reuses existing coder agent spawning pattern

For build/test auto-fix, spawn a coder agent using the same `createAcpConnection` + `prompt` pattern already used in the auto-fix loop (`runAutoFixLoop`). The build/test command runs via `execFileAsync` (same as `MergeQueue.runBuildVerification`).

**Why:** Reuses proven patterns. The coder agent has full access to the worktree and can fix code. No new agent type needed.

**Alternatives considered:**
- Shell out to a script instead of coder agent — can't handle nuanced build errors
- Reuse `MergeQueue.runBuildVerificationWithFix` directly — it's tightly coupled to MergeQueue's entry state and event emission

### D4: Merge Ready check uses existing `WorktreeManager.canFastForward`

Call `worktreeManager.canFastForward(projectPath, projectName, issueNumber, baseBranch)` which already exists and does `git merge-base --is-ancestor`. Result is informational only — stored in CheckResult but always `status: 'passed'`.

**Why:** Already implemented, well-tested. No new git logic needed.

### D5: Post-approval: MergeQueue.enqueue() replaces mergeBackFn

In the `POST /:number/approve` handler, when `approvalStage === Stage.Check`, call `mergeQueue.enqueue(projectId, issueNumber)` instead of `agentRunner.resumePipeline()`. The MergeQueue handles rebase → build verify → FF merge asynchronously. On `merge_completed` event, transition issue stage to `done`. On `merge_failed`, set `mergeState` and keep stage at `check`.

**Why:** MergeQueue already has all the logic (rebase, build verify with auto-fix, conflict resolution, FF merge). Duplicating it in `mergeBackFn` is the current source of the "approval surprise" problem. MergeQueue is serial and reliable.

**Alternatives considered:**
- Keep `mergeBackFn` for FF-only cases, MergeQueue for rebases — two code paths for merging, maintenance burden
- Make WorkflowController call MergeQueue — controller shouldn't manage async merge lifecycle; API handler is the right place

### D6: Checks config lives in WorkflowConfig

Extend `WorkflowConfig` interface with an optional `checks` field parsed from workflow.yaml. `loadWorkflow()` returns it alongside stages. The controller reads checks config from the loaded workflow.

```typescript
interface ChecksConfig {
  buildTest: { command: string; timeout: number; autoFix: boolean; maxFixAttempts: number };
  ffMerge: { enabled: boolean };
  aiReview: { enabled: boolean };
}
```

**Why:** workflow.yaml already defines stage behavior. Checks are stage-specific config. No new config file or mechanism needed.

### D7: CheckSuiteOutput stored in approvalState.output

The `CheckSuiteOutput { checks: CheckResult[], overallResult }` is serialized into `approvalState.output` (a JSON blob column). The frontend reads it from the existing `GET /api/issues/:number` response.

**Why:** Reuses the existing `approvalState` infrastructure — no new DB columns, no new API endpoints. The frontend already renders based on `approvalState.output`.

## Risks / Trade-offs

- **[Build/test auto-fix consumes agent resources]** → Mitigation: max 2 attempts, configurable `autoFix: false`, timeout per attempt
- **[MergeQueue.enqueue on approve creates async gap]** → User sees `mergeState: pending` in UI; MergeQueue processes and emits events that update issue stage to `done`. If server restarts, `MergeQueue.recoverFromDB()` handles it
- **[DB migration renames stage in-place]** → One-way migration; rollback requires reversing the UPDATE. No data loss (stage is just a string)
- **[Check suite is sequential — slow if all checks needed]** → AI review (most expensive) is already the bottleneck. Build/test is fast (seconds). Merge Ready is near-instant. Acceptable for v1
- **[Large build logs in approvalState.output]** → Truncate buildLog to 50KB before storing. Full log available in worktree

## Migration Plan

1. **Code changes (atomic PR):**
   - `types/index.ts`: `Review → Check` in enum, STAGE_ORDER, STAGE_TRANSITIONS
   - `workflow-controller.ts`: rename `runPipelineReviewStage` → `runPipelineCheckStage`, add check suite orchestration
   - `workflow-loader.ts`: add `ChecksConfig` to `WorkflowConfig`, parse `checks` section, update default workflow stage `review → check`
   - `db/migrations.ts`: add `migrateToVersion16()` with `UPDATE issues SET stage = 'check' WHERE stage = 'review'`
   - `api/issues.ts`: approve handler: when `Stage.Check`, call `mergeQueue.enqueue()` instead of `resumePipeline`; status.ts: rename `review` key to `check` in `issuesByStage`
   - `api/status.ts`: `review` → `check` in stage filter
   - `server/index.ts`: pass MergeQueue to approve handler context
   - `agent-runner-service.ts`: remove `mergeBackFn` wiring for Check stage
   - Frontend: rename all `Stage.Review` → `Stage.Check`, kanban column label, replace inline review report with CheckResultsPanel
   - CLI: `--skip-to-review` → `--skip-to-check`

2. **Rollback:** Revert the PR. The DB migration is idempotent (UPDATE only affects `review` rows). If rolled back, new `check` rows would need `UPDATE issues SET stage = 'review' WHERE stage = 'check'` — but this is unlikely needed since the migration only runs once.

3. **Deploy:** No special steps. Server restart applies migration and uses new code.

## Open Questions

- Should MergeQueue completion auto-transition stage to `done`, or should we add a webhook/poll mechanism? Current design uses MergeQueue's `merge_completed` event → update issue stage. Need to wire this in server's event listener.
- Frontend CheckResultsPanel: should we implement SSE-based real-time check status updates in this change, or poll on page load? SSE is specified but adds complexity — could ship with polling first and add SSE in a follow-up.
