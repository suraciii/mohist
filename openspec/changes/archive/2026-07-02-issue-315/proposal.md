## Why

`packages/web/src/app/providers/LiveTaskProvider.tsx` is an app-level event god-file (scc Complexity 235 / 601 lines) that fuses five orthogonal concerns: CloudEvents envelope unwrapping/normalization, the central ~25-arm event-routing switch, reverse-DNS integration-outcome handling, runner-drop transport notices, and viewed-issue history hijacking. Every category of change — wire format, inbox routing, UI toasts — lands in one file for unrelated reasons. It is the next hotspot under the "代码复杂度热点治理" epic, and its `handleReverseDnsIntegrationOutcome` / lifecycle-toast branches currently have no direct coverage, so safe extraction is blocked without tests-first.

## What Changes

- Extract the pure envelope/normalization helpers (`unwrapEnvelope`, `readEnvelopeField`, `asRecord`, `normalizeToolState`, `normalizeTranscriptDetail`, `unwrapTranscriptEnvelope`, `routeTranscriptEventName`) into `packages/web/src/app/providers/model/event-envelope.ts`; keep the `__testing__` export surface available.
- Extract the reverse-DNS integration-outcome handler (`handleReverseDnsIntegrationOutcome` and its `isRebasePayload`/`isMergePayload`/`readOutcome` helpers) into its own module, returning a declarative `{ rebaseConflict?, invalidations, toasts, rebaseEvent? }` result that the caller applies — removing the side-effecting `queryClient`/`setRebaseConflict`/`toast`/`dispatchRebaseEvent` coupling from the pure outcome decision.
- Extract the toast-routing helpers (`notifyRunLifecycleToast`, `notifyApprovalRequestedToast`, `findIssueNumber`) and the `useRunnerDropNotice` hook into their own files; pass `viewedIssueRef` and `projectId` through as explicit parameters where the closures currently capture them.
- Collapse the central `handleEvent` switch from ~25 inline arms into a per-domain-handler route table after the handlers are extracted.
- **Tests-first**: before any extraction, add branch coverage for `handleReverseDnsIntegrationOutcome` (rebase-completed / merge-success / rebase-conflict / merge-failure arms) and the `notifyRunLifecycleToast` pause/error arms, which today are among only ~3 of ~25 arms exercised.
- No **BREAKING** changes: every TanStack Query invalidation key (`['issues']`, `['issues','detail',id]`, `['agent-status']`, `['agent-activity']`, approval-wait, inbox), the CloudEvents envelope wire shape (duck-typed `specVersion+id+source+type` marker + legacy raw-payload fallback + camelCase/PascalCase field tolerance), toast copy and targeting rules, the `LiveTaskContext` value shape, and the `__testing__` surface consumed by `LiveTaskProvider.test.ts` are preserved bit-for-bit.

## Capabilities

### New Capabilities

_None._ This change introduces no new user-visible or system behavior; it is a pure internal restructuring of Web event routing.

### Modified Capabilities

_None._ Existing realtime/inbox/visibility behavior — invalidation-only inbox hints, project-scoped delivery isolation, high-attention in-app notices and duplicate-notice suppression, agent-session visibility, rebase-conflict surfacing — is preserved bit-for-bit. The `project-inbox-realtime`, `project-inbox-subscription`, and `agent-session-visibility` specs describe behavior, not implementation layout, and no spec-level requirement changes. All acceptance is structural (module placement, collapsed switch, unchanged `__testing__`/Query/toast contract) rather than behavioral.

## Impact

- **Code** (`packages/web/src/app/providers/`):
  - `LiveTaskProvider.tsx` — slimmed to provider orchestration (context wiring, `useEventsConnection` subscription, viewed-issue history tracking); loses the inline helpers, the reverse-DNS outcome router, the toast helpers, and the `useRunnerDropNotice` hook.
  - New files: `model/event-envelope.ts` (pure envelope/normalization), a reverse-DNS integration-outcome module, a toast-routing module, and a `useRunnerDropNotice` module — all under `packages/web/src/app/providers/` (or a `model/` sibling). The `LiveTaskProvider` export and `LiveTaskContext` value shape are unchanged; external consumers require zero changes.
- **Tests**: `LiveTaskProvider.test.ts` (23 tests, the regression guard) must pass unchanged. New unit tests are added first for the reverse-DNS outcome and lifecycle-toast branches before extraction proceeds; extracted modules may gain direct unit tests where they raise testability.
- **APIs / Dependencies / Systems**: none. No server, runner, or CLI changes; no HTTP API, SignalR hub, Query-key, envelope wire-format, or toast change; no new dependencies.
- **Risk**: medium — pure helper extraction is mechanical, but the reverse-DNS outcome and lifecycle-toast branches currently lack direct coverage, which is why the test-first ordering is an acceptance criterion.
