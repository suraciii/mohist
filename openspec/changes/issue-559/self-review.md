# Self-Review: issue-559

## Must-Fix Findings

### 1. HIGH - Post-acceptance activation can still leave two incompatible owners

The revised order now prepares and verifies nonclaimable AgentJob and
AgentSession participants before Workflow acceptance, which fixes the original
missing-participant window (`design.md:197-225`; `specs/workflow-agent-action/spec.md:71-84`).
After Workflow acceptance, however, activation is still a multi-participant
sequence that makes the Job claimable and submits it to admission
(`design.md:227-238`). The only stated definitive-failure action is
`ApplyAgentHandoffActivationFailureAsync`, which fails the Workflow TaskRun; the
plan does not require it to fence or abort an AgentJob that was already
activated or submitted, hide a partially activated Session, or release an
Agent concurrency permit.

This matters because the current coordinator promotes Session and Job with
separate calls and submits the Job afterward
(`packages/server/src/Mohist.Server/Agent/Grains/AgentLaunchCoordinatorGrain.cs:710-725`).
Submission durably marks the Job ready and immediately enters admission
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:669-690`), and
an admitted Job can be claimed independently
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:234-279`). If
one activation command succeeds and a later activation step definitively
fails, the proposed Workflow failure operation can therefore terminalize the
TaskRun while the accepted Job remains claimable or executing.

That still violates the issue criteria that one invocation have mutually
locatable Job/Session/Input/Turn lineage and an observable, stable lifecycle
and result. T-001 must define durable per-participant activation acknowledgements
and either complete the same activation or compensate every already-activated
participant before applying the Workflow failure boundary. It must test failure
after each activation/submission boundary, including permit release and a Job
claimed before coordinator recovery (`tasks.json:14-16`).

### 2. HIGH - Invalid AgentJob reports have no Runner settlement contract

The plan now carries `WorkflowAgentFinalizationRequest` through the existing
AgentJob `/report` request and defines validation, stale replay, and payload
conflict (`design.md:493-526`; `specs/workflow-agent-action/spec.md:131-140`).
It says a missing or mismatched envelope returns
`invalid-workflow-finalization` without changing the Job, but it does not state
the HTTP/response acknowledgement or whether the Runner must settle, retry, or
retain that result.

That omission conflicts with the current boundary. AgentJob report outcomes
are returned as HTTP 200 even when `acknowledged=false`
(`packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:226-235`). The Runner
throws only for non-2xx responses
(`packages/runner/src/server/connection.ts:106-135`) and removes an
`awaitingAck` report after any non-throwing response without inspecting the
acknowledgement body (`packages/runner/src/runtime/host.ts:758-777`). Under the
plan as written, an invalid Workflow finalization can therefore be discarded by
the Runner while AgentJob remains nonterminal; using a non-2xx response instead
would retry the same definitively invalid body forever unless another durable
recovery owner is specified. `finalization_conflict` has the same unspecified
settlement boundary.

This violates the issue criteria for distinguishable recovery state and a
stable final result, and leaves the previous transport finding only partially
fixed. T-002/T-003 must define the exact response and Runner behavior for valid,
stale, invalid, retryable, and conflicting AgentJob reports, including which
owner retains a durable obligation when the Job does not transition
(`tasks.json:35-37,51-59`).

### 3. HIGH - The finalizer receipt does not fence variable and TaskRun effects

The new `WorkflowAgentArtifactBindReceipt` correctly closes the previously
reported crash window between artifact binding and the higher-level finalizer
receipt (`design.md:564-585`; `specs/workflow-agent-action/spec.md:138,150-152`).
The finalizer still has later cross-store effects with no durable phase,
however. The design records `WorkflowAgentFinalizationReceipt`, then applies
the keyed variable patch and Workflow completion/recovery boundary
(`design.md:577-598`). The receipt contains fingerprints and bound artifact ids,
but no state saying whether either later effect completed.

If the receipt commits first and the process fails before `setVars` or TaskRun
application, replay returns the existing receipt as `Accepted`/`Stale` and can
skip the missing effects. If the effects commit first, the plan does not state
how a replay reconstructs and completes the absent finalizer receipt. This is a
real boundary in the current code: variable mutation is an independent database
operation (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowRunVariablesStore.cs:33-72`),
while TaskRun has no finalizer-key marker
(`packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:20-44`) and
normal task application is committed separately by WorkflowGrain
(`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:124-139`).

This violates the issue criterion that the Action return one stable result to
Workflow and the plan's required exactly-once Workflow completion, variable,
and recovery semantics (`specs/workflow-agent-action/spec.md:140-152,185-194`).
T-002/T-003 must provide an atomic final receipt/TaskRun commit plus a keyed
variable step, or a durable phase machine that resumes each effect. Tests must
crash after variable patch, after TaskRun application, and before/after final
receipt persistence (`tasks.json:35,54-59`).

This problem existed before the latest fix but was missed because the previous
correctness check stopped at the first irreversible artifact bind and treated
the proposed keyed variable API plus the phrase "applies once" as a complete
fence. Re-tracing the new bind receipt through every subsequent commit exposes
that the finalizer receipt itself does not record those remaining phases.

## Previous Finding Dispositions

- **Preflight rejection replay fence: fixed.** The coordinator now persists the
  fingerprint, rendered input, and `preflight_pending` before mutable preflight,
  persists the original error as `rejection_pending`, and resumes Workflow
  failure application without re-resolving a recovered Agent
  (`design.md:150-188`; `specs/workflow-agent-action/spec.md:20-39`).
- **Workflow acceptance before participant promotion: partially fixed.** The
  complete nonclaimable participant set is now verified before acceptance, but
  Finding 1 is the remaining partial-activation/compensation gap after
  acceptance.
- **Missing Runner-to-AgentJob finalization transport: partially fixed.** The
  optional typed `/report` field, validation, direct-Agent boundary, and replay
  payload rules now exist, but Finding 2 is the missing acknowledgement and
  Runner settlement contract at that route.
- **Artifact bind/finalizer receipt crash window: fixed at the reported
  boundary.** The invocation-keyed bind receipt is committed atomically with
  artifact creation and pending-upload deletion and can recover the bound ids.
  Finding 3 is a distinct later finalizer-phase gap.

## Observations

### 4. MEDIUM - `session` and `timeout` normalization remain implementation choices

The plan deliberately creates a new physical Session per accepted attempt, but
still leaves timeout range, default normalization, and report-deadline margin
open (`design.md:341-345,750-752`). T-002 requires timeout/default tests, so the
implementation must settle these values. This does not independently violate
the issue's six acceptance criteria and does not affect the verdict.

### 5. LOW - The read route shape remains open

The design leaves embedded-versus-dedicated invocation reads open
(`design.md:741-746`; `tasks.json:70-88`) while explicitly requiring stable
cross-links and excluding internal Runner, binding, path, prompt, transcript,
and provider facts (`design.md:625-648`). Either route can satisfy the issue, so
this remains a non-blocking API choice.

## Dimension Checks

- **Issue goals and acceptance criteria:** checked. The current issue body was
  read before the artifacts. All six criteria remain represented in the
  proposal, twelve normative requirements, and T-001 through T-005.
- **Coverage:** checked, no unmapped issue goal found. Findings 1-3 are
  incomplete failure contracts inside otherwise covered lineage, lifecycle,
  and stable-result requirements.
- **Correctness:** failed. Partial activation can leave execution alive after
  Workflow failure, an invalid terminal report can be dropped without a Job
  transition, and finalizer replay can skip or repeat Workflow effects.
- **Current codebase consistency:** failed at the three cited boundaries. The
  overall plan correctly reuses AgentRefResolver, AgentReadinessService,
  AgentJob admission, the existing Runner report route, and Workflow artifact
  storage, but it does not account for their current independent commit and
  acknowledgement behavior.
- **Task breakdown and verifiability:** failed. The dependency chain remains
  coherent (`T-001 -> T-002 -> T-003 -> T-004 -> T-005`), but its tests do not
  require post-first-activation compensation, current Runner handling of every
  report acknowledgement, or crashes around variable/TaskRun/final-receipt
  ordering.

## Verification Notes

- This was a read-only issue, artifact, and current-code contract review; no
  product tests or build gates were run.
- Static evidence came from the required current `mo issue view` read, a
  supplemental JSON body read, all plan artifacts, the previous self-review,
  and the cited Server and Runner sources.
- Only `openspec/changes/issue-559/self-review.md` was modified.

## Verdict

FAIL. Two previous findings remain incomplete at their convergence boundaries,
and the finalizer still lacks a durable fence for all Workflow-owned effects.
The plan is not ready to build until Findings 1-3 are resolved.

<promise>FAIL</promise>
