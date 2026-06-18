## Why

"Run an agent" is the most basic platform operation, but today it cannot be done without a WorkflowRun — every dispatch path (`WorkflowRun → backlog → Runner lease → ACP session → report to IWorkflowGrain`) is welded to a workflow run. This blocks the First-class Agents epic at its foundation: there is no place to land a named, user-defined, autonomous agent that is not a workflow stage. The execution layer (`WorkExecutor` + ACP agent action + session) is already generic and decoupled; the coupling lives entirely in the **coordination layer** (3 hard-coded `IWorkflowGrain` references), so this issue opens that coordination layer with minimal, additive, zero-regression-risk change.

## What Changes

- Add an `owner-kind` dimension to `WorkDispatch` (`workflow` | `agent-job`) and branch on it at the three coordination weld points in `RunnerGrain`:
  - `IsWorkRunnableAsync`: `workflow` → ask `IWorkflowGrain` (unchanged); `agent-job` → ask `IAgentJobGrain` (work still valid / not cancelled).
  - `ReportResultAsync`: route to `IWorkflowGrain` or `IAgentJobGrain` by owner-kind.
  - `AssignWorkAsync`: accept either owner identity; no longer hard-rejects on missing `WorkflowRunId`.
- Introduce `AgentJobGrain` / `IAgentJobGrain`: an agent execution unit with its own lifecycle (`pending` → `running` → `completed` / `failed`) that owns its result and receives `ReportResult`. It dispatches directly to an idle Runner via `RunnerRegistry` (with retry/backoff when no slot is free) and **never touches `WorkflowBacklogGrain`**.
- Relax `WorkspaceManager.ensure` so it does not hard-require `repository.gitUrl` + `issue.number` when the work is an `agent-job` (it must accept a standalone workspace/`workDir` identity).
- Add a **minimal validation API**: `POST` one job (prompt + model + workspace) → returns the job result. This exists only to prove the engine runs end-to-end; it is not the product CLI (`mo agent run <name>` is a separate issue) and not the read-model / board surface (separate Visibility issue).
- Workflow path behavior is **zero change**: when `owner-kind = workflow`, all existing routing, scheduling, and regression tests are preserved.

## Capabilities

### New Capabilities

- `agent-job`: Standalone agent execution unit independent of any WorkflowRun — covers the `AgentJobGrain` lifecycle (`pending` → `running` → `completed` / `failed`), direct Runner dispatch via `RunnerRegistry` (bypassing `WorkflowBacklogGrain`), `WorkDispatch` `owner-kind` branching at the three Runner coordination points (`IsWorkRunnableAsync`, `ReportResultAsync`, `AssignWorkAsync`), result ownership, and the minimal end-to-end validation API.

### Modified Capabilities

<!-- None. The workflow coordination path keeps identical behavior when owner-kind = workflow; owner-kind is an additive dimension, not a spec-level requirement change. -->

## Impact

- `packages/server/src/Mohist.Server/Runner/Grains/IRunnerGrain.cs` — extend `WorkDispatch` with `OwnerKind` (`workflow` | `agent-job`) and an optional `AgentJobId` owner field; relax `WorkflowRunId` from required to owner-dependent.
- `packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs` — branch `IsWorkRunnableAsync` (line ~309), `ReportResultAsync` (lines ~163–173), and `AssignWorkAsync` validation (line ~140) by `OwnerKind`; workflow branch stays byte-for-byte equivalent.
- `packages/server/src/Mohist.Server/Agent/Jobs/IAgentJobGrain.cs` + `AgentJobGrain.cs` (new) — lifecycle state machine, result ownership, `ReportResultAsync` receiver, `RunnerRegistry`-direct dispatch with idle-slot lookup and retry/backoff.
- `packages/server/src/Mohist.Server/Agent/Jobs/AgentJobController.cs` (new, minimal) — `POST` endpoint that accepts `{ prompt, model, workspace }`, creates the `AgentJobGrain`, awaits completion, and returns the result. Validation scope only.
- `packages/runner/src/runtime/workspace.ts` — `WorkspaceManager.ensure` must accept an `agent-job` work item whose identity is just a workspace path / workDir, without forcing the issue-scoped worktree branch.
- `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowBacklogGrain.cs` — **not modified**; agent jobs explicitly bypass it.
- Tests: workflow regression suite (`workflow-run`, `workflow-engine`, `ralph-task-execution` specs) must stay green; new tests cover `AgentJobGrain` lifecycle, owner-kind routing at each of the three Runner weld points, and the end-to-end validation API.
- Non-Goals (explicitly out of scope): Agent entity / named agents, run read-model / board / activity projection, product CLI `mo agent run <name>`, any workflow scheduling path behavior change, ad-hoc non-issue workspace (v1 stays issue-scoped), consumer wiring, authority / permission model.
