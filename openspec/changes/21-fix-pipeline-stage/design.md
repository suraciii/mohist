## Context

The pipeline executes stages (plan → build → review) via `WorkflowController.run()`. Each stage contains sub-steps — plan has rounds (proposal, specs, design, tasks, self-review), build has tasks via `RalphExecutor`. When the server restarts mid-stage, `recoverIssues()` marks orphaned issues as `blocked`. On `reopen`, `resumePipeline()` calls `pipeline.run(issue, ...)` which re-enters the stage from scratch. In plan stage specifically, `cleanChangeDir()` wipes all previously generated artifacts before the rounds loop.

Key constraint: `WorkflowController` has no DB access beyond `IssueRepo`. The checkpoint repo must be threaded through the constructor or `AcpConnectionOptions`.

## Goals / Non-Goals

**Goals:**
- Persist sub-step completion so pipeline resumes mid-stage after server restart.
- Differentiate "interrupted by server crash" from "failed due to agent error" via a new `interrupted` status.
- Minimal disruption to existing stage execution flow — checkpoint read/write wraps the existing round/task loops.

**Non-Goals:**
- General-purpose workflow engine checkpoint system (only plan rounds and build tasks for now).
- Review stage checkpointing (review is a single LLM call, low cost to re-run).
- Cross-server pause/resume of ACP connections (connection state is lost on restart regardless).

## Decisions

### D1: Separate `pipeline_checkpoint` table instead of reusing `workflow_log`

**Choice:** New dedicated table with UPSERT semantics keyed on `(issue_number, stage)`.

**Rationale:** `workflow_log` is an append-only event log — querying the "current checkpoint" requires scanning latest entries. A dedicated row per (issue, stage) gives O(1) reads and atomic updates.

**Alternatives considered:**
- *Add checkpoint fields to `issues` table*: Couples checkpoint lifecycle to issue row. Would bloat the issue record with stage-specific JSON. Hard to clean up independently.
- *File-based checkpoint in changeDir*: Already have artifacts on disk; but `cleanChangeDir()` wipes them. Would need to change cleanup semantics first, and file writes aren't atomic.

### D2: `WorkflowController` receives `PipelineCheckpointRepo` via constructor

**Choice:** Add `checkpointRepo?: PipelineCheckpointRepo` to `WorkflowControllerOptions`.

**Rationale:** Follows the existing pattern (`issueRepo`, `eventBus` are optional constructor params). The repo is created in `executePipeline()` alongside `IssueRepo` and passed in.

**Alternatives considered:**
- *Thread through `AcpConnectionOptions`*: Already carries `workflowLogRepo` but checkpoint is a controller concern, not an ACP session concern.
- *Singleton/service locator*: Anti-pattern; explicit dependency injection matches existing codebase style.

### D3: Plan stage skips `cleanChangeDir()` when checkpoint exists

**Choice:** Guard `cleanChangeDir(changeDir)` with a checkpoint check — if `completedSteps` is non-empty, skip the clean.

**Rationale:** `cleanChangeDir()` deletes all generated artifacts (proposal.md, specs/, etc.). When resuming, we need those files on disk. The checkpoint's existence proves they were previously generated.

**Alternatives considered:**
- *Always skip cleanChangeDir and rely on artifact overwrite*: Risky — stale artifacts from a previous failed attempt could confuse the agent. The checkpoint gives us a reliable signal that artifacts are intentional.

### D4: Plan round loop uses `round.verify()` as secondary truth source

**Choice:** For each round in the loop, check `completedSteps` first. If marked complete, also verify the artifact exists on disk. If artifact missing, treat as incomplete regardless of checkpoint.

**Rationale:** Disk state is the ultimate source of truth (someone could manually delete artifacts). The checkpoint is an optimization hint, not a guarantee.

### D5: `Interrupted` status instead of reusing `Blocked`

**Choice:** New `IssueStatus.Interrupted = 'interrupted'` enum value.

**Rationale:** `Blocked` semantically means "agent failed, needs human intervention." `Interrupted` means "server went away, just resume." The reopen handler needs to distinguish these to decide whether to reset stage to draft (blocked) or resume from checkpoint (interrupted).

**Alternatives considered:**
- *Reuse `Blocked` with a flag in `approval_state`*: Overloads the meaning of `blocked`. All existing code that checks `status === 'blocked'` would need updating anyway. A distinct status is cleaner.
- *Add `pipelineState` sub-field to issue*: More complex. The status enum already captures the lifecycle; `interrupted` fits naturally.

### D6: Build stage checkpoint delegates to `RalphExecutor`

**Choice:** `runPipelineBuildStage()` reads checkpoint, passes `completedTaskIds: string[]` to `RalphExecutor`. The executor updates checkpoint after each task via a callback.

**Rationale:** `RalphExecutor` already manages task execution order and pass/fail tracking. Adding a `skipTaskIds` parameter and a `onTaskCompleted` callback keeps checkpoint logic out of the executor's core loop.

**Alternatives considered:**
- *Checkpoint inside RalphExecutor directly*: Would require threading the repo into the executor, increasing coupling. Callback pattern is cleaner.

### D7: Frontend — minimal changes, reuse existing patterns

**Choice:** Add `Interrupted` to the frontend `IssueStatus` enum. In `IssueCard`, treat `interrupted` similarly to `blocked` visually but with an amber/orange color and "Resume" action button.

**Rationale:** The Kanban already groups by `stage`, not `status`. An `interrupted` issue at `stage=plan` will appear in the Plan column. The card just needs a different badge and action.

## Risks / Trade-offs

- **[Stale checkpoint after manual artifact deletion]** → Disk verify (`round.verify()`) catches this. If artifact gone, round re-runs and checkpoint self-heals.
- **[Checkpoint write failure mid-stage]** → If DB write fails after a round completes, the checkpoint won't update. Next resume re-runs that round (safe, just wasteful). Round completion logged via `workflow_log` for debugging.
- **[Interrupted issues in Kanban with no resume path]** → If checkpoint was deleted (DB reset, migration), reopen falls back to Draft reset (per spec). User sees standard start flow.
- **[Two status values for "not running" (Blocked + Interrupted)]** → All code that checks `status === 'blocked'` must be audited. The `reopen()` method in `IssueService` needs to accept `interrupted` as a reopenable status.

## Migration Plan

1. **Schema migration (v14):** `CREATE TABLE pipeline_checkpoint (issue_number INTEGER, stage TEXT, completed_steps TEXT DEFAULT '[]', next_step TEXT, updated_at TEXT, PRIMARY KEY (issue_number, stage))`
2. **Backward compatibility:** Existing `blocked` issues continue to work. Only new server restarts produce `interrupted` status. No data migration needed.
3. **Frontend deploy:** Must ship simultaneously — old frontend won't recognize `interrupted` status and will silently ignore those issues in Kanban. Add `interrupted` to frontend `IssueStatus` enum first.
4. **Rollback:** If issues arise, `recoverIssues()` can be reverted to use `Blocked`. `interrupted` issues would need manual DB update: `UPDATE issues SET status = 'blocked' WHERE status = 'interrupted'`.

## Open Questions

- Should the checkpoint API be exposed via REST (e.g., `GET /api/issues/:number/checkpoint`) for the frontend to show sub-step progress? Spec says yes (detail page shows completed/pending steps) but the API endpoint isn't designed yet.
