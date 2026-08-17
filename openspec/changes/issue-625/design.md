## Context

Issue 625 addresses a failure in the built-in `mohist/local` and `mohist/github-pr` build stages. They currently execute the aggregate `${{ vars.ci.verify }}` script as one `core/script` task with a `300000` ms timeout. If an earlier command passes and a later command causes the aggregate task to time out, the control plane receives only one failed task result. The completed checks are not durable workflow evidence, so recovery repeats work and can approach downstream side effects more than once.

The existing system already provides the needed durability boundaries: `StageRun` and `TaskRun` are persisted with the workflow run, `WorkflowRun.NextWork` claims work serially, reports are fenced by workflow/task/work/runner identity, and the Runner `WorkResultJournal` persists returned results before retrying delivery. `core/script` and `runCommand` already support process-group termination and per-command timeouts. The current run binding persists only profile identity and stage structure, however, while later stage initialization can reload the current profile. This change must add an immutable effective-definition boundary at run binding before lane gating can be correct across a profile rollout. The stakeholders are workflow users, status/event consumers, the Server workflow control plane, and Runner maintainers. Constraints are the six exact verification commands, unchanged strictness, ordered execution, no resource-profile or Runner-slot change, and no aggregate verification timeout.

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

   Add optional verification metadata to each recognized built-in lane `TaskRun` attempt: stable lane ID, lane order, configured budget in milliseconds, outcome, and diagnostic/error data. The task's existing `TaskRunStatus` remains `Completed` or `Failed`; `timeout` is a lane outcome, not a new generic task status. A shared server-side lane catalog identifies the six built-in IDs and their order, while the profile definition captured at run binding remains authoritative for the command and timeout declaration.

   When a report is applied, the Server classifies a successful script report as `pass`, a normal script failure as `fail`, and `error.code=timeout` as `timeout`. It stores the outcome and diagnostics in the same state commit as the normal task transition and recovery handling. The existing `TaskRun.Id` and its `WorkId` remain the durable attempt identity, so a retry creates a new attempt for the same lane without replacing the old evidence.

   Extend the existing workflow status model with an additive verification-lane projection on the build stage. It reports all six lanes, including pending/missing state, order, budget, current outcome, attempt identity, and failure or timeout diagnostics. No new event stream is required for correctness; existing task events remain available and the status projection is the authoritative current view.

   A separate lane table or a new workflow-definition syntax was considered and rejected. The task attempts already have the required lifetime and persistence boundary, and a built-in catalog avoids parser, serializer, and migration costs for a construct that is not needed by user profiles.

3. **Keep timeout execution and reporting lane-scoped.**

   `core/script` continues to pass the lane's `timeout` to `runCommand`, which kills the command process group and returns the structured timeout outcome. The action maps that result to the existing failed work envelope with `error.code=timeout`; the configured budget and command output are retained on the lane attempt. No enclosing timeout is added around the build stage or the six tasks.

   A new generic `TaskReportStatus.Timeout` or a separate runner protocol was considered, but it would force unrelated workflow consumers to understand a third task status. Classifying the existing action error at the verification boundary preserves the current task/report contract while exposing the required third lane outcome.

4. **Make the build gate explicitly require all lane passes.**

   Stage advancement will retain the existing serial task behavior and additionally require the built-in verification lane catalog to contain six durable `pass` outcomes before the build stage can complete, but only for a lane-enabled run. At `BindWorkflowRun` time, the coordinator already has the resolved effective `WorkflowDefinition`. It must serialize that complete definition into `BoundWorkflowStart.DefinitionJson`, and the binding participant must persist it in a new nullable, write-once `WorkflowRun.BoundWorkflowDefinitionJson` field in the same initial save. The snapshot includes every stage's task, check, approval, lock, and resource data plus top-level recovery and approval data, including command, timeout, and recovery fields. Use the existing `WorkflowYamlSerializer` JSON representation so the snapshot is independent of the current profile row; include the snapshot in binding idempotency checks.

   `WorkflowStageInitializer` and `WorkflowStageLockCoordinator` must resolve stage data from `BoundWorkflowDefinitionJson` for a run that has it. The resolver may parse the stored snapshot once per call, but it must not call the current profile provider for those stages. A run is lane-enabled only when the snapshot's build stage contains the complete six-task sequence, with the recognized lane IDs in catalog order and each task using `core/script`. The check is made from the definition captured when the run was initialized, never from the currently deployed profile, so a mixed-version rollout cannot change a run's mode or task materialization.

   For a run loaded from pre-issue-625 state with no snapshot, absence is an explicit legacy mode: the lane predicate is not evaluated and no missing lane state is synthesized. The compatibility release must retain the pre-change aggregate definitions for the affected built-in profile IDs and use those definitions when a legacy run later initializes an unmaterialized stage; it must never resolve the post-activation six-lane definition for that run. This read-only fallback preserves existing task attempts and does not rewrite or rerun historical runs. For a lane-enabled run, a lane that is pending, missing, failed, or timed out keeps the gate closed and `NextWork` exposes only the first pending lane or its recovery work. For a legacy run whose bound definition still has the aggregate `verify` task, existing aggregate dispatch, recovery, and stage advancement remain unchanged. In either mode, later verification tasks cannot be claimed out of order.

   Checking only whether tasks are terminal was considered insufficient because a failed lane may have completed a recovery helper task, and a generic task state does not distinguish a timeout from another failure. The explicit lane predicate makes the gate depend on durable verification evidence without making legacy aggregate runs wait for lanes that do not exist.

5. **Recover by creating one new attempt for the first non-passing lane.**

   Every one of the six lane tasks carries the same profile-specific recovery declaration that currently belongs to aggregate `verify`: budget `2`, one unconditional handler that adds `recover:fix-ci` with the profile's existing action, build session, `fix-ci` prompt/options, and expectation fields, followed by `retrySelf: true`. The declaration is present on every lane, so a timeout or ordinary script failure has the same declared repair-and-retry path; no lane relies on an unspecified "when applicable" rule.

   The Runner's recovery conversion can return a scheduling result with outer `status=completed`, `addTasks`, and the original `error`. At the verification boundary, the Server persists the failed lane attempt first and classifies the preserved underlying error: `error.code=timeout` is `timeout`, any other core/script error is `fail`, and only a direct successful lane report with no error is `pass`. A scheduling result with `addTasks` is never a pass. The `recover:fix-ci` task is not in the lane catalog and its result cannot change lane outcome.

   Recovery schedules `recover:fix-ci` before a new retry attempt for the same stable lane ID. The retry gets a new `TaskRun` and `WorkId`, the same lane order and configured budget, the same recovery declaration, and a link to the failed attempt; its recovery budget is decremented by the existing `recoveryRemaining` contract. The original fail or timeout attempt and diagnostics remain in attempt history and in the lane projection while repair runs. If repair or the retry fails, the lane remains non-pass; only a later direct successful retry changes the authoritative lane outcome to `pass`. Earlier passing lanes are never requeued and later lanes remain pending.

   Recovery resolution selects the first catalog lane whose authoritative outcome is not `pass`. Orleans grain serialization plus the failed attempt's `TaskRun`/`WorkId` identity make a recovery request idempotent: if the repair/retry chain for that failed attempt already exists, is running, or has passed, a duplicate request reconciles with it instead of inserting another active chain. A late report from the old attempt is rejected as stale by the existing task-run/work/runner fence and cannot overwrite the newer lane result.

   The Runner continues to use `WorkResultJournal`: a returned lane result is journaled before report delivery, report failures are retried, and a fenced work item is not executed again while its exact result is retained. Downstream tasks keep their existing durable identities and are only eligible after the lane gate passes, so repeated recovery cannot duplicate push, review, or merge effects.

6. **Keep the CI contract explicit and testable.**

   Profile-definition tests will inspect both built-in YAML definitions for the six IDs, exact command strings, order, finite per-lane budgets, the per-lane profile-specific `fix-ci` recovery declaration, absence of the old aggregate timeout, and absence of resource or slot-policy settings. Server tests will cover lane classification including recovery scheduling envelopes, durable projection, gate behavior, stale reports, first-non-passing selection, and recovery preservation. Runner tests will cover command timeout propagation, process-group termination, journal fencing, and report retry. End-to-end workflow tests will cover a timeout after an earlier pass, recovery from that lane, and one-time downstream execution.

## Risks / Trade-offs

- `[Per-lane budgets are too small for a cold or busy Runner] -> Choose budgets from observed command timings, keep them explicit per lane, and adjust only the affected lane without restoring an aggregate deadline.`
- `[A timeout report races with a recovery attempt] -> Store the original timeout before recovery, fence reports by task/work/runner identity, and treat late reports as stale acknowledgements.`
- `[Task status and lane outcome diverge while repair work runs] -> Persist the underlying fail/timeout outcome before applying the Runner's recovery scheduling envelope; keep recovery helpers outside the lane catalog, and make the build gate read lane outcomes rather than the outer task status alone.`
- `[Runner result persistence or delivery fails after a command returns] -> Retain the exact result in `WorkResultJournal`, block duplicate execution, and retry persistence/reporting before releasing the work fence.`
- `[The aggregate CI variable remains configured with obsolete commands] -> Remove it from the built-in gate, validate literal commands in profile tests, and retain or deprecate the variable only for compatibility during the transition.`
- `[Existing runs have the old aggregate task and no lane history] -> Select lane behavior from the immutable bound definition. Keep legacy runs on their existing aggregate path, do not synthesize missing lane state, and do not rewrite, migrate, or rerun historical task attempts as part of this change.`
- `[Rollback downgrades code while lane runs are active] -> Keep the additive state reader and lane-aware Server deployed until active lane runs drain, or stop/rerun those runs explicitly before reverting the profile and Server behavior.`

## Migration Plan

1. Add the immutable run-bound definition snapshot before lane metadata, classification, gate, status projection, and idempotent recovery handling. At binding, serialize the complete effective definition into `BoundWorkflowStart.DefinitionJson` and persist it as `WorkflowRun.BoundWorkflowDefinitionJson`; keep the field optional for old state, write it only during initial binding, and make snapshot-backed stage and lock resolution bypass hot reload. The legacy branch for old state without a snapshot must use the retained pre-change aggregate definitions for the affected built-in profiles and must not synthesize lane state.
2. Verify Runner timeout propagation and journal behavior, then deploy the compatibility-aware Runner and Server support while the old profile definitions are still active. Every run bound during this window receives its immutable snapshot. Existing runs without a snapshot are explicitly legacy and remain on the retained aggregate path; no run can acquire a blocking set of missing lanes merely because the Server was upgraded.
3. Update `mohist-local.workflow.yaml` and `mohist-github-pr.workflow.yaml` to the six ordered tasks, remove the aggregate `verify` task and enclosing timeout, and update profile contract tests. New runs initialized after profile activation become lane-enabled from their stored snapshots.
4. Runs already initialized with the aggregate task, including pre-existing runs with no snapshot, keep their legacy aggregate behavior through the compatibility definition source. This change does not rewrite their tasks, synthesize lane history, migrate their state, or rerun them; it remains a read-only compatibility path for historical runs.
5. To roll back, restore the previous built-in profile definitions for new runs but keep the compatibility-aware Server readers, retained legacy definitions, and lane branch deployed until existing lane-enabled runs drain. Do not mutate persisted run definitions or replay downstream work during rollback.

## Open Questions

- What initial timeout values meet cold-workspace and normal-load requirements for each lane on every supported Runner environment?
- Should `vars.ci.verify` be retained as a deprecated, unused project variable for one release, or removed from the built-in CI variable contract immediately?
