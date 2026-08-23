# Self-Review — issue 592 plan artifacts (re-review)

Reviewer: plan reviewer (round 2 — disposition verification) · Date: 2026-08-14
Artifacts: `proposal.md`, `design.md`, `tasks.json`, `specs/session-detail-data-source/spec.md`, `specs/session-transcript-timeline/spec.md`, `specs/web-client-conventions/spec.md`
Prior review: 2026-08-14 first review, **FAIL** with MF-1/MF-2/MF-3; `progress.txt` records all three as fixed (no won't-fix dispositions).

## Verdict

**PASS.** All three must-fix findings are fixed properly; the fixes introduced no regressions meeting the must-fix bar; no new must-fix problem surfaced in this round's re-derivation. The plan is ready to build.

## Disposition verification (round-1 findings)

### MF-1 — groups 2/4/5 omitted → **fixed properly**

Re-verified against the codebase, then against the updated artifacts:

- **Group 2** (T-005 / design D6 / `web-client-conventions` spec): `shared/ui/components/markdown-content.tsx` has zero references outside its own file plus one stale test title (`MarkdownReader.test.tsx:97`, retitled in T-005). `shared/ui/toast/` (3 files) is mounted in `app/App.tsx:13`; zero production `useRuntimeToast` feeders — all outside hits are the three test scaffolds the task prunes (`events-hub.test.tsx:37,179`, `IssueDetailPage.test.tsx:357,378,398`, `live-task-cloud-event.spec.tsx`). Sonner callers (`AppContent.tsx`, `run-lifecycle-toast.ts`, `handle-event.ts`, `agent-sessions.ts:312–379`, `CreateIssueDialog`) stay untouched. `shared/ui/index.ts` exports neither module — "needs no change" holds.
- **Group 4** (T-006 / D7 / spec): the three hooks exist with exactly one usage point each (`use-narrow-viewport` → `IssueDetailPage.tsx:123`, `use-media-query` → `SessionDetailShell.tsx:684`, `use-mobile` → `sidebar.tsx:53`). Boundary equivalence verified: `NARROW_QUERY = '(max-width: 1023.98px)'` is literally T-006's replacement query; `use-mobile` enforces `innerWidth < 768` ≡ `(max-width: 767px)`; `useMediaQuery` is jsdom-safe and keeps the `setMatchesForTest` seam. `MarkdownReader.tsx:24–26,338–340` toggles default `false`; no production call site sets them (hits confined to the component and its test); `MarkdownToc` is internal to `MarkdownReader.tsx:307`, and the heading-remap keep/delete split matches the module's actual exports.
- **Group 5** (T-007 / D8 / spec): `packages/web/AGENTS.md` currently contains no convention section; the spec's two scenarios (doc content + reference implementation keeps the factory/hook pairing) are review-time verifiable, and the unified clients' pairing is untouched by T-002.

Done-When #1/#3/#4 are now coverable; proposal Impact's ≈7,100-line deletion estimate comfortably clears the ≥2,500 bar. See Observations 2 for the one literal-wording nuance on #3.

### MF-2 — sweeps named live symbols → **fixed properly**

Every sweep is now word-boundary exact-token, and I re-derived each token's hit set in the current tree:

- T-001 tokens (`ActiveSessionCard`, `RecentCard`, `PlanProgressPanel`, `Compaction*`, `WorkflowStatusTimeline`, `deriveToolCallTitle`, `session-timeline-reducer`, exact `SessionTimeline`) hit only files deleted in T-001 plus the pruned `index.ts`, the deleted `integrate-stage.spec.tsx`, and the consistency-spec leg that is dropped — zero-hit is achievable. Exact-token `SessionTimeline` no longer collides with the live `useSessionTimeline` or the `SessionTimeline*` view types (both are non-word-boundary matches under `rg -w`).
- `useSessionTimeline` (live in `widgets/session-transcript` + imported by `useUnifiedSessionDataSource.tsx`) and `WaitingCard` (live at `entities/agent-ops/model/activity-cards.ts`) are correctly dropped as sweep tokens, covered instead by the file-deletion criterion, with explicit notes.
- T-002's `stopSession` sweep is made unambiguous by a name-only internal rename (`stopSession` → `stopCurrentTurn`): the current callback at `useUnifiedSessionDataSource.tsx:203` is a local `useCallback` const, not exported and not referenced by tests — the rename is behavior-neutral and disclosed.
- `AgentSessionEvent` under `-w` cannot false-positive on the live `AgentSessionEventSummary`; `postFollowup`/`stopGenericSession` similarly protected. T-004 deliberately excludes `issueTitle` from its sweep — correct: it has many live hits (agent-ops, inbox, dashboard-pulse); `tsc -b` instead proves the shell read is gone.

### MF-3 — always-empty fields and empty-render branches → **fixed properly**

Every location the updated D4/T-004/spec cite exists in code exactly as described: hook constants (`useUnifiedSessionDataSource.tsx:303,306,307`), shell destructuring (`:174–178`), header prop pass-through (`:461,467`), `{siblingSidebar}` slot (`:535`), `SessionHeader` props (`:647–665`) with the title-span (`:709–712`) and sibling-nav (`:726–728`) branches, and the inert `xl:flex-row` modifier. `isWideViewport`'s sole consumer is line 726, so the shell's `useMediaQuery` import legitimately dies here and T-006's end-state count (two repointed usage points) is consistent. `tests/SessionDetailShell.sibling-nav-dedup.spec.tsx` exists, exercises only these branches (it imports `setMatchesForTest` solely to drive the never-populated sibling-nav branch), and is deleted. Done-When #2 is now fully met under the issue's own framing.

## Regression sweep of the fixes

- **New @x/agent-session claims hold**: in `agent-sessions.ts`, `AgentSessionUsage`/`SessionInputObservation`/`AgentTurnObservation` occur only inside `GenericAgentSessionSummaryDto` (lines 55–59) and `AgentSessionTranscriptResponse` only in the generic transcript client/options/hook (187, 285, 296) — all die with T-002's removals, so dropping them from that file's import list compiles. `AgentSessionActivity` survives (launch/observation DTOs at 30/46/118); `entities/agent/model/types.ts:1` consumes `AgentSessionEventSummary` + `AgentSessionUsage` from `@x`, matching the trimmed re-export list; `SessionFollowupResult` is live in `entities/agent`. `SessionUsageSummary.tsx` imports `AgentSessionUsage` from `entities/coder-session`'s public API — the type is not in T-002's coder-session removal list, so it keeps resolving.
- **T-002 → T-003 ordering compiles**: after T-002 removes `AgentSessionEvent` (whose `SessionEvent` import at `coder-session/model/types.ts:1` is `@x/session-view.ts`'s only consumer), T-003 can delete the view family and `@x` file. `SessionDataSource.ts` imports only `Timeline*` types from `entities/session` (lines 3–8), so it survives T-003 untouched until T-004 — the sweep and compiler both stay green.
- **Spec deltas are truthful, not aspirational**: the `session-transcript-timeline` requirements describing surviving code match the live implementation (`useSessionTimeline` composes `{ facts, items, entries, currentActivity }`; `SessionTimelineCurrentActivity` carries `'queued' | 'active' | 'idle' | 'unknown'` with label and source id; `deriveTimelineItems`/`groupTimelineItems`/`isTimelineGroup` are the real exports; `entities/session/index.ts` today exports exactly the view family plus the timeline functions the delta prunes to). `UnifiedSessionPage` calls `useUnifiedSessionDataSource` and feeds `SessionDetailShell` as the data-source spec requires.
- **Test-disposition claims verified**: `integrate-stage.spec.tsx` tests only `WorkflowStatusTimeline` (whole file goes); the consistency spec's three legs are exactly `ActiveSessionCard` (dropped) / `CompactSessionCard` (live, from dashboard-pulse) / `ContextHealthIndicator` (kept); `useCoderSessions`/`useStopSessionMutation` and the removed client/hook symbols hit only files T-002 deletes or edits; the hook's `SessionDataSourceResult` annotation (`:82`) and `useMemo<SessionTurnControlHandle>` (`:228`) exist as D4 describes.
- **Gates**: `typecheck`, `test:ci` (`check:fsd` + `check:test-boundaries` + `vitest run`), `build` all exist in `packages/web/package.json`. Task dependency graph is acyclic and correct (T-006 after T-004; T-007 after all; T-005 independent).

## Missed-problem check

No pre-existing must-fix problem surfaced this round: the sweep/conflict surface I re-derived this round (token collisions, import-list survival, ordering, spec-vs-code truthfulness) was either already covered by round 1 or is genuinely clean, so nothing requires justifying an earlier miss.

## Observations (non-blocking)

1. **Design Migration Plan step order is stale relative to tasks.** Design lists D1→D2→D3 and claims "each compiling and passing gates before the next", but after D2 alone the tree would not compile (`AgentSessionEvent`'s `SessionEvent` import dangles until D3). tasks.json reorders correctly (T-002 before T-003) with an explicit note, and tasks are the executable artifact — documentation inconsistency only; harmonize the design paragraph when convenient.
2. **Done-When #3 literal wording** ("三个原使用点全部指向它") is satisfied as two repointed usage points plus the shell's point deleted together with the group-3-required sibling-nav branch; since the issue's own group 3 mandates deleting that branch, the plan's reading (recorded in T-006's notes) is the only coherent resolution. Worth one sentence in the completion report.
3. The `session-transcript-timeline` spec delta pins detailed behavior of untouched live code (fact derivation, grouping thresholds, current-activity fallbacks). Accurate today and useful as a regression guard, but it enlarges the spec's verification surface beyond what the issue asked for.
4. T-002's internal rename (`stopSession` → `stopCurrentTurn`) is a (disclosed, behavior-neutral) edit inside an otherwise pure-deletion change — acceptable for sweep unambiguity.
5. Design's toast-caller paths are abbreviated (`handle-event.ts` is `src/app/providers/handle-event.ts`; `run-lifecycle-toast.ts` is under `src/app/providers/model/`). Naming only; files exist and none change.
6. Carried from round 1, still fine: the beyond-issue data-layer deletions are now explicitly acknowledged as a scope addition in the proposal; `stopGenericSession` is correctly characterized as the live turn-control engine and kept out of scope.

<promise>PASS</promise>
