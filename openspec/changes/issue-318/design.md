## Context

`WorkflowRunStatus` (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.cs:15`) currently has seven values — `Pending, Running, AwaitingApproval, Paused, Stopped, Completed, Failed` — where a single `Running` conflates three scheduling realities: *waiting for any runner to claim*, *claimed and waiting for pickup*, and *executing*. The status is a plain public setter (`WorkflowRun.cs:34`) with no central transition table; guards are scattered as ad-hoc `if (run.Status …)` checks across eight partial files (19 write sites total).

This conflation is the structural root cause of the `otel.db` trace explosion: the runner poll loop (`RunnerGrain.PollAssignedOrAssignableWorkflowAsync`, `RunnerGrain.cs:682-711`) calls `FindAssignedToAsync`, which filters only on the `AssignedRunnerId` computed column (`WorkflowRunQuerier.cs:34-46`) and so returns ~all assigned rows including terminal corpses. The loop then fans out a `GetCurrentWorkIdAsync` cross-grain call per row (~104/s) to re-derive "is this busy", because `FindAssignableAsync` (`WorkflowRunQuerier.cs:48-89`) cannot push a status filter to SQL — there is **no** `status` computed column on `WorkflowRuns` (unlike `IssueRow`/`AgentRow`), so it deserializes each `State` JSON blob and re-filters in memory.

Persistence is SQLite with `State` stored as one camelCase JSON column (`System.Text.Json` + `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`, so `status` serializes as `"running"`). Three computed columns exist (`MetadataProjectId` STORED; `CreatedAt`, `AssignedRunnerId` virtual), all via `json_extract`. The UI never renders `WorkflowRun.status` directly — run status reaches the web as a lowercase string via `WorkflowStatusMapper.BuildStatusView` (`FrontendStatus(run.Status.ToString())`) on the `GET /issues/{n}/workflow/status` endpoint, consumed as `WorkflowTimeline.status: string`.

Stakeholders: workflow runtime (core state machine), persistence (migration + reclassification), runner scheduler (poll loop), runner-side cleanup guard (`packages/runner/src/runtime/workflow-terminal-status.ts`), web UI. Constraints from `design/architecture.md`: status adjudication belongs to the Server control plane; the Runner only reports facts — it must not write `run.Status`.

## Goals / Non-Goals

**Goals:**
- Make each `WorkflowRunStatus` value denote exactly one scheduling "waiting object" and responsible party, per the `workflow-run-lifecycle` spec.
- Let the two scheduling queries filter on `status` at the DB layer (no `State` deserialization of non-matching rows).
- Remove the per-row `GetCurrentWorkIdAsync` busy pre-check from the poll loop.
- Reclassify historical persisted runs to their true new status and keep the workspace-cleanup guard correct under the new vocabulary.
- Distinguish "待分配" (`Pending`) from "已分配待执行" (`Ready`) in the UI.

**Non-Goals:**
- Sticky-assignment semantics (binding stays on `Assignment.RunnerId`; `Ready` is only a status transition).
- Lock-wait states (runtime-only, not expressible in `status`).
- `otel.db` resource-attribute / sampler remediation and the 1s poll interval.
- Introducing a `TimeProvider` seam into the domain (pre-existing `DateTimeOffset.UtcNow` usage left as-is; new specs assert on `Status`, not timestamps).

## Decisions

### D1 — Repurpose `Pending`, add `Created`/`Ready`, narrow `Running`
New enum order: `Created, Pending, Ready, Running, AwaitingApproval, Paused, Stopped, Completed, Failed` (`WorkflowRun.cs:15`). `Created` absorbs the old "built, not started" meaning; `Pending` is repurposed to "started, unassigned, waiting for claim"; `Ready` is new ("assigned, idle, waiting for pickup"); `Running` narrows to "has in-flight work".

- *Alternative considered:* add `Ready`/`Created` without repurposing `Pending` (keep `Pending`="not started"). Rejected — the product vocabulary (`design`'s domain analysis and the issue's unified-language table) deliberately maps to an OS-scheduling ready/running model where `Pending`="待分配". Keeping the old `Pending` meaning would perpetuate the conflation the change exists to fix.
- *Breaking consequence:* persisted JSON (`"status":"pending"`/`"running"`) changes meaning — addressed by D5 (migration). JSON enum value casing is stable (always camelCase), so the persisted string shape does not change format, only which value a given row carries.

### D2 — Keep the scattered-guard pattern; audit every write site rather than build a central transition table
The domain mutates `Status` through ~19 sites across the partial files and `Advance()` (`WorkflowRun.Stage.cs:67-72`). We will **not** introduce a `CanTransition(from,to)` matrix or privatize the setter in this change — that is a larger refactor with its own blast radius and is out of scope for a high-risk state-machine redefinition.

Instead, audit and correct each write site to the new machine. The specific transition-point changes (derived from the spec and the current write-site inventory):

| Command | Site | Old target | New target |
|---|---|---|---|
| `Create` | `WorkflowRun.Lifecycle.cs:33,69` | `Pending` | `Created` |
| `Start` | `WorkflowRun.Lifecycle.cs:96` | `Running` | `Pending` (guard: admit `Created`/`Paused`) |
| `AssignRunner` | `WorkflowAssignment.cs:20` (no status write) + grain guard `WorkflowGrain.cs:264` (`Status != Running` rejected) | — | domain writes `Ready` when assignment is made on a `Pending` run with dispatchable work; grain guard admits `Pending` |
| `StartTask`/`PollWork` | `WorkflowGrain.cs:280-305` (guard `Status != Running`→null at `:282`) | implicit `Running` | guard admits `Ready` and `Running`; transition `Ready → Running` when work is picked |
| `CompleteTask`→`Advance` default | `WorkflowRun.Stage.cs:67-72` switch `_ => Running` | `Running` | `_ => Ready` if assigned & dispatchable work remains, else `Pending` if unassigned, else terminal/next-stage |
| `Pause`/`Resume` | `WorkflowRun.Lifecycle.cs:107,120` | `Running`/`Paused`↔`Running` | `Pause`: admit any executing state→`Paused`; `Resume`: `Running` if in-flight work exists else `Ready` |
| `Approve`/`Reject`/`Fail`/`Stop` | `Approval.cs`, `Task.cs`, `Check.cs`, `Failure.cs`, `Lifecycle.cs` | (mostly terminal, correct) | `Approve`→`Ready` (via `Advance` default above); fail/stop/reject targets unchanged |

A small, consolidated helper `IsTerminal()` (currently duplicated only in the grain at `WorkflowGrain.cs:492-496`) is added to the domain so the runner-side, querier, and grain share one terminal set.

- *Alternative considered:* centralize all transitions behind a single `Transition(command)` method. Rejected for scope/risk; flagged in Open Questions for a follow-up.

### D3 — `status` as a STORED, casing-normalized computed column + index
Add to `WorkflowRuns` (`MohistDbContext.cs:362-377`):

```sql
LOWER(COALESCE(json_extract(State, '$.status'), json_extract(State, '$.Status')))
```

as a **STORED** computed column, with a new `IX_WorkflowRuns_Status` index.

- *Why STORED (per spec):* the column is read on every poll (~1/s/runner). Materializing the one enum string keeps the index covering and avoids per-read recomputation; the write cost (recompute on each persist) is negligible for a tiny scalar. The existing `MetadataProjectId` column is already STORED on this table — same precedent.
- *Why `LOWER(COALESCE($.status, $.Status))`:* the property path is always `status` (camelCase via `JsonSerializerDefaults.Web`), and the enum value is always camelCase via `JsonStringEnumConverter`. `COALESCE` mirrors the established `IssueRow`/`AgentRow` path-robustness pattern (`MohistDbContext.cs:289-290`); `LOWER` is belt-and-suspenders against any historical PascalCase value — the spec explicitly requires casing normalization.
- *SQLite workaround:* SQLite cannot `ALTER TABLE ADD COLUMN … STORED` directly. Use the established two-step from `20260629112745_AddAgentLaunchLabelComputedColumns.cs:20-26`: `AddColumn` as a nullable plain column, then `AlterColumn` to the STORED computed definition so EF emits the automatic table rebuild.
- *Alternative considered:* virtual computed column (also indexable in SQLite). Rejected — the spec mandates STORED, and materializing avoids recomputation cost on the hot read path.

### D4 — Collapse both scheduling queries to pure DB filters; update the residual fan-out
- `FindAssignableAsync` (`WorkflowRunQuerier.cs:48-89`) becomes `Where(status == Pending)` (+ optional `projectId`), dropping the deserialize-and-re-`NextWork()` loop entirely. `NextWork()` is no longer a query-time concept.
- `FindAssignedToAsync` (`:34-46`) becomes `Where(status == Ready && AssignedRunnerId == runner)`.
- `PollAssignedOrAssignableWorkflowAsync` (`RunnerGrain.cs:686-696`) drops the `GetCurrentWorkIdAsync` busy pre-check — `Ready` already excludes in-flight work, so every surfaced row is directly `PollWorkAsync`-able.
- **Necessary follow-on (not in the issue text but required for correctness):** `ActiveWorkflowCountAsync` (`RunnerGrain.cs:272-280`) has the identical `FindAssignedToAsync`→`GetCurrentWorkIdAsync` fan-out and is used to count in-flight work. Post-change, `FindAssignedToAsync` returns only `Ready` (idle) rows, so its `GetCurrentWorkIdAsync` count would collapse to 0. This site must be rewritten to count `status == Running` rows (e.g., a new `CountRunningAssignedToAsync(runner)` query) or to rely on the runner's own persisted works state.

### D5 — Historical reclassification via conditional SQL migration (+ activation reconciliation)
A new EF migration does two things: (a) adds the STORED `status` column (D3), and (b) reclassifies every persisted run using its assignment + in-flight-work facts, per the four spec scenarios. Because `WorkflowRun.State` JSON shape is fixed and camelCase, the reclassification is expressible in SQL:

1. Old `"pending"` (built, not started) → `"created"`: `json_set(State,'$.status','created')`.
2. Old `"running"` with no `assignment` → `"pending"` (waiting for claim).
3. Old `"running"` with `assignment` and **in-flight work** → stays `"running"`. In-flight = any stage has a task with `"status":"running"` or a non-null `checksWorkId`, detected via a `json_each` subquery over `$.stages`.
4. Old `"running"` with `assignment` and **no** in-flight work → `"ready"`.

Terminal rows (`completed`/`failed`/`stopped`) and `paused`/`awaitingApproval` are already correct and untouched.

- *Self-healing safety net:* even if the SQL in-flight detection is imperfect for edge JSON shapes, a `Ready`/`Pending` run is by definition non-terminal and its grain will re-activate and re-persist the adjudicated status. The risk window is bounded to runs that never re-activate — but those are already terminal (unaffected). To close the one real gap (a row wrongly placed in `Ready` that actually has in-flight work would then be blocked by the `PollWorkAsync` status guard), add a tiny activation-time reconciliation in the domain/grain: *if loaded status is `Ready` but in-flight work exists, correct to `Running` before serving*. This mirrors the existing read-side shim pattern (`WorkflowRunStore.MigrateAssignmentJson`, `WorkflowRunStore.cs:133-171`).
- *Alternative considered:* reclassify purely in code by activating every grain. Rejected — many historical runs are terminal/inert and activating them all is expensive and unnecessary; SQL handles the bulk, the shim handles the tail.
- *Precedent:* `20260629120000_BackfillIssueCompletedAt` (conditional `json_set` backfill) and `20260625000000_EpicIdleRename` (enum rename) — tested by `BackfillIssueCompletedAtMigrationSpecs` / `EpicIdleRenameMigrationSpecs`.

### D6 — UI via the existing `WorkflowTimeline.status` string, not the disconnected model type
The web's `WorkflowRunStatus` union (`packages/web/src/entities/issue/model/workflow-run.ts:4`, `'running'|'passed'|'failed'|'cancelled'`) is **stale and unused** — it neither matches the server enum nor is rendered anywhere. Run status already reaches the UI as a lowercase string through `WorkflowStatusMapper.BuildStatusView` → `WorkflowStatusView.status` → `GET /issues/{n}/workflow/status` → web `WorkflowTimeline.status: string`.

- Extend `FrontendStatus` (`WorkflowStatusMapper.cs:10-13`) to emit `created`/`ready` for the new values (and keep `awaiting-approval`/`paused` mapping).
- Fix the `BuildPendingWork` guard (`WorkflowStatusMapper.cs:191`, currently `Status != Running`→null) to show pending work for `Ready` and `Running`.
- In the web, render `WorkflowTimeline.status` (`pending`/`ready`/`running`) as three distinct presentations where the run-level status is shown, so capacity-shortage vs stuck-runner are diagnosable. Update or remove the dead `WorkflowRunStatus` model type to avoid future confusion.
- *Alternative considered:* build a dedicated run-status badge on the stale `WorkflowRun` fetch path. Rejected — `WorkflowRun` is never fetched by any hook today; `WorkflowTimeline.status` is the live carrier and the lower-risk surface.

### D7 — Runner-side cleanup guard: extend the union type only
`packages/runner/src/runtime/workflow-terminal-status.ts:18-31` defines `WorkflowRunStatusName` and `TERMINAL_WORKFLOW_STATUSES` (only the 3 terminal values). The cleanup safety logic is already correct by construction (everything not in the terminal set blocks removal). The only change needed is to **extend the `WorkflowRunStatusName` union** to include `"Created"` and `"Ready"` so the type contract matches the new vocabulary — no behavioral change required. This satisfies the `runner-workspace-cleanup` spec delta.

## Risks / Trade-offs

- **[Missed write site → inconsistent status]** The 19 scattered `Status =` sites and the `Advance()` default are the primary risk driver. -> Mitigation: exhaustive audit table (D2); add a domain unit test per transition point (`Specs/Workflow/Domain/`); the activation-time reconciliation (D5) catches residual drift at runtime.
- **[Migration in-flight detection wrong]** The `json_each` in-flight subquery (D5) is the most fragile SQL. -> Mitigation: a `*MigrationSpecs` test seeds old-`running` rows with each assignment/task shape and asserts the reclassified value, following `BackfillIssueCompletedAtMigrationSpecs`; the activation shim is the runtime backstop.
- **[`ActiveWorkflowCountAsync` silently returns 0]** D4's residual fan-out is not named in the issue. -> Mitigation: explicitly rewrite it to count `Running` rows; flag in the task list.
- **[STORED column add on a large table]** The `AlterColumn`-driven table rebuild rewrites every `WorkflowRuns` row. -> Mitigation: `WorkflowRuns` is modest-sized; the rebuild is a one-time cost at deploy. Acceptable; note in migration plan.
- **[UI gap if `WorkflowTimeline.status` isn't actually wired]** The field is currently inert (passed into `deriveRuntimeDecision` but unread). -> Mitigation: verify the render path during build; the spec scenario is a visual-distinction assertion that will catch a missing wire-up.
- **[Breaking enum meaning for any out-of-tree consumer]** Repurposing `Pending` is a semantic break. -> Mitigation: the enum is internal (server domain + runner string contract + web string); no external API contract depends on the integer value (enums serialize as strings).

## Migration Plan

1. **Code first, behind the new state machine:** update the enum, all write sites (D2), the queries (D4), the poll loop, `ActiveWorkflowCountAsync`, `WorkflowStatusMapper`, and the runner-side union type (D7). Unit/spec tests for each transition pass against the new in-memory model.
2. **Add the migration** (`yyyyMMddHHmmss_WorkflowRunStatus.cs`): (a) two-step STORED `status` column + `IX_WorkflowRuns_Status` (D3); (b) the conditional `json_set` reclassification (D5). Tested by a new `WorkflowRunStatusReclassificationMigrationSpecs` (conditional backfill) + a schema spec (STORED column + index) following `InboxItemsMigrationSpecs`.
3. **Deploy:** `mo update server`. On startup EF applies the migration (column add + table rebuild + reclassification). The activation-time shim (D5) corrects any residual misclassification as non-terminal grains re-activate.
4. **Rollback:** there is no automatic `Down` for the reclassification (it is destructive on the old `status` values). Rollback strategy is restore-from-backup of the SQLite DB taken before `mo update`. The schema `Down` (drop column + index) is provided but does not restore pre-migration `status` semantics. This is acceptable because the change is forward-only (per AGENTS.md, the project is in active development with no version-compatibility requirement).

## Open Questions

- **D2 follow-up:** should we privatize the `Status` setter and centralize transitions in a follow-up issue? Out of scope here, but the scattered-guard smell persists after this change.
- **D5 in-flight JSON paths:** the exact `$.stages[*].tasks[*].status` / `checksWorkId` paths must be validated against a real persisted `State` sample during implementation (the explorer confirmed tasks carry `status` and stages carry `checksWorkId`, but nested-array `json_each` ergonomics need a spike).
- **D6 render placement:** exactly where in the issue-detail UI should the run-level `pending`/`ready`/`running` indicator live (issue header pill vs workflow stage bar vs `RuntimeDecisionSurface`)? The spec requires visual distinction; the precise component is an implementation-time decision against the existing `HealthPill`/stage-bar layout.
