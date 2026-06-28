# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal referenced `CanStart` as a field available on the list read model in three places (the "Ready to start" bullet of *What Changes*, the `epic-list-presentation` capability description, and the *Impact* server/API line). Verification against `packages/web/src/entities/epic/model/types.ts` confirms `CanStart` is **not** a member of `EpicProgress` (types.ts:43-51); it exists only as `canStart` on `LinkedIssue` (types.ts:72) in the epic detail-enrichment path. Design D2 already resolves this by grouping Ready-to-start on `nextIssue` presence (the server's next-issue selection already encodes startability as `nextIssue == null` + reason), and `specs/epic-list-presentation/spec.md:8` reflects that decision — but the proposal was left out of sync, a factual inconsistency within the artifact set.
  Verification: Edited `proposal.md` to (a) reword the "Ready to start" bullet to state grouping on non-null `nextIssue` and explain why `CanStart` is not used, (b) drop `CanStart` from the capability's fact list, and (c) drop `CanStart` from the Impact line's enumeration of facts provided by `EpicWithProgress`. No behavior changed — the edits bring the proposal into line with the already-documented design D2 decision and the spec. Confirmed all three edits applied and no other `CanStart` mentions remain in the proposal.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design D2's "Ready-to-start" grouping trusts that the server never returns a non-null `nextIssue` for an issue that is not genuinely startable (startability is inferred from `nextIssue` nullness + reason). The design's own Open Questions note flags this as a possible future hardening point if next-issue selection rules change.
  SuggestedAction: If the server's next-issue selection rules are ever relaxed, file a follow-up for a server-side contract test guaranteeing `nextIssue` is non-null only when startable, so the D2 approximation cannot silently mislead users via the "Ready to start" label. Out of scope for this change.
  Status: follow-up

## Review Detail

### Alignment
- The issue's six acceptance criteria map onto the proposal/spec/tasks:
  - AC1 (Running independent + on top) → *What Changes* "Running" group + spec requirement "Running group is displayed first among active epic groups".
  - AC2 (Ready-to-start / waiting / idle-empty distinguishable) → four-group cascade spec requirement.
  - AC3 (Start action not mistaken for Start Epic) → spec requirement "Manual per-card start is labelled Start next issue…" + gating to Ready-to-start only.
  - AC4 (no mobile horizontal overflow, key state not clipped) → spec requirement "Epic list page has no horizontal overflow and keeps card state readable on mobile".
  - AC5 (Done/Closed folded preserved) → spec requirement "Done and Closed groups stay folded while active groups stay expanded by default".
  - AC6 (test coverage for running/ready-to-start/waiting reason/done-closed folded) → T-002 acceptance criteria + inline tests; T-001 covers the cascade invariant.
- No issue requirements missing or misinterpreted; Non-Goals (no backend/domain/query-perf change, no new filter/search) are honored throughout.

### Completeness
- All six spec requirements are covered by tasks: T-001 owns the cascade selector requirement (req 1); T-002 owns the remaining five (ordering, card content, Start semantics, mobile, folded behavior).
- Edge cases are addressed: active-issue-beats-reason precedence (spec.md:40), empty-state vs. readyToMarkDone, mobile widths 320/390/430px, and Running cards losing the inline Start (design D3).

### Consistency
- Naming is uniform across design/tasks/spec: `running / readyToStart / waitingBlocked / idleEmpty`; test-ids `epic-section-running/ready/waiting/idle` + retained `done`/`closed`.
- Tasks reference the correct spec anchors (T-001 → groups-by-actionable-state; T-002 → running-displayed-first) and both span the relevant scenarios.
- Design's line references to current code were spot-checked and are accurate (Active bucket `EpicListPage.tsx:223`, Start button `:78`, `EpicSection` `:180`, `statusText` `:48`).

### Feasibility
- Task granularity is appropriate: two complete feature slices, each with tests inline (no separate "test"/"register DI"/"create file" tasks).
  - T-001 is an independently unit-testable pure selector foundation.
  - T-002 consumes it for the full UI refactor (page + group-aware cards + mobile + test rewrite).
- Dependencies are satisfiable: T-002 depends only on T-001's selector output.

### Dependency completeness
- T-001 has empty `dependsOn` (first task, priority 1).
- T-002 `dependsOn: ["T-001"]`, priority 2 — points to an existing ID with lower priority.
- No cycles.

<promise>PASS</promise>
