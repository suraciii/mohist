# Review Report

## Result: PASS

## Repaired Items

- [ID: item-0]
  Severity: info
  Scope: formatting
  Evidence: `LatestArtifactsPanel.tsx` and `BranchBar.tsx` were missing trailing newlines (files ended with `}` without terminal `\n`), caused by the T-001 extraction and T-003 refactoring respectively.
  Verification: `npm run typecheck -w packages/web` (clean) + `npm test -w packages/web` (302 files, 4627 tests — all pass). Confirmed with `xxd` that both files now end with `}\n`.
  Status: resolved

## Blocking Items

(none)

## Warning Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/model/buildExecutionSignal.ts:38-39`
  Evidence: `buildWaitReason` is called with `as never` casts on both `issue` and `agentStatus` arguments:
  ```
  const reason = buildWaitReason({
      issue: { blocker } as never,
      agentStatus: agentStatus as never,
  })
  ```
  This bypasses TypeScript's type checking entirely. If `buildWaitReason` (now exported at `derive-runtime-decision.ts:92`) is extended to access additional `RuntimeDecisionInput` fields in the future, this call site would silently produce incorrect results or throw at runtime with no compile-time warning. The current runtime behavior is correct because `buildWaitReason` only accesses `blocker.kind`, `agentStatus.runnerAvailable`, `agentStatus.runnerMessage`, and `agentStatus.capacity` — all of which are present in the actual args.
  SuggestedAction: Refactor to extract a narrow helper (e.g., `buildWaitReasonFromFields(blocker, agentStatus)`) that both `buildWaitReason` and `buildExecutionSignal` compose, eliminating the unsafe casts. Out of scope for the current change (design D3 says "Do NOT extend RuntimeDecision or deriveRuntimeDecision" — the constraint is about mixing concerns, not about extraction).
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.control-workspace.test.tsx:947-951`
  Evidence: The model-selection rail assertion uses `if (modelRow)` as a guard:
  ```
  const modelRow = container.querySelector('[data-testid="issue-detail-details-metadata"]')
  if (modelRow) {
      expect(rail.contains(modelRow)).toBe(true)
      expect(headerTier.contains(modelRow)).toBe(false)
  }
  ```
  If `IssueDetailsCard` (which renders `issue-detail-details-metadata`) were unexpectedly absent from the DOM, this assertion would silently skip without failing. The test currently passes because the card IS rendered, but the guard masks potential future regressions.
  SuggestedAction: Replace `if (modelRow)` with `expect(modelRow).toBeTruthy()` followed by the rail/header assertions, so a missing element fails explicitly.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/ArtifactOpener.tsx:65`
  Evidence: The compact evidence list hardcodes `compactLimit = 3` as a default parameter. The surface always shows at most 3 artifacts, even when 5 relevant plan/check artifacts exist for the current stage. The full panel in reading-flow remains available for browsing all artifacts.
  SuggestedAction: Consider a per-stage configurable limit (e.g., show the one most-relevant artifact for the awaiting stage rather than the top N) in a follow-up issue. Per open question in `design.md:110-111`.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: narrow viewport
  Evidence: The compact evidence, execution signal, and drift recovery slots are only rendered on desktop (`!isNarrowViewport` gate at `IssueDetailPage.tsx:323`). On narrow viewports, only `MobileActionBar` carries the primary action — the evidence/signal/drift recovery are not accessible from the mobile first screen.
  SuggestedAction: Deferred per design non-goal ("narrow viewport behavior preserved as-is"). Flag as a follow-up for mobile parity.
  Status: follow-up

## Acceptance Criteria Verification

### AC1: First screen shows issue identity, current workflow stage, health, approval state, runner/session signal when relevant, and primary owner action.

- **PASS**. Verified by `IssueDetailPage.control-workspace.test.tsx`:
  - Running path (line 239): identity (`#14` via pills), `data-summary="running"`, stage progress (`status-headline-stage-progress`), primary action (Stop with `data-primary="true"`) — all in `status-header-tier`.
  - Approval path (line 342): identify, `data-summary="approval-required"`, approve + send-back in same surface.
  - Queued path (line 687): identity, `data-summary="queued"`, no fabricated progress, runner signal (`runtime-execution-signal-runner` with `data-gating-kind`).
  - Done path (line 763): terminal Done summary, no active actions offered.

### AC2: Approval gates clearly show what is awaiting approval and provide approve and reject actions in the same decision context.

- **PASS**. Verified by `IssueDetailPage.control-workspace.test.tsx:342-444`:
  - `runtime-action-approve` and `runtime-action-send-back` both rendered inside `runtime-decision-surface` with `data-summary="approval-required"`.
  - Send-back form (`runtime-send-back-form`) opens inside the surface (`surface.contains(form)`), no navigation away.
  - Evidence slot (`runtime-evidence`) present alongside the approve/send-back actions in the same surface.

### AC3: Blocked, interrupted, and drift states show the relevant recovery action without requiring the user to search below the first screen.

- **PASS**. Verified by:
  - Blocked path (`control-workspace.test.tsx:447`): retry, resume, rerun, stop all in surface; rationale contains "interrupted" text.
  - Interrupted path (line 516): `data-summary="blocked"` in headline, rationale cites "interrupted", no standalone `workflow-interrupted-card`.
  - Drift path (line 546): `runtime-drift-recovery` inside surface with `runtime-drift-recovery-action` button; rail card (`reference-rail-drift`) retained. Defer drift does NOT promote to surface (line 603).

### AC4: Latest plan/check artifacts are discoverable from the operation context where the user makes approval or recovery decisions.

- **PASS**. Verified by:
  - Approval-required surface shows `runtime-evidence` with `data-summary="approval-required"` containing artifact items (`control-workspace.test.tsx:402-403`).
  - Failed surface shows `runtime-evidence` with `data-summary="failed"` (`control-workspace.test.tsx:675-676`).
  - Running summary OMITS evidence slot as expected (`control-workspace.test.tsx:332`).
  - Full `LatestArtifactsPanel` (with `latest-artifacts-list` testid) still renders in reading-flow alongside surface evidence (line 410).

### AC5: Description, comments, model selection, prerequisites, and lower-frequency settings remain available but no longer dominate the operational first screen.

- **PASS**. Verified by `control-workspace.test.tsx:892-985`:
  - Description and comments are NOT contained by `status-header-tier`, appear in `reading-flow`, and FOLLOW `runtime-decision-surface` in document order.
  - `reference-rail-configuration` and `reference-rail-prerequisites` are contained by `reference-rail`, not by `status-header-tier`.

## Spec Compliance Summary

All 8 spec requirements met with concrete test evidence:

| Requirement | Test Evidence |
|---|---|
| First-screen control region | `control-workspace.test.tsx` — 12 describe blocks covering all 9 conditional paths |
| Approval gate as one decision context | Approval-required tests + send-back form test |
| Artifacts discoverable from operation context | Evidence slot in approval/blocked/failed + omission in running/queued/done |
| Blocked/interrupted/drift recovery on first screen | Blocked + interrupted + drift (needs-attention + defer) paths |
| Compact runner/session signal | Runner-unavailable + capacity-full + active-session paths |
| Invalid/unsafe actions visually secondary | Primary-vs-secondary emphasis test (line 1210) |
| Descriptive content demoted | Secondary-content demotion tests (line 892) |
| Lifecycle actions preserved | 6-scenario lifecycle preservation test (line 988) + no-new-actions invariant (line 229) |

## Cross-cutting Concerns

- **Security**: No new input surfaces; no new endpoints; no secrets exposed. All mutations are the same ones already in use (`approveMutation`, `sendBackMutation`, etc.).
- **Data safety**: No server/API/DTO changes. All data consumed through existing TanStack Query hooks; query deduplication prevents double-fetching.
- **Public contracts**: `RuntimeDecisionSurfaceProps` gained 3 new optional props (`evidence`, `executionSignal`, `driftRecovery`). When all 3 are omitted/null, the surface renders byte-for-byte identical to before — verified by tests for non-applicable summaries (running, queued, done all render without the new slots).
- **Migration impact**: Web-only, presentation-layer change. Rollback is a single frontend release; no server/runner migration needed.
- **Test count**: 4627 web tests + 1031 runner tests — all pass. TypeScript clean.
- **Dependency audit**: No new npm dependencies added. All logic is composed from existing hooks and queries.

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:216`
  Evidence: The `status-header-tier` div carries `data-testid` but no `data-tier-weight` attribute, while `reading-flow` and `reference-rail` both carry `data-tier-weight` attributes. The tier-weight is carried by `StatusHeadline` (a child of the tier) rather than the tier container itself. This is pre-existing and unchanged by this issue.
  SuggestedAction: Optional: add `data-tier-weight="status-header"` to the `status-header-tier` div for consistency with the other two tiers. Low priority.
  Status: pre-existing

<promise>PASS</promise>
