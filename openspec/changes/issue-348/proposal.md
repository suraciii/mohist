## Why

Archiving an Agent is currently a one-way trap presented as a two-way
operation. The confirmation dialog (`AgentProfileEditor.tsx:255-258`) promises
the agent "will remain visible" and "can be reversed," yet the stack has no
reverse path anywhere: `IAgentGrain` exposes only `ArchiveAsync` (no
counterpart), `AgentDefinitionRoutes.cs` maps DELETE to archive with no
unarchive verb, PATCH silently ignores a `status` field (it is not in
`GetFields`), and `useAgents` filters archived rows out by default because
`AgentQuerier.ListAsync` returns only `active` unless the caller passes
`all: true`. The result: once archived, an agent is invisible in the UI and
unrecoverable without a direct database edit, while its name keeps occupying the
project-wide uniqueness constraint. Agent profiles are reusable configuration
assets (not consumable records); "archive" should mean "set aside temporarily,"
which is exactly the product intent the dialog already advertises. The right fix
is to make the advertised contract true, not to retract it — especially since the
web already ships dormant scaffolding for it (the "Archived (n)" list section at
`AgentListPage.tsx:165-174` is rendered but always empty).

A second, independent defect compounds the trap: the detail page "Actions" card
button is labeled "Archive" with `ArchiveIcon` and testid `agent-detail-archive-btn`,
but its `onClick` merely opens the Edit dialog (`setEditorOpen(true)`). The label
promises a direct action; the behavior is indirect navigation. Whatever lifecycle
option is chosen, this label-vs-behavior mismatch must be resolved.

Decision: **option (a) — true reversible archive.** It honors the existing data
model (`Status: active|archived` already exists, no third state needed), reuses
the existing list-section UI, and mirrors the Issue domain's proven
archive/unarchive precedent (`IssueRoutes.Lifecycle.cs:160`, `unarchiveIssue`,
`useUnarchiveIssue`). The issue is sized medium/risk-medium specifically to
cover this cross-layer option.

## What Changes

- Add a server-side **unarchive operation symmetric to archive**: a new
  `IAgentGrain.UnarchiveAsync` grain method (sets `Status = Active`, bumps
  `UpdatedAt`, persists — mirror of `AgentGrain.cs:80-87`), and a new API route
  on the agent resource. DELETE is already taken by archive, so the route uses a
  new verb/path following the Issue precedent: `POST /{id}/unarchive`.
- Make archived agents **visible in the list**: `useAgents` (`queries.ts:54-61`)
  now fetches with `all: true` so the existing but dormant "Archived (n)"
  section in `AgentListPage.tsx:165-174` is populated; archived rows render
  distinctly and remain clickable into the detail page.
- Add a **web unarchive path**: `unarchiveAgent` client function (next to
  `archiveAgent` in `client.ts:81`) and a `useUnarchiveAgent` mutation (next to
  `useArchiveAgent` in `queries.ts:102`) with `onSuccess` invalidation of
  `['agents']`; export both from the entity barrel.
- Add a **Restore/Unarchive affordance** in the agent detail page Actions card:
  the current static "This agent is archived and cannot be launched." notice
  (`AgentDetailPage.tsx:295-298`) is replaced by an action that returns the
  agent to Active; after unarchive the agent re-appears in the Active group and
  session launch is re-enabled.
- **Fix the lying confirmation text** in `AgentProfileEditor.tsx:255-258`: the
  archive dialog must describe what archive actually does — removes the agent
  from the Active group and blocks new session launches — and, now that unarchive
  exists, may honestly state it can be reversed from the detail page. The vague
  "remain visible" claim is dropped or rephrased to match post-fix reality.
- **Align the Actions card button with its label** (issue problem 2): the
  "Archive" button either triggers archive directly (with its own confirm step)
  or is removed so archive is entered solely via the editor; the top-level Edit
  button (`AgentDetailPage.tsx:177-185`) remains the canonical edit entry. The
  chosen direction must eliminate the label-vs-behavior mismatch.
- Preserve existing correct behavior as non-goals: an archived agent still
  cannot start new sessions; no third `deleted` status is introduced; the
  existing PATCH/DELETE semantics on the agent resource are unchanged.

## Capabilities

- `agent-archive`: The archive operation on an agent — its honest presentation
  (the confirmation dialog text matches the actual effect; the detail-page
  Actions control behaves as its label promises), the effect of archive
  (`Status` flips to `archived`, the agent leaves the Active list group and
  blocks new session launches), and visibility of archived agents in a distinct
  "Archived" list group (queryable end-to-end, surfacing the already-shipped
  dormant UI). This consolidates the existing-but-broken archive into a correct,
  honest contract.
- `agent-unarchive`: The unarchive operation — a new lifecycle action symmetric
  to archive that returns an archived agent to `active`: a server grain method +
  API route that reverses archive, a web client function + mutation, a UI
  affordance in the agent detail page Actions card, and the post-unarchive
  invariants (the agent re-appears in the Active group; session launch is
  re-enabled). Unarchiving an agent that is already active is a no-op or
  well-defined error; unarchiving an unknown agent returns null/not-found,
  matching the archive path's contract.

## Impact

- **Server (C#)**:
  - `Agent/Grains/IAgentGrain.cs` — add `UnarchiveAsync` to the interface
    (alongside `ArchiveAsync` at line 11).
  - `Agent/Grains/AgentGrain.cs` — implement `UnarchiveAsync` symmetric to
    `ArchiveAsync` (lines 80-87), guarding not-found and already-active.
  - `Api/AgentDefinitionRoutes.cs` — add `POST /{id}/unarchive` mirroring
    `IssueRoutes.Lifecycle.cs:160` (DELETE at line 86 stays as archive; PATCH at
    line 58 is unchanged — `status` remains out of `GetFields`).
  - `Agent/Services/AgentQuerier.cs` — no signature change (the `all`/`status`
    params at lines 41-59 already support what the web now requests); the
    visibility fix is purely caller-side.
  - Tests: grain unit tests (archive → unarchive → active round-trip; unarchive
    of active is no-op/error; unarchive of unknown returns null) and API spec
    tests on the new route.
- **Web (React)**:
  - `entities/agent/api/client.ts` — add `unarchiveAgent(projectId, id)` next to
    `archiveAgent` (line 81).
  - `entities/agent/api/queries.ts` — change `useAgents` (lines 54-61) to pass
    `all: true`; add `useUnarchiveAgent` next to `useArchiveAgent` (line 102)
    with `onSuccess` invalidation of `['agents']` and a success toast.
  - `entities/agent/index.ts` — export `unarchiveAgent` / `useUnarchiveAgent`.
  - `widgets/agent-profile-editor/ui/AgentProfileEditor.tsx` — rewrite the
    archive confirmation `DialogDescription` (lines 255-258) to match reality.
  - `pages/agent-detail/ui/AgentDetailPage.tsx` — replace the archived-state
    static notice (lines 295-298) with an Unarchive/Restore control; resolve the
    Actions button label/behavior mismatch (lines 284-294).
  - `pages/agent-list/ui/AgentListPage.tsx` — no structural change (the Archived
    section at 165-174 already exists); verify it renders once `useAgents`
    returns archived rows, and audit any other `useAgents` consumers that
    assumed only active agents.
  - Tests: extend `client.test.ts` / `queries.test.ts` for `unarchiveAgent` and
    the `all: true` list call; extend list-page and detail-page component tests
    for archived visibility and the Unarchive affordance.
- **APIs/Data**: no schema migration — the `Status` field and `active`/`archived`
  values already exist; one new route (`POST /{id}/unarchive`); no wire-type
  changes (`AgentInfo` already carries `status`).
- **Dependencies/systems**: none. The Issue domain's unarchive path
  (`IssueRoutes.Lifecycle.cs`, `unarchiveIssue`, `useUnarchiveIssue`) is the
  template to mirror, not a runtime dependency.
