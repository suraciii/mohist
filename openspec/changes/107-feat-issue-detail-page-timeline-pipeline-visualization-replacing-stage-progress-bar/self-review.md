## Self-Review: Issue #107 — Timeline Pipeline Visualization

**Reviewer:** Self (automated)
**Date:** 2026-05-01
**Verdict:** PASS (with fixes applied)

---

### Review Criteria Checklist

| Criterion | Status | Notes |
|-----------|--------|-------|
| All requirements covered by specs | PASS | 7 requirements with 15 scenarios in issue-timeline-ui; 2 scenarios in session-timeline-ui |
| All specs have tasks | PASS | Every spec requirement mapped to at least one task |
| Edge cases considered | PASS (fixed) | Added build failure/retry scenarios; Explore stage omission documented |
| Specs align with proposal capabilities | PASS | issue-timeline-ui (new) + session-timeline-ui (modified) both covered |
| Tasks reference correct spec files | PASS | All 5 tasks reference specs/issue-timeline-ui/spec.md |
| Design aligns with specs | PASS (fixed) | Stage enum mapping added; line numbers corrected |
| Naming consistent | PASS (fixed) | "Review" → Stage.Check mapping now explicit |
| Dependencies available or created by earlier tasks | PASS | Verified: reconstructRoundsFromLogs, getRoundColor, useCoderSessions all exist |
| No circular dependencies | PASS | Linear chain: T-001 → T-002 → T-003; T-001 → T-004; T-003 → T-005 |
| Every non-first task has dependsOn | PASS | T-001 has none; T-002→T-001; T-003→T-002; T-004→T-001; T-005→T-003 |
| All dependsOn point to lower-priority IDs | PASS | Priority order: 1→2→3→4→5, all deps point to lower values |
| No cycles | PASS | DAG verified |

---

### Issues Found and Fixed

#### 1. Stage name mismatch: "Review" vs `Stage.Check` [CRITICAL — Fixed]

**Problem:** The proposal, design, specs, and tasks consistently use the label "Review" for a pipeline stage, but the actual `Stage` enum in the codebase uses `Stage.Check`. No mapping between the user-facing timeline label and the implementation enum was documented. An implementer could attempt to reference `Stage.Review` which does not exist.

**Evidence:** 
- Backend enum: `Stage { Draft, Explore, Plan, Build, Check, Done, Backlog }` in `packages/cli/src/types/index.ts:1-9`
- Web enum: `Stage { Backlog, Explore, Plan, Build, Check, Done }` in `packages/cli/web/src/lib/types.ts:1-8`
- Proposal timeline: "Created → Plan → Approved → Build → Review → Done" — "Review" appears nowhere in either enum

**Fix applied:**
- `design.md` D6: Replaced single-line node list with full mapping table showing Timeline Label → Data Source → Stage Enum, with explicit note that "Review" is a display label for `Stage.Check`
- `tasks.json` T-001: Added note about Review → Stage.Check mapping
- `tasks.json` T-003: Added mapping note; acceptance criterion now reads "Review (Stage.Check)"
- `design.md` D6: Added note explaining intentional omission of `Stage.Explore` and `Stage.Draft/Backlog`

#### 2. "100ms" vs RAF timing inconsistency [MODERATE — Fixed]

**Problem:** The spec text claimed "updates throttled to 100ms batches" and the design text said "batch updates every 100ms", but the design's code pattern uses `requestAnimationFrame` which fires at ~16ms (60fps), not 100ms. The "100ms" claim was never implemented in the code pattern.

**Fix applied:**
- `specs/issue-timeline-ui/spec.md`: Changed "updates in batches every 100ms using requestAnimationFrame" → "batches updates via requestAnimationFrame"
- `specs/issue-timeline-ui/spec.md`: Changed "updates throttled to 100ms batches" → "updates batched via requestAnimationFrame"
- `tasks.json` T-001 acceptance criteria: Changed "RAF throttles SSE events to 100ms batches" → "RAF batches SSE events per animation frame"
- `tasks.json` T-001 description: Changed "batch updates every 100ms" → "batch updates per animation frame"
- `tasks.json` T-004 acceptance criteria: Changed "throttles updates to 100ms" → "throttles updates per animation frame"

#### 3. Outdated line number references [MODERATE — Fixed]

**Problem:** The design referenced "lines 376-404" for the horizontal progress bar in IssueDetailPage.tsx, but the actual progress bar code is at lines 352-380 (the `<div className="mb-6">` block containing `STAGES.map`).

**Fix applied:**
- `design.md` D1: Updated from "lines 376-404" to "lines 352-380" with structural description
- `design.md` migration step 3: Same correction applied

#### 4. Missing build failure/retry scenarios in spec [MODERATE — Fixed]

**Problem:** The proposal explicitly lists "Build 有没有失败重试？" as a user question the timeline should answer. The design mentions `build_failed` as an event type. The tasks mention "pass/fail/running status" for build tasks. But the spec had no dedicated requirement or scenario for stage failure visualization or retry handling.

**Fix applied:**
- `specs/issue-timeline-ui/spec.md`: Added new requirement "Timeline displays stage failure states" with two scenarios:
  - "Build stage fails" — failure icon (red ✗), stage remains expandable
  - "Stage is retried after failure" — shows latest attempt result with total duration
- `tasks.json` T-002: Added acceptance criterion "Failed stages display failure icon (red ✗) and remain expandable"

#### 5. Undocumented Explore stage omission [MINOR — Fixed]

**Problem:** The actual pipeline has `Stage.Explore` between Backlog/Draft and Plan, but the timeline intentionally omits it. The current progress bar (being replaced) shows Explore as a distinct stage. No rationale for this omission was documented.

**Fix applied:**
- `design.md` D6: Added note explaining that Explore and Backlog/Draft are intentionally omitted — "The Explore phase is considered part of the pre-Plan workflow, not a distinct pipeline stage visible to the user. This simplification keeps the timeline focused on the core pipeline: Plan → Build → Review → Done."

---

### Dependency Verification

All external dependencies verified to exist in the current codebase:

| Dependency | Location | Status |
|------------|----------|--------|
| `reconstructRoundsFromLogs()` | `packages/cli/web/src/hooks/useSessionTimeline.ts:105` | Exported, available |
| `getRoundColor()` | `packages/cli/web/src/components/SessionTimeline.tsx:181` | Available (private, needs refactoring to export or duplicate) |
| `useCoderSessions` | `packages/cli/web/src/hooks/useCoderSessions.ts` | Exists |
| `useIssue` | Referenced in `useQueries.ts` | Exists |
| Horizontal progress bar | `IssueDetailPage.tsx:352-380` | Confirmed at stated location |
| `STAGES` array | `IssueDetailPage.tsx:21` | Confirmed: `[Backlog, Explore, Plan, Build, Check, Done]` |
| `STAGE_ORDER` | `packages/cli/src/types/index.ts:11-18` | Confirmed: `[Draft, Explore, Plan, Build, Check, Done]` |

**Note:** `getRoundColor` is a private function in `SessionTimeline.tsx`. T-002's notes say to "reuse" it, but it would need to be exported or extracted to a shared utility. This is a minor implementation concern, not an artifact issue.

---

### Artifact Consistency Matrix

| Artifact | Stage Names | RAF Timing | Line Refs | Failure Handling |
|----------|-------------|------------|-----------|------------------|
| proposal.md | Review (display) | — | — | Implicit |
| design.md | Review = Stage.Check (mapped) | RAF per frame | 352-380 | build_failed event |
| spec.md | Review (display) | RAF per frame | — | Explicit scenarios |
| tasks.json | Review (Stage.Check) noted | RAF per frame | 352-380 | Failure AC added |
| session-timeline-ui/spec.md | Delegates to issue-timeline-ui | — | — | — |

---

### Unresolved Open Questions (from design.md)

These are design decisions left to implementer discretion; not blockers:
1. Should "Created" node show for already-running issues? (Spec says yes unconditionally)
2. Duration format: "8m 26s" vs "8m"? (Spec uses "8m 26s" in examples)
3. Pending stages show estimated wait time? (Spec says just gray/hollow)

---

### Files Modified

1. `design.md` — D1 line numbers, D6 stage mapping table + Explore omission note, migration step 3
2. `specs/issue-timeline-ui/spec.md` — RAF wording (2 places), added failure requirement with 2 scenarios
3. `tasks.json` — T-001 description + AC, T-002 AC, T-003 description + AC, T-004 AC

### Files NOT Modified (verified clean)

- `proposal.md` — No issues found
- `specs/session-timeline-ui/spec.md` — Correctly delegates to issue-timeline-ui
