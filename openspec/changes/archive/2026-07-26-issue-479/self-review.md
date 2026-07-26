# Self-Review — issue-479 (pass 3)

Reviewer: self, fresh pass after the pass-2 fixes. Artifacts reviewed:
`proposal.md`, `design.md`, `tasks.json`, `specs/{agent-launch,agent-job-read,
session-command-unification}/spec.md`.

## Previous findings — status

All prior findings are resolved:

- **Pass 1 — F1** (spec over-claim), **N1** (migration order), **N2**
  (cross-project isolation), **N4** (T-001 spec fragment): FIXED in pass 1.
- **Pass 2 — B1** (persistence cutover consistency): FIXED. D2 now retains
  `[PersistentState("agent-job")]` as the grain's authoritative load/recovery
  source and adds a write-through mirror to `AgentJobRow`; `view` reads the
  grain (always authoritative, loads real state for pre-cutover/in-flight jobs),
  `list` reads the row. The design's "remain addressable" / "always-authoritative"
  claims are now literally true with no backfill, and `OnActivateAsync`
  (`AgentJobGrain.cs:75-76`) + recovery branches (`:81-113`) are untouched.
- **Pass 2 — N1'** (D6 prose): FIXED (canonical read path is `agent job`;
  residual session columns are #484-owned).
- **Pass 2 — N2'** (`--run` project scoping): FIXED (T-004 AC asserts the `?run=`
  filter is project-scoped).

## Fresh verification this pass

I checked two things the B1 fix could have disturbed:

1. **Cross-project isolation source for routed jobs.** T-002 keys the view's
   project check on `State.Input.ProjectId`. Routed-launch jobs (a product job
   class) do populate it — `AgentJobGrain.cs:482` and `:512` both set
   `ProjectId: plan.ProjectId` when the plan is projected into `State.Input`.
   Manual (`AgentLauncher.cs:99`) and mention launches set it too. Only
   raw-prompt validation jobs omit it, and those are not product-addressable
   (no `AgentId`, never returned by launch). So the check is sound for every
   product job. No gap.
2. **Dual-persistence per transition.** The mirror write means two stores
   (Orleans grain storage + `AgentJobs` row) are written per transition. This is
   the documented trade-off: the grain remains correct if the row write fails,
   `view` is grain-authoritative (unaffected), and `list` self-heals on the next
   transition / recovery reminder. Consistent with the Risks section. Acceptable.

## Coherence check

- Persistence story is internally consistent across D2, Risks, Rollback,
  "Historical / in-flight jobs", Migration Plan step 1, T-001, and T-002 — all
  say retain `[PersistentState]` + mirror, view-from-grain, list-from-row.
  No lingering `drop [PersistentState]` / `relational-authoritative` references.
- Spec ↔ plan agreement: `agent-job-read` requirement #4 ("canonical
  work-result read path") is deliverable by T-002 and no longer asserts the
  #484-owned DTO cleanup.
- Capability → task coverage complete; DAG acyclic with strictly-ordered
  priorities (T-001→T-002→T-003→T-005, T-004 parallel); every task has spec ref
  + test-backed ACs; no standalone test task.
- Cross-project isolation asserted for both job view (T-002) and session
  show/list (T-004).

## Minor observations (non-blocking, not requiring changes)

- `view` activates the grain, so a cold read runs `OnActivateAsync` recovery
  (e.g. terminal-close delivery). This is pre-existing Orleans behaviour, the
  recovery paths are idempotent, and the validate endpoint already relies on it —
  not a defect introduced here.
- `list` can be briefly stale in the crash window between the two writes; this
  is already documented under Risks and is acceptable for a status overview.

## Verdict

All blocking and non-blocking findings from passes 1–2 are resolved, the fresh
verification found no new blocking issues, and the artifacts are internally
consistent and buildable.

<promise>PASS</promise>
