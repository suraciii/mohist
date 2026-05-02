## Context

After #114, the workflow has three independent stage runners (PlanStageRunner, BuildStageRunner, CheckStageRunner) each implementing the `StageRunner` interface but using completely different execution models internally:

- **PlanStageRunner** → delegates to `AcpRoundRunner` (serial ACP rounds with artifact verification)
- **BuildStageRunner** → delegates to `RalphExecutor` (task-by-task coder spawning loop)
- **CheckStageRunner** → runs `Check[]` array serially, then checks `issue.approvalState` for gate logic

`WorkflowEngine` loops over stages and handles `requiresApproval` / `gateRequired` branching — the gate concept lives at the engine level, not inside the runners.

The `approvalState` field on `Issue` is a persistent gate mechanism: `setApprovalState(id, { status: 'awaiting' })` pauses the pipeline, `clearApprovalState` resumes it. The `check_suites` table only records Check-stage results; Plan/Build have no persistence of their execution state.

**Current file inventory (to be changed):**

| File | Role |
|------|------|
| `workflow-engine.ts` | Pipeline loop with gate branching |
| `stage-context.ts` | `StageRunResult` (has `requiresApproval`), `CheckResult`, `CheckContext` |
| `check-stage-runner.ts` | `StageRunner` interface, `CheckStageRunner` class |
| `checks/index.ts` | `Check` interface (`name` + `run()`) |
| `checks/build-test-check.ts` | BuildTestCheck with internal auto-fix loop |
| `checks/ai-review-check.ts` | AiReviewCheck using AcpRoundRunner |
| `plan-stage-runner.ts` | PlanStageRunner using AcpRoundRunner |
| `build-stage-runner.ts` | BuildStageRunner using RalphExecutor |
| `acp-round-runner.ts` | Serial ACP round execution with checkpoint |
| `workflow-controller.ts` | Legacy 1657-line controller (still importable) |
| `db/check-suite-repo.ts` | `check_suites` table (Check-stage only) |

## Goals / Non-Goals

**Goals:**
- Introduce `BaseStageRunner` with unified Tasks → Checks → Reactions loop
- Make `user-approval` a standard Check (not a gate), eliminating `gateRequired` / `requiresApproval`
- Remove `WorkflowController`, `AcpRoundRunner` (absorbed into BaseStageRunner)
- Generalize `check_suites` → `stage_executions` for all stages
- Preserve all existing behavior: auto-fix in build-test, ACP round-based plan artifact generation, Ralph task loop in build, escalation paths

**Non-Goals:**
- No new Stage enum values or workflow.yaml changes
- No parallel execution (all tasks/checks remain serial)
- No changes to ACP session layer, Explore Mode, or merge queue internals
- No WebUI changes in this change (follow-up)
- No changes to the approval API surface (`POST /approve`, `POST /reject`) — they continue to work but now resolve the `user-approval` check

## Decisions

### D1: BaseStageRunner as abstract class, not interface

`BaseStageRunner` implements the full execution loop (Tasks → Checks → Reactions) as a concrete `async run()` method. Subclasses declare their tasks, checks, and next stage via constructor configuration or abstract getters.

**Why abstract class over interface:** The loop logic is identical across all stages — only the task list, check list, and reaction configurations differ. An abstract class avoids duplicating the serial execution + reaction dispatch in every runner. The existing `StageRunner` interface (`canHandle` + `run`) is preserved as the engine-facing contract; `BaseStageRunner` implements it.

**Alternatives considered:**
- *Trait/mixin pattern*: TypeScript lacks true mixins; composition with a runner helper would require every subclass to delegate manually — same boilerplate, less type safety.
- *Config-driven (data-only runners)*: Define stage behavior as JSON config, one generic runner. Rejected because Plan/Build/Check have fundamentally different task execution logic (ACP rounds vs Ralph loop vs check execution) that can't be expressed declaratively without a DSL.

### D2: Reaction as a property of Check, not a separate dispatch table

Each `Check` carries its own `reaction: ReactionConfig`. This keeps the failure-handling policy co-located with the check definition.

```ts
interface ReactionConfig {
  type: 'retry-task' | 'auto-fix' | 'escalate' | 'ask-user';
  maxAttempts?: number;          // for retry-task, auto-fix
  escalateTarget?: Stage;        // for escalate
  fallbackReaction?: ReactionConfig; // e.g., ask-user → fallback escalate
}

interface Check {
  name: string;
  reaction: ReactionConfig;
  run(ctx: CheckContext): Promise<CheckResult>;
}
```

**Why co-located:** The reaction is semantically tied to what the check validates — `build-test` failing should auto-fix because it's a code issue; `ai-review` failing should escalate because it's a design issue. Separating reactions into a dispatch table would scatter this knowledge.

**Alternatives considered:**
- *Central reaction registry*: Map of check name → reaction. Rejected because it separates the "what happens on failure" from the check definition, making it easy to get out of sync.
- *Reaction as a separate execution step*: A post-processing phase after all checks run. Rejected because it prevents early exit on failure (spec requires remaining checks not to run after a failure).

### D3: Absorb AcpRoundRunner into PlanStageRunner's task execution

The `AcpRoundRunner` serial round logic (create ACP connection → prompt per round → verify artifact → retry once) becomes PlanStageRunner's task execution. The round configs become Plan's task definitions. No separate runner class.

**Why:** AcpRoundRunner is only used by PlanStageRunner and AiReviewCheck. After this refactor, AiReviewCheck becomes a Check under CheckStageRunner, and its review round becomes part of Check's task execution (run AI review). There's no remaining caller for a standalone AcpRoundRunner.

**Migration path:** Move the round execution logic into PlanStageRunner's `executeTasks()` override. The checkpoint logic transfers directly since BaseStageRunner already has `checkpointManager`.

### D4: Keep RalphExecutor but call it from BuildStageRunner's task phase

RalphExecutor's task-by-task loop (read tasks.json → spawn_coder per task → verify AC → update tasks.json) remains intact as BuildStageRunner's task execution. It is NOT refactored into individual BaseStageRunner tasks because the Ralph loop has complex internal state (learnings, retry, checkpoint per task).

**Why keep RalphExecutor as-is:** The Ralph loop is already well-tested and handles edge cases (zero-work detection, WIP commits, task-level retries). Breaking it into individual task objects would be a massive rewrite with high risk. Instead, RalphExecutor becomes the task execution engine called by BuildStageRunner's `executeTasks()`.

### D5: user-approval check reads issue.approvalState, no new DB fields

The `user-approval` check inspects `issue.approvalState?.status === 'approved'` to determine pass/fail. On failure, it returns `pending` status and triggers `ask-user` reaction which emits `approval_requested`. This means the existing `approvalState` field and `setApprovalState`/`clearApprovalState` methods remain on `IssueRepo`.

**Why not remove approvalState entirely:** The approval API (`POST /approve`, `POST /reject`) and agent-runner-service's gate recovery logic both depend on `approvalState`. Removing it would be a larger change touching the API layer. Instead, we keep the field but its semantics shift from "gate state" to "user-approval check state". This is an interim step — a future change can replace it with a check result in `stage_executions`.

### D6: Generalize check_suites → stage_executions table

Replace the `check_suites` table with a `stage_executions` table that records task results and check results for every stage. Schema:

```sql
CREATE TABLE stage_executions (
  id            TEXT PRIMARY KEY,
  issue_id      TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  stage         TEXT NOT NULL,  -- 'plan' | 'build' | 'check' | 'done'
  status        TEXT NOT NULL DEFAULT 'running',  -- running | awaiting-approval | passed | failed
  task_results  TEXT NOT NULL DEFAULT '[]',   -- JSON array of task execution records
  check_results TEXT NOT NULL DEFAULT '[]',   -- JSON array of check results
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
```

**Why not extend check_suites:** `check_suites` has a `snapshot_sha` column specific to the Check stage. Adding a `stage` column and making `snapshot_sha` nullable would leave a confusing schema. A clean table with a migration path is clearer.

**Migration:** Add migration that creates `stage_executions`, keeps `check_suites` as read-only for historical data.

### D7: Simplified StageRunResult and PipelineResult

```ts
interface StageRunResult {
  success: boolean;
  nextStage?: Stage;
  escalateToStage?: Stage;
  checkResults: CheckResult[];
  message?: string;
}

interface PipelineResult {
  completed: boolean;
  stage: Stage;
  message?: string;
}
```

Remove `requiresApproval` from `StageRunResult`, remove `gateRequired` from `PipelineResult`. The engine loop becomes: run stage → check `result.success` → advance or escalate. No approval branching.

### D8: Simplified WorkflowEngine — no approval logic

The engine loop simplifies to:

```
while (stage !== Done):
  runner = findRunner(stage)
  result = runner.run(ctx)
  if result.success:
    updateStage(result.nextStage)
  else if result.escalateToStage:
    updateStage(result.escalateToStage)
  else:
    return { completed: false, ... }
return { completed: true }
```

The `setApprovalState`, `approval_requested` emit, and `gateRequired` return are all removed. Approval is now a check-level concern inside the runners.

## Risks / Trade-offs

- **[Risk] Large refactoring surface** — ~10 files touched, 3 deleted, types changed across API/CLI/services → **Mitigation**: Implement in phases: (1) types + BaseStageRunner, (2) migrate each runner one at a time, (3) simplify engine, (4) delete legacy code. Each phase can be tested independently.
- **[Risk] AcpRoundRunner removal affects AiReviewCheck** → **Mitigation**: AiReviewCheck's review round logic moves into CheckStageRunner's task execution. The ACP connection management in AcpRoundRunner is self-contained and can be extracted into a helper function.
- **[Risk] approvalState field kept as interim** — callers may still treat it as a "gate" concept → **Mitigation**: Rename internal references from "gate" to "user-approval check state" in code comments. Add a `@deprecated` JSDoc on `gateRequired` / `requiresApproval` pointing to the new model.
- **[Risk] Existing tests break** — All stage runner tests use `requiresApproval: true/false` → **Mitigation**: Update tests as part of each runner migration. Each phase has its own test update pass.
- **[Trade-off] RalphExecutor not fully absorbed** — Build still uses RalphExecutor as a black box rather than individual task objects → Accepted. Full absorption would be a separate refactor with its own design doc.

## Migration Plan

**Phase 1: Types & Infrastructure** (no behavior change)
1. Add `ReactionConfig` type and update `Check` interface with `reaction` field
2. Add `BaseStageRunner` abstract class with skeleton `run()` loop
3. Create `stage_executions` table migration
4. Update `StageRunResult` and `PipelineResult` (add new fields, keep old ones temporarily)

**Phase 2: Migrate CheckStageRunner** (simplest, already uses Check[])
1. Extend `CheckStageRunner` from `BaseStageRunner`
2. Add `user-approval` check to its check list
3. Add `reaction` configs to build-test and ai-review checks
4. Remove approval gate logic from `CheckStageRunner.run()`
5. Remove `requiresApproval` from check-stage results

**Phase 3: Migrate PlanStageRunner**
1. Absorb `AcpRoundRunner` round execution into PlanStageRunner's task phase
2. Add check list (artifact verification checks + user-approval)
3. Remove approval gate logic from `PlanStageRunner.run()`
4. Delete `acp-round-runner.ts`

**Phase 4: Migrate BuildStageRunner**
1. Wrap RalphExecutor call as BuildStageRunner's task phase
2. Add check list (all-tasks-complete, code-compiles)
3. Remove `requiresApproval` from build-stage results

**Phase 5: Simplify Engine & Clean Up**
1. Remove `gateRequired` from `PipelineResult`
2. Remove approval branching from `WorkflowEngine.run()`
3. Update `AgentRunnerService.runPipelineToCompletion()` to handle new result format
4. Delete `workflow-controller.ts`
5. Update `workflow/index.ts` exports
6. Update all tests

**Phase 6: Persistence**
1. Add `StageExecutionRepo` for `stage_executions` table
2. Wire repo into `StageContext`
3. Each stage runner persists check results on completion
4. Mark `check_suites` as read-only (keep for historical queries)

**Rollback:** Each phase is independently committable. If a phase introduces regressions, revert that phase's commit. The old code paths (gate logic) remain functional until Phase 5.

## Open Questions

- **Done stage runner**: The Done stage currently has no StageRunner (merge queue is handled in `AgentRunnerService`). Should we create a `DoneStageRunner` in this change, or defer it? Recommendation: defer — create a placeholder that returns `{ success: true, nextStage: Stage.Done }` and implement properly in a follow-up.
- **code-compiles check**: Should this be a new check for Build stage, or is the existing build-test check sufficient? The issue description lists them separately but they could be combined. Recommendation: keep separate — `all-tasks-complete` is a data check on tasks.json, `code-compiles` runs `npm run build`. Different failure modes, different reactions.
