## Context

Current merge-back is a direct event handler in `server/index.ts`: `agent_completed` → `worktreeManager.mergeBack()`. If merge fails (conflict), it logs a warning and stops. The issue is already at `Stage.Done` + `IssueStatus.Completed` at this point. There is no `MergeState` enum, no `merge_state` DB column, no MergeQueue service, and no merge-related events in EventBus.

The build stage uses `RalphExecutor` which reads `tasks.json` and spawns per-task ACP sessions — it is NOT template-based. Conflict resolution needs a different prompt path that bypasses RalphExecutor.

**Key files:**
- `packages/cli/src/server/index.ts` — `agent_completed` handler (lines 142-159)
- `packages/cli/src/git/worktree-manager.ts` — `mergeBack()` (lines 167-243)
- `packages/cli/src/workflow/workflow-controller.ts` — `runPipelineBuildStage()` (lines 433-629)
- `packages/cli/src/agents/artifact-prompt.ts` — prompt construction (193 lines)
- `packages/cli/src/services/agent-runner-service.ts` — `executePipeline()` (lines 263-398)
- `packages/cli/src/services/event-bus.ts` — EventMap (lines 1-26)
- `packages/cli/src/types/index.ts` — types (no MergeState exists)
- `packages/cli/src/db/migrations.ts` — SCHEMA_VERSION = 13
- `packages/cli/src/db/issue-repo.ts` — no merge_state column

## Goals / Non-Goals

**Goals:**
- MergeQueue detects conflicts, aborts master merge, transfers conflict to worktree
- Issue re-enters pipeline at build stage with `mergeState=resolving`
- Agent resolves conflicts in worktree using a dedicated prompt
- After resolution, issue completes pipeline and re-attempts merge (max 3 retries)
- Master only ever receives clean merges

**Non-Goals:**
- No MergeQueue serialisation / queuing — this change only adds conflict recovery, not a serial merge queue
- No three-way merge strategy configuration (ours/theirs)
- No conflict prevention (rebase strategy)
- No WebUI changes
- No MergeState persistence for the happy path (only conflict-related states stored)

## Decisions

### D1: MergeState as a transient field on Issue, not a full MergeQueue service

The issue description mentions a MergeQueue, but the current architecture has no queue. Building a full serial MergeQueue is out of scope for conflict resolution. Instead, add a `merge_state` column to issues that tracks the merge lifecycle, and handle conflicts inline in the `agent_completed` handler.

**Why:** Minimal change — the existing `agent_completed` handler already calls `mergeBack()`. We add conflict detection and recovery there without introducing a new service class.

**Alternatives considered:**
- Full `MergeQueue` service with serial queue: too large a scope, deserves its own issue
- In-memory-only merge state: loses state on server restart

### D2: Conflict resolution bypasses RalphExecutor, uses direct ACP session

The build stage currently delegates to `RalphExecutor` which reads `tasks.json`. For conflict resolution, there are no tasks — just a single goal: resolve all conflict markers. The resolution prompt runs as a direct ACP session (one agent call) rather than going through the Ralph loop.

**Why:** RalphExecutor expects structured tasks in `tasks.json`. Conflict resolution is a single imperative: resolve all `<<<<<<<` markers. A direct prompt is simpler and more appropriate.

**Implementation:** In `runPipelineBuildStage()`, when `mergeState === 'resolving'`, skip RalphExecutor. Instead, run a single ACP session with the conflict resolution prompt. The agent works directly in the worktree directory.

**Alternatives considered:**
- Create a synthetic `tasks.json` with one task per conflict file: over-engineered, RalphExecutor would still not understand the conflict context
- Reuse RalphExecutor with a custom task string: possible but forces an unnatural fit

### D3: Issue state regression — Done → Build for conflict resolution

When a conflict is detected, the issue must go from `Done`+`Completed` back to `Build`. This requires updating both `stage` and `status` fields. The `STAGE_TRANSITIONS` map does not allow `Done → Build`, so this transition must bypass the normal validation.

**Why:** The alternative would be adding a parallel "conflict resolution" stage outside the pipeline, but that would require a new stage enum value and more complex controller logic. Reusing the build stage keeps the pipeline model simple.

**Implementation:** The conflict handler directly sets `stage = Build` and `status = Active` on the issue, bypassing `STAGE_TRANSITIONS`. The `mergeState = 'resolving'` flag signals that this is a conflict resolution build, not a normal build.

**Alternatives considered:**
- New stage `conflict-resolution`: adds pipeline complexity, needs transition rules, prompts etc.
- Keep issue at Done but spawn a separate "fixer" agent: loses pipeline tracking

### D4: conflict_retry_count column in issues table

Add `merge_state TEXT DEFAULT 'pending'` and `conflict_retry_count INTEGER DEFAULT 0` to the issues table via migration v14. This allows tracking retry count across server restarts.

**Why:** Without persistence, a server restart during conflict resolution would lose the retry count and potentially infinite-loop.

**Alternatives considered:**
- Store in a separate merge_attempts table: over-normalized for a simple counter
- Store only in memory: loses state on restart

### D5: Conflict resolution prompt as a new template file

Create `packages/cli/src/agents/prompts/conflict-resolution.md` containing the conflict resolution instructions. `artifact-prompt.ts` gets a new `buildConflictResolutionPrompt(issue, changeDir, conflictFiles)` function that assembles: prompt template + issue context + conflict file list.

**Why:** Consistent with how other prompts are organized (all in `prompts/` directory). The prompt is specialized enough to warrant its own file.

### D6: Skip approval gates during conflict resolution pipeline

When `mergeState === 'resolving'`, the build → review → done cycle should skip any approval gates. The conflict resolution is an internal recovery mechanism, not user-initiated work.

**Why:** Requiring user approval for a conflict resolution that the agent is well-equipped to handle would defeat the purpose of automation.

**Implementation:** In `WorkflowController.run()`, when entering a stage with `mergeState === 'resolving'`, treat all gates as `none`.

## Risks / Trade-offs

- **[Issue already marked Completed then regressed]** → The `agent_completed` handler fires when pipeline reaches Done. The issue is marked Completed. If mergeBack fails, we regress to Build+Active. Any SSE client that saw `agent_completed` will see a confusing state change. **Mitigation:** Emit `merge_conflict_requiring_resolution` immediately after state change so UI can understand the regression.
- **[Re-merge master in worktree also conflicts]** → This is expected and handled — the conflict markers are what the agent resolves. But if master advances significantly during resolution, the resolved worktree may still conflict on the next mergeBack attempt. **Mitigation:** The 3-retry limit caps this. Each retry does a fresh `git merge master` in the worktree.
- **[Conflict resolution prompt quality]** → Agent may resolve conflicts incorrectly (dropping one side's changes). **Mitigation:** Prompt explicitly instructs preserving both sides. Build verification (if configured) catches compilation errors. The review stage catches logical issues.
- **[STAGE_TRANSITIONS bypass]** → Directly setting stage without validation could allow invalid states. **Mitigation:** Only the conflict handler performs this bypass, and it always sets a known-valid combination (Build + resolving).

## Migration Plan

1. Add migration v14: `ALTER TABLE issues ADD COLUMN merge_state TEXT DEFAULT 'pending'` and `ALTER TABLE issues ADD COLUMN conflict_retry_count INTEGER DEFAULT 0`
2. Add `MergeState` to `types/index.ts`
3. Deploy — existing issues have `merge_state='pending'` and `conflict_retry_count=0`, no behavior change
4. New conflict handling code activates on next `agent_completed` event

**Rollback:** Remove the event handler, set all `merge_state='resolving'` issues to `merge_state='conflict'` manually.

## Open Questions

- Should the review stage after conflict resolution also use a specialized prompt (reviewing just the merge resolution), or the standard reviewer? The spec says "normal check stage" — using the standard reviewer seems sufficient.
- Should `merge_state` be reset to `'pending'` when the issue re-enters build via conflict resolution, or stay `'resolving'` until mergeBack succeeds? Current design: stays `'resolving'` through the pipeline, resets to `'pending'` only after successful mergeBack.
