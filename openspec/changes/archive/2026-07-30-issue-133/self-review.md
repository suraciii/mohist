# Self-Review — Issue 133

## Verdict

The plan is ready to build. All five acceptance criteria map to spec requirements and tasks; every spec requirement is owned by a task; the task graph is a valid DAG; and the factual claims about the current codebase were verified against source. Findings below are non-blocking observations for the builder, not problems that must be fixed before building.

## Factual claims verified against source

- `GET /agents` already hydrates **Readiness** per Agent — `AgentQuerier.ListAsync` calls `_readiness.GetAsync` per info (`packages/server/.../Agent/Services/AgentQuerier.cs:84-91`). ✓
- Availability is only reachable **per-Agent** via `GET /agents/{agentRef}/status` (`packages/server/.../Api/AgentJobReadRoutes.cs:39`). No list-scoped Availability serving exists. ✓
- `AgentListPage.getAvailabilityStatus` returns only Active/Archived **lifecycle**, not Availability (`packages/web/.../agent-list/ui/AgentListPage.tsx:28-33`). ✓
- `AgentDetailPage` already has `ReadinessCard`, `AvailabilityCard`, edit entry, and a Readiness-gated New Session button (`packages/web/.../agent-detail/ui/AgentDetailPage.tsx`). ✓
- The composer already implements Readiness gating (block Needs setup / allow Unknown), idempotency-key reuse, archived blocking, no config overrides, and differentiated error feedback (`packages/web/.../agent-session-composer/ui/AgentSessionComposerPage.tsx` + its test). ✓
- The editor carries edit-timing language **only** for archive, not for definition edits; Runtime is derived (`readAgentModelAndVariant`) but not rendered; Max concurrent runs appears only inside the Availability card. ✓ (these are the T-002 gaps)
- `AgentAvailabilityService.GetAsync` calls `RunnerStatusService.GetOnlineRunnersAsync` **per Agent**, so computing it once in a list-scoped method is a real improvement, not a no-op. ✓
- `AgentJobQuerier` currently has only `ListByAgentAsync` (per-Agent) — see Observations.

## Coverage

Acceptance criteria → tasks:

| AC | Covered by |
|---|---|
| AC1 list shows purpose/Readiness/Availability/workload | T-003 (+ T-001 serving) |
| AC2 detail view/edit definition + gap explanation | T-002 |
| AC3 submit test task, get work entry | T-004 |
| AC4 offline/limit = Availability, not config error | T-001, T-002, T-003 |
| AC5 differentiated feedback, no raw logs | T-004 |

All 4 capabilities' requirements (4+4+4+3) are owned by a task. Task graph: T-001(p1) ← T-003(p3); T-002(p2) ← T-004(p4); all `dependsOn` point to strictly lower priority; acyclic.

## Observations (non-blocking, for the builder)

1. **A new `AgentJobQuerier` method is required.** `AgentJobQuerier` has no project-wide pending-jobs query today (only `ListByAgentAsync`, scoped to one Agent). T-001's "single batched pending-jobs query grouped by Agent" therefore means *adding* a project-scoped pending query (filter by `ProjectId` + `Status=pending`, group by `AgentId`). The table has the needed columns. This is implied by "Add a list-scoped method" but worth stating explicitly to avoid a per-Agent fan-out inside the "list-scoped" method.

2. **`canStartNow` can coexist with transient queued work.** `dispatch-pending` is a per-job fallback reason in `BuildWaitingWork`, not an agent-level `waitingReason`. An Agent can report `canStartNow=true` while a just-submitted job is momentarily `dispatch-pending` (`queuedCount > 0`). The list-discovery spec scenarios ("can-start-now with zero workload" vs "waiting workload with a reason") are illustrative examples, not an invariant that `canStartNow ⟺ empty queue`; the requirement text itself does not assert equivalence. No change needed — just don't derive one from the other in rendering.

3. **T-001 criterion wording conflates two responses.** "A Ready Agent with no online runner reports `waitingReason=no-online-runner` … while its Readiness stays Ready" spans two server responses: the summary (Availability) and the list response (Readiness). The summary endpoint does not compute Readiness, so "stays Ready" is naturally true. The intent (offline reads as Availability, never as a config gap) is correct and testable; just assert Readiness on the list-response side and Availability on the summary side separately.

4. **T-004 → T-002 dependency is edit-serialization, not data flow.** Both tasks edit `AgentDetailPage.tsx` (T-002: summary/header; T-004: New Session entry) but do not depend on each other's output. The dependency is pragmatic for autonomous execution to avoid file conflicts. Acceptable as-is.

5. **Terminology: "execution-side unavailability."** The composer's existing class is `EXTERNAL_AGENT_UNAVAILABLE` ("external agent unavailable", i.e. the Pi runtime's external agent). The feedback spec's "execution-side unavailability / configured execution backend cannot run" is a reasonable generalization of the same signal. Map them directly during implementation.

These are clarifications; none block a builder from starting. Each task is independently deliverable, has verifiable acceptance criteria including test coverage, and respects the testing constraints (fakes only, no real network/process/Runner/wall-clock).

<promise>PASS</promise>
