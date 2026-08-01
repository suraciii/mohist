# Self-Review — issue-536 plan (round 3)

Re-reviewed `proposal.md`, `design.md`, `tasks.json`, and both specs against the issue and the current
tree, after the round-2 fix.

## Round-2 finding — verified fixed

- **N1:** T-001's final acceptance criterion no longer claims the converter is "reachable only from
  DatabaseInitializer, not from any service-phase read path." It now reads simply "Server builds with
  TreatWarningsAsErrors (C# lint)." This removes the contradiction with T-001's notes ("The read path
  still calls the converter until T-002"). The confinement obligation remains correctly placed in T-002
  (criterion 2: "The legacy converter's only reachable caller is WorkflowRunStateDataUpgrader via
  DatabaseInitializer") and in `canonical-state-read-path` requirement 3. Fixed.

## Round-1 findings — still fixed (re-confirmed)

- **B1:** read-path spec asserts only non-conversion (no "surface the inconsistency"); design Decision H
  documents the real STJ behavior. **A1:** "control-plane queries that load WorkflowRun State" +
  `IWorkflowRunDeserializer` references; no stale "status query" wording. **A2:** 6 files / 7 call sites
  reconciled. A grep for all previously-flagged stale phrases across the plan artifacts returns none.

## Consistency checks (all pass)

- `tasks.json` is valid JSON; DAG is valid (T-002 → T-001, strictly lower priority); both tasks have a
  spec reference and ≥1 acceptance criterion (T-001: 10, T-002: 5).
- Both specs use correct levels: `workflow-run-state-startup-migration` = 6 requirements / 12 scenarios;
  `canonical-state-read-path` = 3 requirements / 4 scenarios. Every requirement has ≥1 scenario; no delta
  headers; no cross-spec references.
- The two-task split is buildable: T-001 relocates the converter and wires the upgrader (read paths
  repointed to the new location, system correct but redundant); T-002 removes the 7 read-path call sites.
  Each task leaves a compilable, usable state.
- Specs/tasks cover the issue's full Behavior Contract and Done When: byte-identical converter output,
  no-write preflight naming the offending run, consistent online backup + `PRAGMA integrity_check`,
  single-transaction commit with one ETag bump per rewritten row, canonical rows byte-stable, idempotent
  repeat (0 writes), `failed`-run rerun preserved, zero read-path converter calls. The "254/110" figures
  appear only as first-deployment evidence, not a migration precondition (per the issue's "不写死").
- Design Decisions A–H each carry rationale + a rejected alternative; Risks use `[Risk] -> Mitigation`;
  Migration Plan + Rollback respect the WAL/online-backup constraint.

## Verdict

All prior findings are resolved and no new problems were found. The plan is ready to build.

<promise>PASS</promise>
