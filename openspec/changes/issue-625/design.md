## Context

Issue 625 addresses a failure in the built-in `mohist/local` and `mohist/github-pr` build stages. They currently execute the aggregate `${{ vars.ci.verify }}` script as one `core/script` task with a `300000` ms timeout. If an earlier command passes and a later command causes the aggregate task to time out, the control plane receives only one failed task result. The completed checks are not durable workflow evidence, so recovery repeats work and can approach downstream side effects more than once.

The existing system already provides the needed durability boundaries: `StageRun` and `TaskRun` are persisted with the workflow run, `WorkflowRun.NextWork` claims work serially, reports are fenced by workflow/task/work/runner identity, and the Runner `WorkResultJournal` persists returned results before retrying delivery. `core/script` and `runCommand` already support process-group termination and per-command timeouts. The stakeholders are workflow users, status/event consumers, the Server workflow control plane, and Runner maintainers. Constraints are the six exact verification commands, unchanged strictness, ordered execution, no resource-profile or Runner-slot change, and no aggregate verification timeout.

## Goals / Non-Goals

**Goals:**

- Replace aggregate verification in both built-in profiles with six ordered, independently reportable lanes:
  1. `npm ci`
  2. `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`
  3. `npm run typecheck -w packages/web`
  4. `npm run test:run -w packages/web`
  5. `npm run typecheck -w packages/runner`
  6. `npm run test:run -w packages/runner -- --no-file-parallelism`
- Give every lane a positive, finite `core/script` timeout and preserve the exact command and test scope.
- Persist each lane's stable identity, order, configured budget, terminal outcome (`pass`, `fail`, or `timeout`), and diagnostics in the workflow state; expose the results through the existing workflow status projection.
- Resume at the first lane without a durable pass, preserving earlier passes and preventing later lanes from starting early.
- Keep build-stage advancement and existing push, review, PR, and merge ordering behind the all-lanes-pass gate.

**Non-Goals:**

- Running lanes in parallel or changing Runner slot policy, resource containment, test thresholds, skips, allowlists, or test scope.
- Adding a general-purpose verification-lane DSL or changing verification semantics for arbitrary user-defined workflows.
- Redesigning generic workflow recovery, artifact handling, or downstream Git/GitHub Actions.
- Increasing the old aggregate timeout or retaining an enclosing timeout around all six lanes.

## Decisions

1. **Use ordinary workflow tasks as the lane boundary.**

   Each built-in build stage will replace `verify` with six stable `core/script` task definitions, for example `verify-install`, `verify-dotnet`, `verify-web-typecheck`, `verify-web-tests`, `verify-runner-typecheck`, and `verify-runner-tests`. The task `run` values contain the exact commands above, and each task has its own literal positive `with.timeout`. The aggregate `${{ vars.ci.verify }}` task and its `300000` ms timeout are removed from this gate.

   This uses the existing task ordering, claim, report, and retry machinery. A single new `verification-lanes` Action was considered, but it would have to duplicate durable child identities, partial-result persistence, ordered scheduling, timeout attribution, and recovery fencing inside the Runner. Six workflow tasks make those facts control-plane state instead.

2. **Represent lane evidence as additive task-attempt state plus a derived status view.**

   Add optional verification metadata to each recognized built-in lane `TaskRun` attempt: stable lane ID, lane order, configured budget in milliseconds, outcome, and diagnostic/error data. The task's existing `TaskRunStatus` remains `Completed` or `Failed`; `timeout` is a lane outcome, not a new generic task status. A shared server-side lane catalog identifies the six built-in IDs and their order, while the profile YAML remains authoritative for the command and timeout declaration.

   When a report is applied, the Server classifies a successful script report as `pass`, a normal script failure as `fail`, and `error.code=timeout` as `timeout`. It stores the outcome and diagnostics in the same state commit as the normal task transition and recovery handling. The existing `TaskRun.Id` and its `WorkId` remain the durable attempt identity, so a retry creates a new attempt for the same lane without replacing the old evidence.

   Extend the existing workflow status model with an additive verification-lane projection on the build stage. It reports all six lanes, including pending/missing state, order, budget, current outcome, attempt identity, and failure or timeout diagnostics. No new event stream is required for correctness; existing task events remain available and the status projection is the authoritative current view.

   A separate lane table or a new workflow-definition syntax was considered and rejected. The task attempts already have the required lifetime and persistence boundary, and a built-in catalog avoids parser, serializer, and migration costs for a construct that is not needed by user profiles.

3. **Keep timeout execution and reporting lane-scoped.**

   `core/script` continues to pass the lane's `timeout` to `runCommand`, which kills the command process group and returns the structured timeout outcome. The action maps that result to the existing failed work envelope with `error.code=timeout`; the configured budget and command output are retained on the lane attempt. No enclosing timeout is added around the build stage or the six tasks.

   A new generic `TaskReportStatus.Timeout` or a separate runner protocol was considered, but it would force unrelated workflow consumers to understand a third task status. Classifying the existing action error at the verification boundary preserves the current task/report contract while exposing the required third lane outcome.

4. **Make the build gate explicitly require all lane passes.**

   Stage advancement will retain the existing serial task behavior and additionally require the built-in verification lane catalog to contain six durable `pass` outcomes before the build stage can complete. A lane that is pending, missing, failed, or timed out keeps the gate closed. `NextWork` exposes only the first pending lane or its recovery work, so later verification tasks cannot be claimed out of order.

   Checking only whether tasks are terminal was considered insufficient because a failed lane may have completed a recovery helper task, and a generic task state does not distinguish a timeout from another failure. The explicit lane predicate makes the gate depend on the durable verification evidence.

5. **Recover by creating one new attempt for the first non-passing lane.**

   Existing recovery handling remains the repair boundary: a lane failure or timeout records its result, runs the declared `fix-ci` recovery task when applicable, and retries the failed lane as a new `TaskRun` attempt. Recovery resolution selects the first lane in catalog order whose authoritative outcome is not `pass`; earlier passing attempts are never requeued and later lanes remain pending.

   Orleans grain serialization plus the failed attempt's stable task/work identity provide idempotency for repeated recovery requests. If a retry attempt already exists, is running, or has passed, a duplicate request reconciles with that state instead of inserting another active attempt. A late report from the old attempt is rejected as stale by the existing task-run/work/runner fence and cannot overwrite the newer lane result.

   The Runner continues to use `WorkResultJournal`: a returned lane result is journaled before report delivery, report failures are retried, and a fenced work item is not executed again while its exact result is retained. Downstream tasks keep their existing durable identities and are only eligible after the lane gate passes, so repeated recovery cannot duplicate push, review, or merge effects.

6. **Keep the CI contract explicit and testable.**

   Profile-definition tests will inspect both built-in YAML definitions for the six IDs, exact command strings, order, finite per-lane budgets, absence of the old aggregate timeout, and absence of resource or slot-policy settings. Server tests will cover lane classification, durable projection, gate behavior, stale reports, and recovery preservation. Runner tests will cover command timeout propagation, process-group termination, journal fencing, and report retry. End-to-end workflow tests will cover a timeout after an earlier pass, recovery from that lane, and one-time downstream execution.

## Risks / Trade-offs

- `[Per-lane budgets are too small for a cold or busy Runner] -> Choose budgets from observed command timings, keep them explicit per lane, and adjust only the affected lane without restoring an aggregate deadline.`
- `[A timeout report races with a recovery attempt] -> Store the original timeout before recovery, fence reports by task/work/runner identity, and treat late reports as stale acknowledgements.`
- `[Task status and lane outcome diverge while repair work runs] -> Update lane metadata and task lifecycle in one workflow commit, and make the build gate read lane outcomes rather than task status alone.`
- `[Runner result persistence or delivery fails after a command returns] -> Retain the exact result in `WorkResultJournal`, block duplicate execution, and retry persistence/reporting before releasing the work fence.`
- `[The aggregate CI variable remains configured with obsolete commands] -> Remove it from the built-in gate, validate literal commands in profile tests, and retain or deprecate the variable only for compatibility during the transition.`
- `[Existing runs have the old aggregate task and no lane history] -> Do not rewrite active task attempts in place; keep legacy runs readable and require an explicit retry/rerun policy for converting an unfinished build.`
- `[Rollback downgrades code while lane runs are active] -> Keep the additive state reader and lane-aware Server deployed until active lane runs drain, or stop/rerun those runs explicitly before reverting the profile and Server behavior.`

## Migration Plan

1. Add the optional lane metadata, classification, gate, status projection, and idempotent recovery handling. Keep deserialization compatible with existing workflow state that has no lane fields.
2. Verify Runner timeout propagation and journal behavior, then deploy the Runner and Server support before enabling new profile definitions.
3. Update `mohist-local.workflow.yaml` and `mohist-github-pr.workflow.yaml` to the six ordered tasks, remove the aggregate `verify` task and enclosing timeout, and update profile contract tests.
4. New workflow runs use six lanes. Runs already initialized with the aggregate task keep their recorded definition/state and are not silently rewritten; operators can finish legacy recovery or explicitly rerun the build stage under the new definition according to the run-control policy.
5. To roll back, restore the previous built-in profile definitions and stop creating new lane runs. Keep the additive fields and readers so stored lane evidence remains readable; do not discard or replay downstream work during rollback.

## Open Questions

- What initial timeout values meet cold-workspace and normal-load requirements for each lane on every supported Runner environment?
- Should `vars.ci.verify` be retained as a deprecated, unused project variable for one release, or removed from the built-in CI variable contract immediately?
- For an already-created run with the aggregate verification task, should the supported operator path be legacy retry only, explicit build-stage rerun, or a one-time state migration before rollout?
