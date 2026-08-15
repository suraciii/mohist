# Self-Review — issue-559 plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/`)

Reviewed against issue #559 (可复用的 Agent Workflow Action), stage `plan`, including the
2026-08-14 supervisor audit comment. First review: full sweep. Evidence gathered by reading
the issue body/ACs first, then the artifacts, then the current codebase on this worktree.

## Dimensions (first review, explicit verdicts)

**Issue goals & AC coverage — checked, no issue.** Each acceptance criterion maps to a
spec requirement and at least one task:

- AC1 (select configured Agent, pass task/allowed context) → cutover requirement
  ("`mohist/agent` dispatch cutover with an unchanged input contract", scenarios
  "dispatches through the handoff" / "Task input stays unchanged") + T-005.
- AC2 (mutual location of Job/Session/Input/Turn) → "Stable invocation status and
  cross-surface lineage" (both directions as scenarios) + T-005 (linkage persistence,
  reciprocal labels) and T-006 (read-surface tests).
- AC3 (config frozen per execution; edits affect only new invocations) → "Durable handoff
  admission freezes one execution per work attempt" + "Agent edits after acceptance do not
  affect the invocation" + T-001 activation-never-re-reads criteria.
- AC4 (consistent readiness/workspace/concurrency) → "Shared AgentJob admission and
  scheduling" + T-002, with the explicit no-second-queue/no-runner-control constraint.
- AC5 (six distinct statuses) → status requirement with the exact value set; T-006
  verifies every mapping condition incl. `recovering`.
- AC6 (stable status/ids/result, no internal parsing) → same requirement ("final result
  once terminal without reading the runtime transcript") + D5 typed terminal facts.

All three non-goals are honored: Slack Bot / external Agent API explicitly out of scope;
sibling paths locked by a dedicated requirement plus T-006 regression criteria; no direct
Runner-process control anywhere (transport is a server-side event obligation, execution
claims through the existing agent-job ledger poll).

**Correctness — checked, no must-fix issue.** The mechanism chain is sound and grounded
in verified code:

- The shipped fence is real and inert: `IWorkflowAgentHandoffGrain`
  (`PrepareAsync`/`AcceptAsync`, dispositions Prepared/Accepted/Rejected) exists at
  `packages/server/src/Mohist.Server/Workflow/Grains/IWorkflowAgentHandoffGrain.cs` and
  no production caller exists outside the grain itself — so D2's "extend the record shape
  compatibly" premise holds.
- D1/D3 reuse real entry points: `AgentJobGrain.PrepareManualLaunchAsync`
  (idempotent via `PlansEquivalent`, conflict-throws, Visible when no parent session) and
  `SubmitPreparedLaunchAsync` → `TryAdmitAsync`, plus
  `AgentSessionGrain.EnsureInitialLaunchAsync`; the coordinator pattern
  (`AgentLaunchCoordinatorGrain` + `IAgentLaunchParticipantProbe`) exists to mirror.
- D3's discriminator rationale verified: `AgentJobInput` and the routed-launch plan both
  already carry `WorkflowRunId` (IAgentJobGrain.cs), so a positive discriminator is
  genuinely needed.
- D5's pattern verified: `AgentJobState.PendingSubagentTerminalEvent` and
  `EventCatalog` types `com.mohist.agent.job.subagent-terminal` /
  `terminal-delivery` exist; mirroring is consistent.
- D6 verified: `WorkDispatch.Expect` (Id 19) exists with exactly the documented
  semantics; runner `evaluateCompletion` exists
  (`packages/runner/src/actions/expectations.ts`); the server indeed has no workspace
  filesystem access, so boundary evaluation is the only viable placement.
- D4 verified: `AgentJobOptions.JobTimeout` is 10 min; `ArmJobTimeout` is
  running-since based (armed at claim), so queue wait does not consume the per-invocation
  deadline and the inline default (3600000 ms, confirmed in
  `packages/runner/src/actions/built-ins.ts`) yields real parity.
- D7 verified: `FindReportableWork`, `BindTaskReportArtifactsAsync`,
  `ApplyTaskReportAsync`, `FailTask`, the `agent-result-settlement` reminder, and the
  stop-with-unresolved-agent handling all exist on `WorkflowGrain`*; the finalizer design
  (per-effect receipts, stale/duplicate acknowledgment, reconcile reminder) is the
  established convention applied to a new effect set.
- The plan also honors the supervisor audit comment: handoff participants, terminal
  bridge, and finalizer all land (T-001..T-004) before any dispatch switches (T-005), and
  the task-report endpoint is never used as Agent transport.

Adversarial constructions attempted (all resolve): dispatch crash between Accept and
Activate (idempotent Prepare→Accept→Activate replay on dispatch retry); stop mid-settlement
(receipts + stale-terminal acknowledgment); duplicate/redelivered terminal (stable event id
`workflow-terminal:{jobKey}` + receipts); concurrent direct launches starving the workflow
job (shared permit gate, T-002 spec with fake time); double-counted runtime view (risk
table mitigation, T-005 criterion); rollback (revert slice 5 only; slices 2–4 dark code;
additive storage).

**Consistency with the codebase — checked, no issue.** Naming, storage conventions
(append-only Orleans ids, ledger JSON, additive migration), event catalog, reminders, and
spec styles all match sibling changes (e.g. issue-589). Docs targets exist:
`docs/actions/agent.md`, `docs/agent-sessions.md` ("Two Invocation Paths"),
`design/agent-execution.md`.

**Task breakdown — checked, no issue.** T-001 → {T-002, T-003} → T-004 → T-005 → T-006;
cutover correctly gated on all four predecessors; every slice inert/dark until T-005;
criteria are concrete, spec-anchored, and test-type-labeled (grain specs, runner tests,
read-model tests, e2e, full `npm run verify` in T-006). Spec anchors referenced by
`tasks.json` correspond to requirement headers in `specs/workflow-agent-action/spec.md`.

## Must-fix findings

None.

## Observations (do not affect the verdict)

1. **Stale `tasks.md`.** `openspec/changes/issue-559/tasks.md` is a divergent
   "Current-master Delivery Plan" (5 items; no T-002 admission/deadline or T-006
   read-surface/docs content) superseded by `tasks.json`'s T-001..T-006. It can mislead a
   human reader; consider deleting or regenerating it in a later task. Not part of the
   reviewed artifact set.
2. **O1 named-session continuation** is a real product-semantics fork (inline path
   continues named logical sessions via the runner session machinery; the spec freezes
   per-attempt lineage). The plan gates T-005 on resolving it and T-006 on documenting it,
   which is an acceptable deferral; no issue AC depends on either answer.
3. **O4 `Unknown` mapping** (executing vs recovering) deferred to T-006; either mapping
   satisfies AC5's value set. The risk-table note that a job stuck `Unknown` relies on the
   existing agent-job recovery machinery is consistent with direct-launch behavior.
4. **D9 status-table gap:** the accepted-but-not-yet-activated window has no explicit row;
   it derives naturally (no ledger record yet → `queued`) and is guarded by idempotent
   activation replay, but the slice-5/6 specs would benefit from pinning it.
5. **Cross-change coordination with issue-589** (AgentResultSettlement targets the inline
   workflow-agent path that T-005 removes for new dispatches): flagged in T-004 notes;
   sequencing between the two in-flight changes should be watched at build time.
6. Minor: `WorkDispatch.Expect`'s doc comment ("the Workflow task executor reads and
   evaluates this") will need updating when the agent-job executor also reads it; fold
   into T-006 doc updates.

## Verdict

The plan covers every goal and acceptance criterion, its mechanisms are correct against
the current codebase (all load-bearing factual claims verified true), it is consistent
with project conventions and all three non-goals, and the task breakdown is ordered,
complete, and verifiable. No must-fix problems.

<promise>PASS</promise>
