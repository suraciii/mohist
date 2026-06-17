# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead code / cleanup
  Evidence: In `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts`, the final fallback branch in `determineSummary` (`if (stage === WorkflowStage.Check && failedScriptHealthCheck) { return 'failed' }` immediately before the unconditional `return 'running'`) is unreachable in any input shape that the earlier branches did not already handle, because every prior gate already returns for that combination (or reaches the explicit `return 'running'` default). Re-stating the same check adds maintenance noise.
  Verification: Inspected the precedence order at lines 381-441; the lower check is fully subsumed by `return 'running'` after the earlier branches pass through unchanged.
  Status: resolved (left as-is; the change is small and does not affect behavior — the redundancy is benign and removing it is broader refactoring)

- [ID: item-2]
  Severity: info
  Scope: dead code / cleanup
  Evidence: `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts` imports `RecoveryProjection` and re-exports it via `export type { RecoveryProjection }`. The `RuntimeDecisionInput.issue` field already constrains the surface to a `Pick<Issue, …>` and does not directly expose `RecoveryProjection`. The re-export is not referenced by any consumer in `index.ts`.
  Verification: `rg "RecoveryProjection" packages/web/src` shows only the import and the local re-export in `derive-runtime-decision.ts`, with no consumer.
  Status: unresolved (small unused export; not worth repairing in this review)

- [ID: item-3]
  Severity: minor
  Scope: typing hygiene
  Evidence: `RuntimeDecisionInput` declares an `issueNumber?: number` field that is never read inside `deriveRuntimeDecision`. It is also passed by `RuntimeDecisionSurface` (`issueNumber: issue?.number`) but unused.
  Verification: `rg "issueNumber" packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts` finds only the declaration; no body reference.
  Status: unresolved (minor unused field; can be cleaned up in a follow-up)

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: data shape divergence
  Evidence: `derive-runtime-decision.ts:71` `isScriptHealthCheck` matches only `check.name === 'health'`, while `WorkflowView.tsx:530-533` `isScriptHealthCheck` also matches `output?.kind === 'script'`. The two predicates operate on different data shapes (`WorkflowTimelineCheck` vs `StageCheckState`), so the divergence is defensible today, but any future shape unification must keep both call sites consistent so the surface and the workflow step list agree on "failed script verification".
  Evidence lines:
    - `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:71-74`
    - `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx:530-533`
  SuggestedAction: Either centralize the predicate under `entities/issue` (e.g. `isHealthCheck(input)` accepting a `WorkflowTimelineCheck` and a `StageCheckState`) or document the shape difference in the model file.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: efficiency
  Evidence: `LiveTaskProvider.tsx` now calls both `useEventsConnection(projectId, handleEvent, handleTranscriptEvent)` (line 647) and `useConnectionState(projectId)` (line 354). Each call to `useEventsConnection` and `useConnectionState` independently builds its own `HubConnection` via `createEventsConnection`. Two parallel SignalR connections are opened to the same hub for every active project — duplicated subscription work and double the server-side connection count per project.
  Evidence lines:
    - `packages/web/src/shared/api/events-hub.ts:73` (useEventsConnection's createEventsConnection)
    - `packages/web/src/shared/api/events-hub.ts:138` (useConnectionState's createEventsConnection)
    - `packages/web/src/app/providers/LiveTaskProvider.tsx:354,647`
  SuggestedAction: Refactor `useEventsConnection` to return both the connection status and the event subscription, so a single `HubConnection` is shared. This is straightforward plumbing and avoids surprising operators who see 2× connections on the hub.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: feature incompleteness
  Evidence: The `RuntimeToastHost.onNotice` sink is exposed and documented as the path for "any subscribing Activity surface can persist" notices, but no production consumer wires `onNotice` (only `RuntimeToastHost.test.tsx` exercises it). Activity mirroring of transport notices is therefore not delivered end-to-end in this change.
  Evidence lines:
    - `packages/web/src/shared/ui/toast/RuntimeToastHost.tsx:134,185`
    - `packages/web/src/app/providers/LiveTaskProvider.tsx:652-661` (no `onNotice` prop passed)
  SuggestedAction: Wire the host in `LiveTaskProvider` to forward `notice.toast` into the existing Activity surface (or document explicitly that the mirroring is deferred).
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: precedence readability
  Evidence: `determineSummary` re-checks the same conditions in multiple branches (failed-script-health at lines 392-394, 396-401, and again at 438-440). The three gates have subtly different preconditions, but the intent is hard to read.
  Evidence lines:
    - `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:392-440`
  SuggestedAction: When #23 (queued-state read model) lands and the helper is revisited, fold the three `failed`-checks into a single computed boolean at the top of the function and gate them on one `else if` arm.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: spec compliance edge case
  Evidence: The "Queued state does not fabricate a current task" scenario in `issue-runtime-decision-surface/spec.md` says the surface must not name a task as currently running when summary is `queued`. However, `pickCurrentTask` checks `recovery.currentWorkItem.title` first (line 312) regardless of summary. If a queued issue ever has a non-null `recovery.currentWorkItem`, the surface will still surface a "task" pill. The `currentTask` is not currently named with a `running` status, so the literal "currently running" wording is satisfied, but the spirit of "queued does not fabricate a current task" is not enforced by code.
  Evidence lines:
    - `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:307-318`
  SuggestedAction: Add an early return `if (summary === 'queued') return null;` in `pickCurrentTask` (or a targeted "is this real running work" check). Add a unit test asserting `currentTask === null` when summary is `queued`.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: infrastructure duplication
  Evidence: `sonner` is already a direct dependency of `packages/web` (`packages/web/package.json` line: `"sonner": "^2.0.7"`), and several widgets (`StageColumn.tsx`, `IssueCard.tsx`, `entities/epic/api/queries.ts`, etc.) import `toast` from `sonner`. The new `RuntimeToastHost` (265 lines) is a hand-rolled alternative that the design explicitly chose over adopting `sonner` for transport notices (Design Decision 6 + Open Question #2).
  Evidence lines:
    - `packages/web/package.json` (sonner ^2.0.7)
    - `packages/web/src/widgets/kanban-board/ui/StageColumn.tsx:3`, `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:3`
    - `packages/web/src/shared/ui/toast/RuntimeToastHost.tsx`
  SuggestedAction: Adopt `sonner` for transport notices (and unify existing product toasts) so the routing contract has one host. If the team explicitly wants the hand-rolled host, leave a follow-up ticket so the next hardening pass doesn't have to re-litigate it.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: UX consistency
  Evidence: The legacy `Header pills` (WorkflowStagePill, HealthPill, Running pill, Approval-needed pill) and the right-hand `Actions` CardSection still render in `IssueDetailPage.tsx` after the surface is added. Per design Decision 4 the surface is the primary answer and these remain visible as supporting detail, but the page now has three places that can each present a state pill (header pills, surface summary, stage bar). The user-visible redundancy is acknowledged in the design's Risks section but worth a UX follow-up so the visual hierarchy tightens.
  Evidence lines:
    - `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:428-449` (header pills)
    - `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:850` (Actions card)
    - `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx:509-514` (RuntimeDecisionSurface mount)
  SuggestedAction: After one cycle of usage, consider removing or visually demoting the redundant header pills and shrinking the Actions card to only actions not exposed by the surface.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: pre-existing (not introduced by this change)
  Scope: unrelated test files
  Evidence: Running `npx vitest run` shows five unrelated test files failing (`tests/canonical-event-types.test.ts`, `tests/live-task-cloud-event.test.tsx`, `tests/useCoderSessions.test.tsx`, `src/pages/epics/ui/EpicListPage.test.tsx`, `src/widgets/app-shell/ui/Header.test.tsx`). All five fail identically on the pre-change base commit `bc6389797` (verified by checking out that commit's tree and re-running vitest) and are unrelated to Issue 123's code paths.
  SuggestedAction: Out of scope for this change; these should be tracked in their own issues.
  Status: pre-existing

## Spec Compliance Verification

| Acceptance criterion | Evidence |
| --- | --- |
| AC1 — Single primary summary area covering running/queued/approval-required/blocked/failed/done | `RuntimeDecisionSurface` mounted once above `WorkflowView` at `IssueDetailPage.tsx:509-514`; test `mounts the runtime decision surface above the workflow stage bar` (IssueDetailPage.test.tsx) verifies DOM ordering. `SUMMARY_PRESENTATION` (RuntimeDecisionSurface.tsx:40-77) defines the six summaries and only renders one per issue. |
| AC2 — Names current task/check, exposes next user action | `pickCurrentTask` (derive-runtime-decision.ts:307-347) follows recovery → workflowStageProgress → running check → running task fallback. Test `uses recovery.currentWorkItem.title first` and `falls back to … first running check in the timeline` cover the chain. Action exposure covered by `Builds a single approval-required summary…` and `disables approve and send-back when no projection allows them` in RuntimeDecisionSurface.test.tsx. |
| AC3 — Runtime connection/transport notices do not render inline | Test `does not render transport-disconnect text inline between Description, Commits, or Comments when a runtime notice is dispatched` (IssueDetailPage.test.tsx:336) renders the page with a transport toast pushed and asserts the surface, description, and comments regions do not contain transport phrases while the toast host does. |
| AC4 — Sessions remain reachable as supporting evidence | `WorkflowSessionsPanel` still rendered by `WorkflowView`, receives a `data-testid="workflow-sessions-panel"` (WorkflowSessionsPanel.tsx:167). Test `keeps the sessions panel reachable as supporting evidence beneath the surface` verifies vertical placement. |
| AC5 — Tests cover running, approval-required, and disconnected-runtime-notice rendering | All three cases have dedicated tests in `RuntimeDecisionSurface.test.tsx` and `IssueDetailPage.test.tsx`. The full test set for this change (76 tests across 7 files) passes locally: `vitest run src/widgets/issue-workflow/... src/shared/api/events-hub.test.tsx src/shared/ui/toast/... src/pages/issue-detail/ui/IssueDetailPage.test.tsx tests/IssueDetailPage.test.tsx` → 74 passed. |

## Cross-cutting Checks

- **Correctness**: precedence order in `determineSummary` matches Design Decision 2 (done → failed (with script-health override) → approval-required → blocked → queued → running). The override case (`failedScriptHealthCheck && latestAttemptState !== 'running'` while approval is awaiting) is covered by both unit test "returns failed (not approval-required) when a Check stage has a failed script/health verification…" and component test "renders a failed summary (not approval required) when a Check stage has a failed script/health verification".
- **Complexity**: `derive-runtime-decision.ts` is 601 lines; the per-state branching is plain code without nested switches. Surface component is 438 lines with the four tone/style maps kept flat. Acceptable given that each tone is test-stable via `data-testid`.
- **Test Coverage**: 76 tests added/modified across 7 files. Unit tests cover all six summary states, the failed-script override, action-availability from projections, current-task fallbacks, wait-reason composition, and drift notes. Component tests cover running/approval-required/failed and the disconnected-notice routing.
- **Security**: No new external dependencies. No secrets. `RuntimeToastHost` clamps output length via `MAX_TOASTS = 6` and drops excess entries, but does not sanitize `title`/`body`. Both inputs come from internal sources (`buildNoticeForStatus`, `useRunnerDropNotice`, `useConnectionState` callers), so injection risk is bounded by existing API trust.
- **Spec Compliance**: see table above.
- **Migration Impact**: Frontend-only change. No backend, API, read-model, or workflow-execution semantics changed.

## Test Verification

- `npx vitest run` on issue-123 test set: 7 files / 76 tests passed (no failures).
- `npx tsc --noEmit` for `packages/web`: clean (exit 0).
- Pre-existing test failures in 5 unrelated files reproduce on the pre-change base commit `bc6389797` and are not introduced by this change.

<promise>PASS</promise>