# Self-Review — issue-536 plan

Reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/workflow-run-state-startup-migration/spec.md`,
`specs/canonical-state-read-path/spec.md` against the issue body and the current tree.

Overall the plan is coherent, maps cleanly to the issue's Behavior Contract and Done When, the two
capabilities match the two spec directories, and the task graph is a valid DAG (T-002 → T-001). The
specs largely track the already-shipped implementation in `d3f992f00`. Two problems block a clean
build, both concentrated in the read-path spec.

## Blocking problems

### B1. `canonical-state-read-path` spec asserts deserialization behavior that contradicts the configured serializer

`specs/canonical-state-read-path/spec.md`, requirement "Read paths do not mask un-migrated legacy rows"
and its scenario state:

- "...encountering one in the service phase is a defect **signaled by failed deserialization**..."
- "...deserialization against the current model **SHALL surface the inconsistency rather than mask it**"

This is factually wrong for the configured options. `JSON.Options`
(`packages/server/src/Mohist.Server/Infrastructure/JSON.cs:11`) is built from
`JsonSerializerDefaults.Web` with `PropertyNameCaseInsensitive = true` and **no**
`UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow`. System.Text.Json therefore **silently
ignores** unmapped legacy fields (`claim`, task-level `runnerId`, `dispatchActivated`, annotation-only
`workflowProfileId`, legacy recovery without `recoveryRemaining`) and returns a degraded `WorkflowRun`
(null assignment/workerId, defaulted recovery) instead of throwing. A scenario that mandates "surface
the inconsistency via failed deserialization" is not testable as written — the existing code does not
satisfy it and cannot without an out-of-scope global serializer change.

**Fix direction:** keep the correct, testable claim ("a read entry point SHALL NOT convert, rewrite, or
otherwise normalize a legacy-shaped row; zero converter invocations on the read path") and drop the
"signaled by failed deserialization / surface the inconsistency" assertion. The real safety property is
that startup migration blocks the service phase (T-001), so a legacy row never reaches a read path; the
read-path obligation is non-conversion, not detection.

## Accuracy problems (should fix)

### A1. Read-entry-point enumeration and the "status query" example are stale post-#538

`proposal.md` (Impact), `design.md` (Context), `tasks.json` (T-002 description) and the
`canonical-state-read-path` requirement 1 all describe **"control-plane status queries"** as a
State-deserializing read path and list six entry points. After issue #538 landed:

- `WorkflowQuerier.GetStatusAsync` (`Workflow/Services/WorkflowQuerier.cs:41`) no longer deserializes
  State on every call — it is ETag-gated through `WorkflowRunStatusCache` (line 51) and only deserializes
  on a cache miss.
- A central `IWorkflowRunDeserializer` (`Workflow/Services/WorkflowRunDeserializer.cs:12`,
  injected at `WorkflowQuerier.cs:23,75`) now performs the canonical deserialize.

For #536's own scope (commit `d3f992f00`) the six-file enumeration is correct; the staleness is only in
the spec's example wording. **Fix direction:** in the read-path spec, reword "control-plane status
queries" to "control-plane queries that load WorkflowRun State" and reference the central deserializer,
so the target behavior matches the current tree rather than the pre-#538 shape.

### A2. "7 production files" (issue) vs "6 entry points" (plan)

The issue states conversion calls were spread across 7 production files; the plan lists 6
(`WorkflowRunStore`, `WorkflowRunQuerier`, `WorkflowQuerier`, `IssueMetricsQuerier`,
`IssueReadModelLoader`, `ActiveSessionReconciler`), matching the actual `d3f992f00~1` call-site grep
(six distinct files; `WorkflowRunQuerier` has two call sites). Not buildability-blocking, but the plan
should either reconcile the count or note that the issue's "7" likely counts a duplicate call site / the
converter host. Verify before build.

## Non-blocking observations

- **Implementation pre-exists the plan.** T-001 and T-002 are already shipped (`d3f992f00` relocated the
  converter and removed the read-path calls; the current tree confirms zero `MigrateLegacyWorkflowRunJson`
  callers outside `WorkflowRunStateDataUpgrader.cs:37,182`). The build phase is therefore largely
  verification-against-spec — except that, because of B1, the existing code does **not** satisfy the
  read-path "surface the inconsistency" scenario as written, so the spec fix must precede/ accompany
  build.
- Spec→task traceability, idempotency/preflight/backup/atomic-commit coverage, the `failed`-run rerun
  scenario, and the "no SchemaVersion / no SQL duplication" decisions all check out against the code and
  tests (`WorkflowRunStateDataUpgraderSpecs`, `WorkflowRunRerunMigrationSpecs`,
  `WorkflowRunLegacyBindingSpecs`).

## Verdict

B1 is a spec defect (a scenario asserts behavior the configured serializer does not provide), so the
plan is not ready to build as-is. A1/A2 are accuracy fixes that should land in the same pass.

<promise>FAIL</promise>
