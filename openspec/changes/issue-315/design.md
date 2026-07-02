## Context

`packages/web/src/app/providers/LiveTaskProvider.tsx` is an app-level event god-file (scc Complexity 235 / 601 lines). It fuses five orthogonal concerns behind one `useEventsConnection` subscription:

1. **CloudEvents envelope unwrapping/normalization** — `unwrapEnvelope`, `readEnvelopeField`, `asRecord`, `normalizeToolState`, `normalizeTranscriptDetail`, `unwrapTranscriptEnvelope`, `routeTranscriptEventName` (LiveTaskProvider.tsx:42-211). Pure, already exposed via `__testing__`, but interleaved with impure code.
2. **Central ~25-arm event-routing switch** — `handleEvent` (LiveTaskProvider.tsx:461-561), grouped by Stage / Issue / WorkflowRun / StageApproval / Inbox.
3. **Reverse-DNS integration-outcome handling** — `handleReverseDnsIntegrationOutcome` (LiveTaskProvider.tsx:354-397), side-effecting: it directly calls `queryClient.invalidateQueries`, `setRebaseConflict`, `dispatchRebaseEvent`, and `toast`.
4. **Runner-drop transport notices** — `useRunnerDropNotice` hook (LiveTaskProvider.tsx:227-259), independent of the event stream.
5. **Viewed-issue history hijacking** — monkey-patches `history.pushState`/`replaceState` (LiveTaskProvider.tsx:410-430) to keep `viewedIssueRef` current.

Every category of change — wire format, inbox routing, UI toasts — lands in this one file for unrelated reasons. The file is the next hotspot under the "代码复杂度热点治理" epic (issue #315 / epic #22).

**Testing gap (the medium-risk root cause):** of the ~25 switch arms, only ~3 are exercised today (`StageApprovalResolved` invalidation, and the `InboxItemPersisted` branches). The reverse-DNS outcome arms (`rebase-completed` / `merge-success` / `rebase-conflict` / `merge-failure`) and the `notifyRunLifecycleToast` pause/error arms have **no direct coverage**. This blocks safe extraction until tests exist.

**Constraints that must hold bit-for-bit (the refactor's contract):**
- All TanStack Query invalidation keys: `['issues']`, `['issues','detail',id]`, `['agent-status']`, `['agent-activity']`, `['issues','metrics','approval-wait']`, `['inbox', projectId]`.
- CloudEvents envelope wire shape: duck-typed `specVersion+id+source+type` marker + legacy raw-payload fallback + camelCase/PascalCase field tolerance.
- Toast copy and targeting rules (issue-number lookup, viewed-issue suppression, inbox-page suppression).
- The `LiveTaskContext` value shape `{ activeTaskId, activeTaskElapsedMs, rebaseConflict }` and its single export.
- The `__testing__` surface consumed by `LiveTaskProvider.test.ts`: `{ unwrapEnvelope, unwrapTranscriptEnvelope, routeTranscriptEventName, buildTimelineLiveEvent, parseInboxItemPersistedHint, getCurrentIssueNumber }`.
- The compile-time `_AssertEventNameSubscribed` guard (LiveTaskProvider.tsx:17-34) must keep typechecking after the switch moves.

Stakeholders: the Web app only; no server/runner/CLI touch.

## Goals / Non-Goals

**Goals:**
- Split `LiveTaskProvider.tsx` by concern so each category of future change (wire format vs inbox routing vs UI toast) lands in its own file.
- Extract pure envelope/normalization helpers into `app/providers/model/event-envelope.ts`; keep `__testing__` available.
- Make the reverse-DNS outcome handler side-effect-free (returns a declarative result the caller applies) so it is directly unit-testable.
- Extract toast routing and `useRunnerDropNotice` into their own files, threading `viewedIssueRef` / `projectId` as explicit parameters.
- Collapse the central switch into a per-domain-handler route table.
- Close the coverage gap on reverse-DNS outcome and lifecycle-toast arms **before** any extraction (test-first).

**Non-Goals:**
- No change to Query invalidation semantics, toast behavior, or envelope wire format.
- No performance optimization.
- No new user-visible or system behavior (pure internal restructuring).
- No server / runner / CLI changes; no new dependencies.

## Decisions

### D1. Module layout under `app/providers/`

New files under `packages/web/src/app/providers/`:

| File | Contents (moved from) | Purity |
|------|----------------------|--------|
| `model/event-envelope.ts` | `unwrapEnvelope`, `readEnvelopeField`, `asRecord`, `normalizeToolState`, `normalizeTranscriptDetail`, `unwrapTranscriptEnvelope`, `routeTranscriptEventName`, `isAgentDetailEvent` (LiveTaskProvider.tsx:38-211) | Pure, no React/TanStack/sonner imports |
| `model/reverse-dns-outcome.ts` | `readOutcome`, `isRebasePayload`, `isMergePayload`, `handleReverseDnsIntegrationOutcome` (LiveTaskProvider.tsx:339-397) | Pure after D3 — returns a declarative result |
| `model/run-lifecycle-toast.ts` | `findIssueNumber`, `notifyRunLifecycleToast`, `notifyApprovalRequestedToast` (LiveTaskProvider.tsx:261-300) | Side-effecting (calls `toast`), but takes `queryClient` + `viewedIssue` as params |
| `use-runner-drop-notice.ts` | `useRunnerDropNotice` hook (LiveTaskProvider.tsx:227-259) | React hook |
| `model/timeline-live-event.ts` | `readIssueNumber`, `readTimelineEventId`, `readTimelineTime`, `buildTimelineLiveEvent` (LiveTaskProvider.tsx:302-337) | Pure |
| `use-viewed-issue.ts` | `getCurrentIssueNumber` + the `history.pushState`/`replaceState` monkey-patch effect (LiveTaskProvider.tsx:216-219, 410-430), exposed as a `useViewedIssueRef()` hook returning the ref | React hook |
| `handle-event.ts` (or inline in provider) | The route table + `handleEvent` orchestration | Orchestrator |

`LiveTaskProvider.tsx` is left with: the `_AssertEventNameSubscribed` guard, the provider component, `useLiveEvents` orchestration (subscribe, compose hooks, apply reverse-DNS outcomes), and the `__testing__` re-export.

**Rationale:** A `model/` sibling mirrors the existing `entities/issue/model/` convention (pure logic separated from React). Keeping a `use-*.ts` flat filename for hooks matches React hook naming. Toast helpers and the reverse-DNS outcome are grouped under `model/` because, while they cause side effects, they are pure functions of their arguments once `queryClient`/`toast`/refs are passed in.

**Alternative considered:** Move everything into `entities/issue/model/`. Rejected — these helpers are *app-level event-routing* concern, not issue-domain logic; they route events from many domains (Stage, WorkflowRun, Inbox, AgentSession), so they belong with the app provider, not the issue entity.

### D2. Test-first ordering (acceptance-gated)

Extraction proceeds only after these tests are green, added to `LiveTaskProvider.test.ts` (or sibling unit files once the modules exist):

- `handleReverseDnsIntegrationOutcome`: `IssueWorkCompleted` + rebase payload (clears conflict, dispatches `rebase_completed`, invalidates `['issues']`); `IssueWorkCompleted` + merge payload (merge-success toast); `WorkflowRunFailed`/`StageFailed` + rebase payload (sets `rebaseConflict`, dispatches `rebase_conflict`, error toast); merge-failure arm; and the no-match fallthrough returning `false`.
- `notifyRunLifecycleToast`: `WorkflowRunPaused` → pause toast; `WorkflowRunFailed` → error toast; suppression when `issueNumber === viewedIssue`; suppression when `findIssueNumber` returns `null`.

These are written against the **current** in-file implementations first (via the existing `mountWith` / `handleEvent` harness that drives `useEventsConnection` mock), so they pin behavior before any code moves. The same assertions then guard the extracted modules unchanged.

**Alternative considered:** Write the new tests only against the extracted modules. Rejected — that would not prove behavior was preserved *across* the move; tests must exist on the old code first to act as a refactor safety net.

### D3. Reverse-DNS outcome becomes side-effect-free (declarative result)

`handleReverseDnsIntegrationOutcome` currently couples the pure "which outcome is this?" decision to four side effects (`queryClient`, `setRebaseConflict`, `dispatchRebaseEvent`, `toast`). It will instead return a declarative result:

```ts
type ReverseDnsOutcome =
  | { handled: false }
  | {
      handled: true
      invalidations: QueryKey[]          // e.g. [['issues']]
      rebaseConflict?: RebaseConflictState | null  // null => clear
      rebaseEvent?: RebaseEvent          // 'rebase_completed' | 'rebase_conflict'
      toast?: { tone: 'success' | 'error'; message: string }
    }
```

The caller in `handleEvent` applies the result: runs each invalidation, calls `setRebaseConflict`, `dispatchRebaseEvent`, and the toast. This makes the outcome decider a pure function of `(eventName, parsed)` — directly unit-testable with no React/Query/sonner fakes — while the *application* of effects stays in the orchestrator where the closures live.

**Why not keep it side-effecting and just move it:** the side-effecting form requires a `queryClient`, a React state setter, and the toast module in scope, so it can only be tested through the full provider mount. That is exactly the coverage gap that made this refactor medium-risk. Decoupling decision from effect application is the point of the extraction.

**Alternative considered:** Keep side effects but inject them as a callbacks object. Rejected — equivalent ceremony, but leaves the branching logic impure and harder to read than a plain returned value.

### D4. Collapse the switch into a per-domain-handler route table

After handlers are extracted, the ~25-arm switch becomes a dispatch table keyed by event name. Because the branches share structure (reverse-DNS-outcome gate first, then a domain-specific invalidation set, then an optional toast), but each has enough conditional logic that pure data is awkward, the table maps each name to a **per-domain handler function** rather than a static invalidation list:

```ts
// Pseudocode shape, not final API
type DomainHandler = (ctx: HandlerContext) => void
interface HandlerContext {
  parsed: Record<string, unknown>
  queryClient: QueryClient
  setRebaseConflict: Setter
  viewedIssue: number | null
  projectId: string | null
}
const ROUTE: Partial<Record<EventName, DomainHandler>> = {
  [REVERSE_DNS_EVENT_TYPES.StageStarted]: stageHandler,
  ...
  [REVERSE_DNS_EVENT_TYPES.WorkflowRunPaused]: workflowRunHandler,
  ...
  [REVERSE_DNS_EVENT_TYPES.InboxItemPersisted]: inboxHandler,
}
```

Five handlers: `stageHandler`, `issueHandler`, `workflowRunHandler`, `approvalHandler`, `inboxHandler`, each owning its invalidation set + toast + (for stage/issue/workflowRun) the reverse-DNS-outcome gate. The `handleEvent` body reduces to: `unwrap` → agent-detail dispatch → activity-activity invalidation guard → `ROUTE[name]?.(ctx)` → timeline forward.

**Alternative considered:** A fully data-driven table (static `{ invalidations: QueryKey[], toast?: ... }` per name). Rejected — the reverse-DNS-outcome gating and the `viewedIssue`/inbox-page conditional suppression are runtime decisions, not static data; forcing them into data would re-introduce inline conditionals inside each row and lose clarity.

### D5. Thread captured closures as explicit parameters

Two values are currently captured by closures and shared across concerns; both become explicit parameters on extraction:
- `viewedIssueRef` (LiveTaskProvider.tsx:404) — read by `notifyRunLifecycleToast`, `notifyApprovalRequestedToast`, and the `InboxItemPersisted` branch (via `shouldSuppressInAppNotice`). Extracted toast helpers and `inboxHandler` receive `viewedIssue: number | null` (read from the ref at call site).
- `projectId` (LiveTaskProvider.tsx:541, in `applyInboxHint`'s `{ currentProjectId: projectId }`, and the `useCallback` dep at :569) — passed into `inboxHandler` via `HandlerContext`.

**Rationale:** Passing them explicitly is what makes the handlers unit-testable and relocatable; keeping them as captured closures would force every handler test to mount the full provider.

### D6. Preserve the compile-time guard and `__testing__` surface

- The `_AssertEventNameSubscribed` assertion (LiveTaskProvider.tsx:17-34) stays in `LiveTaskProvider.tsx` (or in `handle-event.ts`) wherever the `ROUTE` table's keys live, so the "every routable name is subscribed" invariant is still enforced at compile time. Moving the table without the guard would silently let an unsubscribed name slip in.
- `__testing__` is re-exported from `LiveTaskProvider.tsx` re-exporting from the new modules (`unwrapEnvelope`/`unwrapTranscriptEnvelope`/`routeTranscriptEventName` from `event-envelope.ts`, `buildTimelineLiveEvent` from `timeline-live-event.ts`, `getCurrentIssueNumber` from `use-viewed-issue.ts`). `LiveTaskProvider.test.ts` imports from `./LiveTaskProvider` unchanged.

## Risks / Trade-offs

- **[Behavior drift during the reverse-DNS outcome decoupling (D3)]** → Mitigation: D2 test-first pins all four outcome arms + fallthrough on the old side-effecting code before any move; the declarative result is applied by the caller in the exact same order (invalidate → setConflict → dispatch → toast).
- **[Missed shared closure (`viewedIssueRef` / `projectId`) silently becomes undefined after extraction]** → Mitigation: D5 enumerates both explicitly; TypeScript signatures on `HandlerContext` make a forgotten parameter a compile error, and the existing inbox-notice suppression tests (viewing same issue / inbox page / other project) guard the runtime path.
- **[`['issues','detail',issueId]` invalidation lost when Issue handler moves]** → Mitigation: carried as an explicit invalidation in `issueHandler`; the contract list above is the acceptance checklist.
- **[Compile-time `_AssertEventNameSubscribed` guard stops firing if the ROUTE table moves away from it]** → Mitigation: D6 co-locates guard and table; a follow-up unit assertion can also check `Object.keys(ROUTE)` is non-empty to avoid an accidentally-empty table that makes the guard vacuously true.
- **[Toast copy / targeting regressions]** → Mitigation: existing 23-test suite + new lifecycle-toast tests assert exact strings and suppression rules; no copy constant is retouched.
- **[Extraction churn obscures a real regression in review]** → Mitigation: commits are ordered tests-first → pure-helper move → outcome decoupling → switch collapse, each independently green so review is incremental.

## Migration Plan

This is a Web-only internal refactor with no API, wire-format, or persistence change — there is no data migration and no coordinated deploy. Rollout is by commit ordering within the PR:

1. **Tests-first** — add the reverse-DNS outcome and lifecycle-toast branch tests against the current in-file code; confirm green.
2. **Pure helpers** — move envelope/transcript/timeline helpers to `model/event-envelope.ts` / `model/timeline-live-event.ts`; re-export `__testing__`; confirm `LiveTaskProvider.test.ts` green + `npm run typecheck -w packages/web`.
3. **Reverse-DNS outcome decoupling (D3)** — convert to declarative result; caller applies effects; confirm the new outcome tests still pass unchanged.
4. **Toast + runner-drop + viewed-issue extraction** — move to their files with explicit params (D5).
5. **Switch → route table (D4)** — collapse; co-locate the compile-time guard (D6).
6. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; optionally add direct unit tests for the now-pure `handleReverseDnsIntegrationOutcome`.

**Rollback:** revert the PR — there is no state to migrate back, and the public surface (`LiveTaskProvider` export, `LiveTaskContext` shape) is unchanged so consumers are unaffected either direction.

## Open Questions

- Should `handle-event.ts` (route table + orchestration) be a separate file, or stay inline in `LiveTaskProvider.tsx`? Decide at step 5 based on whether the provider remains readable; leaning toward a separate `handle-event.ts` since the table + guard are the bulk of the remaining logic.
- Whether to add a dedicated `reverse-dns-outcome.test.ts` (unit, against the pure decider) in addition to the provider-level branch tests — recommended for long-term coverage but not strictly required by the acceptance criteria.
