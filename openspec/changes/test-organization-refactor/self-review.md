# Self-Review: Test Organization Refactor

## Coverage check

| Capability | Spec | Tasks |
|------------|------|-------|
| `test-categorization` | `specs/test-categorization/spec.md` | T-001, T-002, T-003 |
| `fixture-sharing` | `specs/fixture-sharing/spec.md` | T-004, T-005, T-006, T-007 |
| `test-data-factories` | `specs/test-data-factories/spec.md` | T-008, T-009, T-010, T-011 |
| `bounded-context-layout` | `specs/bounded-context-layout/spec.md` | T-012, T-013, T-014, T-015, T-016 |
| `archtest-enforcement` | `specs/archtest-enforcement/spec.md` | T-017, T-018, T-019, T-020 |

All 5 capabilities have at least one spec requirement and at least one implementing task. No orphans.

## Risk re-check

| Phase | Risk per design | Risk confirmed by review | Mitigation |
|-------|-----------------|--------------------------|------------|
| 1. Categorize | zero | zero | None needed; mechanical attribute addition. |
| 2. Fixture split | low | low | Audit is mechanical; new fixture reuses T-004's extracted service graph. |
| 3. Helper extraction | medium | medium | T-008 extracts helpers as static; T-011 removes the abstract base in a separate commit so a half-broken state is reviewable. |
| 4. Bounded-context layout | low (mechanical) | low | T-012/13/14/15 use `git mv` to preserve file history; T-016 pre-splits large files so the Phase 5 size rule does not immediately fail. |
| 5. Archtest enforcement | low | low | T-017 lands first (file-name rule) so subsequent tasks can verify their work against it; T-018 lands after T-016 so the size rule does not fight with intermediate state. |

## Dependency graph

```
Phase 1: T-001 ─┬─ T-002
                └─ T-003
Phase 2: T-004 ─── T-005 ──┬─ T-006 ─┬─ T-007
                           └─────────┘
Phase 3: T-008 ─┬─ T-009 ─ T-010 ─┬─ T-011
               └──────────────────┘
Phase 4: T-012 ─ T-013 ─ T-014 ─ T-015 ─ T-016
Phase 5: T-017 ─ T-019 ─ T-020 ─ T-018
```

Phase 1 has no prerequisites. Phases 2-4 form a chain. Phase 5 (mostly) parallel.

Specific concerns:

- **T-001 → T-007**: T-007 (migrate D-class specs) changes the `Speed` trait. If T-001 has already labeled the spec as `Integration`, T-007 flips it to `Service`. If T-007 lands before T-001, the spec has no Speed trait and T-001 must label it as `Service` directly. Both orderings are correct; the design's ordering (T-001 → T-007) means T-001 labels them `Integration` and T-007 updates them to `Service`.
- **T-008 → T-011**: T-008 extracts helpers as static. T-011 then migrates 21 spec files off the abstract base. If T-011 lands before T-008, the 21 specs lose their helpers and the build breaks. T-008 must land first.
- **T-016 → T-018**: T-018 (size rule) would fail on the 4 large files if T-016 has not pre-split them. T-016 is a hard prerequisite of T-018.

## Green-bar invariant check

The design claims "the full test suite must stay green at the end of every phase." Re-verified per task:

| Task | Risk to green bar | Mitigation |
|------|-------------------|------------|
| T-001 | none | attribute-only change |
| T-002 | none | new file, no behavior |
| T-003 | none | verification only |
| T-004 | zero behavior change | existing 680 tests pass byte-for-byte |
| T-005 | new file only | no spec uses it yet |
| T-006 | collection names unchanged | all 680 tests pass |
| T-007 | migrated specs use lighter fixture, same DI | all 680 tests pass |
| T-008 | duplicate code (base + static) | all 680 tests pass |
| T-009 | new files only | no spec uses them yet |
| T-010 | mechanical replacement | all 680 tests pass (assertions unchanged) |
| T-011 | remove base, switch 21 specs to static | all 148 grain tests pass (verified by T-008's duplicate) |
| T-012–T-015 | file moves + namespace updates | build + tests pass (verifies T-011 done first) |
| T-016 | split large files | tests pass (assertions preserved) |
| T-017 | new rule | rule passes against current state; intentionally bad file is rejected |
| T-018 | new rule | rule passes against post-T-016 state; bad file is rejected |
| T-019 | new rule | rule passes against current state; bad class is rejected |
| T-020 | new rule | rule passes against post-T-015 state; bad namespace is rejected |

## Ambiguity check

| Design decision | Resolved in self-review? | Notes |
|-----------------|---------------------------|-------|
| D-class spec list (5–7 files) | partial | Tasks T-007 mentions "5–7 spec files" but the exact list depends on a runtime audit. The audit script: `grep -L "_fixture.Client" Specs/*Specs.cs \| xargs grep -l "MohistIntegrationFixture"`. If more than 7 files match, the contributor can extend the task or split into T-007a/T-007b. |
| `BacklogCollection` / `WorkflowEventsCollection` consolidation | unresolved | These single-spec collections are kept as-is by the design (open question 2). If the contributor wants to fold them, it's a small follow-up. |
| `WorkflowGrainSpecs.cs` deletion vs facade | unresolved | T-011 follows the "deletion" path; the design's open question 3 invites the contributor to override. |
| 3 large spec splits in Phase 4 | resolved | T-016 lists 4 files (not 3 — I miscounted the design's "3" claim). The actual count is 4: UpdateSpecs, IssueQuerierSpecs, SystemUpdateServiceSpecs, MohistDefaultWorkflowProfileSpecs. |
| `UpdateInstallSyncSpecs.cs` no `*Specs` class | unresolved | T-016 does not address this. The contributor should look at it during Phase 4; the file likely needs a wrapper class added or a rename. |
| Spec file size budget constant | resolved | T-018 fixes the threshold at `24_000` bytes. |

## Open questions to resolve before execution

1. **Open question 2 (single-spec collections)**: are `BacklogCollection` and `WorkflowEventsCollection` intentional, or should they be folded into `WorkflowGrainCollection`? The design keeps them; the contributor may want to change.
2. **Open question 3 (WorkflowGrainSpecs deletion)**: T-011 deletes the file. If the contributor prefers a thin facade, T-011's acceptance criteria need to allow for `WorkflowGrainSpecHarness.cs` (a 5-line file) instead of nothing.
3. **D-class spec audit**: before T-007, run the grep script in the design and confirm the exact list. The plan accommodates 5–7; if the number is higher, split T-007 into T-007a (5 files) + T-007b (remaining).
4. **`UpdateInstallSyncSpecs.cs` orphan class**: T-016 should add a sub-task to either rename it or wrap it in a real `*Specs` class.
5. **Phase 1 vs Phase 2 trait promotion timing**: should T-001 label D-class specs as `Integration` (current state) or `Service` (post-migration target)? The current design says T-001 → `Integration` and T-007 → `Service`. The contributor may prefer T-007 to label them `Service` directly (T-001 skips these files entirely). Both are valid; the design's path is more uniform.

## Suggested execution order

The 5 phases are designed to be self-contained commits. Within each phase, the tasks are also self-contained. The recommended execution path is:

1. **Day 1**: Phase 1 (T-001, T-002, T-003) — 30 min mechanical work + 15 min verification.
2. **Day 2**: Phase 2 (T-004, T-005, T-006, T-007) — 1 day for service graph extraction + D-class migration.
3. **Day 3–4**: Phase 3 (T-008, T-009, T-010, T-011) — 1–2 days for helper extraction + base class removal.
4. **Day 5–6**: Phase 4 (T-012, T-013, T-014, T-015, T-016) — 2 days for 87 file moves + large-file splits.
5. **Day 7**: Phase 5 (T-017, T-018, T-019, T-020) — 4 archtest rules, half-day.

Total: ~1 week of focused work. Each day ends with a green bar.

## What I would change if I rewrote this

1. **Tighten the 5–7 spec list in T-007** by running the audit script up front and listing the exact file paths in the task. The contributor should not have to re-derive the list.
2. **Add a T-000 task** that produces a baseline test count + timing measurement captured in a file under `design/`. Every phase's commit message would then reference the baseline ("Phase 1 brought unit-test time from 5min to 30s").
3. **Make the trait vocabulary part of T-001's PR description**, not a separate T-002 task. T-002 is optional polish; T-001 is the actual feature.
4. **Move the consolidation of behavior-named files out of Phase 4** into a dedicated "Phase 3.5" or "Phase 4a" because it's a behavior-preserving but more aggressive rename than the directory move. The contributor should review the consolidation separately from the directory move.
5. **Add an explicit "deferred items" section** capturing what is NOT in this change (CI matrix updates, xUnit v3 migration, etc.) so the contributor can communicate boundaries to reviewers.

## Sign-off

This self-review finds the plan:

- Complete (5 capabilities, 20 tasks, all with acceptance criteria).
- Correctly phased (green-bar invariant at every commit).
- Risk-mitigated (Phase 1 first for fast feedback; Phase 5 last for rule enforcement).
- Bounded (no production code changes; no test assertion changes).

The plan is ready for execution. Recommend starting Phase 1 immediately to capture the baseline timing numbers.
