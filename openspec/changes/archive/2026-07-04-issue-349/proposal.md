## Why

A user who launches a generic agent session from the Web Agent workbench has no
way to stop it once it's running. When the session hangs (e.g. the transcript
stops writing, so the session never reaches a terminal state — the very symptom
fixed in prerequisite #345) or the agent runs away, the user is helpless: they
can only wait for a timeout or kill the runner process.

The pieces to fix this already exist and are tested — the
`POST .../agent-sessions/{sessionId}/cancel` endpoint
(`AgentSessionCancelRoutes.cs:44`, 11 spec cases) and the
`useCancelGenericSession` React Query hook (`agent-sessions.ts:193`) — but the
generic session detail page never calls them. The hook is dead UI code, invoked
only by its own unit test. This change wires the existing, tested cancel path
into the page; it adds no new backend capability.

The issue-242 constraint ("the composer SHALL NOT include a stop control") is
scoped to the followup composer and is respected: the new control lives at the
page level, not inside the composer. Generic agent sessions are user-initiated
work (`source-kind=agent-launch`, no workflow-run-id, no session reaper or
timeout backstop), so the user has a reasonable expectation of controlling what
they started — distinct from issue/workflow sessions, which keep their own
flow/timeout mechanisms and stay unchanged.

## What Changes

- Surface a page-level cancel/stop control on the generic session detail page
  (`GenericSessionPage` → `SessionDetailShell`), placed in the page header
  region — not inside the followup composer.
- The control is visible only while the session is in a running/active
  (non-terminal) state; it is hidden or disabled once the session reaches
  `completed` / `failed` / `cancelled` / `stopped`.
- A confirmation step (reusing the existing shared `AlertDialog` in
  `destructive` tone) guards the action to prevent accidental cancellation.
- Wire the already-implemented `useCancelGenericSession` mutation into the
  control; the cancel is best-effort (the runner fires an ACP `session/cancel`
  *notification* and reports the honest state — `cancelled` /
  `not-cancellable` / terminal — without guaranteeing the agent honours it).
- On a successful cancel, invalidate the session summary/transcript queries so
  the page reflects the new state without a manual refresh.
- Issue/workflow session detail pages are explicitly unaffected: separate
  routes (`App.tsx:70` vs `:75`), and the runner's `handleCancel` rejects any
  non-`generic` target with `not-cancellable` (`runner-signalr.ts:569`), giving
  a second layer of guard.

Non-goals: adding stop controls to issue/workflow sessions (issue-242 excludes
it; runner rejects non-generic targets); changing the cancel backend semantics
(issue-129/T-005 contract is final); introducing pause/resume (#133).

## Capabilities

- `generic-agent-session-cancel`: The page-level cancel affordance on the
  generic (agent-launch) session detail page — visibility rules (running/active
  only), the confirmation step, terminal-state hiding, wiring of the existing
  `useCancelGenericSession` mutation, and the isolation boundary that keeps the
  issue/workflow session pages stop-control-free (honouring issue-242's
  composer constraint). The backend cancel contract is a pre-existing
  dependency, not part of this capability.

## Impact

- **Web (React)**: `pages/session/ui/GenericSessionPage.tsx`,
  `pages/session/data/useGenericSessionDataSource.ts` (expose cancel mutation +
  running flag through `SessionDataSourceResult`), and
  `pages/session/ui/SessionDetailShell.tsx` (render the cancel control in the
  header). New cancel control reuses `@/shared/ui/components/alert-dialog`
  (`destructive` tone) and `entities/agent/api/agent-sessions.ts`
  (`useCancelGenericSession`, already present).
- **Server (C#)** / **Runner (TypeScript)**: no changes — the cancel endpoint
  (`AgentSessionCancelRoutes.cs`), `ResolveGenericCancelTargetAsync`, the
  `CancelAgentSession` SignalR method, and `handleCancel`
  (`runner-signalr.ts:561`) are already implemented and spec-covered.
- **Specs**: the existing `agent-session-ui` capability (issue-242) is not
  modified — its composer constraint still holds; the new
  `generic-agent-session-cancel` capability sits alongside it and encodes the
  page-level affordance plus the non-regression on issue/workflow pages.
- **Tests**: extend `GenericSessionPage.test.tsx` to cover cancel-control
  visibility (running vs terminal), the confirmation gate, mutation
  invocation, and query invalidation on success; add a regression guard that
  the issue/workflow `SessionPage` route renders no cancel control.
