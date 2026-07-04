## Context

Archiving an Agent is currently a one-way trap advertised as a two-way operation. Two
defects are in scope:

1. **No reverse path + invisible archived agents.** The confirmation dialog
   (`AgentProfileEditor.tsx:255-258`) promises the agent "will remain visible" and "can be
   reversed," but `IAgentGrain` exposes only `ArchiveAsync` (`IAgentGrain.cs:11`), `DELETE`
   maps to archive with no unarchive verb (`AgentDefinitionRoutes.cs:86`), PATCH silently
   ignores a `status` field (it is absent from `GetFields`, `AgentDefinitionRoutes.cs:147`),
   and `useAgents` (`queries.ts:54-61`) calls `listAgents` without `all` so the server
   (`AgentQuerier.ListAsync`, `AgentQuerier.cs:41-59`) returns only `active` rows. The result:
   archived agents are invisible in the UI and unrecoverable without a direct DB edit, while
   their names keep occupying the project-wide uniqueness constraint.

2. **Actions card label/behavior mismatch.** The detail page "Archive" button
   (`AgentDetailPage.tsx:284-294`, testid `agent-detail-archive-btn`) only opens the Edit
   dialog (`setEditorOpen(true)`); the label promises a direct action.

Current state that lowers effort: the data model already has `Status: active|archived`
(`Agent.cs:20-24`, no third state needed), the list page already ships a dormant "Archived
(n)" section (`AgentListPage.tsx:165-174`) that is always empty only because `useAgents`
omits `all`, and the Issue domain already proves the archive/unarchive precedent
(`IssueRoutes.Lifecycle.cs:160` `POST /{number}/unarchive`, `IIssueGrain.UnarchiveAsync`,
`IssueGrain.cs:383`).

The proposal already selected **option (a) — true reversible archive**, sized medium /
risk-medium to cover this cross-layer change. This design explains how to implement it.
See `proposal.md` for motivation and `specs/` for requirements.

## Goals / Non-Goals

**Goals:**
- Make archive reversible end-to-end: grain → API → web client/mutation → detail-page UI.
- Make archived agents visible in the list by populating the already-shipped Archived section.
- Make the archive confirmation text match reality (and, now that unarchive exists, honestly
  state reversibility from the detail page).
- Resolve the Actions card label/behavior mismatch and add an Unarchive affordance for
  archived agents.

**Non-Goals:**
- No third `deleted` status.
- No change to archive's effect on session launch (an archived agent still cannot start new
  sessions — preserved as correct behavior).
- No PATCH `status` support (PATCH semantics unchanged; `status` stays out of `GetFields`).
- No runner / CLI changes; no schema migration.
- No freeing of the name uniqueness slot occupied by an archived agent.

## Decisions

### D1 — Mirror the Issue domain's unarchive precedent, not a new verb
Add `POST /api/projects/{projectRef}/agents/{id}/unarchive` mirroring
`IssueRoutes.Lifecycle.cs:160`. DELETE stays archive; PATCH stays unchanged.
- *Rationale:* consistency with the proven Issue lifecycle; the spec explicitly demands this
  symmetry.
- *Alternative considered:* overload PATCH to accept a `status` field. Rejected — PATCH
  silently ignores `status` today by design, the spec forbids changing PATCH semantics, and
  lifecycle transitions read more clearly as explicit actions than as field patches.

### D2 — `UnarchiveAsync` returns `Task<AgentInfo?>`, symmetric to the *agent* archive contract
The agent grain's `ArchiveAsync` (`AgentGrain.cs:80-87`) returns `Task<AgentInfo?>` and uses
`null` for not-found; the API route maps that to 404 (`AgentDefinitionRoutes.cs:94`).
`UnarchiveAsync` must follow the **agent** archive contract, returning the unarchived
`AgentInfo` (or `null` for not-found) — *not* the Issue domain's `Task`/throw shape.
- *Rationale:* the spec requires unarchive to "match the archive path's contract" for
  not-found; keeping the agent domain internally symmetric lets the route reuse the exact
  `result is null ? NotFound : Ok(result)` shape already at `AgentDefinitionRoutes.cs:94`.
- *Alternative considered:* void + `EnsureIssue()`-style throw (Issue shape). Rejected — loses
  the null/not-found signal the agent API already relies on and breaks agent-domain symmetry.

### D3 — Unarchive of an already-active agent is an idempotent no-op (short-circuit)
If `_agent.Status == Active`, return `AgentQuerier.ToInfo(_agent)` without bumping `UpdatedAt`
or persisting. Archive, by contrast, does not short-circuit (it re-writes and bumps
`UpdatedAt` even when already archived).
- *Rationale:* there is no real state transition on an already-active agent, so `UpdatedAt`
  should not advance and no write should occur; the spec permits either a no-op or a
  well-defined error, and a non-exceptional no-op is the least surprising, most symmetric
  choice.
- *Alternatives considered:* (a) always-write idempotent mirroring archive's non-short-circuit
  — rejected to avoid a spurious `UpdatedAt` bump and write; (b) return 409/conflict —
  rejected as over-engineered (archive itself does not error on already-archived).

### D4 — Unarchive API route returns the unarchived `AgentInfo` (200 with data)
Mirror the DELETE archive route (`AgentDefinitionRoutes.cs:94`), not the Issue route's bare
`Ok()`.
- *Rationale:* agent-domain consistency — archive returns the entity, and the web client
  (`client.ts:81`) already expects `request<AgentInfo>` for `archiveAgent`.

### D5 — Visibility fix: `useAgents` passes `all: true` globally; filter at consumption sites
Change `useAgents` (`queries.ts:58`) to `listAgents(projectId!, { all: true })` so the dormant
Archived section renders, then filter to active-only at the one other consumer that must show
only launchable agents: `AgentSessionComposerPage` (`agent-session-composer/.../AgentSessionComposerPage.tsx:161`).
- *Rationale:* a single query and a single cache key `['agents']` gives uniform invalidation
  across list + detail + composer, and matches the spec ("the agent list query SHALL include
  archived agents end-to-end"). The blast radius is known and small (one other consumer).
- *Alternative considered:* split into `useAgents` (active) + `useAllAgents` (list page).
  Rejected — fragments the `['agents']` cache, risks stale archived state, and diverges from
  the proposal.

### D6 — Actions card: direct-trigger archive for active agents, Unarchive for archived
For an **active** agent, the misleading "Archive" button triggers archive **directly** with
its own confirm dialog (local `archiveConfirmOpen` state, reusing the destructive-confirm
pattern already in `AgentProfileEditor.tsx:251-275`). The testid `agent-detail-archive-btn`
stays but now behaves as its label promises. For an **archived** agent, replace the static
notice (`AgentDetailPage.tsx:295-298`) with an "Unarchive" control bound to
`useUnarchiveAgent`. The top-level Edit button (`AgentDetailPage.tsx:177-185`) stays the
canonical edit entry.
- *Rationale:* the spec permits either direct-trigger or removal; keeping a direct action
  makes the Actions card genuinely useful and gives archive a single, consistent confirm
  pattern, while the unarchive control closes the loop on the detail page.
- *Alternative considered:* remove the active Actions "Archive" button entirely (archive only
  via Edit dialog). Rejected — leaves the Actions card empty for active agents and discards a
  useful shortcut; the Edit-dialog archive entry remains available regardless.

### D7 — Cache invalidation mirrors `useArchiveAgent`
`useUnarchiveAgent.onSuccess` invalidates `['agents']` (covers list and detail — `useAgent`
uses the `['agents', projectId, agentRef]` prefix at `queries.ts:66`) **and** `['agent-status']`
(mirroring `useArchiveAgent` at `queries.ts:108-109` so the runner re-evaluates the agent),
plus a success toast.
- *Rationale:* symmetry with archive; omitting `['agent-status']` would leave the runner's view
  stale after unarchive.

### D8 — Confirmation text rewrite
Rewrite the `DialogDescription` (`AgentProfileEditor.tsx:255-258`) to state the real effect
(leaves the Active group, cannot start new sessions) and, because unarchive now exists, may
honestly state the action is reversible from the agent detail page. Drop the vague "remain
visible" claim and the unbacked "can be reversed" promise.

## Risks / Trade-offs

- **`useAgents` with `all: true` surfaces archived agents in the session-launch picker**
  (`AgentSessionComposerPage.tsx:161`) → *Mitigation:* filter the composer's picker to
  `agents?.filter((a) => a.status !== 'archived')`; the page already guards launch via
  `isArchived`/`canLaunch` (`AgentSessionComposerPage.tsx:185,190`), and the server enforces
  the archived-cannot-launch invariant as pre-existing behavior. Add a composer test covering
  an archived agent not appearing / not launching.
- **Archived agent name still occupies the uniqueness constraint** → not in scope (acceptance
  criteria do not require freeing it); users can rename before archiving via PATCH if name
  reuse is needed. Documented as a known limitation.
- **Concurrent archive/unarchive races** → Orleans single-activates each agent grain key, so
  calls serialize per agent; no extra locking required.
- **Direct-trigger Actions archive duplicates the confirm dialog** (one in the editor, one on
  the detail page) → acceptable; both reuse the same destructive-confirm affordance; the
  alternative (remove the button) was considered in D6.
- **Unarchive of an already-active edge case** → defined as a no-op (D3); deterministic and
  non-exceptional.

## Migration Plan

- **No schema migration** — the `Status` field and `active`/`archived` values already exist
  (`Agent.cs`); `AgentInfo` already carries `status` on the wire.
- **Backward-compatible additive server change** — one new grain method + one new `POST`
  route; existing `DELETE`/`PATCH` semantics are unchanged, so old clients keep working.
- **Ordering** — server and web ship together in this change. If deployed separately, deploy
  server first (route must exist before the web calls it); web-before-server yields 404s on
  unarchive until server catches up.
- **Rollback** — revert the commit. No data migration to undo: archived agents remain
  archived (no data harm), the dormant Archived UI returns to empty, and the confirm text
  reverts to its prior (still-incorrect) wording — i.e. rollback returns to the pre-fix bug,
  not to a broken state.

## Open Questions

- Should unarchive of an already-active agent bump `UpdatedAt`? (Decision D3: no —
  short-circuit no-op. Trivial to flip during review.)
- Toast wording: "Agent restored" (user-facing) vs "Agent unarchived" (jargon)? Lean
  "restored".
- Should the session composer show archived agents as disabled rows (discoverability) rather
  than hiding them? Current recommendation: hide/filter for a clean launcher; the detail page
  is where archived agents are surfaced.
