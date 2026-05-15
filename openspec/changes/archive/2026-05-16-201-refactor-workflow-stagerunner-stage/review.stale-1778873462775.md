## Findings

1. Error: Build config-driven execution can create duplicate `stage_executions` rows for a single stage run.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:345-359`, `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:51-53`
Why it matters: the unified runner already creates or reuses the active stage execution before dispatching work, but the Ralph handler unconditionally calls `stageExecutionRepo.create(...)` again for every Build task. That can split one Build stage across multiple execution records, fragment task/log persistence, and violate the compatibility/projection goal for `stage_executions` during migration.
Suggested change: plumb the active stage execution id through `StageContext` or reuse `findActiveByIssueId()` inside `createRalphTaskHandler()` instead of always calling `create()`.

2. Warning: the new Plan path bypasses git hooks when auto-committing artifacts.
File: `packages/cli/src/workflow/config-driven-stage-runner.ts:1098-1101`
Why it matters: `git commit --no-verify` skips repository validation hooks on the config-driven path. That weakens safety guarantees for generated planning artifacts and diverges from normal repository protections.
Suggested change: remove `--no-verify` and let the normal hook chain run, or make hook skipping an explicit, opt-in operational override.

## Correctness

- FAIL: finding 1 is a real migration regression risk for Build projection consistency.

## Complexity

- Warning: `packages/cli/src/workflow/config-driven-stage-runner.ts` remains a very large orchestration module, with several stage-specific branches still embedded in one class. It works, but it is above the requested complexity target and will be harder to evolve safely.

## Test Coverage

- PASS: targeted tests passed with `npx vitest run tests/workflow-run-domain.test.ts tests/workflow/stage-runner-migration-regression.test.ts tests/workflow-engine-aggregate.test.ts`.
- PASS: build passed with `npm run build`.

## Security

- Warning: finding 2 introduces hook bypass on a write path.

## Spec Compliance

- PASS: `workflow-definition` default stages expose declarative policy data and preserve stage order in `packages/cli/src/workflow/domain/index.ts:485-656`.
- PASS: `workflow-definition` remains non-executing data in `packages/cli/src/workflow/domain/index.ts:98-110,485-656`.
- PASS: static non-Build work resolves from definitions via static loader wiring in `packages/cli/src/services/agent-runner-service.ts:1200-1227` and execution policy lookup in `packages/cli/src/workflow/config-driven-stage-runner.ts:759-815,793-803`.
- PASS: checks resolve from configured policy and registry in `packages/cli/src/workflow/config-driven-stage-runner.ts:282-297` and `packages/cli/src/services/agent-runner-service.ts:1234-1250`.
- PASS: Plan stage tasks, checks, and approval are declared in `packages/cli/src/workflow/domain/index.ts:487-534` and exercised in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:271-538`.
- PASS: Check stage review task, repair policy, invalidation policy, and approval policy are declared in `packages/cli/src/workflow/domain/index.ts:564-623` and exercised in `packages/cli/tests/workflow-run-domain.test.ts:514-687`.
- PASS: Integrate ordered tasks and post-merge health semantics are declared in `packages/cli/src/workflow/domain/index.ts:626-654` and exercised in `packages/cli/tests/workflow-run-domain.test.ts:572-686`.
- PASS: WorkflowRun remains the authority for task/check/approval/failure decisions in `packages/cli/src/workflow/domain/index.ts:742-1082`.
- PASS: repair tasks are scheduled as ordinary task work with `causedBy` metadata in `packages/cli/src/workflow/domain/index.ts:832-845` and tested in `packages/cli/tests/workflow-run-domain.test.ts:473-493`.
- PASS: approval is modeled separately from checks in `packages/cli/src/workflow/domain/index.ts:1040-1048` and tested in `packages/cli/tests/workflow-run-domain.test.ts:647-654`.
- PASS: rebase invalidation is fact-driven in `packages/cli/src/workflow/domain/index.ts:1170-1210` and tested in `packages/cli/tests/workflow-run-domain.test.ts:514-645`.
- PASS: aggregate single-work execution remains covered in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:781-1227` and `packages/cli/tests/workflow-engine-aggregate.test.ts:108-320`.
- PASS: legacy and config-driven runners coexist, with rollback env gating, in `packages/cli/src/services/agent-runner-service.ts:1252-1268`.
- PASS: Build tasks materialize from Ralph before health-check selection in `packages/cli/src/workflow/config-driven-stage-runner.ts:685-753`, `packages/cli/src/workflow/workflow-engine.ts:193-220,241-245`, and tests `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:599-778`.
- PASS: Build selected tasks execute through the Ralph handler path in `packages/cli/src/workflow/config-driven-stage-runner.ts:436-469` and `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:11-121`.
- PASS: Build checkpoint/rematerialization coverage exists in `packages/cli/tests/workflow/stage-runner-migration-regression.test.ts:657-778`.
- PASS: Build health repair remains ordinary task work through policy in `packages/cli/src/workflow/domain/index.ts:542-559,832-845` and tests `packages/cli/tests/workflow-run-domain.test.ts:473-493`.
- FAIL: Build compatibility projections are not fully preserved because config-driven Build can create multiple `stage_executions` for one stage via `packages/cli/src/workflow/config-driven-stage-runner.ts:345-359` plus `packages/cli/src/workflow/task-runtime/ralph-task-handler.ts:51-53`. This violates the migration goal to preserve existing projection behavior while moving orchestration under the generic runner.

## Overall

- FAIL: one error-level issue found.

<promise>FAIL</promise>
