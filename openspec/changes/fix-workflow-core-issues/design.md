## Context

Mohist uses an AI-driven workflow to process issues through stages: Explore → Plan → Build → Review → Done. The system has two layers of execution control:

1. **AgentRunnerService** — orchestrates Main Agent lifecycle, including pause/resume based on workflow.yaml `approval` flags
2. **RalphExecutor** — executes individual tasks from prd.json with retry and failure categorization

Key files:
- `workflow-controller.ts` — stage execution logic (Plan/Build/Review)
- `agent-runner-service.ts` — agent lifecycle with existing pause/resume
- `ralph-executor.ts` — OpenSpec task execution with onAskUser callback
- `advance-stage.ts` — stage transition validation (uses hardcoded M1 rules)
- `issue-repo.ts` — approval state persistence (queries by projectId, not issueId)

The codebase already has a working pause mechanism (`sessionManager.pause()/resume()`, `shouldPauseAtCurrentStage()`), but it's not properly configured for the new stage flow.

## Goals / Non-Goals

**Goals:**
- Fix all integration bugs that prevent end-to-end workflow execution
- Unify duplicate type definitions and transition rules
- Connect existing RalphExecutor to Build stage
- Make approval pause work correctly for new (Explore/Review) and legacy (Draft/Check) stages

**Non-Goals:**
- Rewriting the agent loop architecture
- Adding new workflow stages or capabilities
- Persisting sessions to disk for crash recovery
- Changing the LLM model or prompt strategy
- Removing backward compatibility with Draft/Check stages

## Decisions

### Decision 1: Use existing AgentRunnerService pause, not new shouldPause flag

**Why:** The system already has `shouldPauseAtCurrentStage()` which reads workflow.yaml `approval` flags and calls `sessionManager.pause()`. Adding a parallel mechanism would create confusion.

**What needs fixing:** The DEFAULT_WORKFLOW in `workflow-loader.ts` only defines stages `plan`, `build`, `check` — missing `explore`, `review`, `done`, `draft`. And `approval` flags may not align with new flow.

**Alternative considered:** Adding `shouldPause` to `StageResult` return value — rejected because it duplicates existing mechanism.

### Decision 2: RalphExecutor without onAskUser — stage-level approval instead

**Why:** Providing `onAskUser` to RalphExecutor inside `executeBuildStage` causes a deadlock: the tool call blocks waiting for user input, but the Agent Loop can't pause until the tool returns. The existing `run_ralph_loop` tool already demonstrates that omitting `onAskUser` works — failed tasks are simply marked as failed and the loop continues.

**How:** Call `RalphExecutor.execute()` without providing an `onAskUser` callback. When tasks fail after retries, RalphExecutor records them in `RalphLoopResult.failed`. `executeBuildStage` maps this to `StageResult { requiresApproval: true }`, letting the user decide at stage level via `submit_approval`.

**Alternatives considered:**
- Event-based Promise bridge (onAskUser → pause) — rejected: causes deadlock inside tool execution
- Per-task step execution — rejected: over-engineered, poor performance
- Keeping `run_ralph_loop` as separate tool — rejected: duplicates Build stage logic

### Decision 3: Single source for STAGE_TRANSITIONS

**Why:** `types/index.ts` defines `STAGE_TRANSITIONS` and `isValidTransition()`. `advance-stage.ts` has a separate hardcoded `M1_ALLOWED_TRANSITIONS` that's stale.

**How:** Delete `M1_ALLOWED_TRANSITIONS`, import `isValidTransition` from types.

### Decision 4: types/workflow-results.ts for shared interfaces

**Why:** `PlanResult` and `ReviewResult` are independently defined in `workflow-controller.ts`, `planner-agent.ts`, `reviewer-agent.ts` with drifting field sets (e.g., missing `duration` in some places).

**How:** Create single file, export from there, update all imports.

### Decision 5: Conservative prompt modification

**Why:** The Main Agent prompt is 136 lines of carefully tuned instructions. Rewriting it risks breaking agent behavior.

**How:** Only remove `run_ralph_loop` tool references. Keep everything else.

## Risks / Trade-offs

- **Risk: Changing transition rules breaks existing issues** → Mitigation: STAGE_TRANSITIONS already includes Draft/Check entries. No data loss.
- **Risk: Unified interfaces miss edge cases** → Mitigation: Make optional fields explicit (`error?`, `selfReviewNotes?`). Run full test suite.
- **Risk: workflow.yaml approval flag semantics misunderstood** → Mitigation: `shouldPauseAtCurrentStage` checks NEXT stage's approval flag, not current. The flag belongs ON the stage that triggers the pause boundary (plan and review). New and legacy stages must not be mixed in DEFAULT_WORKFLOW — only new flow stages are listed, legacy stages work via STAGE_TRANSITIONS alone.
