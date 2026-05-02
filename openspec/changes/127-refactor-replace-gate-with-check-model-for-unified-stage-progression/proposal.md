## Why

Three independent execution models (AcpRoundRunner for Plan, RalphExecutor for Build, CheckStageRunner for Check) each handle task execution, stage progression, and user approval differently, creating inconsistency in how stages advance. The `gate` concept — where `user-approval` is a property of the stage boundary — conflates two distinct concerns (checking completion quality vs. requesting human confirmation) and prevents uniform failure handling across stages. Unifying all stages under a single Task → Check → Reaction loop eliminates this inconsistency and makes stage progression predictable: all checks pass → advance, any check fails → react.

## What Changes

- **BREAKING**: Remove `gate`/`gate_after`/`requiresApproval` concept entirely from types, `PipelineResult`, `StageRunResult`, and `WorkflowEngine`
- **BREAKING**: `user-approval` becomes a check item within each stage's checks list (not a stage boundary property)
- **BREAKING**: Remove `WorkflowController` (already partially deprecated by #114 StageRunner architecture — delete remaining code)
- **BREAKING**: Remove `AcpRoundRunner` — Plan stage absorbs its round logic into the unified Task + Check model
- **BREAKING**: Remove `RalphExecutor` direct usage — Build stage uses the unified BaseStageRunner
- Introduce `BaseStageRunner` abstract class providing the unified execution loop: Tasks → Checks → Reactions
- Introduce `Reaction` model per check: `retry-task`, `auto-fix`, `escalate`, `ask-user`
- Each stage (Plan, Build, Check, Done) declares its Task list and Check list declaratively
- `StageRunResult` simplified: `success` + `nextStage` + `checkResults` (no `requiresApproval`, no `gateRequired`)
- `WorkflowEngine` simplified: no approval gate logic — just run checks and advance on all-pass
- All stages persist execution state uniformly (Plan/Build no longer skip persistence)
- `CheckStageRunner` and its `Check` interface generalized into the shared Check model used by all stages

## Capabilities

### New Capabilities

- `unified-check-model` — Task + Check + Reaction execution loop that all stages use for consistent stage progression and failure handling

### Modified Capabilities

- `pipeline-model` — Remove `gate_after` requirement, replace with Check-based stage progression; `user-approval` becomes a check, not a gate
- `workflow-definition` — Update stage behavior specs from independent execution models to unified BaseStageRunner model; remove `approval` field from stage config
- `ralph-task-execution` — Task loop becomes an instance of the unified BaseStageRunner rather than a standalone executor

## Impact

- **Core files**: `workflow-engine.ts`, `stage-context.ts`, `check-stage-runner.ts`, `plan-stage-runner.ts`, `build-stage-runner.ts`, `checks/index.ts`
- **Deleted files**: `workflow-controller.ts`, `acp-round-runner.ts`, `workflow-engine.ts` gate/approval logic
- **Types**: `PipelineResult` (remove `gateRequired`), `StageRunResult` (remove `requiresApproval`), `Check` interface (add `reaction` field), new `Reaction` type
- **Database**: `check_suites` table generalized or replaced with unified execution records for all stages
- **API**: Approval endpoints (`/approve`, approval state in issue responses) remain but are driven by `user-approval` check resolution, not gate logic
- **Specs affected**: `pipeline-model`, `workflow-definition`, `ralph-task-execution` require delta specs
