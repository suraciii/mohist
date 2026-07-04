## Context

A user who launches a generic (agent-launch) agent session from the Web Agent
workbench has no way to stop it from the UI. When the agent runs away or the
session hangs (the symptom addressed by prerequisite #345), the user can only
wait for a timeout or kill the runner process.

The cancel path is already implemented and tested end-to-end:

- **Server**: `POST .../agent-sessions/{sessionId}/cancel`
  (`AgentSessionCancelRoutes.cs`) → `ResolveGenericCancelTargetAsync` → SignalR
  `CancelAgentSession`. 11 spec cases cover the endpoint.
- **Runner**: `handleCancel` (`runner-signalr.ts:561`) sends an ACP
  `session/cancel` *notification* (not a request) to the agent and reports the
  honest observed state — `cancelled` / `not-cancellable` / terminal. It
  explicitly rejects any non-`generic` target with `not-cancellable`
  (`runner-signalr.ts:569`), giving a backend guard independent of the UI.
- **Web**: `cancelGenericSession` + `useCancelGenericSession`
  (`entities/agent/api/agent-sessions.ts:110`, `:193`) wrap the endpoint in a
  React Query mutation.

The missing piece is purely UI: `GenericSessionPage` → `SessionDetailShell`
never renders a cancel control, so the hook is dead UI code (invoked only by its
own unit test). This change wires the existing, tested cancel path into the
generic session page header.

**Stakeholders / constraints:**

- **issue-242 spec** (`openspec/changes/archive/2026-06-23-issue-242/specs/agent-session-ui/spec.md:25-29`):
  "the composer **SHALL NOT** include a stop control." This constraint is scoped
  to the *followup composer* — it is respected by placing the new control in the
  *page header*, never inside the composer. issue/workflow sessions stay
  unchanged.
- **Cancel semantics are best-effort**: `session/cancel` is a notification; the
  resolve only proves the message reached the link, not that the agent stopped.
  The session reaches a terminal state only when the agent honours the cancel
  and emits a terminal event (read by `ReadTerminalStateAsync`).
- **Two independent routes** (`app/App.tsx:70` vs `:75`): the generic session
  page and the issue/workflow `SessionPage` share `SessionDetailShell` but are
  fed by different data sources (`useGenericSessionDataSource` vs
  `useIssueSessionDataSource`). The shared shell is the natural seam.

## Goals / Non-Goals

**Goals:**

- Surface a page-level cancel/stop control on the generic session detail page,
  rendered in the header region (not the composer).
- Tie control visibility to the non-terminal state (`isRunning`); hide/disable
  it once the session reaches `completed` / `failed` / `cancelled` / `stopped`.
- Gate the action behind a destructive-toned confirmation dialog.
- Surface the honest cancel outcome (`cancelled` / `not-cancellable`) to the
  user; do not fabricate success.
- On a successful cancel, refresh session summary + transcript without a manual
  reload.
- Preserve the issue-242 composer constraint and leave issue/workflow session
  pages entirely untouched.

**Non-Goals:**

- Adding stop controls to issue/workflow sessions (issue-242 excludes it; runner
  rejects non-generic targets with `not-cancellable`).
- Changing the cancel backend semantics or adding any new server/runner
  capability. The endpoint, SignalR method, and `handleCancel` are final.
- Introducing pause/resume (out of scope — belongs to #133 "Interactive agent
  sessions").
- Guaranteeing the agent honours the cancel.

## Decisions

### D1. Extend the shared `SessionDataSourceResult` with optional cancel fields

Add nullable cancel-related fields to `SessionDataSourceResult`
(`pages/session/data/SessionDataSource.ts`):

```ts
cancel: {
  mutate: () => void
  isPending: boolean
} | null
```

- The **generic** data source (`useGenericSessionDataSource`) returns a populated
  `cancel` object (wrapping the existing `useCancelGenericSession` mutation).
- The **issue/workflow** data source (`useIssueSessionDataSource`) returns
  `cancel: null`, so `SessionDetailShell` renders no control on those routes.

**Rationale:** `SessionDetailShell` is already the shared presentation layer for
both routes; an optional field is the smallest change that keeps the shell
polymorphic without forking it into two components. The nullable discriminator
also makes the non-regression guarantee structural (not just behavioural): the
issue source physically cannot supply a cancel mutation.

**Alternatives considered:**

- *Render the cancel control only inside `GenericSessionPage`, bypassing the
  shell.* Rejected — the control belongs in the header, which the shell owns and
  renders in four different branches (loading / waiting / empty / live). Duplicating
  the header in `GenericSessionPage` would split header logic and regress on the
  existing sticky-title/recovery-bar behaviour.
- *A separate `<SessionCancelControl>` slotted via `siblingNav` / a new header
  slot.* Rejected — `siblingNav` is issue-session-only lineage navigation and is
  already `null` for generic sessions; overloading it would conflate two
  concerns. A dedicated optional `cancel` field is explicit and typed.

### D2. Place the control inside `SessionHeader`, gated by `isRunning`

`SessionHeader` already receives `statusKind` and renders the status badge. Add
the cancel trigger (a `Button` with `variant="destructive"` + a stop icon) to
the header's status row, rendered **only when** `cancel != null && isRunning`.

- **Visibility predicate:** reuse the already-computed `isRunning` from the data
  source (`useGenericSessionDataSource.ts:53`). This is the same flag that
  drives the followup composer's `disabled` state, so cancel visibility and
  composer enablement stay in lockstep — no second derivation of "terminal".
- Terminal states (`completed` / `failed` / `cancelled` / `stopped`) all map to
  `isRunning === false`, so the control disappears automatically once the
  session settles — including after a successful cancel when the next summary
  refetch lands.

**Rationale:** `isRunning` is the canonical "session may still be doing work"
signal already consumed by every other running-gated UI branch (auto-scroll,
followup composer). Deriving a separate predicate in the shell would be a
source of drift.

### D3. Confirmation via the existing shared `AlertDialog` (destructive tone)

Reuse `@/shared/ui/components/alert-dialog` with `tone="destructive"`, matching
the pattern already established by `IssueCommentsSection.tsx:122` for
destructive confirmations.

- Local component state (`useState<boolean>`) holds the dialog's `open` flag.
- The header `Button` only opens the dialog; it does **not** call `cancel.mutate`.
- `AlertDialog.onConfirm` calls `cancel.mutate()`. The dialog's `loading` prop
  is bound to `cancel.isPending` so the confirm button shows "Working..." and
  disables dismiss while the request is in flight (matching the `AlertDialog`
  component's `loading` guard at `alert-dialog.tsx:39-47` and its loading text
  at `:82`).
- On dismiss (`onOpenChange(false)` without confirm), no request is sent and the
  session keeps running.

**Alternatives considered:**

- *Window.confirm().* Rejected — not styled, not accessible, blocks the event
  loop, inconsistent with the rest of the app.
- *A new dedicated confirm component.* Rejected — `AlertDialog` already covers
  the exact shape (title / description / confirm+cancel buttons / loading /
  destructive tone). Adding a second dialog would violate the conventions doc's
  "no redundant primitives" guidance.

### D4. Make the cancel outcome honest (fix the existing hook's toast)

The existing `useCancelGenericSession.onSuccess`
(`entities/agent/api/agent-sessions.ts:199-207`) unconditionally fires
`toast.success('Session cancelled')` and ignores the response `state`. This
conflicts with the spec scenario *"Non-terminal outcome is surfaced honestly"*:
when the backend returns `{ state: 'not-cancellable' }` (no live ACP session or
the agent did not honour cancel), the user would see a misleading success
toast.

**Decision:** refine `useCancelGenericSession.onSuccess` to inspect `data.state`:

- `state === 'cancelled'` or a terminal state → `toast.success('Session cancelled')`.
- `state === 'not-cancellable'` → `toast.warning('Session could not be cancelled')` (or equivalent honest copy), **and still invalidate** the queries so the page refetches the current truth.
- Keep `onError` as the network/5xx path.

The query invalidation behaviour is otherwise unchanged: the existing
`invalidateQueries({ queryKey: ['agent-session', projectId, sessionId] })` is a
prefix match and already covers both the summary query
(`['agent-session', projectId, sessionId]`) and the transcript query
(`['agent-session', projectId, sessionId, 'transcript']`), so no additional
invalidation call is needed. (Verified against TanStack Query prefix semantics;
matches the pattern in `useGenericFollowup`.)

**Alternatives considered:**

- *Leave the hook alone and surface honesty only in the page.* Rejected — the
  toast fires from the hook's `onSuccess` before the page can intercept it, so
  the misleading message would already be shown. The honesty requirement must
  be fixed at the hook.
- *Drop the toast entirely and rely only on query refetch.* Rejected — silent
  cancels are worse UX; the user needs acknowledgement that the click did
  something.

### D5. No backend / runner changes

The server endpoint, `ResolveGenericCancelTargetAsync`, the SignalR method, and
runner `handleCancel` are unchanged. The runner's existing
`target.kind !== "generic" → not-cancellable` guard
(`runner-signalr.ts:569`) is the second layer of isolation: even if a cancel
request were somehow misrouted at an issue/workflow session, the runner would
reject it. This is asserted by the spec's "Non-generic cancel target is
rejected by the runner" scenario and covered by existing runner specs.

## Risks / Trade-offs

- **[Agent ignores `session/cancel`]** → The control's mutate resolves with
  `not-cancellable`; D4 surfaces this honestly via a warning toast and a query
  refetch so the page reflects the true state. The user is not lied to, but
  they may need to fall back to killing the runner. This is an accepted
  limitation — the ACP `session/cancel` contract is notification-only by
  design.
- **[Race: session terminates between click and confirm]** → After confirm,
  `cancel.mutate()` runs; if the session already terminated server-side, the
  backend reports the terminal state (or `not-cancellable`), the toast reflects
  it, and the next refetch hides the control via `isRunning === false`. No
  user-visible corruption.
- **[Race: control clicked while summary refetch in flight]** → `isRunning` is
  derived from the latest cached summary; React Query's default stale-while-revalidate
  means the user might see the button for a frame after the session ended.
  Accepted — the confirmation dialog adds a ~hundreds-of-ms window during which
  a refetch will land and hide the button.
- **[Shared `SessionDetailShell` change could regress issue/workflow page]** →
  Mitigated structurally: the issue source returns `cancel: null`, so the shell
  renders no control on those routes (D1). Additionally, a dedicated regression
  test (see Tests below) asserts the issue/workflow `SessionPage` renders no
  cancel control. The runner's `not-cancellable` guard is a third, backend
  backstop.
- **[Dialog state leaks across page navigation]** → `open` lives in component
  state inside `SessionDetailShell`; unmounting on route change discards it. No
  global state involved.
- **[Misleading toast copy if hook change is skipped]** → D4 is mandatory; if
  omitted, the spec's "honest outcome" scenario fails. Called out explicitly so
  it is not mistaken for an optional polish step.

## Migration Plan

This is a **web-only** change; there is no backend, runner, persistence, or
config migration.

**Deploy:**

1. Merge the web diff (data source extension, shell control, hook toast fix,
   tests). No server/runner redeploy is required.
2. The cancel affordance appears for running generic sessions on the next web
   bundle load. Existing running sessions are cancellable immediately — the
   backend path was already live.

**Rollback:**

- Revert the web commit. The hook returns to being dead UI code (no behaviour
  change for users; cancel endpoint remains available but unreachable from the
  UI). No data migration to undo.

**Compatibility:**

- Old web bundle + new server/runner: unaffected (web simply does not call the
  endpoint).
- New web bundle + old server/runner: the endpoint already exists on every
  supported server/runner version (the hook has had tests since issue-129/T-005),
  so this is not a real matrix.

## Open Questions

- **Exact copy for the `not-cancellable` toast.** D4 specifies a warning toast
  but not its final wording ("Session could not be cancelled" vs "Agent didn't
  respond to cancel" vs …). Pick the clearest phrasing during implementation;
  keep it short and non-alarming. No spec impact.
- **Button visual treatment.** Whether the header cancel trigger should be a
  ghost/outline button (low emphasis, matches the header's quiet chrome) or a
  solid destructive button (high emphasis, draws the eye). Lean towards
  outline-with-icon to match the existing header button density and reserve
  solid-red for the dialog's confirm action; confirm during visual review.
- **Accessibility: button label.** The trigger should expose an accessible name
  (e.g. `aria-label="Cancel session"`) since an icon-only variant may be used
  on narrow viewports. Trivial, but worth pinning down in the implementation.
