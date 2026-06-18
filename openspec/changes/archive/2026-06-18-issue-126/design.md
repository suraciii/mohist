## Context

Today every agent execution on Mohist is welded to a `WorkflowRun`. The dispatch chain `WorkflowRun → WorkflowBacklogGrain → Runner lease → ACP session → ReportResult → IWorkflowGrain` has no branch point that bypasses the workflow run: `WorkDispatch.WorkflowRunId` is required (`IRunnerGrain.cs:40`), `IsWorkRunnableAsync` hard-codes `IWorkflowGrain` checks (`RunnerGrain.cs:309`), and `ReportResultAsync` hard-codes `IWorkflowGrain.ReportResultAsync` + `GetRunStatusAsync` (`RunnerGrain.cs:163-185`).

A key finding from the codebase is that the **execution layer is already generic**: the runner's `WorkExecutor` + ACP agent action only need `uses` + prompt/`with` + `workDir`, and the runner's `WorkItem.workflowRunId` is just an opaque in-flight correlation key on the TS side (`packages/runner/src/core/types.ts`). The `WorkspaceManager.ensure` already short-circuits to `variables.workspace.path` when present (`workspace.ts:32-36`) and otherwise falls back to a `<runnerRoot>/fallback/<workId>` dir (`workspace.ts:58-60`) — so a standalone workspace identity is already reachable without `repository.gitUrl` + `issue.number`. The coupling is therefore confined to the **server-side coordination layer**, which is what this change opens.

Constraints:
- **Zero regression** on the workflow coordination path is a hard requirement (issue body, spec `Workflow coordination path behavior is preserved`).
- **No backlog changes** — `WorkflowBacklogGrain` must not learn about agent jobs.
- **Validation scope** — the HTTP endpoint exists only to prove end-to-end execution, not to be the product surface.
- Orleans `[GenerateSerializer]` records evolve by adding `[Id(n)]` fields with defaults; existing workflow wire format must remain decodable.

Stakeholders: First-class Agents epic #5 (this is the foundation issue); downstream issues (Agent entity / naming, Visibility / read-model, product CLI `mo agent run`) build on this engine.

## Goals / Non-Goals

**Goals:**
- Open the Runner coordination layer so an agent execution unit can run with **no WorkflowRun present**.
- Introduce `AgentJobGrain` as the lifecycle + result owner for that unit, dispatching directly via `RunnerRegistry`.
- Keep the workflow coordination path **byte-equivalent** for `owner-kind = workflow`.
- Prove the engine end-to-end with a minimal `POST { prompt, model, workspace } → result` API.
- Reuse the existing generic execution layer (`WorkExecutor` + ACP) and the existing workspace short-circuit — minimize new surface.

**Non-Goals:**
- Agent entity / naming / user-defined agents (separate issue).
- Run read-model, board, activity projection (Visibility issue).
- Product CLI `mo agent run <name>` (Agent Run issue).
- Any workflow scheduling/path behavior change beyond additive owner-kind branching.
- Ad-hoc non-issue workspace (v1 stays workspace-path-scoped).
- Consumer/result-consumer wiring and authority/permission model.

## Decisions

### Decision 1: Add `OwnerKind` to the existing `WorkDispatch` record (server-side only)

Add `OwnerKind` (`"workflow" | "agent-job"`, default `"workflow"`) and an optional `AgentJobId` field to the C# `WorkDispatch` record with new `[Id(n)]` slots. Default values preserve workflow wire compatibility.

**Alternatives considered:**
- *Separate `AgentJobWorkDispatch` type with polymorphic dispatch.* Rejected: doubles `AssignWorkAsync`/`ReportResultAsync` surface and forces a discriminator on the wire anyway. A single record with an explicit kind is simpler and matches the issue's "owner-kind dimension" framing.

### Decision 2: Three weld points branch on `OwnerKind` via a `switch`

`AssignWorkAsync`, `IsWorkRunnableAsync`, and `ReportResultAsync` in `RunnerGrain` each gain a `switch (work.OwnerKind)`. The `workflow` arm keeps the **exact** existing code (claim check, status check, current-work-id check; `IWorkflowGrain.ReportResultAsync` + `GetRunStatusAsync`). The `agent-job` arm calls `IAgentJobGrain` equivalents.

**Alternatives considered:**
- *Strategy registry (`IWorkOwnerResolver` keyed by kind).* Rejected for v1: two arms is too few to justify the indirection, and the issue names the three weld points explicitly. Left as a documented evolution path if a third owner kind ever appears.

### Decision 3: Agent-job dispatch is push-from-grain via `RunnerRegistry`, not backlog

`AgentJobGrain` queries `IRunnerRegistryGrain.ListEligibleRunnersAsync(projectId)`, picks an online runner with a free slot (reusing `MaxWorkflowSlots` accounting via `IRunnerGrain.GetRuntimeStateAsync`), and calls `runner.AssignWorkAsync(dispatch with owner-kind=agent-job)` directly. No `IWorkflowBacklogGrain` call.

**Alternatives considered:**
- *Mirror the workflow backlog with an `AgentJobBacklogGrain` that runners poll.* Rejected: the issue explicitly says agent jobs bypass backlog, and v1 has no fan-out / priority / multi-project queueing needs. The backlog architecture is justified for workflow runs because many runs compete across projects; a single ad-hoc job does not need it.
- *Long-poll from runner.* Rejected: requires a new runner-side loop. Push-from-grain keeps the runner code untouched on the dispatch side.

### Decision 4: Agent jobs share the runner's existing workflow slot pool

A runner's `MaxWorkflowSlots` is a single capacity budget. An agent-job dispatch consumes one slot, tracked by the same `_works` map and `ActiveWorkflowCount` accounting. The runner sees the agent-job's owner identity (`AgentJobId`) the same way it sees `WorkflowRunId`.

**Alternatives considered:**
- *Separate `MaxAgentJobSlots` budget.* Rejected for v1: no data on agent-job load, no product reason to partition yet. Revisit when agent volume is real.

### Decision 5: Agent-job state is in-memory grain state (no DB persistence)

`AgentJobGrain` holds `pending → running → completed/failed` in grain-internal fields, no `[Reentrant]`, no `[PersistentState]`. The grain is kept alive for the lifetime of the job by the validation API request awaiting it (and by a grain timer driving the dispatch backoff). On silo restart, in-flight jobs are lost.

**Alternatives considered:**
- *Persist to SQLite like `WorkflowRun`.* Rejected: persistence + read-model is explicitly the separate Visibility issue. v1 only proves the engine path.

### Decision 6: Validation API blocks until terminal

`AgentJobController` `POST` awaits the grain's terminal state and returns the full result. A request timeout (default ~10 min, configurable) bounds the hold. Failure/timeout produce a structured `failed` job result rather than an opaque 500.

**Alternatives considered:**
- *Return `202 Accepted` with a job id and require polling.* Rejected for v1: this is a validation surface, not the product API. Async/poll is the natural evolution for the CLI issue and is called out there.

### Decision 7: Runner TS types stay unchanged; agent-job identity rides on existing fields + `variables`

The runner does not route by owner-kind — only the server's `RunnerGrain` does. So the TS `WorkItem.workflowRunId` is reused as an opaque correlation key (server sends the `AgentJobId` there, or a synthetic `agent-job:<id>`), and the `ownerKind` + workspace path travel inside `variables`. This means **zero changes to the runner's type system, WorkExecutor, ACP action, or dispatch loop**.

**Alternatives considered:**
- *Add `ownerKind` + `agentJobId` fields to the TS `WorkItem`.* Rejected: forces threaded changes through executor/host/connection for no behavioral gain. The server is the only owner-aware boundary.

### Decision 8: Workspace is supplied by the server via `variables.workspace.path`

For an agent-job the server always populates `variables.workspace.path` (or `workDir`), which triggers the existing early-return branch in `WorkspaceManager.ensure` (`workspace.ts:32-36`). If absent, the existing `fallback/<workId>` branch (`workspace.ts:58-60`) handles it. **No `workspace.ts` code change is required** for the agent-job case; the spec requirement "WorkspaceManager accepts standalone agent-job work" is satisfied by the existing branches. A short comment noting agent-job reliance on these branches will be added.

**Alternatives considered:**
- *Add a first-class `ownerKind` check in `WorkspaceManager.ensure` that skips the issue-worktree path explicitly.* Rejected: redundant given the early-return already wins. The explicit guard only matters if a caller forgets to pass `workspace.path` *and* passes `issue.number` — which the server will not do for agent-jobs.

## Risks / Trade-offs

- **Orleans wire-compat regression** -> New `WorkDispatch` fields use fresh `[Id(n)]` slots and have defaults (`OwnerKind = "workflow"`, `AgentJobId = null`). Workflow arms never read the new fields. Add a serialization round-trip test against a pre-change payload fixture.
- **Workflow arm drift** -> The `switch (work.OwnerKind)` workflow arm must stay byte-equivalent. Lock this in by extracting the existing workflow body verbatim into a private `IsWorkRunnableForWorkflowAsync` / `ReportResultForWorkflowAsync` helper and leaving the agent-job arm as the only new code path. Regression suite (`workflow-run`, `workflow-engine`, `ralph-task-execution`) is the safety net.
- **Orphaned job on runner crash** -> If a runner dies after `AssignWorkAsync`, the `AgentJobGrain` is waiting on a report that never arrives. Mitigation: a grain-level job timeout (default 10 min) transitions the job to `failed` with a `runner-unavailable` / `report-timeout` reason. The runner's existing `HandleTimeoutAsync` already clears its `_works` map on heartbeat loss, so no slot leak on the runner side.
- **Double report / retry** -> Network-retried `ReportResultAsync` could double-deliver. Mitigation: `AgentJobGrain.ReportResultAsync` rejects reports against terminal jobs (spec: "completed or failed job rejects further reports"). Runner removal of the tracked-work key is already idempotent.
- **Validation API holds a connection for the whole agent run** -> Acceptable for v1 (single caller, validation scope). A request timeout + structured timeout result bound the worst case. Real product surface uses async/poll (out of scope).
- **In-memory state lost on silo restart** -> Explicit v1 trade-off; persistence lives in the Visibility issue. Documented; no recovery guarantee in this issue.
- **Shared slot pool lets agent jobs starve workflow runs (or vice versa)** -> v1 trade-off accepted; volume is validation-only. Revisit with per-kind budgets when load is real.
- **No auth on validation endpoint** -> Authority model is an explicit non-goal. Endpoint is validation-only and should not be exposed on a production edge as-is; note in code comment.

## Migration Plan

This is a **pure additive** server-side change. There is no data migration, no schema migration, no config change.

**Deploy:**
1. Ship server changes (`IRunnerGrain`/`RunnerGrain` owner-kind branching, new `IAgentJobGrain` + `AgentJobGrain`, `AgentJobController`).
2. Runners require **no redeploy** — the runner TS types are unchanged and the server sends only additive optional fields. Existing workflow dispatch continues to set `OwnerKind = "workflow"` by default.
3. Workflow regression suite must pass before and after deploy.

**Rollback:**
1. Revert the server commit. Any in-flight `AgentJobGrain` instances vanish (acceptable v1).
2. The workflow path is byte-identical under rollback — no workflow data, scheduling, or reporting is affected.
3. The validation endpoint disappears; nothing else depends on it yet.

**Test gates:**
- Pre-merge: owner-kind routing tests at each of the three weld points (workflow arm unchanged, agent-job arm routes to `IAgentJobGrain`); `AgentJobGrain` lifecycle tests (pending → running → completed/failed, terminal rejection); end-to-end validation API test (success + failure + missing-field).
- Pre-merge: full `workflow-run` / `workflow-engine` / `ralph-task-execution` regression suite remains green.

## Open Questions

- **Job-level timeout default** — proposal is 10 min for v1 with a single config knob. Confirm against realistic agent-run durations once the validation API is exercised.
- **Backoff schedule for no-slot retry** — proposal: exponential 1s → 60s cap, total bound 10 min, then `failed` with `runner-unavailable`. Needs a sanity check against deployment runner-pool size.
- **Runner selection strategy** — when multiple runners are idle, pick first / least-loaded / random? v1 proposal: first eligible from `ListEligibleRunnersAsync` for determinism; revisit when load balancing matters.
- **Validation API path and versioning** — e.g. `POST /api/agent-jobs/validate` vs `/api/v1/agents/run`. Pick a name that the future product CLI issue can either reuse or deprecate cleanly.
- **Should `AgentJobGrain` emit workflow-log-style events for observability?** v1 proposal: no (event-bus wiring is read-model-adjacent and belongs to the Visibility issue). Confirm this stays out of scope.
