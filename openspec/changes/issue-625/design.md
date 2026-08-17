## Context

The built-in `mohist/local` and `mohist/github-pr` profiles currently model build verification as one `core/script` task whose command is the aggregate `vars.ci.verify` script and whose timeout is `300000` ms. The Runner can already enforce a timeout while executing a command, but the workflow only receives one task result. A timeout therefore loses which checks already passed, and normal recovery replays the whole sequence.

Workflow task attempts are already durable in `TaskRun`, are claimed serially by `WorkflowRun.NextWork`, and are reported with a `(workflowRunId, workId, taskRunId, runnerId)` identity. The Runner's `WorkResultJournal` also persists a returned result before reporting it and retries failed result delivery. This change builds on those boundaries. The stakeholders are workflow users waiting for builds, workflow/status projections, the Server control plane, and Runner maintainers. The main constraints are the six exact commands and existing strict thresholds, no parallel verification, no resource-profile or slot-policy change, and no aggregate timeout.

## Goals / Non-Goals

**Goals:**

- Represent verification in both built-in profiles as six ordered `core/script` tasks with stable lane IDs and finite per-lane budgets.
- Preserve these commands exactly and run them in order: `npm ci`; `dotnet test Mohist.sln --nologo -m:1 -p:UseSharedCompilation=false`; `npm run typecheck -w packages/web`; `npm run test:run -w packages/web`; `npm run typecheck -w packages/runner`; and `npm run test:run -w packages/runner -- --no-file-parallelism`.
- Persist lane identity, order, configured budget, attempt identity, pass/fail/timeout outcome, and diagnostics so status survives grain reloads.
- Resume recovery at the first lane whose latest durable outcome is not `pass`, without re-running earlier passing lanes or starting later lanes prematurely.
- Keep downstream push, review, PR, and merge tasks behind the existing build-stage boundary and make their task identities idempotent under replay.

**Non-Goals:**

- Running lanes in parallel, changing Runner slot policy, adding resource containment, or increasing one global timeout.
- Changing test scope, failure thresholds, allowlists, skips, or the semantics of the six commands.
- Adding a general-purpose lane DSL or changing verification for user-defined workflows outside the built-in profiles.
- Redesigning unrelated workflow recovery, artifact, or downstream side-effect actions.

## Decisions

1. **Use six ordinary serialized workflow tasks as the lane boundary.** The profiles will replace `verify` with stable task definitions such as `verify-install`, `verify-dotnet`, `verify-web-typecheck`, `verify-web-tests`, `verify-runner-typecheck`, and `verify-runner-tests`. Each task has one command and one literal finite `timeout` input. The profile owns the exact command contract; the aggregate `vars.ci.verify` value is no longer used for this gate. This keeps ordering in the existing `NextUnclaimedTask`/stage machinery and avoids a compound Action that would need to recreate claiming, reporting, and recovery semantics.

   An alternative is one new `verification-lanes` Action that invokes all commands and emits child results. That would preserve one workflow task but would put durable child identity, partial completion, and retry fencing inside the Runner, duplicating the control plane. Six tasks make each lane independently claimable and observable with no new scheduling model.

2. **Store lane evidence as additive task-attempt metadata, with a derived lane projection.** `TaskRun` will retain a stable lane ID and order, the resolved budget, terminal outcome (`pass`, `fail`, or `timeout`), attempt ID, and diagnostic/error data. The existing `TaskRun.Id` is the recovery-attempt identity; all attempts for a lane remain in the stage history. A `VerificationLaneView` status projection will group those attempts by lane ID and expose the latest authoritative outcome while retaining prior diagnostics. The projection is derived from durable run state rather than becoming a second mutable source of truth.

   The Server will record the lane outcome before applying normal task completion/recovery logic. This matters when an Action schedules a recovery task: the generic task may be marked complete so the recovery task can run, but the original lane attempt must still be visible as failed or timed out. Existing task and workflow events remain emitted, with an additive lane-result event or equivalent read-model update committed in the same grain save.

   A new generic workflow-definition field for lane metadata was considered and rejected. The built-in lane catalog can be validated from the six stable task IDs and their command/timeout declarations, avoiding parser, schema, and migration costs for a construct that user profiles do not need.

3. **Make timeout a lane-scoped wire fact, not runner loss.** `core/script` continues to use `runCommand`'s per-command timer and process-group termination. The Runner result envelope will carry the optional fired `timeoutMs` alongside the existing `timeout` error code. The Server translator maps a successful result to `pass`, a non-timeout error to `fail`, and the structured timeout result to `timeout`, attaching the lane identity and configured budget from the dispatch. The generic task report status remains compatible with existing task processing; the separate lane outcome supplies the required third state.

   A single `TaskReportStatus.Timeout` was considered, but it would widen the task protocol and force every existing task consumer to understand a third generic status. Keeping timeout as an outcome classification preserves ordinary workflow behavior while making verification status precise.

4. **Gate advancement on the lane catalog, not only generic task status.** The build-stage advancement predicate will require all six required lane IDs to have a durable latest `pass`. Missing, pending, failed, and timed-out lanes keep the build stage blocked. `NextWork` continues to expose only the first pending lane or its recovery work, so later lanes cannot be claimed out of order. The sixth pass then allows the existing downstream task order to continue unchanged.

   Relying only on `TaskRunStatus.Completed` was considered insufficient because recovery scheduling can complete an attempt while its recorded lane outcome is still a failure. The explicit predicate prevents that representation detail from opening the gate.

5. **Recover by adding a new attempt for the first non-passing lane.** Recovery resolves the ordered lane catalog, selects the first lane whose current outcome is not `pass`, and creates a new attempt for that lane after any configured repair task. Earlier passing attempts are retained and are not requeued. Later lanes remain pending until the recovery attempt and all preceding lanes pass. The existing `fix-ci` recovery behavior can be attached to each lane without creating downstream tasks.

   The grain's task-run ID, work ID, runner ID, and attempt identity remain the authoritative fence. A duplicate recovery request or duplicate report finds the existing attempt and reconciles as already applied/stale instead of inserting another active attempt. The Runner journal continues to hold an exact returned result while persistence or report delivery is unavailable; it must not execute the fenced lane again.

6. **Keep the CI contract and side effects bounded.** The six command declarations and budgets will be covered by built-in profile tests for both profiles. Initial per-lane budgets will be explicit profile values chosen from current command behavior (for example, 300000 ms for dependency and .NET lanes and 120000 ms for the four focused Web/Runner lanes); changing one budget will never create or extend an aggregate deadline. No recovery path adds `push`, review, PR, or merge work. Those tasks remain after the lane gate and retain their existing durable task identities and idempotent Action/report behavior.

## Risks / Trade-offs

- `[Profile and project-variable drift] -> Remove the aggregate variable from the built-in gate, validate the six exact command declarations in profile tests, and document any required environment setup outside the command contract.`
- `[A recovery report races with a late original report] -> Fence reports by task-run/work/runner identity, keep the newer durable attempt authoritative, and treat stale reports as acknowledgements without reopening lanes.`
- `[A returned timeout is lost during local persistence or network failure] -> Preserve the exact result in `WorkResultJournal`, block duplicate execution, retry persistence/reporting, and only release the fence after durable acknowledgement.`
- `[Task status and lane outcome can diverge while repair work is running] -> Record lane outcome and diagnostics atomically with the task transition, and make the build gate read lane outcomes rather than task status alone.`
- `[Existing runs contain the old aggregate verify task] -> Do not rewrite a running attempt in place. Provide a controlled build rerun/migration path for an unstarted or failed legacy verification task and keep legacy records readable during rollout.`
- `[A lane budget is too small for a real environment] -> Tune the individual lane budget from observed command duration and diagnostics; never compensate with a new full-suite timeout or reduced checks.`

## Migration Plan

1. Add the optional timeout/outcome fields and Server projections in a backward-compatible form. Old Runner results remain ordinary failures; new Runner results preserve timeout details.
2. Deploy the Runner changes that propagate structured script timeouts and retain exact results through the existing journal/report retry path.
3. Update both built-in profiles and their CI contract to the six task declarations, then update profile, translator, workflow-domain, Runner-action, and end-to-end recovery tests.
4. New build stages use the lane catalog. A stage already executing the legacy aggregate task is left intact; operators recover it by rerunning the build stage under the new profile rather than mutating a live task or discarding its evidence. A pending legacy task may be expanded only before it is claimed, through an explicit compatibility migration that creates the six ordered tasks once.
5. To roll back, restore the prior profile definitions and stop creating new lane stages. Keep the additive fields and projections readable, because removing stored lane outcomes would lose recovery evidence. Existing lane runs can be stopped or completed under their recorded identities without replaying downstream effects.

## Open Questions

- Confirm the final per-lane budget values against cold-workspace timing for the supported Runner environments; the design requires finite independent values but the specifications do not mandate exact numbers.
- Confirm whether the existing project-level CI variable should be deleted immediately or retained as a deprecated, unused compatibility value during one release.
- Decide whether the lane result should be exposed only through the existing workflow status projection or also through a dedicated workflow event stream entry for clients that consume events directly.
