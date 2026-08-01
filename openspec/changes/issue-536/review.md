# Review — issue-536: WorkflowRun State 启动期 backfill 并移除读路径兼容转换

Reviewed the current tree (`master..HEAD` on `mohist/run-wr_4cb905febea74fb2a47bddcaad5afc29`)
against the issue body, the two specs under `openspec/changes/issue-536/specs/`, `proposal.md`,
`design.md`, and `tasks.json`. The product-code delta on this branch is a single visibility
tightening in `WorkflowRunStateDataUpgrader.cs`; the upgrader, wiring, read-path repointing, and
tests landed earlier in `d3f992f00` (already on `master`). The review therefore covers the current
state of the relevant files, not just the 1-line diff.

## Verification (re-run on this branch)

- `dotnet build Mohist.sln -p:SkipWebBuild=true` → **0 warnings, 0 errors** (TreatWarningsAsErrors).
- `Mohist.Server.SpecTests` → **3711/3711** (includes `WorkflowRunStateDataUpgraderSpecs` 7/7,
  `WorkflowRunLegacyBindingSpecs` 1/1, and `WorkflowRunStoreSpecs.LoadAsync_MigratedFailedRun_RerunPersistsFreshStageAttempt`).
- `Mohist.Server.UnitTests` → **1737/1737** (reaches the converter via `InternalsVisibleTo`).
- `Mohist.Server.ArchTests` → **51/51**.

These match the figures recorded in `progress.txt`.

## What is correctly delivered

- **Converter confined to the cold-start boundary.** `MigrateLegacyWorkflowRunJson` is now
  `internal static` (`WorkflowRunStateDataUpgrader.cs:182`), matching the
  `WorkflowDispatchSnapshotDataUpgrader.StripDispatchSnapshots` convention. `AssemblyInfo.cs`
  grants `InternalsVisibleTo` only to `SpecTests`/`UnitTests`; no production assembly outside
  `Mohist.Server` can reach it. A `rg MigrateLegacyWorkflowRunJson` over production source returns
  exactly one caller — `WorkflowRunStateDataUpgrader.UpgradeAsync` (`:37`) — satisfying the
  `canonical-state-read-path` "converter confined to database initialization" requirement.
- **Enumerated read paths are clean.** The six files / seven call sites named in the plan
  (`WorkflowRunStore.Deserialize`, `WorkflowRunQuerier` ×2, `WorkflowQuerier`, `IssueMetricsQuerier`,
  `IssueReadModelLoader`, `ActiveSessionReconciler`) all deserialize directly via
  `JSON.Deserialize<WorkflowRun>` / `JsonSerializer.Deserialize<WorkflowRun>` with no converter call
  and no legacy-shape branch. Service-phase converter invocations = 0 (the issue's headline Done When
  criterion).
- **Upgrader matches the migration spec.** No-write preflight that names every failing run and
  writes nothing on failure (`:32-60`); `AsNoTracking` read so preflight cannot persist; online backup
  via `source.BackupDatabase` + `PRAGMA integrity_check`, with `:memory:` rejection and open-state
  restoration (`:131-180`); single-transaction commit over `Chunk(500)` fetches with
  `CurrentValue = OriginalValue + 1` per rewritten row and full rollback on any write failure
  (`:89-122`); byte-ordinal idempotency so canonical rows are untouched and a repeat run reports
  `CandidateCount=0, WrittenCount=0, BackupPath=null` (`:42, :67-68`); no lifecycle filter. Wired into
  `DatabaseInitializer.InitializeAsync` after `MigrateAsync` and before the dispatch-snapshot and
  Profile migrations (`DatabaseInitializer.cs:21-23`); a throw propagates and blocks the service phase.
- **Spec coverage is real.** `WorkflowRunStateDataUpgraderSpecs` exercises full migration, preflight
  failure naming, backup failure, atomic rollback, canonical no-op + idempotency, >500-candidate
  batching, and in-memory rejection. `WorkflowRunRerunMigrationSpecs` exercises the failed/exhausted
  recovery → migrate → load → rerun → reload scenario verbatim.

## Findings

### F1 — Two service-phase WorkflowRun State read entry points still branch on legacy-vs-canonical shape (FAIL)

The `canonical-state-read-path` spec is explicit and general, not limited to the six enumerated
converter call sites:

- Requirement 1: "Every WorkflowRun State read entry point … SHALL NOT parse the whole document to
  probe for historical fields … and SHALL NOT branch on legacy-versus-canonical shape."
- Requirement 2: "the read path's only obligation toward a non-canonical row is to leave it
  untouched … (no converter invocation, no rewrite, no legacy-shape branching)."

Two live service-phase control-plane queries that load WorkflowRun State violate both by parsing the
whole document and falling back from the canonical top-level field to the legacy annotation form:

1. **`WorkflowDefinitionResolver.LoadBoundProfileIdAsync`** —
   `packages/server/src/Mohist.Server/Workflow/Services/WorkflowDefinitionResolver.cs:232-259`.
   Reached from the public `LoadTemplateAsync` (`:55`). It does `JsonDocument.Parse(state)` (`:241`),
   reads the canonical `workflowProfileId` (`:243`), and on miss falls back to
   `metadata.annotations.workflowProfileId` (`:249-256`).

2. **`WorkflowProfileDeletionBlockerQuery.ReadProfileId`** —
   `packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileDeletionBlockerQuery.cs:87-119`.
   Reached from `ListActiveRunsAsync` → the public blocker query (`:82`). Same shape:
   `JsonDocument.Parse(state)` (`:92`), canonical `workflowProfileId` first (`:97`), then legacy
   annotation fallback (`:103-111`).

Why this is in scope and must be fixed:

- T-001's converter promotes `metadata.annotations.workflowProfileId` to a top-level
  `workflowProfileId` (`WorkflowRunStateDataUpgrader.cs:190, :341-344`), so after startup migration
  every run that had a profile binding carries the top-level field. The annotation fallbacks in both
  readers are therefore dead code in the post-migration service phase — exactly the "读路径保留历史
  格式分支" the issue's Behavior Contract says must not remain.
- These are not converter invocations, so the issue's literal Done When ("转换器调用数为 0") is met,
  but the spec this issue delivers (Requirement 1/2) prohibits the *branching pattern itself*,
  independent of the converter. Leaving the dead branches keeps a legacy-shape decision on the read
  path and contradicts the issue's stated goal of canonical-only reads.
- The canonical-only pattern already exists in the same file:
  `WorkflowDefinitionResolver.ReadWorkflowProfileId` (`:273-288`) reads only the top-level
  `workflowProfileId` with no legacy fallback. The fix is to drop the annotation fallback from both
  readers (collapse each to its canonical branch, mirroring `ReadWorkflowProfileId`); post-migration
  behavior is unchanged because the canonical field is always present.

Suggested fix (for the follow-up task, not this review): in
`WorkflowDefinitionResolver.LoadBoundProfileIdAsync` and
`WorkflowProfileDeletionBlockerQuery.ReadProfileId`, remove the `metadata`/`annotations`
fallback blocks so each returns the top-level `workflowProfileId` (or null) directly. No test
change expected beyond confirming existing profile-binding/blocker specs stay green on migrated
data; if any spec seeds a legacy-annotation-only run it should migrate it through the upgrader first
(as `WorkflowRunLegacyBindingSpecs` already does).

## Notes (non-blocking)

- `progress.txt` and the design accurately describe the implemented behavior; the verification numbers
  reproduce. `design.md` Decision H and the spec scenario enumerate only the six converter call sites,
  which is why F1 was not caught at plan time — the enumerated scope is narrower than the spec's
  general "every read entry point" wording.
- Open Questions (backup retention, converter retirement) are explicitly deferred as Non-Goals and
  need no action here.

<promise>FAIL</promise>
