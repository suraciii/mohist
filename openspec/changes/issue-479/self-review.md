# Self-Review — issue-479 (pass 2)

Reviewer: self, fresh pass after the pass-1 fixes. Artifacts reviewed:
`proposal.md`, `design.md`, `tasks.json`, `specs/{agent-launch,agent-job-read,
session-command-unification}/spec.md`.

## Previous findings — status

Pass-1 findings are resolved:

- **F1 (spec over-claim)** — FIXED. `agent-job-read` requirement #4 is now
  "AgentJob is the canonical work-result read path", scoped to the CLI reading
  the result from `mo agent job view`; the #484-owned "session presents no
  verdict" clause is removed and explicitly marked a separate concern. The
  scenario is now deliverable by T-002.
- **N1 (migration order)** — FIXED. Design Migration Plan reordered to match the
  task graph (read model → job routes → launch identity → session route → CLI).
- **N2 (cross-project isolation)** — FIXED. T-002 has an AC asserting
  `agent-jobs/{jobId}` returns 404 when the job's `ProjectId` differs from the
  route.
- **N3 (historical jobs)** — ADDRESSED, but the chosen fix introduces a new
  blocker (B1 below).
- **N4 (T-001 spec fragment)** — FIXED. T-001 now references
  `#List AgentJobs for an Agent`.

DAG re-verified acyclic; priorities strictly ordered; all tasks carry spec refs
+ test-backed ACs; no standalone test task.

## Blocking finding

### B1. The grain-fallback for historical jobs cannot work once `[PersistentState]` is dropped

The N3 fix says `agent job view` "falls back to the grain" when no read-model
row exists, and the design asserts this "makes view always-authoritative
without a one-time backfill" (`design.md`, "Historical / in-flight jobs at
cutover") and that "existing AgentJobs remain addressable by their existing
stable ids" (`design.md`, "Rollback"). T-002 has an AC to the same effect.

These claims are **inconsistent with the chosen persistence migration.** D2/T-001
make the relational store authoritative and **drop `[PersistentState("agent-job")]`**
(`AgentJobGrain.cs:57`), loading state from `IAgentJobStore` on activation. But
today `OnActivateAsync` reads the *old* Orleans grain storage
(`AgentJobGrain.cs:75-76`), and every recovery path — terminal-close delivery,
routed-launch advance, dispatch retry — branches on `State.Input` /
`State.RoutedPlan` (`AgentJobGrain.cs:81-113`).

For a job that was running or already terminal at cutover, its state lives only
in the old Orleans grain-storage blob. After `[PersistentState]` is dropped:

- The grain activates, `IAgentJobStore.LoadAsync(jobKey)` finds **no row** (it
  was never written), and loads default/empty state.
- `State.Input is null && State.RoutedPlan is null` (`:81`) → the grain returns
  early; recovery does not fire; a terminal job's `PendingSessionClose` is never
  delivered.
- The T-002 "grain fallback" then reads that **empty** grain, not the real
  orphaned state.

So the fallback does not recover pre-cutover / in-flight jobs, and the plan's
"remain addressable" / "always-authoritative without backfill" claims are false
under the primary migration path. Worse, an in-flight job re-activating to empty
state is a correctness hazard (lost runner assignment, lost terminal-close
obligation), not merely a stale read.

**Fix (pick one and make all artifacts consistent):**
- Retain `[PersistentState]` as an activation load source and write-through to
  the relational row (the D2 "alternative (a)" mirror), so grains always load
  their real state and the fallback works — accepting the documented dual-write
  caveat; or
- Add a one-time backfill that reads each in-flight/terminal job from Orleans
  grain storage and writes its `AgentJobRow` before the new load path takes over
  (then the "no backfill" claim must be removed); or
- Explicitly document that in-flight jobs at cutover are **not** preserved and
  require a deploy-time drain (and delete the "remain addressable" /
  "always-authoritative" claims plus the T-002 grain-fallback AC, since those
  jobs are intentionally lost).

Until one of these is chosen, the persistence section of the plan is
self-contradictory and not safely buildable.

## Non-blocking findings

### N1'. Design D6 prose is looser than what is delivered

D6 says "the CLI stops reading result from it [the Session DTO]." No task
removes `failureReason`/`failureCategory` from the `mo session show` table shape
(that is #484-owned), so after this issue `mo session show` still renders those
columns. The **spec** is correct (it no longer asserts the session presents no
verdict), so no test fails; but the D6 sentence should be softened to "the
canonical result read path becomes `agent job`; the residual session columns are
removed by #484."

### N2'. `mo session list --run` does not validate the run belongs to the project

T-004 asserts cross-project 404 for `show`/`transcript` by id, and its note
claims the unified list "resolves project from the route, not from the run." But
the `?run=` filter delegates to `ListByWorkflowAsync(runId)`, which is not
project-scoped, and no AC asserts a run belonging to a different project is
rejected or empty. Either add an AC (the run must belong to the route's project)
or note that `--run` is project-validated. (Already half-listed under design
Open Questions; promote it to an AC so it is not dropped at build time.)

## What is solid

- Capability → task coverage complete; DAG acyclic with strict priority
  ordering; every task has spec ref + test-backed ACs.
- F1/N1/N2/N4 from pass 1 are genuinely resolved; spot-checks against the
  codebase still hold (`OnActivateAsync` reads `[PersistentState]` at
  `AgentJobGrain.cs:75-76`; recovery branches on `State` at `:81-113`;
  `FindGenericSessionAsync` source-kind gate at `AgentSessionQuerier.cs:608`).
- Decisions carry rationale + rejected alternatives; the top risk (grain
  persistence migration) is flagged — B1 is precisely the unresolved detail
  inside that risk.

## Verdict

B1 is a must-fix: the persistence-migration path contradicts the plan's own
"jobs remain addressable / view is always-authoritative" claims, so the plan is
not safely buildable as written.

<promise>FAIL</promise>
