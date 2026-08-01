# Self-Review — issue-536 plan (round 2)

Re-reviewed `proposal.md`, `design.md`, `tasks.json`, and both specs against the issue and the current
tree, after the round-1 fixes.

## Round-1 findings — verified fixed

- **B1 (was blocking):** `canonical-state-read-path` no longer claims deserialization "surfaces the
  inconsistency." Requirement 2 is now "Read paths do not convert un-migrated legacy rows"; its scenario
  asserts only non-conversion (no converter call, no rewrite, no legacy-shape branching). `design.md`
  Decision H documents the real STJ behavior (`JSON.Options` has no `UnmappedMemberHandling.Disallow`, so
  legacy fields are silently ignored), and `tasks.json` T-002 criterion 3 mirrors the corrected claim.
  Fixed.
- **A1:** "control-plane status queries" replaced with "control-plane queries that load WorkflowRun State"
  across proposal/design/tasks/spec; the shared `IWorkflowRunDeserializer` and the #538 status-cache note
  are referenced. Fixed.
- **A2:** Reconciled as **6 files / 7 call sites** (`WorkflowRunQuerier` has two) in proposal Impact,
  design Migration Plan, and tasks. Matches the `d3f992f00~1` call-site grep (7 call sites across 6 files).
  Fixed.

## New blocking problem

### N1. T-001 acceptance criterion contradicts T-001's own notes

`tasks.json`, T-001, final acceptance criterion:

> "Server builds with TreatWarningsAsErrors (C# lint); **the converter is reachable only from
> DatabaseInitializer, not from any service-phase read path.**"

T-001's own `notes` (same task) say:

> "The read path **still calls the converter until T-002**, so the system stays correct (just redundant)
> after this task alone."

These are mutually exclusive. By the task split, T-001 relocates the converter and keeps the system
correct by leaving the read paths calling it (repointed to its new home); T-002 is what removes those
calls. So at T-001 completion the converter **is** still reachable from service-phase read paths, and the
criterion as written would fail its own verification. The "reachable only from DatabaseInitializer, not
from any service-phase read path" obligation is T-002's deliverable (it is already captured in T-002
criterion 2 and in `canonical-state-read-path` requirement 3).

**Fix direction:** drop the "the converter is reachable only from DatabaseInitializer, not from any
service-phase read path" clause from T-001's acceptance criteria, leaving T-001's build criterion as just
"Server builds with TreatWarningsAsErrors (C# lint)." The confinement claim stays where it belongs — T-002
and the read-path spec. (Equivalently: if T-001 is meant to also delete the read-path calls, then the
two-task split collapses into one and T-002's existence is the contradiction — but the split is sound, so
the criterion is the part to remove.)

## Otherwise sound (checked)

- Spec↔task traceability, scenario hashtag levels (3×`### Requirement`, 4×`#### Scenario`), and "every
  requirement has ≥1 scenario" hold for both spec files.
- Startup-migration spec scenarios match the shipped upgrader and its tests: no-write preflight naming the
  run, clean-DB short-circuits without backup, online-backup + `PRAGMA integrity_check`, in-memory source
  rejected without altering open state, single-transaction commit with one ETag bump per rewritten row,
  >500-candidate batching still one transaction, canonical rows byte-stable, repeat-run no-op, `failed`
  rerun preserved (`WorkflowRunRerunMigrationSpecs`).
- `tasks.json` is valid JSON; DAG is valid (T-002 → T-001, strictly lower priority); every task has a spec
  ref and ≥1 acceptance criterion.
- Design Decisions A–H each carry rationale + a rejected alternative; Risks are in `[Risk] -> Mitigation`
  form; Migration Plan + Rollback are present and consistent with the WAL/online-backup constraint.
- The "254 / 110" figures appear only in the design's first-deployment narrative (evidence, not a
  migration precondition), consistent with the issue's "不写死为其它安装的迁移条件."

## Verdict

N1 is a self-contradictory, verifiable acceptance criterion inside T-001. The plan is not ready to build
until it is removed/relocated to T-002.

<promise>FAIL</promise>
