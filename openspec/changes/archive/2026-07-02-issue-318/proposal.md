## Why

The `WorkflowRunStatus` enum conflates fundamentally different scheduling states under `Running`: today the scheduler cannot tell a workflow that is "waiting for any runner to claim it" from one that is "claimed, queued, waiting for its runner to pick up work" or "actively executing". This single ambiguity is the structural root cause of `otel.db` bloat (94 GB / 169M spans over 3.5 days): `FindAssignedToAsync` filters only by `AssignedRunnerId`, so every runner poll (~1/s) deserializes ~179 assigned rows of which ~177 are already terminal, then fans out ~104 `GetCurrentWorkIdAsync` grain calls/s against corpses. It also makes the UI unable to distinguish "no runner capacity" from "a runner is stuck" — two different operational diagnoses that look identical today.

## What Changes

- **Redefine `WorkflowRunStatus`** to make each status's "waiting object" explicit and singular: `Created` (built, not started; was `Pending`), `Pending` (started, has work, unassigned — waiting for *any* runner), `Ready` (assigned, has work, waiting for *its* runner to pick up; new), `Running` (has in-flight work executing), plus unchanged `AwaitingApproval`, `Paused`, `Stopped`, `Completed`, `Failed`. **BREAKING**: the `Pending` value is repurposed and `Running`'s semantics narrow; persisted `State` JSON and every consumer change shape.
- **Implement the transition rules** as the single state machine: `Start`→`Pending`, `AssignRunner`→`Ready`, `StartTask`(pick work)→`Running`, `CompleteTask` with remaining work→`Ready` (natural re-readiness), `Advance` with no pending→`Completed`/next stage, plus `Pause`/`Resume`/approval-`Approve`/`Fail`/`Stop` each landing on the correct status. Every `run.Status =` write site is reviewed for consistency.
- **Persist `status` as a STORED computed column** on `WorkflowRuns` (`json_extract(State, '$.status')` with a `LOWER` normalization for enum-case), so the DB can filter on it without deserializing rows into memory.
- **Collapse the two runner scheduling queries to pure DB-layer status filters**: `FindAssignableAsync` = `status == Pending`; `FindAssignedToAsync` = `status == Ready && assigned == runner`. This removes the in-memory `Status`/`Assignment`/`NextWork()` re-filter loop and the per-row `WorkflowRun` deserialization from the hot poll path.
- **Drop the `GetCurrentWorkIdAsync` busy pre-check** from `RunnerGrain.PollAssignedOrAssignableWorkflowAsync`: since `Ready` already excludes in-flight work, every workflow surfaced by `FindAssignedToAsync` is directly `PollWorkAsync`-able — eliminating ~104 grain calls/s.
- **Migrate historical data**: reclassify every persisted `Running` row to its true new status using its assignment and in-flight-work facts, so existing runs land in the right `Created`/`Pending`/`Ready`/`Running` bucket.
- **Surface the new states in the Web UI**, at minimum distinguishing "待分配 runner" (`Pending`) from "已分配待执行" (`Ready`), so the two failure modes (capacity shortage vs stuck runner) become diagnosable separately.

## Capabilities

### New Capabilities
- `workflow-run-lifecycle`: The domain contract for the `WorkflowRun` lifecycle state machine — the `WorkflowRunStatus` enumeration and its transition rules (each status's "waiting object" and responsible party), the persistence/query contract that lets the scheduler filter by `status` directly at the DB layer (the STORED `status` computed column and the status-filtered `FindAssignableAsync`/`FindAssignedToAsync`), the runner poll loop's direct-pickup behavior (no busy pre-check), the historical-data reclassification rule, and the Web UI's status surface that distinguishes the two waiting kinds. Establishes the state machine as a specified contract rather than an undocumented enum.

### Modified Capabilities
- `runner-workspace-cleanup`: The non-terminal-state enumeration in the "Non-eligible workspaces are never auto-cleaned" requirement changes — `Created`, `Pending`, `Ready` join `Running`, `Paused`, `AwaitingApproval` as states that block automatic workspace removal, so the cleanup safety guard stays correct under the new state vocabulary.

## Impact

- **Server domain** (`packages/server/src/Mohist.Server/Workflow/Domain/Run/`): `WorkflowRunStatus` enum redefined; `WorkflowRun` and every command handler that writes `Status` (start, assign-runner, start-task, complete-task, advance, pause, resume, approve, fail, stop) updated to the new transitions.
- **Server persistence** (`packages/server/src/Mohist.Server/Infrastructure/Data/`): `WorkflowRuns` table gains a STORED `status` computed column; new EF Core migration (computed-column + index) plus a data-reclassification step. `WorkflowRunQuerier.FindAssignableAsync`/`FindAssignedToAsync` rewritten as pure DB filters.
- **Server runner** (`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs`): `PollAssignedOrAssignableWorkflowAsync` removes the `GetCurrentWorkIdAsync` busy check (lines ~686–696).
- **Web UI** (`packages/web/src/entities/issue/model/workflow-run.ts` and status-badge surfaces): the `WorkflowRunStatus` projection type and its rendering updated to distinguish `Pending` vs `Ready`.
- **Consumers of the enum** (status badge, workspace-cleanup guard, any query/dashboard that keys off workflow run status) audited for the new vocabulary.
- **Risk**: high — touches the workflow runtime core state machine, a persistence migration with historical reclassification, and cross-cutting surfaces (domain / query / runner / UI). Blast radius and transition-point consistency are the primary risk drivers.
- **Non-goals** (per issue): sticky-assignment semantics unchanged (`Assignment.RunnerId` still carries the binding, `Ready` is only a status-machine transition); lock-wait states out of scope; `otel.db` resource-attribute / sampler remediation and the 1s poll interval are independent.
