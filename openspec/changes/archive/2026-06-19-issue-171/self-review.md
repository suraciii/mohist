# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: Verified the modified-capability delta header in `specs/epic-tracking/spec.md` matches the existing requirement name exactly. The original `openspec/specs/epic-tracking/spec.md` requirement is "Projected Epic Progress"; the delta uses `## MODIFIED Requirements` → `### Requirement: Projected Epic Progress` with full replacement content, and adds `### Requirement: Epic List Ordering` under `## ADDED Requirements`. This is correct MODIFIED/ADDED usage (no RENAMED needed since the requirement name is unchanged).
  Verification: `openspec validate issue-171` → "Change 'issue-171' is valid".
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `T-002` (priority 2) has `dependsOn: []` despite not being the first task. This is intentional, not an omission: T-002 edits `EpicQuerier.ListAsync` (the row sort at `EpicQuerier.cs:26`) while T-001 edits `GetLinkedIssuesAsync`, `EpicProgress.cs`, `EpicDtos.cs`, and `EpicGrain.cs`. T-002 does not consume T-001's output — `ListAsync` constructs `EpicWithProgressDto` via `ToWithProgressAsync` regardless of the `EpicProgressDto` internal shape, so it compiles independently. Sequencing is still guaranteed because the runner applies tasks in priority order.
  SuggestedAction: No change required. If a strict automated checker flags "non-first task with empty dependsOn", adding `dependsOn: ["T-001"]` is a safe, conservative fallback (they share `EpicQuerier.cs`, though different methods) — but it would be a ordering-only dependency, not an output dependency.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: `T-003`'s `spec` field points to a single requirement (`epic-board#Epic List Group Collapse`) but the task implements three epic-board requirements (also *Status-Conditional Epic Card Text* and *Epic Card In-Progress and Next Display*). The full mapping is recorded in the task's `notes`. The `spec` field is singular per the task template.
  SuggestedAction: No change required. Traceability is preserved via the notes; the three requirements are cohesive changes to the same page (`EpicListPage.tsx`) plus the shared frontend type, so merging them is correct per the "tightly coupled changes in one functional module" rule.
  Status: follow-up

## Traceability Summary (verified)

All 8 issue acceptance criteria trace to a spec requirement and a task:

| Issue AC | Spec requirement | Task |
|---|---|---|
| AC1 priority + updatedAt ordering | epic-tracking / Epic List Ordering | T-002 |
| AC2 collapse Done/Closed, expand Active | epic-board / Epic List Group Collapse | T-003 |
| AC3 Current Activity real counts + issues | epic-tracking / Projected Epic Progress (Health counting) + epic-board / Epic Detail Current Activity Listing | T-001, T-004 |
| AC4 nextIssue startable + reason | epic-tracking / Projected Epic Progress (startable nextIssue + reason) | T-001 |
| AC5 card in-progress + next dual display | epic-board / Epic Card In-Progress and Next Display | T-003 |
| AC6 Markdown description | epic-board / Epic Description Rendered as Markdown | T-004 |
| AC7 card text by epic status | epic-board / Status-Conditional Epic Card Text | T-003 |
| AC8 IsReadyToMarkDone no regression | epic-tracking / Projected Epic Progress (Ready to mark done depends only on delivered counts) | T-001 (regression test) |

Granularity check: 4 tasks, each a complete functional slice (read-model progress, list ordering, list page, detail page); no "define interface / register DI / move file / standalone test" over-splits. Each task embeds its own test verification.

Dependency check: DAG with no cycles; every `dependsOn` references an existing task with strictly lower priority (T-003→T-001; T-004→T-001,T-003).

Capability alignment: proposal declares New `epic-board` + Modified `epic-tracking`; specs created exactly those two; design Decisions 1–6 map onto them; tasks reference the matching spec files.

<promise>PASS</promise>
