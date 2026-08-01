## Why

The task-log read path treats a full WorkflowRun `State` deserialization as a cheap
metadata lookup. A single log-upload request does it twice — active-work gate then
publish-scope resolution — and every log-query poll does it once, yet each needs only
the `taskId ↔ workId` mapping and the active-work identity. With State averaging
~390 KB and peaking ~4.4 MB, every call pays a full STJ deserialize + Large-Object-Heap
allocation, and together with the status poll (#539) these are the dominant high-frequency
full-load sources driving the Server memory regression (Epic #65). This is the exact
anti-pattern `design/workflow/run-state.md` forbids: "只需 status 等标量字段的查询必须走
投影列，不反序列化 State".

## What Changes

- **Log upload no longer deserializes State.** `TaskLogService.AppendAsync` resolves
  active-work membership (`IsActiveWorkAsync`) and the publish scope
  (`ResolvePublishScopeAsync`: `workId → taskId` + `projectId`) from a lightweight
  projection instead of `_runQuerier.LoadAsync`. Both full-load calls are removed.
- **Log query no longer deserializes State.** `TaskLogService.QueryByTaskIdAsync`
  resolves `taskId → workId` (`ResolveWorkIdAsync`) from the same projection instead
  of a full `State` load.
- **New run-work projection surface.** A write-maintained projection exposes, per
  WorkflowRun, the `taskId ↔ workId` mapping and the current active-work identity
  (`workId` + owning `workerId`), queryable without deserializing `State`. It is kept
  in step with `State` on every run write, following the same "project on write, never
  deserialize on read" rule already used by the `Status` / `AssignedWorkerId` /
  `MetadataProjectId` computed columns and the `DispatchSnapshotStore` table.
- **External contract unchanged.** The task-log upload and query HTTP shapes,
  acceptance semantics (outstanding → persist; unknown/stale → 4xx), fan-out scope,
  and pagination behavior are identical; only the read cost changes.
- **`projectId` already projection-satisfiable.** The publish scope's `projectId`
  reads the existing `WorkflowRunRow.MetadataProjectId` computed column; no new field
  is needed for it.

## Capabilities

- `run-work-projection`: The write-maintained, read-without-State projection of a
  WorkflowRun's work surface — the `taskId ↔ workId` mapping and the single current
  active-work identity (`workId` + `workerId`) — its maintenance contract on every run
  write, and its query API. This is the shared read-model the log path (and future
  read paths) consume.
- `task-log-stateless-read`: The task-log upload and query paths resolve work identity
  and active-work membership through the projection, never deserializing WorkflowRun
  `State`; acceptance, fan-out scope, and query results are unchanged from today's
  contract.

## Impact

- **Server (`packages/server`):**
  - `Runner/Services/TaskLogService.cs` — rewrite `ResolveWorkIdAsync`,
    `IsActiveWorkAsync`, and `ResolvePublishScopeAsync` to call the new projection
    instead of `WorkflowRunQuerier.LoadAsync`; remove all three `State` deserializations.
  - `Infrastructure/Data/Workflow/WorkflowRunQuerier.cs` (or a new projection store)
    — add lightweight query methods: `taskId → workId`, `workId → taskId`, and
    active-work membership, none of which deserialize `State`.
  - New projection persistence (table or computed columns) + write-time maintenance,
    wired into the run commit path (`WorkflowRunStore.StageRunAsync` and/or the grain
    commit), kept in lock-step with `State`.
  - EF Core migration for the new projection schema.
- **Design docs (`design/`):** `design/workflow/run-state.md` 现状差距小节 — the
  "日志路径把整载读当廉价查询" gap item moves to closed; `design/task-log.md` notes the
  resolution path no longer crosses `State`.
- **Tests:** spec coverage asserting the log upload and query paths no longer trigger
  `State` deserialization (e.g. a State-load spy/counter) while preserving acceptance,
  publish-scope, and pagination behavior; unit tests for the projection's write-maintenance
  and query correctness.
- **Runner / Web / CLI:** no change — the task-log wire contract is unchanged.
