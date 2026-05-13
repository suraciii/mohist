## Why

The workflow data model now has relational WorkflowRun tables, but the business rules that decide stage, task, check, approval, repair, and integration outcomes are still split across runners, services, issue fields, and compatibility projections. This change is needed so an issue pipeline has one authoritative consistency boundary: runners execute external work, while the WorkflowRun aggregate decides valid state transitions and persists them transactionally.

## What Changes

- Introduce `WorkflowRun` and `StageRun` domain objects as the only business entry point for starting workflows, starting stages, completing tasks, recording checks, scheduling fix tasks, handling approvals, advancing stages, and completing or failing runs.
- Replace scattershot `WorkflowRunService` lifecycle writes such as `setStagePassed`, `setStageFailed`, and `setStageAwaitingApproval` with repository/application-service flows that load the aggregate, invoke domain methods, save one transaction, and update projections.
- Remove `StageRunResult.nextStage` as the mechanism for issue progression; next-stage decisions come from the aggregate's `stageOrder`, and issue stage/status updates become projections.
- Narrow runner responsibility to executing task/check side effects and reporting results; task order, task/check boundary enforcement, check failure classification, repair scheduling, approval state, stage completion, and workflow completion move into `WorkflowRun`/`StageRun` rules.
- Materialize Build tasks from `tasks.json` into runtime `workflow_tasks` at Build start while keeping `tasks.json` as the Plan artifact and Build input, not the runtime source of truth.
- Treat Integrate work as first-class aggregate state, including ordered spec-sync/archive/merge tasks, delivery metadata such as landed sha, a freeze point after merge, and non-repairable post-merge health failures.
- Keep `stage_executions`, `stage_states`, check suites, logs, checkpoints, and session streams as evidence, resume cursors, or compatibility projections rather than business state authorities.
- Preserve P0 scope by avoiding full event sourcing, workflow DSL work, complete attempt-history tables, or removal of `tasks.json` as a design artifact.

## Capabilities

### New Capabilities


### Modified Capabilities

- workflow-run
- workflow-engine
- workflow-definition
- pipeline-model
- http-api
- web-ui

## Impact

- Domain/application layer: add aggregate-oriented workflow runtime objects and a coordinating application service for loading, deciding, saving, and projecting WorkflowRun state.
- Persistence: refactor `packages/cli/src/db/workflow-run-repo.ts` and `packages/cli/src/services/workflow-run-service.ts` so `workflow_runs`, `workflow_stage_runs`, `workflow_tasks`, and `workflow_checks` are saved as one aggregate transaction rather than through public lifecycle CRUD shortcuts.
- Workflow execution: update `WorkflowEngine`, `BaseStageRunner`, stage runners, `StageRunResult`, task result reporting, check result reporting, fix-task handling, approval handling, and Integrate execution so they report facts to the aggregate instead of directly deciding stage pass/fail or next stage.
- Artifacts and Build runtime: keep Plan `tasks.json` generation and reviewability, but make Build runtime progress and failure evidence come from materialized `workflow_tasks`.
- Integrate behavior: record spec sync, archive, merge, landed sha, freeze state, and post-merge health failures as WorkflowRun task/check facts visible to API/UI.
- Projections and compatibility: update issue stage/status, stage-state API, legacy `stage_executions`, `stage_states`, check suites, workflow logs, checkpoints, and recovery paths to consume or mirror aggregate decisions without becoming sources of truth.
- API/UI: ensure active WorkflowRun and stage-state responses expose aggregate-backed current progress, including task/check separation, approval state, failure reasons, runtime-added fix tasks, and Integrate delivery side effects.
- Tests: add or adjust unit and integration coverage for aggregate invariants, repository transactions, stage advancement, task/check ordering, approval consistency, Build task materialization, and Integrate freeze/post-merge-health behavior.
