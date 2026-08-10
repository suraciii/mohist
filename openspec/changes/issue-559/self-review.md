# Self-Review: issue-559

## Review Context

This is a re-review. The previous review identified three must-fix gaps in
handoff rejection, terminal delivery, and artifact binding. The current issue
was read first with:

```text
mo issue view 559 --project proj_f6c141d63b6243bfbb481737b2243b87
```

The issue is in progress, P1, high risk, and its body defines six acceptance
criteria: Agent selection and permitted task context; mutually locatable
Job/Session/Input/Turn lineage; immutable Agent configuration; shared
readiness/workspace/concurrency rules; queued/executing/terminal/recovering
states; and stable results without transcript parsing. The plan contains twelve
normative requirements mapped across T-001 through T-005.

## Must-Fix Findings

### 1. HIGH - Preflight rejection is not durably fenced for replay

The revised rejection operation correctly applies a Workflow failure boundary
(`design.md:163-174`; `specs/workflow-agent-action/spec.md:23,37-39`). However,
the stated coordinator order resolves Agent, readiness, and workspace before
invoking the coordinator (`design.md:183-194`), while the coordinator is where
the `(projectId, commandId)` fingerprint and acknowledgement are persisted
(`design.md:146-181`). The same pre-coordinator ordering is repeated at
`design.md:299-302`.

If an unavailable Agent or readiness failure causes
`ApplyAgentHandoffRejectionAsync` to succeed and the HTTP response is lost, the
plan does not require a durable rejected acknowledgement to have been stored.
A replay of the same command can rerun preflight after the Agent becomes
available and accept a new Job instead of replaying the original rejection.
That violates the handoff requirement that the same command and fingerprint
replay the original acknowledgement (`specs/workflow-agent-action/spec.md:23,33-39`)
and can turn a definitive pre-acceptance rejection into accepted Agent
execution.

The plan must persist the command and terminal rejection outcome before or as
part of preflight handling, or route every preflight failure through a durable
coordinator rejection record. The record must retain the original error and
make duplicate Workflow failure application idempotent.

### 2. HIGH - Workflow acceptance precedes Agent participant promotion without a compensation path

The participant sequence records the Workflow lineage before promoting the
provisional AgentJob and AgentSession (`design.md:183-204`). Separately, the
design says that acceptance atomically records `WorkflowAgentInvocation`, marks
the TaskRun `Running`, clears the Runner assignment, and removes the Workflow
dispatch obligation (`design.md:250-255`).

There is no named operation for the failure case after that Workflow commit but
before AgentJob/AgentSession promotion, and `WorkflowAgentInvocation` has no
provisional or recovering materialization state (`design.md:227-243`). A
promotion failure or coordinator loss can therefore leave a Running TaskRun
with an accepted invocation pointing at hidden or absent participants, no
claimable AgentJob, and no active handoff on which the rejection operation can
act. That violates the issue's lineage criterion and T-001's requirement for
one mutually locatable Job/Session/Input/Turn lineage for every accepted attempt
(`tasks.json:14-16`; `specs/workflow-agent-action/spec.md:71-84`).

The plan must either promote and durably verify all provisional participants
before accepting the Workflow lineage, or define a durable compensation and
recovery state that can reject or finish the accepted invocation without
leaving the TaskRun stuck.

### 3. HIGH - The typed finalization request has no specified Runner-to-AgentJob transport

The design now correctly defines `WorkflowAgentFinalizationRequest` and requires
it in `PendingWorkflowTerminalDelivery` (`design.md:362-394,457-504`; the
corresponding spec is `specs/workflow-agent-action/spec.md:132-154`). It does
not define how the Runner completion adapter transmits that request to
AgentJob.

The current boundary cannot carry the required fields: the server `WorkResult`
contains status, output, artifact upload ids, and error but no
`capturedOutputs`, `setVars`, or typed Workflow finalization field
(`packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs:256-273`),
and `RunnerReportRequest` exposes the same limitation
(`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:939-951`). The Runner's
`ServerConnection.report` also serializes only the generic result fields
(`packages/runner/src/server/connection.ts:106-129`).

T-002 says the adapter prepares the typed envelope and T-003 says AgentJob
persists it, but neither task names a report field, separate endpoint, request
acknowledgement, or retry/idempotency rule connecting those two steps. An
implementation can therefore produce the envelope in memory while dropping
`capturedOutputs` or `setVars` before terminal delivery, violating the Workflow
artifact/variable side-effect criteria and the stable-result criterion
(`tasks.json:30-37,50-59`; `specs/workflow-agent-action/spec.md:136-146`).

The plan must specify one canonical Runner-to-AgentJob wire contract, including
its direct-Agent compatibility boundary, validation, acknowledgement, and
replay behavior.

### 4. HIGH - Artifact binding has a crash window before the finalizer receipt

The revised plan names `BindAgentInvocationAsync` and requires it to create
visible artifacts and remove pending uploads in an idempotent transaction
(`design.md:481-500`; `specs/workflow-agent-action/spec.md:136,156-158`). It then
explicitly records the `WorkflowAgentFinalizationReceipt` after that bind
operation (`design.md:493-504`).

If the bind transaction commits and the process fails before the finalizer
receipt is persisted, the pending uploads are gone and the visible artifact
rows exist, but there is no stated durable record from which a replay can
recover the bound artifact ids. A retry can see no pending upload and fail, or
create duplicate visible rows if it treats the upload as new. This violates the
requirements that a repeated finalizer payload not repeat artifact binding and
that bound ids remain in the receipt (`tasks.json:35,54-57`; `specs/workflow-agent-action/spec.md:138,144-146`).

The bind result needs its own durable finalizer-key fence/receipt, a unique
invocation-keyed artifact binding that can be replayed after the crash, or an
atomic transaction covering the bind outcome and finalizer receipt. The plan
must define which mechanism owns this boundary.

## Previous Finding Dispositions

- Previous finding 1, missing Workflow transition for definitive handoff
  rejection: **fixed in the direct contract**. `ApplyAgentHandoffRejectionAsync`
  now verifies lineage, applies failure/recovery, and is required before the
  rejected acknowledgement (`design.md:163-174`; `spec.md:23,37-39`). Finding 1
  above is a separate replay-fence gap exposed by tracing the revised order.
- Previous finding 2, incompatible terminal-delivery contracts: **fixed**. The
  plan now has one `PendingWorkflowTerminalDelivery`, one typed finalization
  request, and one `Accepted/Stale/Retry/Conflict` operation
  (`design.md:362-394`; `tasks.json:50-59`). Finding 3 above is the distinct
  upstream Runner-to-AgentJob transport gap.
- Previous finding 3, no explicit artifact bind operation: **partially fixed**.
  `BindAgentInvocationAsync` and its invocation-keyed identity are now named
  (`design.md:481-500`; `spec.md:136,156-158`), but Finding 4 remains an
  exact-once crash-safety gap at the bind/receipt boundary.

## Observations

### 5. MEDIUM - `session` and `timeout` semantics remain open

The plan intentionally creates a new physical Session per accepted attempt but
leaves timeout range, default normalization, and delivery margin as open
questions (`design.md:309-313,651-662`). Existing OpenCode and Pi paths do not
currently normalize all of these inputs identically. This is non-blocking for
the issue's six acceptance criteria, but T-002 should settle it before
implementation.

### 6. LOW - Read route shape and privacy boundary remain implementation choices

The design leaves embedded-versus-dedicated invocation reads open
(`design.md:651-659`; `tasks.json:70-88`) and carries workspace metadata
internally while requiring public projections to omit raw paths and Runner
details (`design.md:330-341,540-563`). This is an observation because the
projection requirement is explicit; the chosen route and sanitization boundary
should nevertheless be fixed in T-004.

## Dimension Checks

- **Issue goals and acceptance criteria:** checked. The issue body was read
  before the artifacts, and all six criteria are represented in the proposal,
  twelve spec requirements, and T-001 through T-005.
- **Coverage:** checked, no unmapped goal found. Findings 1-4 are completeness
  defects inside covered handoff, lineage, finalization, and artifact criteria.
- **Correctness:** failed. Findings 1-4 permit replay, promotion, transport,
  or crash paths that can violate the issue's stable lineage and side-effect
  guarantees.
- **Current codebase consistency:** checked with issues recorded. The plan
  reuses the existing AgentRefResolver, readiness service, AgentJob ledger,
  Runner admission, and artifact store boundaries, but Findings 3-4 must define
  the extensions to the current report and artifact contracts.
- **Task breakdown and verifiability:** failed. The dependency graph is
  coherent (`T-001 -> T-002 -> T-003 -> T-004 -> T-005`) and focused tests are
  listed, but no task explicitly verifies preflight rejection replay,
  post-acceptance promotion failure, finalization-envelope delivery, or
  bind/receipt crash recovery.

## Verification Notes

- No product tests or build gates were run; this was a read-only issue,
  artifact, and current-code contract review.
- Static evidence came from the current `mo issue view` output, all four plan
  artifacts, the prior self-review, and the cited Server/Runner sources.
- Only this `self-review.md` file was modified.

## Verdict

FAIL. The direct text fixes from the prior review are present, but the plan is
not ready to build until the four must-fix failure paths above have explicit
durable contracts and focused verification.

<promise>FAIL</promise>
