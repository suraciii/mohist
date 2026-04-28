## Context

Two separate state stores track build progress: `pipeline_checkpoint` (SQLite) written incrementally by `onTaskCompleted`, and `tasks.json` (filesystem) written by `runRalphLoop`. After a crash/restart, `runPipelineBuildStage` reads the checkpoint, passes its task IDs as `skipTaskIds` to RalphExecutor, which marks those tasks as `passes=true` and skips the main loop. But the `completed` counter only increments inside the main loop — so it stays at 0. The `zero_work` guard at `workflow-controller.ts:617` then fires and the issue becomes permanently blocked.

The current code also unconditionally treats `allTasksPassed` as corrupted state (line 425–435), resetting all tasks to `passes=false` and writing to disk. During checkpoint recovery this is wrong — all-pass is the expected outcome when every task was previously completed.

## Goals / Non-Goals

**Goals:**
- Eliminate `zero_work` false positives when all tasks are recovered from checkpoint
- Make `allTasksPassed` reset conditional on not being in a checkpoint recovery path
- Add a short-circuit path in RalphExecutor that returns correct `completed` count without entering the main loop
- Verify checkpoint consistency with tasks.json before execution

**Non-Goals:**
- Redesigning the dual-state architecture (checkpoint + tasks.json) — that's a larger refactor
- Adding transactional semantics between SQLite and filesystem
- Changing the `recoverBuildStageIssue` path in `agent-runner-service.ts` — that's a separate recovery flow that already handles all-pass correctly
- Modifying plan stage or review stage checkpoint logic

## Decisions

### D1: Short-circuit in RalphExecutor after skipTaskIds processing

After the `skipTaskIds` block (lines 437–449), check if all tasks now have `passes=true`. If so, return a successful `RalphLoopResult` immediately with `completed = tasks.length`, `skipped = 0`, `success = true`. Do not enter the main while-loop.

**Why this location:** The skipTaskIds processing already wrote tasks.json. Placing the short-circuit immediately after means zero wasted work — no emit functions created, no learnings loaded unnecessarily.

**Alternatives considered:**
- *Fix in workflow-controller only (skip zero_work when skipTaskIds was full):* Would still run the main loop with all tasks passed, hitting the "no pending tasks" branch and returning `completed=0`. The result would still be misleading even if we suppress zero_work.
- *Increment `completed` for each skipTaskIds task:* Would require reworking the counter logic and still enters the main loop to find no pending tasks. More complex, less clear.

### D2: Guard allTasksPassed reset behind `skipTaskIds.size === 0`

Move the allTasksPassed check after the `skipTaskIds` set construction but add a guard: only reset when `skipTaskIds.size === 0`. During checkpoint recovery (`skipTaskIds.size > 0`), all-pass is the expected valid state — the tasks were completed in a previous run.

**Why:** The allTasksPassed reset exists to handle corrupted state where tasks.json was manually tampered with. During checkpoint recovery, the pass states are legitimate. Checking `skipTaskIds.size` is a reliable signal for recovery mode because it's only non-empty when coming from a checkpoint.

**Alternatives considered:**
- *New boolean flag `isRecovery`:* Over-engineering; `skipTaskIds.size > 0` already encodes this.
- *Remove allTasksPassed reset entirely:* Too risky — the corruption case (external edit) still needs handling.

### D3: Narrow zero_work guard to exclude full-checkpoint-recovery

In `workflow-controller.ts`, the zero_work check at line 617 currently fires on `completed === 0 && total > 0`. With the RalphExecutor short-circuit (D1), this condition can no longer occur during full checkpoint recovery because `completed` will equal `total`. But as a safety net, add a secondary check: also skip zero_work when `completedTaskIds.length > 0` (checkpoint was present) and the result reports `success === true`.

**Why defense-in-depth:** The short-circuit in D1 should prevent this entirely, but the zero_work guard is a safety check. Making it aware of checkpoint recovery ensures no regression can re-introduce the false positive.

### D4: Checkpoint consistency cleanup in workflow-controller

After reading the checkpoint and detecting the change, read tasks.json and verify which checkpoint task IDs actually have `passes=true`. Filter out any that don't. If all checkpoint IDs are verified passed and they cover all tasks, the checkpoint is fully consistent — delete it early. Pass only the verified-passed IDs as `skipTaskIds`.

**Why:** Handles the edge case where checkpoint says T-001,T-002 are done but tasks.json shows T-002 as `passes=false` (partial write during crash). Without this, T-002 would be skipped despite not actually being complete.

**Alternatives considered:**
- *No cleanup, rely on RalphExecutor handling:* Could mask real data loss. The workflow-controller is the right place for consistency checks since it has access to both stores.

## Risks / Trade-offs

- **[Short-circuit bypasses SSE events]** → Mitigation: the short-circuit logs `recovered-from-checkpoint`. Build stage events are emitted by workflow-controller, not RalphExecutor, so `build_stage_completed` still fires correctly.
- **[skipTaskIds as recovery signal could be set externally]** → Mitigation: skipTaskIds is only populated from checkpoint data in workflow-controller. No external API sets it. The risk is minimal.
- **[Checkpoint cleanup could remove valid partial progress]** → Mitigation: cleanup only deletes when ALL checkpoint tasks are verified in tasks.json. Partial consistency preserves the checkpoint.

## Migration Plan

No migration needed. The fix is purely behavioral — existing databases with stale checkpoints will self-heal on next `runPipelineBuildStage` invocation. No schema changes, no config changes.

## Open Questions

None.
