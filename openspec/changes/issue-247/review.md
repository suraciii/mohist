# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/coder-session/model/useSessionTimeline.ts`
  Evidence: Raw `usage.updated` events can now erase the last server-provided health state on the session timeline. `AgentDetailEventMap['usage.updated']` makes `contextUsagePercent` and `healthStatus` optional (`packages/web/src/entities/agent/model/types.ts:92-105`), and the runner emits `usage.updated` payloads with raw `contextWindowSize` / `contextWindowUsed` but no derived health fields (`packages/runner/src/actions/acp/session-events.ts:96-122`). The handler processes such raw-window events (`useSessionTimeline.ts:673-675`) and replaces the timeline context health with `status: null` and `contextUsagePercent: null` when those optional derived fields are absent (`useSessionTimeline.ts:678-683`). Since `SessionTimeline` only renders the health bar when both status and percent exist (`packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx:520-555`), ordinary usage updates can hide a previously visible server health reading until a separate `context_health_update` arrives. That violates the requirement to consume server health as the source of truth without rendering stale/fabricated or losing the last authoritative value. [disallowed:behavior-change]
  SuggestedAction: Preserve the previous server-provided `status` / `contextUsagePercent` when a raw `usage.updated`, compaction, or reset event only updates window counts. Only replace derived health fields when the event actually carries server-derived values, and add a regression test where a `context_health_update` is followed by a raw `usage.updated` with only `contextWindowUsed` / `contextWindowSize`.
  Verification: Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; the new regression should keep the timeline health bar visible with the prior server status after the raw usage event.
  Status: open

- [ID: item-2]
  Severity: minor
  Scope: `packages/web/src/pages/session/ui/SessionUsageSummary.tsx`
  Evidence: The usage summary does not fully omit the health field when `contextUsagePercent` exists but `healthStatus` is absent. The wrapper span with `data-testid="usage-summary-health"` renders whenever `usage.contextUsagePercent != null` (`SessionUsageSummary.tsx:79-87`), but `ContextHealthIndicator` returns `null` when `healthStatus` is absent or invalid (`packages/web/src/widgets/session-health/ui/ContextHealthIndicator.tsx:99-101`). This leaves an empty health node in the summary instead of omitting the unavailable field, and it also creates a false-positive test target for health display. The existing test only covers `contextUsagePercent: null`, not `contextUsagePercent` present with `healthStatus: null` (`packages/web/src/pages/session/ui/SessionUsageSummary.test.tsx:136-140`). [disallowed:behavior-change]
  SuggestedAction: Gate the wrapper on a valid server health status as well as a finite `contextUsagePercent`, or let `ContextHealthIndicator` own the test target so absent health produces no health node. Add a test for `contextUsagePercent: 72, healthStatus: null` that asserts the health summary is omitted.
  Verification: Run `npm run test:run -w packages/web -- SessionUsageSummary.test.tsx` and the full `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/pages/session/ui/SessionPage.tsx`
  Evidence: The sticky usage title uses a fixed one-line layout and the recovery bar is pinned with the hard-coded `top-9` offset (`SessionPage.tsx:580-613`, `SessionPage.tsx:1016-1032`). The new strip adds title, status, turn count, total tokens, and context percent into a single non-wrapping flex row, but the title item lacks the `min-w-0` flex constraint needed for reliable truncation in a constrained row. On narrow viewports or long session names, the strip can overflow horizontally; if its actual height grows or differs from 36px, the `top-9` recovery offset can also overlap the sticky title. This risks the sticky-title acceptance criterion that identity info and usage remain visible while scrolling. [disallowed:behavior-change]
  SuggestedAction: Give the sticky title row stable responsive constraints: use `min-w-0` on the title flex item/container, keep usage tokens from forcing overflow, and avoid coupling the recovery bar offset to an implicit title height. Prefer one combined sticky wrapper or a CSS variable/shared height if both sticky regions remain separate.
  Verification: Add/update sticky tests for a long session name and narrow container, then visually verify a session with both the sticky title and recovery bar while scrolling.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/entities/coder-session/model/useCoderSessions.ts`
  Evidence: The design already flags `useCoderSessions` realtime health parity as a follow-up. This candidate fixes `useWorkflowRunSessions` and the session timeline path, but surfaces fed only by `useCoderSessions` still do not handle `context_health_update`; they rely on `usage.updated` or later refetches for derived health. This is outside the strict `useWorkflowRunSessions` acceptance criterion but remains a known cross-surface consistency risk.
  SuggestedAction: Add a `context_health_update` handler to `useCoderSessions` when a non-session-page consumer needs live server health without refetch.
  Status: follow-up

## Pre-existing or Out-of-scope Items

_(none)_

## Verification

- `git diff origin/master...HEAD --check` passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 207 test files, 3133 tests passed, 1 skipped.

<promise>FAIL</promise>
