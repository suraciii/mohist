# Self-Review: issue-559

## Must-Fix Findings

None.

## Previous Finding Dispositions

- **Workflow stop orphaning: reclassified; not a must-fix.** The previous
  review incorrectly treated Workflow `StopAsync` as an authoritative Agent
  Turn cancellation. The current Runner contract explicitly says that stopping
  a run or Job while work executes does not cancel the process and that its
  report becomes stale (`design/runner.md:273-289`). The issue requires
  Workflow to distinguish an AgentJob that is authoritatively cancelled; it
  does not require Workflow stop to cause that cancellation. The plan keeps
  AgentJob as execution owner after acceptance (`design.md:28-34`), rejects a
  late application when its TaskRun is already terminal
  (`design.md:309-314`), and verifies TaskRun applicability before finalization
  effects (`design.md:654-659`). The accepted AgentJob can therefore retain its
  canonical status and result without reviving the stopped Workflow task. The
  previous finding does not violate acceptance criteria 5 or 6 and is recorded
  below only as an explicit-policy observation.
- **Post-acceptance activation convergence: fixed.** Session acceptance is
  non-executing, one idempotent AgentJob command owns the complete transition to
  admission, uncertain responses replay the same command even after claim, and
  a definitive pre-admission failure terminalizes the same AgentJob before
  normal terminal delivery (`design.md:228-259`;
  `specs/workflow-agent-action/spec.md:71-94`; `tasks.json:14-16`).
- **AgentJob report settlement: fixed.** The plan defines typed
  `accepted | stale | retry | rejected | conflict` dispositions, terminalizes
  invalid active reports from the frozen contract, makes Runner settlement
  depend on the acknowledgement body rather than HTTP 200, and requires tests
  for every disposition and malformed or unknown responses
  (`design.md:516-578`; `specs/workflow-agent-action/spec.md:146-174`;
  `tasks.json:35-37,53-59`).
- **Variable and TaskRun finalization fence: fixed.** Durable
  `pending | artifacts_bound | variables_applied` progress resumes artifact and
  variable effects from keyed receipts, while TaskRun application and the final
  receipt commit atomically. The tasks cover every intervening crash and lost
  acknowledgement boundary (`design.md:629-693`;
  `specs/workflow-agent-action/spec.md:152-194`; `tasks.json:35-37,51-59`).

## Observations

### 1. MEDIUM - Parent Workflow stop policy remains implicit

The ownership split and stale-delivery guard support letting an accepted
AgentJob retain its independent lifecycle after its parent Workflow stops, and
the rollback plan likewise lets accepted AgentJobs finish
(`design.md:835-840`). The artifacts do not state explicitly whether an
accepted but not-yet-claimed Job may begin after a user stops the Workflow.
Documenting that policy and testing stale finalization after Workflow stop
would remove ambiguity, but neither possible policy is required by the issue's
six acceptance criteria, so this does not affect the verdict.

### 2. MEDIUM - `session` and `timeout` normalization remain implementation choices

The plan leaves timeout range, default normalization, and delivery margin open
(`design.md:362-366,851-853`). T-002 requires timeout and default tests, so the
implementation must settle these values. The issue does not prescribe those
values.

### 3. LOW - The read route shape remains open

The design permits either an embedded invocation projection or a dedicated
route (`design.md:842-847`; `tasks.json:70-88`). Both choices can satisfy
bidirectional lineage and stable-result lookup.

## Re-Review Checks

- **Issue goals and acceptance criteria:** re-read first from the canonical
  Issue JSON. All six criteria are represented: Agent selection and permitted
  input (`specs/workflow-agent-action/spec.md:1-69`), bidirectional
  Job/Session/Input/Turn lineage
  (`specs/workflow-agent-action/spec.md:71-95,227-240`), immutable execution
  facts (`specs/workflow-agent-action/spec.md:96-109`), shared readiness,
  workspace, and concurrency (`specs/workflow-agent-action/spec.md:111-139`),
  six lifecycle states (`specs/workflow-agent-action/spec.md:208-225`), and a
  stable result without transcript parsing
  (`specs/workflow-agent-action/spec.md:227-240`).
- **Previous findings:** checked individually. The activation, report
  settlement, and finalizer findings remain fixed. The Workflow-stop finding
  was based on a cancellation semantic that conflicts with the current Runner
  contract and does not follow from the issue criteria; it is reclassified as
  Observation 1.
- **Fix regressions:** checked, no must-fix regression found. The repaired
  activation state machine preserves one accepted lineage, report settlement
  preserves the immutable terminal decision, and finalizer progress preserves
  all Workflow-owned effects before acknowledgement.
- **Current codebase consistency:** checked, no must-fix issue. The plan reuses
  the current Agent resolver, readiness service, AgentJob admission and Runner
  ledger, typed report boundary, Session lineage, Workflow artifact store, and
  owner-stopped stale-report convention rather than introducing parallel
  execution owners.
- **Task breakdown and verifiability:** checked, no issue. The dependency chain
  `T-001 -> T-002 -> T-003 -> T-004 -> T-005` orders contract/materialization,
  execution/finalization, terminal arbitration, reads, and cutover coherently.
  Each task names focused failure, replay, compatibility, and end-to-end tests,
  and T-005 requires the repository verification gate.

## Verification Notes

- Static evidence came from the required current `mo issue view` read, a
  supplemental canonical JSON-field read, the prior review, every current plan
  artifact, and the cited current Server, Runner, and design contracts.
- This was a plan review; no product tests or build gates were run.
- Only `openspec/changes/issue-559/self-review.md` was modified.

## Verdict

PASS. No must-fix problem remains; the plan covers every issue goal and
acceptance criterion and is ready to build.

<promise>PASS</promise>
