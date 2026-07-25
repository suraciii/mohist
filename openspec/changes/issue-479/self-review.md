# Self-Review — issue-479

Reviewer: self (author pass). Artifacts reviewed: `proposal.md`, `design.md`,
`tasks.json`, `specs/{agent-launch,agent-job-read,session-command-unification}/spec.md`.

## Summary

The plan is coherent in shape: three capabilities map cleanly to five vertical
tasks, the dependency graph is a valid acyclic DAG with strictly-ordered
priorities, every task carries a spec reference and test-backed acceptance
criteria, and there is no standalone test task. Factual claims spot-checked
against the codebase are correct (the `source-kind == "agent-launch"` gate is at
`AgentSessionQuerier.cs:608`; `AgentSessionShow` renders `failureReason`/
`failureCategory` at `ResourceOutput.cs:65-66`; the launch response drops the job
key at `IAgentLauncher.cs:121`).

However, one spec/plan coherence defect must be fixed before building.

## Blocking finding

### F1. `agent-job-read` spec requirement #4 over-claims what this issue delivers

`specs/agent-job-read/spec.md` requirement **"AgentJob is the sole work-result
read path"** asserts:

> "The CLI SHALL NOT present a competing terminal verdict on the AgentSession
> read surface."

with scenario:

> "the session read surface does not present a separate job-result verdict"

This is **not deliverable by issue-479 alone.** The proposal and design
Non-Goals explicitly defer removal of `failureReason`/`failureCategory` from the
Session DTO to **#484** ("No removal of Session terminal-state fields from DTOs
— that is #484"), and no task in `tasks.json` removes them. Today the CLI
`mo ... session show` table shape renders both fields
(`ResourceOutput.cs:65-66`), and nothing in T-002/T-005/T-003 removes them. So
after this issue lands, `mo session show` still emits a competing verdict, and a
test written for that scenario would fail.

The spec therefore asserts a MUST that the plan does not deliver, and silently
depends on #484 — contradicting the proposal's "interlocking, not blocking"
framing. This will surface as a failing scenario during implementation.

**Fix (one of):**
- Scope the requirement to what this issue owns — "the CLI reads a launch's
  terminal outcome from `mo agent job view`, the canonical result path" — and
  drop the "session presents no verdict" clause (move that assertion into #484's
  spec, which owns the DTO cleanup); or
- Make the dependency explicit: declare a hard ordering on #484 in the proposal
  and `tasks.json` (e.g. a predecessor or a note that the scenario is satisfied
  once #484 lands in the same release).

Either way, the spec and the plan's non-goals/sequencing must agree before a
builder starts.

## Non-blocking findings (should fix, do not block PASS on their own)

### N1. Design migration-plan ordering contradicts the task dependency order

`design.md` "Migration Plan" lists **step 2 = launch identity (D3)** *before*
**step 3 = job routes**. But `tasks.json` makes **T-003 (launch identity)
depend on T-002 (job routes + CLI)** (T-003 AC: "jobId returned at launch is
accepted verbatim by the T-002 view route"). A builder following the design's
sequence vs the tasks' `dependsOn` gets conflicting signals. Reconcile: either
rewrite the design migration-plan step order to match the tasks (routes before
launch), or relax T-003 → T-002 and move the "jobId accepted by view route"
check into a later integration verification.

### N2. No cross-project isolation assertion for `agent job view`

T-004 asserts a cross-project 404 for sessions, but T-002
(`GET /api/projects/{projectRef}/agent-jobs/{jobId}`) has no acceptance
criterion that a job whose `ProjectId` differs from the route's project returns
404. Manual-launch job keys are global GUIDs (`agent-job-launch-{guid}`), so the
view handler must verify the row's `ProjectId` against the route. Add an AC to
T-002 mirroring T-004's cross-project isolation.

### N3. Historical / in-flight jobs at cutover are not listable

T-001 populates the `AgentJobs` read model only on grain transitions going
forward. A job that is running or already terminal at deployment has no row
until its grain next activates and writes, so `mo agent job list/view` returns
empty / 404 for it. The design "Rollback" acknowledges the read model "populates
going forward" but no task addresses backfill or a fallback. Recommend either a
documented `agent job view` fallback to the grain (`GetTerminalResultAsync`,
already on `IAgentJobGrain.cs:18`) when the row is absent, or a one-time
backfill; at minimum state the limitation explicitly in T-001.

### N4. T-001 `spec` field has no requirement fragment

T-001 is the only task whose `spec` is a bare capability path
(`specs/agent-job-read/spec.md`) with no `#requirement` fragment; every other
task references a concrete requirement. Acceptable for an infrastructure task,
but for consistency either point it at a concrete requirement or note that it is
foundational storage only.

## What is solid

- Capability → task coverage is complete: `agent-launch` → T-003;
  `agent-job-read` → T-001 + T-002; `session-command-unification` → T-004 +
  T-005.
- DAG verified acyclic; every `dependsOn` points to a strictly-lower-priority
  task; two parallel foundations (T-001, T-004) correctly fan into T-005.
- Each task has verifiable acceptance criteria with explicit test-coverage
  statements and a build/test pass clause; no standalone test task.
- Design decisions each carry rationale + rejected alternatives; the top risk
  (AgentJob grain persistence migration) is flagged with a documented fallback.
- Specs are well-formed (4-hashtag scenarios, SHALL/MUST language, every
  requirement has ≥1 scenario).

## Verdict

F1 is a must-fix: the `agent-job-read` spec asserts a requirement the plan
defers to #484, so the plan is not buildable as written.

<promise>FAIL</promise>
