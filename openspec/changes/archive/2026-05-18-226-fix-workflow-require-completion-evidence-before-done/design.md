## Context

Mohist currently has the right workflow shape: `StageDefinition` describes promised work, `StageRun` records actual work, `WorkflowRun` advances the domain state, and `WorkflowRunProjection` displays the result. The gap is that stage completion can still be inferred from the absence of runnable work. Empty task/check collections can satisfy `every(...)` checks, and missing dynamic Build tasks or lost hydrated state can look equivalent to a genuinely completed stage.

This design makes completion evidence explicit. A stage can pass only when the run contains the evidence required by the stage definition and by any run-specific work appended during execution. Missing static work, unevaluated dynamic work sources, empty Build task materialization, pending runtime tasks, missing approval, stale checks, or missing Integrate delivery evidence must produce a blocked or failed workflow reason instead of silently advancing to `Done`.

## Goals / Non-Goals

**Goals:**

- Make `WorkflowRun` the single authority for whether a stage or workflow can advance.
- Require static stage tasks/checks declared by `StageDefinition` to have matching `TaskRun`/`CheckRun` evidence in `StageRun`.
- Treat generated Build tasks and runtime-added tasks as run-owned `TaskRun` evidence, not static workflow definition entries.
- Block Build completion when its dynamic task source is missing, invalid, unevaluated, or materializes zero tasks.
- Require Check and Integrate completion to be backed by current task/check evidence, including Integrate delivery evidence before final `Done`.
- Keep projection defensive without making stale `AgentSession` status or `mergeState` the authoritative completion source.
- Add regression coverage for the completion invariant and projection impossible-state guards.

**Non-Goals:**

- Do not introduce a workflow DSL, event-sourced workflow model, or separate generated task registry.
- Do not copy Build `tasks.json` entries or runtime repair/rebase tasks into static `StageDefinition.tasks`.
- Do not redesign task/check execution boundaries or make checks perform repair work.
- Do not use raw session status as the final issue completion authority.

## Decisions

### D1: Centralize Completion In A Stage Evidence Guard

Add a single domain guard used by both `WorkflowRun.nextWork()` and explicit stage completion paths such as `completeStage()`. The guard compares the active `StageDefinition` with the active `StageRun` and returns either complete or a structured not-complete reason. Completion must require:

- Every static task definition has a corresponding successful terminal `TaskRun`.
- Every existing `TaskRun` in the stage, including dynamic and runtime-added tasks, is terminal and successful.
- Every required check definition has a corresponding current passed `CheckRun`.
- Required approval is approved.
- Any stage-specific evidence requirement, such as Build dynamic work evaluation or Integrate delivery evidence, is satisfied.

This avoids separate paths where `nextWork()` blocks but a direct completion method can still advance.

**Alternatives considered:** Keep the existing scattered `every(...)` and pending-work checks with extra empty-list conditions. This is lower-touch but preserves duplicated completion logic and makes future runtime work easy to bypass.

### D2: Distinguish Static Definition Requirements From Run-Owned Work

Keep `StageDefinition.tasks` limited to static workflow tasks. Add or use definition metadata such as `workSources` and execution policies to describe dynamic work sources and known runtime task kinds, but never mutate the definition with generated task identities.

`StageRun.tasks` remains the complete list of concrete work for this issue run. Once a dynamic Build task, rebase task, repair task, or convergence task is appended, it becomes required evidence for this run and blocks completion until successful.

**Alternatives considered:** Normalize generated Build tasks into `StageDefinition.tasks` after Plan. This makes completion comparison simple but leaks run-specific input into global workflow design and creates confusing persistence/hydration semantics.

### D3: Represent Dynamic Work Source Evaluation Explicitly

Build needs a run-level marker for whether its dynamic task source was evaluated and what happened. The StageRun should record enough state to distinguish these cases:

- Not evaluated yet.
- Evaluated and materialized one or more tasks.
- Evaluated but source was missing, invalid, or empty.

Only the second case can proceed toward completion. Missing, invalid, or zero-task `tasks.json` should create a clear blocked/failed reason that the runner and UI can surface. After materialization, generated tasks are ordinary `TaskRun` records and the completion guard does not need to know their source details beyond requiring them to pass.

**Alternatives considered:** Infer dynamic task source evaluation from `StageRun.tasks.length`. This repeats the current bug pattern because an empty list cannot distinguish “nothing to do” from “required work was never materialized.”

### D4: Require Check Freshness Through Evidence, Not Session Status

Check completion should depend on authoritative stage evidence: the review task/result for the current candidate, required review verdict checks, and merge-readiness checks. Stale failed sessions should not block `Done` if later task/check evidence proves success, and a later successful session should not substitute for missing check evidence.

The implementation should invalidate or replace check runs when their inputs become stale, such as when a runtime rebase task is appended or the candidate changes. Completion then naturally waits for current checks to pass.

**Alternatives considered:** Gate issue completion on the latest `AgentSession.status`. This is simple to query but conflates execution attempts with domain evidence and incorrectly fails workflows that recovered after an earlier failed session.

### D5: Make Integrate The Final Completion Boundary

Final issue completion must require the workflow to reach and pass the final Integrate stage. Integrate must have required task/check evidence and delivery evidence, such as spec sync/archive/merge results according to the existing Integrate model, before the workflow can become passed or the issue can be projected as `Done`.

The projection layer should defensively reject impossible snapshots where a workflow is marked passed without finishing the final stage, or where final-stage evidence is absent. Projection may report inconsistency, but it should not invent completion truth from `mergeState` alone.

**Alternatives considered:** Continue allowing `mergeState` to drive Done-like projection. This preserves historical behavior but allows repository state to mask missing workflow evidence.

### D6: Preserve Recoverable Reasons In Domain Results

When completion is blocked because evidence is missing, the domain should return a reason specific enough for runners and users: missing static task run, missing check run, dynamic source not evaluated, dynamic source empty/invalid, runtime task pending/failed, approval required, stale check, or missing Integrate delivery evidence.

These reasons should be stored or emitted through existing workflow failure/blocking paths instead of adding a separate error channel. The goal is to make the invariant observable while keeping the public workflow model small.

**Alternatives considered:** Throw exceptions for impossible completion attempts. Exceptions are appropriate for programmer errors, but evidence gaps are recoverable workflow states and should be visible to users and retry logic.

## Risks / Trade-offs

- [Risk] Existing persisted runs may hydrate with incomplete evidence and become blocked instead of completing. → Mitigation: treat this as the intended safe behavior for active runs; surface a clear recoverable reason and avoid destructive migration.
- [Risk] Adding dynamic work-source state increases StageRun complexity. → Mitigation: keep the state minimal and only model evaluation status plus failure reason; concrete tasks remain ordinary `TaskRun` records.
- [Risk] Completion checks can become duplicated between runners and domain logic. → Mitigation: runners should materialize work and record results only; `WorkflowRun` remains the only completion authority.
- [Risk] Projection guards may disagree with older snapshots. → Mitigation: make projection defensive and diagnostic, not a replacement for domain completion; do not use session status or merge state as hard final truth.
- [Risk] Runtime-added tasks can stale existing checks and surprise users. → Mitigation: preserve `reason` / `causedBy` on appended tasks and invalidate affected checks so the UI explains why more work is required.

## Migration Plan

1. Add the shared stage completion guard and route both `nextWork()` and explicit stage completion through it.
2. Extend `StageRun` persistence/hydration with minimal dynamic work-source evaluation state, preserving existing task/check arrays.
3. Update Build materialization to record source evaluation before completion decisions and to block missing, invalid, or zero-task sources.
4. Update runtime task append paths so appended tasks carry `reason` / `causedBy`, become required run work, and stale affected checks.
5. Update Check and Integrate runners to record the required current evidence before allowing stage completion.
6. Harden `WorkflowRunProjection` to reject impossible passed snapshots, especially passed workflows that did not finish Integrate, while allowing stale failed sessions when later evidence is successful.
7. Add regression tests for empty static work, missing/empty dynamic Build work, pending runtime work, stale failed session plus later success, Integrate evidence, and impossible projection snapshots.
8. Rollback strategy: if rollout blocks valid active runs unexpectedly, revert the guard and persistence changes together; no external API or dependency rollback is expected.

## Open Questions

- Which existing StageRun persistence shape is the least disruptive place to store dynamic work-source evaluation status?
- Should zero-task Build sources always be treated as invalid, or should future workflow definitions be allowed to opt into an explicit zero-task policy?
