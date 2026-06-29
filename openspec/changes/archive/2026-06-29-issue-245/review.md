# Review Report

## Result: FAIL

## Repaired Items

- _None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/pages/session/ui/SessionPage.tsx; packages/web/src/widgets/session-transcript; packages/server/src/Mohist.Server/Workflow/Services/Sessions/SessionTranscriptBuilder.cs
  Evidence: The compact compaction summary is not available on the current SessionPage transcript path. `SessionPage` renders `SessionTranscriptLayout` for the primary session page transcript (`packages/web/src/pages/session/ui/SessionPage.tsx:981`) and passes only title/count/turn/status/scroll props, with no compaction data or summary slot. `SessionTranscriptLayout` has no compaction-summary prop and renders only toolbar, turn list, thinking, streaming, and TOC (`packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx:32`, `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx:68`). The server transcript DTO builder also drops persisted compaction parts: it handles text/reasoning/tool/status/session_closed only, with no branch for `part.Type == "compaction"` (`packages/server/src/Mohist.Server/Workflow/Services/Sessions/SessionTranscriptBuilder.cs:42`, `packages/server/src/Mohist.Server/Workflow/Services/Sessions/SessionTranscriptBuilder.cs:77`). The new `CompactionCompactSummary` is wired only into the older `SessionTimeline` component (`packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx:528`, `packages/web/src/widgets/coder-session/ui/SessionTimeline.tsx:560`), which the current `SessionPage` does not render. This fails the issue AC "CompactionTimeline 在紧凑视图中可见（不折叠在 round 内）" for the primary session page. [disallowed:product-behavior-change]
  SuggestedAction: Carry compaction events through the session transcript read model, project them into the current session transcript UI, and render `CompactionCompactSummary` above the `SessionTranscriptLayout` turn list while keeping per-round/detail entries available where applicable.
  Verification: Add a SessionPage or SessionTranscriptLayout integration test using real-shaped transcript data containing a persisted `compaction` part, asserting `compaction-compact-summary` renders without expanding any round. Run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `npm test`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/pages/session/ui/SessionPage.tsx; packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx
  Evidence: Lineage links change the URL query string but do not navigate to, anchor, filter, or scroll to the linked runtime session. `SessionPage` reads `?rt=` only to compute `viewedRuntimeSessionId` for link direction (`packages/web/src/pages/session/ui/SessionPage.tsx:710`, `packages/web/src/pages/session/ui/SessionPage.tsx:712`). The `buildTargetPath` callback only returns the same session route with `?rt=<runtimeId>` (`packages/web/src/pages/session/ui/SessionPage.tsx:717`, `packages/web/src/pages/session/ui/SessionPage.tsx:719`). There is no effect that responds to the query param, no transcript boundary ids for runtime sessions, and no scroll/anchor behavior. `CompactionLineageLink` comments promise the page can anchor the transcript at the compaction boundary (`packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx:59`), but the implementation only emits `Link to={...}` anchors (`packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx:119`, `packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx:136`). This fails the AC "compaction 后的新旧 session 间有显式链接" because activating the link does not actually navigate to the linked runtime session in the shared-route model. [disallowed:product-behavior-change]
  SuggestedAction: Implement the promised `?rt=` behavior: expose/derive compaction boundary anchors in the current transcript, mark the relevant boundary/runtime section, and scroll or focus it when the query param changes. Tests should assert URL activation and resulting anchored/visible target behavior, not only href construction.
  Verification: Add a SessionPage integration test with a three-entry lineage and compaction boundaries, click predecessor/successor links, then assert the matching runtime boundary is focused/scrolled/marked. Run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `npm test`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs; packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs
  Evidence: Compact/reset updates the latest context-window snapshot but does not append the post-recovery point to `ContextUsageHistory`. `ApplyRecoveryTransitions` calls `session.RebindRuntimeSession(... usedAfter ...)` and `session.RecordCompaction(...)` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:188`, `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:190`). `RebindRuntimeSession` updates `UsageSummary.ContextWindowUsed` and `ContextWindowSize` but only appends lineage (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:125`, `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:140`), while `RecordCompaction` also updates `UsageSummary` but never calls `AppendUsageHistorySample` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:151`, `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:160`). The history appender is used only by `ApplyUsage` and `RecordContextHealthUpdate` (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:112`, `packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.Transitions.cs:220`). After a compact/reset, Pulse can keep showing the trend ending at the pre-compaction high usage until some later usage/health event arrives, so the "context 用量趋势迷你图" is stale exactly when the recovery action changes context usage. [disallowed:product-behavior-change]
  SuggestedAction: Append a thinned usage-history sample when compaction/reset records the post-recovery context snapshot, ideally in the same transition that updates `UsageSummary`, and cover same-bucket coalescing behavior.
  Verification: Add server domain tests proving `RecordCompaction` or recovery transition appends/coalesces the post-compaction percentage into `ContextUsageHistory` and activity DTO projection includes it. Run `npm test`.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/web/src/pages/session/ui/SessionPage.sticky.test.tsx; packages/web/src/pages/session/ui/SessionPage.lineage.test.tsx
  Evidence: The new SessionPage tests mock `SessionTranscriptLayout` and `projectTurn` (`packages/web/src/pages/session/ui/SessionPage.sticky.test.tsx:106`, `packages/web/src/pages/session/ui/SessionPage.sticky.test.tsx:112`; `packages/web/src/pages/session/ui/SessionPage.lineage.test.tsx:78`, `packages/web/src/pages/session/ui/SessionPage.lineage.test.tsx:84`). That makes them unable to catch the current compaction-summary integration miss in the real transcript layout. The lineage tests assert link presence and href/query construction (`packages/web/src/pages/session/ui/SessionPage.lineage.test.tsx:315`, `packages/web/src/pages/session/ui/SessionPage.lineage.test.tsx:330`) but do not assert that activating the link anchors, scrolls, marks, or changes the viewed runtime transcript. This leaves the main acceptance paths unprotected. [disallowed:test-coverage-behavior-gap]
  SuggestedAction: Add at least one integration test using the real `SessionTranscriptLayout` and real transcript projection for compaction events, plus a lineage activation test that verifies the query param has a visible transcript effect.
  Verification: The new tests should fail on the current candidate and pass after item-1 and item-2 are fixed. Run `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx
  Evidence: The lineage component uses a hand-written inline SVG for the chain glyph (`packages/web/src/widgets/session-health/ui/CompactionLineageLink.tsx:101`) while the same component already uses lucide chevrons and the project convention favors lucide icons where available.
  SuggestedAction: Replace the custom SVG with an appropriate lucide icon during the behavior fix if a matching icon exists.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: issue scope
  Evidence: The issue body calls out active-session Compact/Reset unavailability as a key pain point, but the proposal/design explicitly mark changing active-session Compact/Reset availability as a non-goal. This review did not treat that omission as a failure because it is outside the accepted product scope for this candidate.
  SuggestedAction: Track separately if active-session recovery actions should become available.
  Status: out-of-scope

Verification performed:

- `mo issue show 245 --project-id proj_f6c141d63b6243bfbb481737b2243b87` reviewed current issue details.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 195 files, 2900 passed, 1 skipped.
- `npm test` passed on rerun with a longer timeout. An earlier 120s run timed out and ended with an MSBuild child-node premature-exit message, so it was not used as final verification evidence.

<promise>FAIL</promise>
