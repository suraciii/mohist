## Context

The workflow runtime already has the pieces needed for a unified stage runner: `WorkflowRun.nextWork()` selects task/check/approval/failure work, `StageContext.emit/log` centralizes stage side effects, `workflow/task-runtime/` contains shared task handler contracts, Build has Ralph task execution, and runtime-added work such as `rebase-branch` is represented as WorkflowRun tasks.

The remaining complexity is that `PlanStageRunner`, `BuildStageRunner`, `CheckStageRunner`, and `IntegrateStageRunner` still each encode their own task loading, check construction, repair hooks, approval handling, checkpoint behavior, and special cases. This creates modification amplification: changing a cross-stage concept requires understanding four shallow subclasses plus WorkflowRun aggregate execution.

The design direction is to make the runner a deep module: a small interface that executes the work requested by WorkflowRun, with stage differences hidden behind `StageDefinition`, work sources, task handlers, check registry, approval policy, repair policy, and invalidation policy.

## Goals / Non-Goals

**Goals:**

- Extend the existing `StageDefinition` model so Plan, Build, Check, and Integrate can describe work sources, task execution, checks, approvals, repairs, and invalidations declaratively.
- Introduce a registry-backed config-driven runner path that resolves tasks and checks from definitions and executes only the work requested by `WorkflowRun.nextWork()`.
- Preserve WorkflowRun as the authority for task ordering, check ordering, approval waiting, repair scheduling, stage pass/fail, workflow pass/fail, and rebase fact invalidation.
- Migrate stages incrementally in the order Integrate, Plan, Check, Build, with each stage independently testable before changing default runner registration.
- Keep runtime-added tasks, especially `rebase-branch`, first-class and blocking through the same task semantics as static and dynamic tasks.
- Keep legacy runners and current event/log/projection behavior available as rollback and compatibility paths during this issue.

**Non-Goals:**

- Delete legacy runner files or remove rollback paths.
- Rename or remove existing SSE/event names.
- Merge checkpoint, `stage_state`, `stage_executions`, check suite, and WorkflowRun persistence systems.
- Change user-visible stage order or approval semantics.
- Redesign #199 task runtime contracts, #200 Ralph runtime, or #206 rebase task entrypoints.
- Introduce new database schema as a prerequisite for this refactor.

## Decisions

### D1: Extend `StageDefinition` as the stage behavior contract

`StageDefinition` will remain the domain description of a stage, but it will grow from `tasks/checks/requiresApproval/checkFailurePolicies` into a richer configuration object:

```ts
interface StageDefinition {
  stage: Stage;
  tasks: TaskDefinition[];
  checks: CheckDefinition[];
  workSources: WorkSourceDefinition[];
  taskExecution: TaskExecutionPolicy[];
  checkPolicy: CheckPolicy;
  approvalPolicy?: ApprovalPolicy;
  repairPolicies: RepairPolicy[];
  invalidationPolicies: InvalidationPolicy[];
}
```

The new fields should be additive at first so existing WorkflowRun construction and tests can keep working while each stage is migrated. Existing `tasks`, `checks`, `requiresApproval`, `approvalCheckName`, and `checkFailurePolicies` can either be preserved as compatibility fields or treated as derived fields until the config-driven path is stable.

**Alternatives considered:** Put configuration in each runner subclass. This keeps the current shallow-module problem: the same stage semantics remain scattered and any cross-stage change still requires reading runner code. Move all policy into WorkflowRun immediately. This makes WorkflowRun too broad and couples aggregate state decisions to task/check execution details.

### D2: Add a generic config-driven runner rather than rewrite all legacy runners at once

Implement a `ConfigDrivenStageRunner` or a config-driven branch inside `BaseStageRunner`. It should implement the existing `StageRunner` interface so `WorkflowEngine` does not need a new execution contract. The runner should:

- Load/materialize work from the stage definition only when needed.
- Execute `ctx.requestedWork.kind === 'task'` through a task loader and handler registry.
- Execute `ctx.requestedWork.kind === 'check'` through a check registry.
- Return normal `StageRunResult` values while reporting task/check results through existing `workflowApplicationService` calls.
- Reuse `BaseStageRunner` helpers for `stage_executions`, `stage_state`, check-suite, approval output, and safe emit/log compatibility where practical.

The legacy `BaseStageRunner.run()` path stays available for stages not yet switched to config-driven execution.

**Alternatives considered:** Replace `BaseStageRunner` completely. This is higher risk because current runners contain compatibility behavior for checkpoints, stage state, aggregate single-work execution, approval output, and event emission. Add a separate workflow engine for config-driven execution. This would duplicate orchestration and increase the chance that legacy and aggregate execution diverge.

### D3: Introduce explicit task loader and check registries

Task loading and check lookup should be explicit registries rather than `if stage/taskId` branches in the generic runner.

The task side should have:

- `TaskLoaderRegistry`, keyed by work source type.
- `static` loader for Plan, Check, Integrate static tasks.
- `ralph` loader for Build tasks from `tasks.json` and WorkflowRun materialization.
- `runtime` source for already-materialized ad-hoc tasks like `rebase-branch`, repair tasks, and convergence tasks.
- Existing `TaskHandlerRegistry` extended as needed to include `ralph` and `rebase` kinds in addition to `agent-session` and `service-call`.

The check side should have:

- `CheckRegistry`, keyed by check name.
- Factories or context-aware constructors for checks that need stage-specific inputs, such as health gate policies and worktree paths.
- Check policy phase metadata so pre-task, post-task, and approval checks are not inferred from subclass methods.

**Alternatives considered:** Let the generic runner instantiate concrete checks directly from switch statements. That would centralize code but still leak every stage-specific check detail into the runner. Treat checks as task handlers. That blurs the important domain boundary that checks are read-only and repairs are tasks.

### D4: Keep WorkflowRun responsible for all decisions, move special invalidation into policy

The runner executes and reports results; WorkflowRun continues to decide next work. Invalidation logic currently hardcodes cases such as Check repair resetting `ai-review`, `review-passed`, and `merge-ready`, and rebase invalidating review state only when SHA facts change. Those hardcoded cases should move behind `InvalidationPolicy` entries attached to stage definitions.

Initial policy support can be narrow and explicit:

- Check `fix-review-findings*` completion invalidates `ai-review`, `review-passed`, `merge-ready`, and approval state.
- Check `rebase-branch` completion with `shaChanged=true` invalidates the same review-dependent state.
- Check `rebase-branch` completion with `shaChanged=false` preserves review-dependent state.
- Integrate freeze point prevents post-merge health from scheduling code-modifying fixes.

WorkflowRun should apply these policies after successful task completion and before `maybeCompleteStage()`. Existing hardcoded logic can remain temporarily while policy behavior is introduced, but the final default config-driven path should use policy-driven invalidation.

**Alternatives considered:** Let task handlers invalidate state directly. This would hide workflow consequences inside execution code and violate the invariant that task executors do not decide stage flow. Keep all invalidation hardcoded in WorkflowRun forever. This preserves behavior but fails the maintainability goal because new stage policies still require aggregate code edits.

### D5: Migrate stages in risk order: Integrate, Plan, Check, Build

Integrate is the best pilot because its current work already maps cleanly to deterministic service-call tasks plus one health check. Plan is next because it is static agent-session artifact generation with approval. Check follows because it combines agent-session review, read-only review/merge checks, approval output, repair, convergence, and stale review invalidation. Build is last because it depends on Ralph dynamic tasks, task materialization, checkpoint resume, aggregate `onlyTaskId`, and health auto-fix.

**Alternatives considered:** Start with Build because it is the most painful. This maximizes risk before the generic runner/check registry are proven. Migrate all stages in one pass. This removes rollback granularity and makes failures hard to isolate.

### D6: Build consumes Ralph through adapter contracts, not a new Build-only loop

Build migration should wrap the existing Ralph behavior behind `RalphTaskLoader` and `RalphTaskHandler` rather than reimplementing task parsing, dependency ordering, retries, session execution, learnings, task file updates, commits, or aggregate reporting.

The loader materializes tasks into WorkflowRun/StageRun using task id, title, order, and dependencies. The handler executes the selected task with the existing Ralph single-task/`onlyTaskId` path and reports the result. The generic runner should not contain Build-specific task loop logic.

**Alternatives considered:** Move `runRalphLoop` logic into the generic runner. This would make the generic runner a special-case Build runner and leak Ralph details into the orchestration layer. Rewrite Ralph execution around pure task handlers immediately. This is larger than this issue and risks regressing Build retry and checkpoint behavior.

### D7: Preserve compatibility projections while WorkflowRun remains source of truth

The config-driven runner should continue writing current compatibility outputs: `stage_executions`, `stage_state`, workflow logs, existing events, check suite updates, and checkpoints. These remain evidence/projections/resume cursors, not state-machine authority.

The safest implementation path is to reuse `BaseStageRunner` reporting helpers where possible. If helper visibility is too restrictive, introduce a small reporting collaborator rather than duplicating append/mirror/report logic in the generic runner.

**Alternatives considered:** Stop writing legacy projections for config-driven stages. This would simplify code but break UI/API compatibility before #202. Duplicate reporting code in the new runner. This risks divergence between legacy and config-driven behavior.

## Risks / Trade-offs

- [Risk] `StageDefinition` grows into a large mixed domain/runtime object → Mitigation: keep execution classes, function references, and concrete registry instances outside the domain definition; definitions store stable ids, policy data, and source names.
- [Risk] Legacy and config-driven paths diverge during migration → Mitigation: migrate one stage at a time, keep focused parity tests per stage, and only switch default registration after all stages pass.
- [Risk] Build task execution regresses because Ralph has many hidden side effects → Mitigation: consume existing Ralph loader/handler and preserve `onlyTaskId`, task file persistence, checkpoint restore, stage state sync, and aggregate reporting behavior.
- [Risk] Approval is accidentally treated as an ordinary check or invalidated too eagerly → Mitigation: keep approval as StageRun state, not repairable check work; invalidation must require explicit policy plus task result facts.
- [Risk] The generic runner becomes a switch statement over all task/check names → Mitigation: resolve through registries and move special behavior into task handlers, check factories, and invalidation policies.
- [Risk] Post-merge Integrate health repair could modify code after merge freeze → Mitigation: keep the existing WorkflowRun freeze-point guard and represent Integrate health repair policy as non-applicable after `integrate:merge` completes.
- [Risk] Existing tests assert legacy runner details → Mitigation: first preserve legacy files and exports, then update/add tests around observable behavior and aggregate next-work decisions rather than subclass internals.

## Migration Plan

1. Extend domain and runtime types.
   Add `WorkSourceDefinition`, `TaskExecutionPolicy`, `CheckPolicy`, `ApprovalPolicy`, `RepairPolicy`, and `InvalidationPolicy` types. Keep existing `StageDefinition` fields compatible while adding new fields to `DEFAULT_STAGE_DEFINITIONS`.

2. Add registries and config-driven runner shell.
   Add `TaskLoaderRegistry` and `CheckRegistry`. Create the config-driven runner path that can execute one requested task or one requested check and report through existing `workflowApplicationService` and projection helpers. Keep all legacy runners registered by default at this point.

3. Migrate Integrate.
   Model `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as service-call tasks. Register `health:integrate` as the post-task check. Validate single task execution, ordered task blocking, freeze point, and post-merge health failure behavior. Keep `IntegrateStageRunner` as rollback.

4. Migrate Plan.
   Model proposal, specs, design, tasks, and self-review as static agent-session tasks. Preserve artifact verification, retry prompt behavior, checkpoint step completion, plan session update bridge, plan approval output, and `health:plan`. Keep `PlanStageRunner` as rollback.

5. Migrate Check.
   Model `ai-review` as a static agent-session task. Register `review-passed`, `merge-ready`, and approval policy. Move review repair, merge repair, review snapshot convergence, and stale review invalidation to repair/invalidation policy plus task handlers. Validate approval output and re-review truth before approval.

6. Migrate Build.
   Register Build Ralph work source and handler. Materialize `tasks.json` into Build StageRun, execute selected Build tasks via Ralph single-task support, preserve checkpoint resume, task file updates, session learnings, aggregate `onlyTaskId`, and `health:build` auto-fix repair.

7. Switch default runner registration.
   After all stage-specific tests and full/aggregate workflow tests pass with config-driven stages, register the unified runner as the default handler for Plan, Build, Check, and Integrate. Leave legacy runner files and rollback construction available.

8. Rollback strategy.
   If a migrated stage regresses, change runner registration for that stage back to its legacy runner without deleting the new definitions or registries. Because event names, projections, checkpoints, and WorkflowRun state remain compatible, rollback should not require data migration.

## Open Questions

- Should the first implementation store `workSources` and handler/check ids directly in `workflow/domain/index.ts`, or introduce a separate runtime configuration layer that derives from domain `StageDefinition`?
- Should legacy `requiresApproval`, `approvalCheckName`, and `checkFailurePolicies` remain as long-term aliases, or be removed in a later cleanup once policy fields are stable?
- How much of `BaseStageRunner` reporting should be extracted into a shared collaborator versus made protected for reuse by the config-driven runner?
