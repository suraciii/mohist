## Why

Both "create agent" entry points on the `/agents` list page navigate to
`/agents/new`, but no such route exists in `App.tsx` (only `agents` and
`agents/:agentId` are registered). The path segment `new` is therefore captured
as `:agentId` and the user lands in `AgentDetailPage`, which calls
`useAgent("new")` → backend 404 → a red "Failed to load agent." banner plus four
404s in the console. The net effect is that **no agent can be created from the
UI**, even though the create capability itself (`AgentProfileEditor` in
`agent === null` mode) is fully implemented and self-contained. This is a P0
entry-point wiring bug isolated to a single file.

## What Changes

- In `AgentListPage.tsx`, replace both `navigate(toProjectPath('/agents/new'))`
  calls — the "New Agent" header button and the `AgentEmptyState`
  "Create Agent" button — with local state that opens the editor dialog.
- Mount `AgentProfileEditor` with `agent={null}` (create mode) using
  **conditional rendering** (`{editorOpen && <AgentProfileEditor … />}`),
  mirroring the pattern already used by `AgentDetailPage`. Conditional mounting
  avoids stale form state when the dialog is reopened.
- Rely on the editor's existing create-mode contract: on success it invalidates
  `['agents']` (so the list refreshes itself) and navigates to the newly created
  agent's detail page. The list page performs no navigation itself.
- Update `AgentListPage.test.tsx` to add regression coverage asserting that
  clicking either create button opens the editor (testid
  `agent-profile-editor`), rather than asserting a route change.

### Non-Goals

- No new `agents/new` route is added — agent creation is a Dialog, not a page,
  consistent with the rest of the codebase.
- No changes to `AgentProfileEditor` form logic, `AgentDetailPage`, the route
  table in `App.tsx`, or any API/persistence contract.

## Capabilities

- `agent-list-create`: The `/agents` list page's agent-creation entry contract —
  the "New Agent" header button and the empty-state "Create Agent" button open
  the `AgentProfileEditor` dialog in create mode (`agent === null`) rather than
  navigating to a route; on a successful create the list refreshes itself via
  query invalidation and the user is routed to the new agent's detail page by
  the editor. Replaces the broken navigation-to-`/agents/new` behavior with the
  single-file dialog-open behavior.

## Impact

- **Web** (`packages/web`):
  - `src/pages/agent-list/ui/AgentListPage.tsx` — the only production file
    changed: add `editorOpen` state, repoint both create handlers, and mount
    `AgentProfileEditor` conditionally.
- **Tests** (`packages/web`):
  - `src/pages/agent-list/ui/AgentListPage.test.tsx` — add regression specs
    asserting the editor dialog (testid `agent-profile-editor`) appears on click
    of both create entry points, replacing any assertion on route navigation.
- **No impact**: server, runner, CLI, API contracts, persistence, the route
  table, `AgentProfileEditor`, or `AgentDetailPage`. No new dependencies.
- **Risk** (low): single-file UI wiring change with no API, persistence, or
  schema surface; behavior is covered by the existing `AgentProfileEditor`
  create-mode contract and guarded by the new regression specs.
