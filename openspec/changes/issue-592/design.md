## Context

The unified session page (`pages/session/ui/UnifiedSessionPage` → `useUnifiedSessionDataSource` → `SessionDetailShell`, fed by `widgets/session-transcript` + `entities/session/model/timeline`) replaced the earlier issue-scoped coder-session UI, but the superseded layer was never removed. Three clusters of dead code remain, plus one contract maintained twice:

1. **Dead presentation chain in `widgets/coder-session`** — `SessionCard.tsx` (ActiveSessionCard / RecentCard / WaitingCard), `SessionTimeline.tsx`, `PlanProgressPanel.tsx`, `model/anomaly.tsx`, `model/useSessionTimeline.ts` + `model/session-timeline-reducer.ts`, and the compaction views in `ui/session-health/`. Production consumers: none. The only remaining imports are the slice's own `index.ts` and tests.
2. **Dead SessionEvent view projections in `entities/session`** — `model/view.ts` (`viewSessionEvents`), `model/view/{chat,compact,timeline,helpers}.ts`, the entire `model/types.ts` (which is exclusively the `SessionEvent` / `SessionView*` family), and the cross-slice re-export `@x/session-view.ts`. Their only consumer was the dead chain above plus `AgentSessionEvent` in `entities/coder-session/model/types.ts`, itself used only by the dead `getAgentSessionEvents` client.
3. **Dead session data layer** — `useCoderSessions` + `getCoderSessions`; `useFollowupMutation` + `postFollowup`; `useStopSessionMutation` + `stopSession` + `SessionStopResult`; the issue-scoped clients `getAgentSessionMetadata` / `getAgentSessionTranscript` / `getAgentSessionEvents`; the activity helpers `canRecoverSession` / `deriveSessionActivity`; `buildGenericSessionMetadata.ts`; and in `entities/agent`, the generic-session summary/transcript duplicates (`getGenericSessionSummary`, `getGenericSessionTranscript`, their query options and hooks, `GenericAgentSessionSummaryDto`).
4. **Duplicated data contract** — `pages/session/data/SessionDataSource.ts` re-declares the hook's result as `SessionDataSourceResult` / `SessionTurnControlHandle`. It has already drifted: `runtimeSessionLineage`, `viewedRuntimeSessionId`, `buildLineageTargetPath`, `historicalRuntimeTarget`, `historicalRuntimeId` exist only in the interface (the hook never returns them), and the union `StatusKind` carries members (`live`, `finalizing`, `probing`, `completed`, `failed`, `stale`) that `deriveSessionStatusKind` can never produce.

Verified live consumers that must survive untouched: the six still-consumed `widgets/coder-session` exports (activity events, usage snapshot, `SessionFollowupComposer`, `SessionRecoveryActions`, `ContextHealthBar`, `UsageSnapshotLabel`), `useWorkflowRunSessions` + `getWorkflowRunSessions`, the recovery clients (`compactSession` / `resetSession` / `compactGenericSession` / `resetGenericSession`), the unified clients (`getUnifiedSessionSummary` / `getUnifiedSessionTranscript` + query options + hooks), `canFollowupSession` / `deriveSessionStatusKind`, the generic operations in `entities/agent` (`postGenericFollowup`, `useGenericFollowup`, `useGenericTurnControl`, `getAgentSessions` → `useAgentSessions`), and the timeline projection under `entities/session/model/timeline`.

Constraints: FSD slices with `index.ts` public APIs and `@x/*` cross-slice re-exports; gates `tsc -b`, `check:fsd`, `check:test-boundaries`, `vitest run`, plus the 1000-line git ratchet (deletions only help). The repo's TypeScript version performs negative literal narrowing on property accesses, so the existing `!== 'queued' && !== 'executing'` guard already narrows `currentTurn.status` to `'queued' | 'executing'` — the stop handle's shape survives removal of the hand-written interface.

## Goals / Non-Goals

**Goals:**

- Delete the dead `widgets/coder-session` presentation chain and prune the slice's public API to exactly the six still-consumed export families.
- Delete the `SessionEvent` chat/compact/timeline view family from `entities/session` so the timeline projection is the only session-event projection; prune `entities/session`'s public API to timeline-only exports.
- Delete all session data-layer code with no production consumers (hooks, client functions, helpers, types) in `entities/coder-session`, `entities/agent`, and `pages/session/data`, keeping the verified survivors functional.
- Make the session detail data contract the **inferred** return type of `useUnifiedSessionDataSource`: delete `SessionDataSourceResult`, `SessionTurnControlHandle`, `SessionStopOptions`, and `SessionDataSource.ts`; `SessionDetailShell` and tests consume the hook's actual shape so removing a field becomes a compile-time error at every reader.
- Remove tests that cover only deleted code, prune partially-covered test legs, and switch surviving shell tests to the concrete contract.
- No user-visible behavior changes.

**Non-Goals:**

- Any server, runner, or CLI change; deleted web clients stop existing, their HTTP routes remain for other consumers.
- Touching `entities/session/model/timeline/*` or `widgets/session-transcript` (the surviving chain), beyond the public-API shape they already consume.
- Touching the surviving recovery, workflow-run-session, or unified-session clients and hooks.
- Removing `stopGenericSession` in `entities/agent` (an alias of `controlGenericSession` with no production consumer) — it is not listed in the proposal; see Open Questions.
- Renaming or reshaping the hook's returned fields beyond what inference and dead-field removal require.

## Decisions

### D1. Delete the coder-session presentation chain wholesale; keep the six live exports

Delete: `ui/SessionCard.tsx` (+test), `ui/SessionTimeline.tsx` (+test), `ui/PlanProgressPanel.tsx`, `ui/session-health/CompactionTimelineEntry.tsx` (+test), `ui/session-health/CompactionCompactSummary.tsx` (+test), `model/anomaly.tsx`, `model/useSessionTimeline.ts` (+dom test), `model/session-timeline-reducer.ts` (+test), and `model/derive-tool-call-title.test.ts` (it tests `deriveToolCallTitle`, which lives in the deleted `useSessionTimeline.ts`). Verified none of the survivors (`activity-events.ts`, `usage-snapshot.ts`, `SessionFollowupComposer.tsx`, `SessionRecoveryActions.tsx`, `ContextHealthBar.tsx`, `UsageSnapshotLabel.tsx`) import anything from the deleted modules.

Prune `widgets/coder-session/index.ts` to: `useActivityEvents` / `buildActivityEvents` / `sortActivityEvents` (+ `ActivityEvent*` types), `useActivityUsageSnapshot`, `SessionFollowupComposer` (+props type), `SessionRecoveryActions` (+props type), `ContextHealthBar` (+props type), `UsageSnapshotLabel` — exactly the spec's survivor list.

Two tests need leg-level pruning rather than deletion: `tests/integrate-stage.spec.tsx` tests only `WorkflowStatusTimeline` from the deleted `SessionTimeline.tsx`, so the whole file goes; `tests/context-health-indicator.consistency.spec.tsx` compares `ActiveSessionCard`, `CompactSessionCard`, and `ContextHealthIndicator` for consistent health presentation — drop the `ActiveSessionCard` leg, keep the pulse-vs-indicator comparison.

**Alternative considered:** keep `WorkflowStatusTimeline` by moving it out of `SessionTimeline.tsx`. Rejected: it is a stage-progress chip strip with no production consumer (only its own test imports it); keeping dead UI contradicts the change's purpose.

### D2. Delete the SessionEvent view family completely, including the whole `entities/session/model/types.ts`

`model/types.ts` is 100% the `SessionEvent`/`SessionView*` family (verified export-by-export), so the file is deleted, not edited. Delete `model/view.ts` (+ `view.test.ts`, `view.pi.test.ts`), the `model/view/` directory, and `@x/session-view.ts`. Prune `entities/session/index.ts` to the timeline projection only: `detectShellDomainAction`, `detectToolDomainAction`, `deriveTimelineItems`, `groupTimelineItems`, `isTimelineGroup`, and the `Timeline*` types — matching the spec scenario verbatim. `widgets/session-transcript` already imports only those (verified; its `matchesSessionEvent` etc. are local function names, not the type).

This also removes the last dependency of `entities/coder-session` on `entities/session` (the `SessionEvent` import dies with `AgentSessionEvent` in D3), satisfying the spec's "no module … SHALL import `SessionEvent`" scenario at the type level.

**Alternative considered:** keep `model/types.ts` and prune it. Rejected: nothing in it survives, an empty module is worse than none.

### D3. Delete dead data-layer code bottom-up; remove only types whose consumers are all deleted

Files deleted outright: `entities/coder-session/model/useCoderSessions.ts` (+test), `model/useFollowupMutation.ts`, `model/useStopSessionMutation.ts` (+test), `pages/session/data/buildGenericSessionMetadata.ts`.

Edits:
- `entities/coder-session/api/client.ts`: remove `getCoderSessions`, `getAgentSessionMetadata`, `getAgentSessionTranscript`, `getAgentSessionEvents`, `postFollowup`, `stopSession`, `SessionStopResult`; prune the now-unused `createIdempotencyKey` import. Keep unified + workflow-run + recovery clients, `SessionFollowupResult` / `SessionAttachment` / `SessionAttachmentRejection` (used by the generic followup path and the composer).
- `entities/coder-session/model/sessionActivity.ts`: remove `canRecoverSession` and `deriveSessionActivity` (inline the normalization into `deriveSessionStatusKind`, which keeps exporting the same behavior); keep `canFollowupSession`, `deriveSessionStatusKind`. Prune `sessionActivity.test.ts` accordingly.
- `entities/coder-session/model/types.ts`: remove `AgentSessionEvent` (+ its `SessionEvent` import), `AgentSessionMetadata`, `AgentSessionMetadataCounts`, `CoderSessionSummary`, `CoderSessionItem`, `CoderSessionDetail`, `ToolCallEntry`, `LoopProgress`, `TaskProgressEntry`, `TaskProgressMap`, `CoderTextBuffer` — every one is consumed only by deleted modules (`SessionTimeline.tsx` is the sole consumer of `ToolCallEntry`/`TaskProgress*`; `CoderTextBuffer` has zero consumers anywhere).
- `entities/agent/api/agent-sessions.ts`: remove `GenericAgentSessionSummaryDto`, `getGenericSessionSummary`, `getGenericSessionTranscript`, `genericSessionSummaryQueryOptions`, `genericSessionTranscriptQueryOptions`, `useGenericSessionSummary`, `useGenericSessionTranscript`; prune the `@x/agent-session` import list to the types still used (`AgentSessionActivity` survives in launch DTOs; `AgentSessionTranscriptResponse`, `AgentSessionUsage`, `AgentTurnObservation`, `SessionInputObservation` die with the DTO) and prune `@x/agent-session.ts`'s re-export list to match. Keep `getAgentSessions`, `postGenericFollowup`, `controlGenericSession`, `useGenericFollowup`, `useGenericTurnControl`, launch/observation clients.
- Prune all four `index.ts` public APIs (`widgets/coder-session`, `entities/session`, `entities/coder-session`, `entities/agent`) of removed symbols.

`issueWorkflowKeys` in `entities/issue` survives (other live consumers: issue-workflow, workspace, LiveTaskProvider).

**Alternative considered:** keep the issue-scoped HTTP clients as "alternates" to the unified clients. Rejected: parallel client families for the same data are exactly what made this surface rot; the specs require the unified family to be the only session-detail data clients.

### D4. The data contract is `ReturnType<typeof useUnifiedSessionDataSource>`

- Delete `pages/session/data/SessionDataSource.ts` entirely (`SessionDataSourceResult`, `SessionTurnControlHandle`, `SessionStopOptions`).
- In `useUnifiedSessionDataSource.tsx`: drop the explicit `: SessionDataSourceResult` return annotation and the `useMemo<SessionTurnControlHandle | null>` generic so both types are inferred; inline the stop callback's options parameter type (`options?: { onSuccess?: (result: { state: string }) => void; onSettled?: () => void }`), which stays structurally compatible with `SessionDetailShell`'s `stop.mutate({ onSuccess })` call. Export the contract as an alias: `export type UnifiedSessionDataSourceResult = ReturnType<typeof useUnifiedSessionDataSource>`.
- `SessionDetailShell.tsx` types `data: UnifiedSessionDataSourceResult` (and the header's `stop` as `UnifiedSessionDataSourceResult['stop']`), importing from `../data/useUnifiedSessionDataSource`. Shell tests (`SessionDetailShell.test.tsx`, `tests/SessionDetailShell.followup-queue.spec.tsx`, `tests/SessionDetailShell.sibling-nav-dedup.spec.tsx`) switch their `makeData` fixtures to the alias.
- Status typing: delete the local `StatusKind` union. Its extra members were unreachable and the inferred `statusKind` field narrows to `SessionStatusKind` (`'idle' | 'active' | 'unknown'`); the shell's presentation map is `Partial`, and `SessionStatusKind` is assignable to `shared/lib`'s `SessionTimeStatusKind`, so the shell simply uses `SessionStatusKind` from `entities/coder-session`. No drift between a named union and the actual field is possible anymore.
- `emptyStateKind` + `EmptyStateKind`: delete them with the interface. The hook computes the field but **zero production modules read it** (verified: only test fixtures set it); it is presentation state of the superseded UI. The contract keeps exactly what the hook returns, and the hook stops returning dead weight.

Inference details that make this safe: the `stop` handle keeps `state: 'queued' | 'executing'` because the current TypeScript negative-literal-narrows `currentTurn.status` through the existing guard (verified with a scratch compile); fields the interface declared optional but the hook always returns (`transcriptView`, `canFollowup`, `projectId`, …) become required — stricter and intended.

**Alternative considered:** keep `SessionDataSourceResult` but derive its fields from the hook via a mapped/pick type. Rejected: still a hand-maintained module between producer and consumer; the spec explicitly bans a standalone re-declaring interface and requires unused parallel fields to be absent.

**Alternative considered:** keep `emptyStateKind` since the hook does return it. Rejected as inconsistent: the change's premise is that unread contract fields are drift; this one has no readers at all.

### D5. Test strategy: delete-with-subject, prune legs, exact-shape fixtures

Delete with their subjects: all tests listed in D1–D3, plus `entities/coder-session/api/client.test.ts` blocks for removed client functions and `entities/agent/api/agent-sessions.test.ts` blocks for removed generic-session functions. Prune `tests/session-page-test-utils.tsx`: remove `convertLegacyToAgentMetadata` (its only users were the deleted types `CoderSessionDetail`/`AgentSessionMetadata`; no spec imports it) and the dead type imports; keep `makeTurn` / `renderHookWithQueryClient` (used by live-transcript specs). Shell `makeData` fixtures are rewritten against `UnifiedSessionDataSourceResult`, which forces every fixture to carry exactly the fields the hook returns — a fixture omitting a real field no longer compiles.

## Risks / Trade-offs

- [A consumer of a deleted symbol is missed (dynamic import, string key, storybook)] -> Mitigation: exhaustive `rg` sweep over `src/` + `tests/` for every removed symbol before the change is considered done, plus the `tsc -b` gate; all current references were enumerated during design and are confined to the deleted set.
- [Inferred contract is stricter than the old interface — optional fields become required, `statusKind` narrows to three members] -> Mitigation: that strictness is the point (field removal becomes a compile error); the only affected code is `SessionDetailShell` and its three test files, updated in the same change.
- [`emptyStateKind` removal or `StatusKind` deletion turns out to have a hidden reader] -> Mitigation: verified zero production readers via `rg`; if one surfaces, `tsc -b` catches it immediately and the field can be restored in the hook (not in a parallel interface).
- [The context-health consistency test loses one of its three comparison legs] -> Remaining `CompactSessionCard` ↔ `ContextHealthIndicator` comparison still guards health-chip consistency; the deleted leg guarded a deleted component.
- [Deleted issue-scoped client functions suggest their server routes are dead] -> Explicitly out of scope: server routes remain for other consumers; only the web duplicate clients disappear. Documented in the proposal's Impact section.
- [Large single deletion is hard to review] -> Mitigation: implement as ordered mechanical steps (D1 → D2 → D3 → D4), each compiling and passing gates before the next; deletions are pure removals with no logic edits except the two typed-contract touchpoints (D4) and the activity-helper inline (D3).

## Migration Plan

Single PR against `packages/web`, no server/schema/config changes, ordered so every intermediate commit compiles:

1. **D1** — delete the coder-session widget chain + its tests, prune `widgets/coder-session/index.ts`, drop the `ActiveSessionCard` consistency-test leg and `tests/integrate-stage.spec.tsx`.
2. **D2** — delete the `entities/session` view family (`model/types.ts`, `model/view*`, `@x/session-view.ts`), prune `index.ts` to timeline-only exports.
3. **D3** — delete dead data layer (`useCoderSessions`, `useFollowupMutation`, `useStopSessionMutation`, issue-scoped + generic duplicate clients, `sessionActivity` dead helpers, `buildGenericSessionMetadata.ts`, orphaned types) and prune the four slice `index.ts` files, `client.test.ts`, `agent-sessions.test.ts`, `session-page-test-utils.tsx`.
4. **D4** — delete `SessionDataSource.ts`; switch the hook to an inferred return with the exported `UnifiedSessionDataSourceResult` alias; update `SessionDetailShell` and the three shell test fixtures; remove `emptyStateKind` / local `StatusKind`.
5. Gates after each step (mandatory after 4): `npm run typecheck`, `npm run test:ci` (includes `check:fsd` + `check:test-boundaries` + `vitest run`), and a final `rg` sweep proving no removed symbol remains. `npm run build` before merge.

**Rollback:** the change is pure web deletion plus one typing switch — `git revert` of the single commit restores the previous state; no data, API, or persisted-state migration exists. The spec deltas ship in the same commit, so a revert also retracts them.

## Open Questions

- `stopGenericSession` in `entities/agent` is a zero-consumer alias of `controlGenericSession` (only its own test references it). Out of the proposal's scope — delete in a follow-up cleanup or amend this change's task list? Recommendation: follow-up, to keep this change strictly to the enumerated dead set.
- The 1000-line git ratchet: deletions only lower line counts, but `useUnifiedSessionDataSource.tsx` grows slightly (type alias + inlined options type). Confirm it stays comfortably under the ratchet — expected yes (file is ~330 lines).
- Whether `@x/agent-session.ts` should prune re-exports that `entities/agent` no longer imports after D3 (`AgentSessionTranscriptResponse`, `AgentSessionUsage`, `AgentTurnObservation`, `SessionInputObservation`). Recommendation: prune to the consumed set so the cross-slice surface stays minimal; keep the file itself (other re-exports stay live).
