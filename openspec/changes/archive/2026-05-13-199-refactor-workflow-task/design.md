## Context

Plan, Build, Check, and Integrate currently share the same high-level stage lifecycle through `BaseStageRunner`, but each runner still embeds private task execution details. The duplication shows up in three places that now block further StageRunner unification work:

- runner-local safe event and workflow-log helpers, especially in Build, while task modules separately emit `stage_task_update`
- per-runner `executeReportedTask` / `runFixTask` branching that hard-codes repair and fix task ids
- stage-specific task assembly mixed directly into execution code, especially Plan and Check agent-session flows and Integrate service-call steps

The current code already has the right separation at the stage boundary: `BaseStageRunner` owns check orchestration, repair retry loops, approval handling, stage execution persistence, and stage-state mirroring; task modules such as `runHealthFixTask`, `runPlanRepairTask`, and `runReviewFixTask` only execute one unit of work and return `StageTaskResult`. This change formalizes that boundary so later issues can move runners onto shared definitions without first untangling private task branches.

This design is constrained by `specs/workflow-engine/spec.md`, with the proposal and issue acceptance criteria providing the rationale and implementation boundaries for that spec.

## Goals / Non-Goals

**Goals:**

- Centralize stage-scoped safe `emit` and `log` behavior behind `StageContext`-level helpers while preserving current event names and workflow log payloads.
- Define a minimal shared task runtime contract that accepts task definition plus `StageContext` and returns `StageTaskResult`.
- Route existing plan/build/check/integrate repair and fix entrypoints through shared handler infrastructure via adapters, without removing legacy exports.
- Introduce a minimal static loader model for Plan, Check, and Integrate tasks so task preparation can be tested separately from task execution.
- Add two reusable non-Build handlers: one for AgentSession-backed tasks and one for service-call tasks.
- Keep default workflow behavior unchanged: same runners, same `WorkflowEngine` registration, same check flow, same repair semantics.

**Non-Goals:**

- Replacing current stage runners with a single generic runner.
- Refactoring Build/Ralph dynamic task execution into the new loader/handler model.
- Moving checkpoint, approval, stage progression, or workflow-run decisions into handlers.
- Renaming SSE events or introducing a generic event taxonomy.
- Deleting legacy repair functions such as `runHealthFixTask`, `runReviewFixTask`, or `runPlanRepairTask`.

## Decisions

### D1: Extend `StageContext` with shared side-effect helpers instead of introducing a separate runtime service

Add `ctx.emit(event, payload)` and `ctx.log(eventType, data)` helpers, or an equivalent `ctx.runtime` object hanging directly off `StageContext`. These helpers will wrap existing `eventBus.emit` and `workflowLogRepo.insert` behavior with the same fire-and-forget safety currently implemented in runner-private helpers. The helpers should be stage-agnostic and accept current raw event names and log payloads so callers keep control over semantics.

This keeps the side-effect entrypoints close to the rest of stage execution dependencies and avoids a new service object that every runner and task module would need to construct manually. It also lets task handlers and legacy runner code share the same safe behavior immediately.

**Alternatives considered:** keep private helpers in each runner and only deduplicate repair execution. Rejected because later handler extraction would still need direct `eventBus` and `workflowLogRepo` access, preserving the same duplication in a different form.

### D2: Introduce a two-phase task runtime: `TaskLoader` prepares executable tasks, `TaskHandler` executes them

Define minimal task runtime types under `packages/cli/src/workflow/`:

- `TaskDefinition`: declarative description of a task before context-dependent fields are resolved
- `ExecutableTask`: a task ready to run, including resolved prompt/input, title, artifacts, and handler id
- `TaskHandler`: `execute(task, ctx) -> Promise<StageTaskResult>`
- `TaskHandlerRegistry`: small resolver used by adapters and focused tests
- `StaticTaskLoader`: converts static definitions plus `StageContext` into `ExecutableTask[]`

The critical boundary is that loaders may read context and build task input, but they do not decide ordering beyond the order encoded by the supplied static definitions, and handlers do not decide stage flow after execution. `BaseStageRunner` keeps ownership of retries, fix policy, persistence, reporting, and follow-up checks.

This two-phase split matches the long-term `StageDefinition -> loader -> handler` direction, but only for static tasks. It gives immediate value because Plan/Check agent prompts and Integrate service step inputs can be expressed as data prepared ahead of time instead of being recreated inside runner branches.

**Alternatives considered:** define only `TaskHandler` and keep task preparation inside each runner. Rejected because the current duplication is not only in execution; prompt construction and step metadata are also repeated and need a stable pre-execution shape for testing.

### D3: Use typed adapters to map existing repair/fix task ids onto shared handlers

Retain all existing public repair/fix entrypoints, but reimplement them on top of a shared adapter layer. The adapter owns the mapping from legacy task ids and failure context to one of these execution paths:

- health-fix tasks -> AgentSession-style handler with stage-specific prompt factory
- plan artifact repair -> AgentSession-style handler with plan artifact repair prompt and artifact diffing
- review repair -> AgentSession-style handler with review-report prompt
- merge repair -> dedicated service-call style handler wrapping `worktreeManager.rebaseOntoMaster`

`BaseStageRunner.executeTaskWork`, `BaseStageRunner.tryLegacyRepair`, and runner overrides of `executeReportedTask` / `runFixTask` continue to call familiar methods. The behavioral change is only that most of the branching moves into a shared adapter or registry lookup instead of being spread across runner classes.

This preserves compatibility for current callers and tests while reducing the number of places that know which task ids are executable in each stage.

**Alternatives considered:** delete legacy functions and switch all callers to registry lookups immediately. Rejected because the proposal explicitly keeps legacy runner paths and compatibility exports in place for this slice.

### D4: Model Plan and Check artifact/review tasks as `AgentSessionTaskHandler`, not as special runner code

Create `AgentSessionTaskHandler` as the common execution primitive for tasks that:

- create workflow session observers
- open an `AgentSession`
- execute a prompt or retry prompt
- emit `stage_task_update`
- optionally verify expected outputs and report repaired/generated artifacts

The handler should accept executable input such as:

- `cwd`
- `stage`
- `taskId` / `title`
- prompt builder output already resolved by the loader
- optional artifact verification callback
- optional retry prompt factory
- optional output summarizer

Plan artifact generation, plan repair, review repair, and any minimal Check artifact-generation path can all share this primitive even if their prompts differ. Existing wrapper functions may still compute task-specific prompts or artifact snapshots before delegating into the handler.

**Alternatives considered:** keep each AgentSession task as a standalone function and merely normalize return types. Rejected because the session lifecycle and task reporting code are nearly identical today and are the main duplication this issue is meant to collapse.

### D5: Model Integrate static steps and merge repair as `ServiceCallTaskHandler`

Introduce `ServiceCallTaskHandler` for tasks whose execution is synchronous application logic rather than an agent session. The handler executes an injected function against `StageContext`, wraps timing and task status normalization, and returns `StageTaskResult`. For Integrate this covers at least:

- `integrate:spec-sync`
- `integrate:archive-change`
- `integrate:merge`
- merge repair and other non-agent fix tasks that already call repository or worktree services directly

The goal is not to fully rewrite `IntegrateStageRunner`. The first step is to let static integrate tasks be represented as executable tasks and then invoked through the same contract as other tasks, even if the runner still decides when to emit stage-level `integration_started` and `integration_completed` events.

**Alternatives considered:** create separate specialized handlers for each integrate step. Rejected because those steps already share the same structural behavior: invoke a service function, convert output to `StageTaskResult`, let the runner own stage boundaries.

### D6: Keep `BaseStageRunner` as the sole owner of reporting and stage control, even when handlers emit task-level updates

Handlers may call `ctx.emit` for existing task-level events such as `stage_task_update`, and they may use `ctx.log` where the current behavior already writes workflow logs. They must not:

- mark checkpoints
- mutate workflow stage or issue status
- append task results to stage execution history directly
- schedule additional tasks
- run checks or decide whether a failed check is repairable

`BaseStageRunner` remains the only place that appends task results, retries fix tasks, persists check results, requests approval, and returns stage success/failure to the workflow domain. This maintains the accepted boundary: task executes, check verifies, runner reports, workflow decides.

**Alternatives considered:** let handlers append their own `StageTaskResult` or update checkpoints for convenience. Rejected because that would immediately blur ownership and make later generic runner cutover harder, not easier.

### D7: Add focused tests at the contract boundary rather than broad runner rewrites

The test plan should target the new shared boundaries directly:

- `StageContext` safe emit/log helpers ignore infrastructure failures without changing payloads
- `StaticTaskLoader` converts static definitions for Plan, Check, and Integrate into executable tasks with resolved prompts/inputs
- `AgentSessionTaskHandler` handles success, retry-after-missing-artifact, and failure result normalization
- `ServiceCallTaskHandler` normalizes successful and failed service invocations
- adapter coverage proves current task ids still resolve for plan repair, build health fix, review repair, merge repair, and integrate health fix

Existing runner tests should remain in place and only be adjusted where they need to assert the new shared path. This gives confidence that behavior is unchanged without pretending the system has already moved to a generic runner.

**Alternatives considered:** only add end-to-end stage tests after refactoring. Rejected because this issue is about introducing stable internal contracts, and those contracts need direct tests to protect future follow-up issues.

## Risks / Trade-offs

- [Loader and handler abstractions are introduced before full runner cutover] -> Keep the contracts intentionally small and only cover static tasks plus existing repair/fix paths.
- [`StageContext` may become more crowded] -> Limit new helpers to side-effect wrappers and avoid turning context into a policy object.
- [Legacy and new paths can drift if both build prompts separately] -> Make wrappers delegate into shared handler input builders so prompt/session logic exists in one place.
- [Merge repair does not naturally fit the same shape as AgentSession tasks] -> Use `ServiceCallTaskHandler` for service-backed work instead of forcing everything through one handler type.
- [Tests may pass while event/log semantics subtly change] -> Preserve raw event names and payload shapes, and add focused assertions around emitted event names and workflow log calls.
- [Future issues may assume static loader support applies to Build/Ralph] -> Name the loader `StaticTaskLoader` and document that dynamic ordering and Ralph execution remain outside this slice.

## Migration Plan

1. Add shared safe emit/log capability to `StageContext` construction and switch runner-private helper usage to the shared helper without changing event names or log payloads.
2. Introduce task runtime types: task definition, executable task, handler interface, and a small handler registry.
3. Implement `AgentSessionTaskHandler` and `ServiceCallTaskHandler` with focused unit tests.
4. Add `StaticTaskLoader` and tests showing it can express Plan, Check, and Integrate static tasks by resolving prompt/input from `StageContext`.
5. Create repair/fix adapters that map current legacy task ids to shared handler execution, while keeping `runHealthFixTask`, `runReviewFixTask`, `runPlanRepairTask`, and equivalent compatibility entrypoints exported.
6. Update Plan, Check, Build, and Integrate runners to delegate duplicate repair/fix and emit/log behavior through the shared infrastructure, but keep current runner classes and default registration unchanged.
7. Run focused workflow tests for Plan, Check, Integrate, and repair/fix paths. If any regression appears, rollback is low-risk: revert runner delegation to the previous private path while leaving the new handler code unused.

## Open Questions

- Should merge repair be exposed as a compatibility wrapper named `runMergeRepairTask`, matching the other legacy exports, even if its first shared implementation lives inside Check-specific code today?
- Where should the new task runtime types live so they remain obviously pre-cutover infrastructure and not a de facto generic runner API: `workflow/task-runtime.ts`, a `workflow/tasks/` folder, or alongside `stage-context.ts`?
