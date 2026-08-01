# Review — issue-538 (log path eliminates WorkflowRun State full-load read)

Reviewed against the issue's acceptance criteria and the plan artifacts under
`openspec/changes/issue-538/` (`proposal.md`, `design.md`, `specs/`, `tasks.json`).
Build + full test suite run locally: `npm test` is green
(Server UnitTests 1736, SpecTests 3637, ArchTests 51, Cli 1509, Runner 1510, Slack 8;
`TreatWarningsAsErrors` clean).

## What landed and is solid

- **All three `State` deserializations on the log path are gone.**
  `TaskLogService` (`packages/server/src/Mohist.Server/Runner/Services/TaskLogService.cs`)
  now depends only on `IWorkflowRunWorkProjection`; the `WorkflowRunQuerier` and
  `Workflow.Domain.Run` imports are removed. The interface exposes no
  State-deserializing member, so the no-`State` invariant is enforced by the type
  system, exactly as `design.md` D3 specified. Acceptance, fan-out scope, and
  empty-page-on-miss behavior are preserved (verified by the rewritten
  `TaskLogServicePersistThenPublishSpecs`, including the `ProjectIdLookups == 0`
  assertion that proves `GetProjectIdAsync` is skipped when `taskId` is null).
- **Projection read-model is correct and matches the spec.**
  `WorkflowRunWorkProjectionBuilder.Build`
  (`Infrastructure/Data/Workflow/WorkflowRunWorkProjection.cs`) produces the
  run-wide `(TaskId, WorkId)` map (with `EffectiveWorkId = WorkId ?? Id` so unset
  work ids stay well-defined) and the single active-work identity. The active-work
  computation matches the old `FindActiveWork` semantics:
  `CurrentActiveWorkFor(assignedWorkerId)` + `ActiveWorkId/ActiveWorkerId`
  equality is equivalent to the prior `runnerId == assignedWorkerId &&
  active.WorkId == workId` check (verified against `WorkflowRun.Work.cs:65-91,216-239`).
- **Write-maintenance is on the single funnel and on delete.**
  `WorkflowRunStore.StageRunAsync` rebuilds the projection on both `SaveAsync`
  overloads; `DeleteAsync` clears map rows
  (`Infrastructure/Data/Workflow/WorkflowRunStore.cs:126-208`). `WorkflowRunStoreProjectionSpecs`
  covers multi-stage tasks, active→inactive transition, and delete cleanup.
- **Backfill upgrader is sound.** `WorkflowRunWorkProjectionDataUpgrader`
  preflights every row via the current model + shared builder, writes only
  mismatches in one transaction, leaves `State`/`ETag` untouched, and is ordered
  after `WorkflowRunStateDataUpgrader` (#536) in `DatabaseInitializer.cs`.
  `WorkflowRunWorkProjectionDataUpgraderSpecs` covers terminal+active backfill,
  idempotence, and preflight-no-write-on-failure.
- **Structural no-`State` read invariant is tested at the implementation layer.**
  `WorkflowRunWorkProjectionTests` uses a `DbCommandInterceptor` to assert no
  projection read SQL references the `State` column.
- Migration/snapshot wiring is internally consistent; the migration-tests that
  roll back schema correctly switched to explicit column reads
  (`TypedWorkflowRunLineageMigrationSpecs.cs`).

## Findings

### F1 (must-fix) — Promised design-doc update not performed; `run-state.md` gap section is now stale

`proposal.md` "Impact" explicitly commits:

> **Design docs (`design/`):** `design/workflow/run-state.md` 现状差距小节 — the
> "日志路径把整载读当廉价查询" gap item moves to closed; `design/task-log.md` notes
> the resolution path no longer crosses `State`.

Neither happened — `git diff origin/master..HEAD -- design/ docs/` is empty, and
no task in `tasks.json` covers it. Concretely,
`design/workflow/run-state.md:85-87` still lists as an open gap:

> 日志路径把整载读当廉价查询：日志上传单次请求对同一 run 做两次整载读（活跃校验与
> publish scope 解析各一次），日志查询每次轮询再做一次整载读，而所需信息只是
> taskId ↔ workId 映射与活跃 work 判定（`TaskLogService`）。

After this change the upload does **zero** `State` loads (active-work gate +
publish-scope both via the projection) and the query does **zero**, so the bullet
now misdescribes the current state. Per `AGENTS.md`, the 现状差距 section is the
place that tracks "what is still broken vs. the spec" — leaving a closed gap
marked open misleads future prioritization of the *remaining* real gaps in that
section (e.g. the status ETag cache, which is #539's, not this one).

**Fix:** in `design/workflow/run-state.md`, remove or rewrite the lines 85-87
bullet to reflect that the log path now reads the run-work projection (or, if the
doc update was intentionally descoped, reconcile `proposal.md`'s Impact to say
so). The `design/task-log.md` note is optional polish (that doc is high-level and
does not currently describe the State-crossing resolution path, so there is no
stale fact there).

### F2 (non-blocking, latent) — `ResolveTaskIdAsync` assumes WorkId uniqueness the projection does not guarantee

`WorkflowRunWorkProjection.ResolveTaskIdAsync`
(`Infrastructure/Data/Workflow/WorkflowRunWorkProjection.cs:80-92`) uses
`SingleOrDefaultAsync` over `WHERE WorkflowRunId = .. AND WorkId = ..`. The map is
built with `.Distinct()` on the full `(TaskId, WorkId)` record
(`WorkflowRunWorkProjectionBuilder.Build`, same file lines 19-23), so two entries
that share a `WorkId` but differ in `TaskId` are both kept. If such a collision
ever occurs, `SingleOrDefaultAsync` throws `InvalidOperationException`, which
surfaces as a 500 on a log query — violating the
"unresolvable taskId returns an empty page, never an error" contract
(`specs/task-log-stateless-read/spec.md:57-61`).

Under today's identity invariant (`WorkId == Id` for claimed tasks; pending tasks
get `EffectiveWorkId = Id`, which equals their own distinct `Id`) this is **not
reachable**, so it is not a live bug. But `design.md` D1 carries `WorkId`
explicitly "so it stays correct if a future change lets `WorkId` diverge from
`Id`" — i.e. the storage is built to tolerate divergence, while this read is not.
That is a half-implemented robustness. Cheap fix if desired: `FirstOrDefaultAsync`,
or add a one-line uniqueness assumption note.

## Minor observations (not findings, no action required)

- `MohistDbContextModelSnapshot.cs` lists `WorkflowRunTaskMapRow.WorkflowRunId`
  and `TaskId` without `.IsRequired()` while `WorkId` has it. They are key
  columns, so EF treats them as required by convention; the live migration and
  `MohistDbContext` entity config both mark them `nullable: false`. Cosmetic
  snapshot inconsistency only.
- The upload read path does 3 indexed round-trips (`IsActiveWorkAsync` +
  `ResolveTaskIdAsync` + `GetProjectIdAsync`) vs. the old 2 full `State`
  deserializations — a net win, and `design.md` Open Question #2 explicitly left
  the projectId fold as an implementation-time decision. Acceptable.

## Verdict

The functional change fully meets the issue's acceptance criteria and the
`tasks.json` task graph: the log path no longer deserializes `State`, the
projection is write-maintained on the single funnel + backfill, and the contract
is preserved with strong test coverage. The one must-fix is F1 — a design-doc
update that `proposal.md` explicitly promised and no task delivered, leaving
`design/workflow/run-state.md`'s gap section inaccurate.

<promise>FAIL</promise>
