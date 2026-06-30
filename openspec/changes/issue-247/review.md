# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/session-health/`
  Evidence: The candidate does not satisfy the acceptance criterion that UI context health consumes server `healthStatus` directly and does not reclassify client-side. `packages/web/src/widgets/session-health/model/context-health.ts:1-19` still exports `classifyContextHealth`, `ContextHealthIndicator` falls back to `classifyContextHealth(percent)` when `healthStatus` is absent (`packages/web/src/widgets/session-health/ui/ContextHealthIndicator.tsx:104-110`), and `ContextHealthBar` does the same (`packages/web/src/widgets/session-health/ui/ContextHealthBar.tsx:71-76`). The tests explicitly lock in this non-compliant fallback: `packages/web/src/widgets/session-health/ui/ContextHealthIndicator.test.tsx:392-403` expects classification from `contextUsagePercent` without `healthStatus`, and `packages/web/src/widgets/session-health/ui/ContextHealthBar.test.tsx:35-44` expects green status from percent-only input. This violates `openspec/changes/issue-247/specs/session-health/spec.md:3-29`. [disallowed:behavior-change]
  SuggestedAction: Remove the percent-to-status fallback from shared health widgets. Render health only when the server-provided `healthStatus` is present and valid, or render a percent-only display that does not fabricate a classification. Update tests to assert graceful omission/no fabricated classification when `healthStatus` is absent.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; add a regression test where `contextUsagePercent=72` and `healthStatus=null` does not render `data-status="yellow"`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/pages/session/ui/SessionPage.tsx`
  Evidence: The session page recovery/context bar still drops server `healthStatus`. Metadata now maps `healthStatus` into `detail.metadata.usage` (`SessionPage.tsx:66-79`), but the `ContextHealthBar` call only passes `contextWindowUsed`, `contextWindowSize`, and `contextUsagePercent` (`SessionPage.tsx:771-775`). Because the bar falls back to client classification when `healthStatus` is absent, the recovery bar can contradict the server classification even when the server provided it. This misses the acceptance criterion for direct server-health consumption in the session-page health surface. [disallowed:behavior-change]
  SuggestedAction: Pass `healthStatus={detail?.metadata?.usage?.healthStatus ?? null}` to the recovery `ContextHealthBar`, and add a session-page test where `contextUsagePercent` would imply green/yellow but server `healthStatus` renders the server value.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` after adding the test.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/widgets/coder-session/model/useSessionTimeline.ts`
  Evidence: Adjacent live session/recovery paths still recompute context percent and health classification client-side. The `usage.updated` handler derives percent from `contextWindowUsed/contextWindowSize` and classifies with `percent >= 80 ? 'red' : percent >= 60 ? 'yellow' : 'green'` (`useSessionTimeline.ts:669-686`). The `context_health_update` and reverse-DNS health handlers also fabricate fallback statuses when `healthStatus` is absent (`useSessionTimeline.ts:691-699`, `useSessionTimeline.ts:816-831`), and compaction events derive health from raw window fields (`useSessionTimeline.ts:745-758`, `useSessionTimeline.ts:798-811`). These values feed `SessionTimeline`'s `ContextHealthBar` (`packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx:548-556`), so a visible UI surface still computes its own health classification. [disallowed:behavior-change]
  SuggestedAction: Stop deriving health classification in live timeline state. Apply server `healthStatus` from health events when present, avoid fabricated status when absent, and only compute non-health display values when explicitly allowed. Add live-event regression tests for `usage.updated`, `context_health_update` without `healthStatus`, and compaction events.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; verify no `percent >= 80 ? 'red'` classification remains in UI health paths.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/pages/session/ui/SessionPage.tsx`
  Evidence: The new sticky title and existing sticky recovery bar both use `sticky top-0 z-20` inside the same scroll container (`SessionPage.tsx:595` and `SessionPage.tsx:1024-1029`). Once the scroll position reaches the second sticky element, both pin to the same top edge and the later recovery bar can paint over the sticky title. That breaks the sticky-title acceptance criterion that title/status/turn count plus usage remain visible while the transcript scrolls. The updated sticky tests assert both elements are sticky and the title is first (`SessionPage.sticky.test.tsx:328-353`, `391-400`), but they do not verify non-overlap or continued title visibility after the recovery bar sticks. [disallowed:behavior-change]
  SuggestedAction: Put the title and recovery affordances into one sticky container, or offset the recovery bar below the title strip with a stable top value. Add a DOM/layout test that both sticky regions have distinct pinned positions or that a combined sticky wrapper preserves all content.
  Verification: Run `npm run test:run -w packages/web -- SessionPage.sticky.test.tsx` and visually verify a session with both usage and recovery controls while scrolling.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/web/src/pages/session/ui/SessionPage.tsx` and `packages/web/tests/SessionPage*.tsx`
  Evidence: The issue acceptance criteria require `cachedReadTokens` and `thoughtTokens` to appear in the SessionPage observability/header row, and require `buildSessionMetadata` to preserve `contextUsagePercent`/`healthStatus`. The candidate adds rendering in `SessionHeader` (`SessionPage.tsx:526-545`) and mapping in `buildSessionMetadata` (`SessionPage.tsx:66-79`), but the changed SessionPage tests only loosen duplicate `Completed` assertions and do not assert cached/thought token rendering or metadata health propagation (`packages/web/tests/SessionPage.test.tsx:2183-2203`, `packages/web/tests/SessionPage.endpoints.test.tsx:253-261`). Component-level `SessionUsageSummary` tests do not cover the actual header row. [disallowed:test-only-gap]
  SuggestedAction: Add SessionPage-level tests with metadata usage containing `cachedReadTokens`, `thoughtTokens`, `contextUsagePercent`, and `healthStatus`, then assert the header/observability row and health widgets consume those values.
  Verification: Run `npm run test:run -w packages/web -- SessionPage.test.tsx SessionPage.endpoints.test.tsx`.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx`
  Evidence: `summarizePeakContext` still computes a peak context percentage from raw `contextWindowUsed/contextWindowSize` (`WorkflowSessionsPanel.tsx:116-128`). This is an existing aggregate display rather than the row-level health classification changed by the issue, so it is not counted as a blocker here. It is still worth clarifying whether aggregate context percentages should also come from server-provided `contextUsagePercent` values for consistency.
  SuggestedAction: Decide whether peak context should use per-session `contextUsagePercent` when present, then keep or adjust the aggregate intentionally.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_(none)_

## Verification

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 207 files, 3132 passed, 1 skipped.

<promise>FAIL</promise>
