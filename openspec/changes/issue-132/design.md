## Context

#128, #129, #130 shipped the backend for **direct Agent usage** (no workflow/issue owner): project-scoped Agent profiles (#128 CRUD), generic `AgentSession` launch/followup/cancel keyed by `sessionId` alone (#129), and a visibility layer (#130) — agent-scoped session list, generic-session summary/transcript, and issue/epic context associations. None of it is reachable from the Web today: the only way to drive an agent is through a workflow run tied to an issue, and the existing session page is hard-coupled to `(issueNumber, workflowRunId, sessionName)`.

This change is **web-only** (`packages/web`). No backend work, no new external dependency. The server already exposes every endpoint the workbench consumes.

Relevant existing state:

- **Routing/shell** (`app/App.tsx`, `widgets/app-shell/ui/AppSidebar.tsx`): routes are flat children under `/:projectName` → `ProjectRouteScope` (name-based scope, URL is source of truth). Nav entries are `{ key, label, icon, to }` objects with **project-relative** `to` paths, auto-prefixed via `useProjectPath()`. No `:projectId` param convention — projects are addressed by URL-safe name.
- **`SessionPage`** (`pages/session/ui/SessionPage.tsx`, ~1000 lines): mounted on two issue-scoped routes; `issueNumber` is mandatory everywhere — route resolution, the metadata/transcript fetch (`getAgentSessionMetadata/Transcript(issueNumber, name, projectId)`), the `SessionHeader` breadcrumb (always links to `/issues/{number}`), `SessionRecoveryActions`, `SessionFollowupComposer`, the compaction-lineage link, and sibling navigation (needs `issue.workflowRunId`). The **transcript rendering subtree** (`widgets/session-transcript/*` — `SessionTranscriptLayout`, `TurnList`, `PromptBlock`, `AssistantParts`, tool-views, `projectTurn`, `ContextHealthBar`, `CompactionLineageLink`) is cleanly separable: it operates purely on `SessionTurn`/`DisplayTurn` with no issue dependency.
- **Agent entity** (`entities/agent/`): `#128` low-level CRUD client fns exist (`listAgents`, `getAgent`, `createAgent`, `updateAgent`, `archiveAgent`) plus model helpers (`readAgentModelAndVariant`, `writeAgentModelAndVariant`). **No TanStack Query hooks for CRUD exist.** `#129`/`#130` endpoints are **not consumed at all** — there is no generic-session client wrapper.
- **`ModelSelect`** (`shared/ui/ModelSelect.tsx`): Popover combobox with `onChangeModelVariant(model, variant)` atomic callback. `features/select-issue-model/ui/IssueModelSelector.tsx` is the canonical reuse template.
- **`SessionFollowupComposer`** (`widgets/coder-session/ui/SessionFollowupComposer.tsx`): good UX/state-machine/error-mapping, but hardwired to `useFollowupMutation({ issueNumber, sessionName })`.
- **`AttachmentComposer`** (`shared/ui/attachment-composer/`): drag/drop prompt textarea with attachment uploads — ideal base for the new-session composer.

**Backend contracts the design targets (all complete):**

| Verb | Path | Returns |
|---|---|---|
| `GET` | `/projects/{p}/agents[?status=]` | `AgentInfo[]` (#128) |
| `POST`/`PATCH`/`DELETE` | `/projects/{p}/agents[/{ref}]` | CRUD (#128) |
| `GET` | `/projects/{p}/agents/{ref}/sessions[?status=&limit=]` | `AgentSessionListItemDto[]` (#130), status normalized to `running/completed/failed/stopped` |
| `POST` | `/projects/{p}/agents/{ref}/sessions` | `{ sessionId, agentId, agentName, status, transcriptUrl }` (#129); body `{ prompt, context:{ issueNumber?, epicNumber?, repository?, workspacePath? } }` |
| `GET` | `/projects/{p}/agent-sessions/{sessionId}` | `GenericAgentSessionSummaryDto` (#130) — **different shape** from issue-scoped metadata |
| `GET` | `/projects/{p}/agent-sessions/{sessionId}/transcript` | `AgentSessionTranscriptResponse` (#130) — **shared** `turns[]` shape |
| `POST` | `/projects/{p}/agent-sessions/{sessionId}/followup` | `{ status:'sent' }` (#129) |
| `POST` | `/projects/{p}/agent-sessions/{sessionId}/cancel` | `{ state }` (#129) |
| `GET` | `/projects/{p}/issues/{number}/agent-sessions` · `/epics/{epicRef}/agent-sessions` | `AgentSessionContextAssociationDto[]` (#130) |

Critical constraint: **no `compact`/`reset` endpoint exists for generic sessions** (only issue-scoped). The transcript response shape is shared, so transcript rendering needs no change; only the *metadata* DTO differs.

Stakeholders: end users who today must spawn an issue+workflow to talk to an agent; this surface makes agents directly drivable like Codex/OpenCode/Hermes.

## Goals / Non-Goals

**Goals:**

- Agent list & detail pages, profile create/edit/archive UI, agent-scoped session history grouped by lifecycle state.
- New-session composer (prompt + optional context refs as metadata only) and follow-up input for non-terminal generic sessions.
- A generic session detail/transcript reachable by `sessionId` with no owning issue or workflow stage, reusing the existing transcript rendering.
- "Ask Agent" quick entries on issue/epic/project surfaces that pre-fill context, introducing no supervisor/mount/workflow lifecycle.
- Explicit empty/error states for every blocking condition.
- Consume the already-complete #128/#129/#130 APIs with **zero backend changes**.

**Non-Goals:**

- No backend execution, read-model, or API work (#128/#129/#130 are done; #103 auto-approval and #131 named-agent config are separate).
- No workflow named-agent config UI, no scheduling/autopilot/squads/standing instructions.
- No issue/epic/project/global mount management — context refs are metadata only.
- No `compact`/`reset` for generic sessions in this change (no endpoint; tracked as an open question).

## Decisions

### D1 — Route & shell placement (match existing flat-layout convention)

Add flat children of `ProjectRouteScope` in `app/App.tsx`, using descriptive id params (`:agentId`, `:sessionId`) consumed via `useParams`:

- `agents` → `AgentListPage`
- `agents/:agentId` → `AgentDetailPage`
- `agent-sessions/new` → `AgentSessionComposerPage` (declared **before** `agent-sessions/:sessionId` so the literal wins)
- `agent-sessions/:sessionId` → `AgentSessionDetailPage`

Add one `primaryNav` entry `{ key:'agents', label:'Agents', icon, to:'/agents' }` in `AppSidebar.tsx` and a matching `Tab` in `MobileBottomNav.tsx`. Extend the `usePageTitle` switch in `Header.tsx`. Internal links use `useProjectPath()` (e.g. `toProjectPath('/agent-sessions/' + id)`).

**Alternatives considered:** nesting `agents/:agentId/sessions/:sessionId` — rejected; the generic session is the primary navigable identity and benefits from a short, shareable top-level path mirroring `issues/:number/session/:sessionId`. A modal/drawer composer instead of a route — rejected because "Ask Agent" deep links and browser back must work, and a route is testable and bookmarkable.

### D2 — Generalize `SessionPage` via a `SessionDataSource` seam (not a parallel page)

`SessionPage` is too issue-coupled to dual-purpose by sprinkling conditionals. Instead, introduce a small **data-source abstraction** consumed by a shared `SessionDetailShell`:

- A `SessionDataSource` interface exposing `{ meta: SessionMetadata; transcript: AgentSessionTranscriptResponse; followup: (text)=>Promise; recoveryActions?: ReactNode; backPath: string; backLabel: string }`.
- Two implementations: `IssueSessionDataSource` (current `getAgentSessionMetadata/Transcript` + issue-scoped `postFollowup` + `SessionRecoveryActions`) and `GenericAgentSessionDataSource` (new generic endpoints + generic followup + **no recovery actions**).
- The existing issue-scoped routes keep resolving through `IssueSessionDataSource`; the new `agent-sessions/:sessionId` route resolves through `GenericAgentSessionDataSource`.

The **transcript subtree is reused unchanged** — it already only consumes `SessionTurn`/`DisplayTurn`. Only the header/breadcrumb, recovery region, and follow-up wiring differ per source.

**Rationale:** the spec (`agent-session-ui`) explicitly requires the header-above-transcript, recovery, compaction-summary, followup, and responsive behaviors to "apply uniformly." A shell + two data sources makes that uniformity structural rather than a forest of `if (isGeneric)` branches. It also keeps the issue-scoped path regression-free.

**Alternatives considered:** (a) a brand-new minimal generic page reusing only transcript widgets — rejected, it would duplicate header/recovery/compaction/sticky-scroll logic and drift from the `agent-session-ui` "uniform behavior" requirement. (b) In-place conditionals inside today's `SessionPage` — rejected as too entangled (9 distinct coupling points) and fragile.

### D3 — Normalize `GenericAgentSessionSummaryDto` → internal `SessionMetadata`

The generic summary DTO lacks `sessionName`, `stage`, and `runtimeSessionLineage` that the issue-scoped metadata DTO carries. Add a `buildGenericSessionMetadata(summary)` adapter (sibling to the existing `buildSessionMetadata`) that maps `GenericAgentSessionSummaryDto` → the internal `SessionMetadata`, leaving workflow-only fields **absent** (`stage: null`, `runtimeSessionLineage: []`, `issueId: ''`). The header renders the agent name as the title, links back to the owning Agent profile (`/agents/{agentId}`), and **omits** the workflow-stage badge and owning-issue link rather than fabricating them — exactly the `agent-session-ui` "Generic session header links back to the owning agent" scenario. When a context ref carries an `issueNumber`, the back link prefers the issue (per the spec's `OR` clause).

**Rationale:** the existing `buildSessionMetadata` already hardcodes `issueId: ''`/`executionId: null`, proving the internal shape tolerates a non-workflow source. Normalizing to one internal shape lets the header/recovery/observability bar render from a single code path.

**Alternatives considered:** carrying two parallel metadata shapes through the view layer — rejected, doubles every consumer and violates D2's uniformity goal.

### D4 — Agent profile CRUD query hooks (fill the missing layer)

Add to `entities/agent/api/queries.ts`, following the issue/epic query conventions: `useAgents()`, `useAgent(agentRef)`, `useCreateAgent()`, `useUpdateAgent()`, `useArchiveAgent()`. Query keys `['agents', projectId]`, `['agents', projectId, agentRef]`. Mutations invalidate `['agents']` and (for launch-affecting ones) `['agent-status']`. Re-export from the barrel. The profile editor calls `readAgentModelAndVariant`/`writeAgentModelAndVariant` + `useUpdateAgent`, reusing `ModelSelect` exactly as `IssueModelSelector` does.

### D5 — Generic-AgentSession client module (consume #129/#130)

New module `entities/agent/api/agent-sessions.ts` with pure client fns over `projectApiPath` + the shared `request` envelope: `getGenericSessionSummary(sessionId)`, `getGenericSessionTranscript(sessionId)`, `launchAgentSession(agentRef, { prompt, context })`, `postGenericFollowup(sessionId, text)`, `cancelGenericSession(sessionId)`, plus query/mutation hooks (`useGenericSessionSummary`, `useGenericSessionTranscript`, `useLaunchAgentSession`, `useGenericFollowup`, `useCancelGenericSession`). Query keys `['agent-session', projectId, sessionId]` (+ `'/transcript'`). Hooks `enabled: !!projectId`. Launch/followup/cancel mutations invalidate `['agent-status']`, `['agent-activity']`, the owning `['agents', projectId, agentRef, 'sessions']` list, and the relevant session query.

### D6 — New-session composer & context refs

`AgentSessionComposerPage` (route `agent-sessions/new`) reads query params (`?agent=<ref>`, `?issue=<n>`, `?epic=<ref>`, `?repo=`, `?ws=`) to pre-select the agent and pre-fill context. Prompt textarea reuses `AttachmentComposer`; context refs render as removable chips. Required-field validation mirrors the launch endpoint contract (prompt required; archived agent disables launch). Launch calls `useLaunchAgentSession`; on the `201 { sessionId }` it `navigate(toProjectPath('/agent-sessions/' + sessionId))`. Context is passed **only** as the launch body's `context` envelope — never creating scope/mount/supervisor.

**Alternatives considered:** a dedicated `ContextRefPicker` that fetches issue/epic/repo lists — deferred; v1 accepts explicit refs (from "Ask Agent" entries and manual `?issue=`) and manual entry, keeping the surface small. The association endpoints (#130) already let issue/epic pages show related sessions without a multi-select.

### D7 — Follow-up composer generalization

Generalize `SessionFollowupComposer` to accept an injected followup callback (`onSend(text): Promise`) instead of the hardwired `useFollowupMutation({ issueNumber, sessionName })`. Keep its UX, Enter-to-send, and error mapping (`resolveFollowupErrorMessage` already handles 409/503/404). The issue-scoped call site passes the issue-scoped mutation; the generic session detail page passes `useGenericFollowup`. Terminal-state disabling is driven by the shared `statusKind` and applies uniformly.

### D8 — Recovery bar: context-health + lineage read-only for generic sessions

The recovery region (context-health bar, compaction-summary, Compact/Reset actions) renders from the `SessionDataSource`. For the generic source, `Compact`/`Reset` actions are **omitted** (no endpoint exists and this change adds none); `ContextHealthBar` and the read-only `CompactionLineageLink` still render when data is present, preserving the "recovery bar visible/sticky" requirement. The sticky-in-scroll behavior is retained for both sources (it lives in the shell). If a generic session has no lineage, the whole region is absent — matching "absent rather than fabricated."

**Alternatives considered:** adding generic `compact`/`reset` endpoints — out of scope (web-only Non-Goal) and tracked as an open question; the lineage/context-health value still surfaces without it.

### D9 — Session history grouping on the detail page

The agent-scoped list returns `AgentSessionListItemDto[]` with `status` normalized to `running/completed/failed/stopped` (#130). Group client-side into the spec's four sections — **Running** (`running`), **Failed** (`failed`), **Ended** (`completed`+`stopped`), **Recent** (all, ordered by `lastActivityAt ?? createdAt`, capped) — using a single `useAgentSessions({ agentRef })` query. This avoids N filtered round-trips and matches the endpoint's "ordered by recency" contract.

### D10 — "Ask Agent" quick entries (context pre-fill only)

Add a compact "Ask Agent" entry to:
- `IssueDetailPage` action cluster (`pages/issue-detail/ui/IssueDetailPage.tsx` ~L485–501 and/or the right-rail `Actions` CardSection) → `navigate(toProjectPath('/agent-sessions/new?issue=' + number))`.
- `EpicDetailPage` action cluster (~L705–788) → `…?epic=<id>`.
- `DashboardPage` (project surface) — a hero/zone button → `…/agent-sessions/new` (no ref; project is the implicit scope).

Each entry only builds the composer URL with pre-filled context metadata — it creates **no** supervisor/mount/workflow config, satisfying the "Ask Agent" requirement and its "metadata only" scenario.

## Risks / Trade-offs

- **[DTO shape divergence between issue-scoped and generic session metadata]** → Mitigation: D3's adapter normalizes to one internal `SessionMetadata`; the generic DTO's absent workflow fields are mapped to null/empty, and the header omits workflow-only fields by design. Type-checked at the adapter boundary.
- **[No compact/reset for generic sessions]** → Mitigation: D8 omits those actions for the generic source; context-health + lineage still surface. Surfaced as an Open Question for a future backend task.
- **[`SessionPage` refactor risk to the issue-scoped path]** → Mitigation: the issue-scoped routes resolve through `IssueSessionDataSource`, which wraps the **existing** client fns unchanged — behavior is preserved by construction; existing session-page tests are the regression gate.
- **[Generic-session live SSE matching]** → `useSessionTranscript` matches events by `acpSessionId`/`sessionId` and is largely source-agnostic; its only issue coupling is a `['issues', n, 'coder-sessions']` invalidation on terminal events. → Mitigation: parameterize that invalidation target so the generic source invalidates `['agent-session', …]` instead; verify with a live-session test.
- **[Launch status returns `inactive` until a runner binds]** → the generic summary's `status` normalizes to `running` only after the runner opens the session. → Mitigation: the composer navigates to the detail page immediately and the summary query (with refetch) advances through `running` → terminal; the "no available runner"/"external agent unavailable" error states (D-empty/error states) cover the stuck-`inactive` case.
- **[Composer query-param encoding]** → refs may contain characters needing encoding. → Mitigation: `encodeURIComponent` on every emitted param, matching the existing `projectPath`/`encodeURIComponent` convention.

## Migration Plan

This is an additive, web-only change with no data migration and no backend contract change.

1. **Data layer first** (D4, D5): add CRUD query hooks and the generic-agent-session client/hooks; cover with unit tests against faked responses (no real API — per test policy). Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
2. **SessionPage seam** (D2, D3, D7, D8): extract `SessionDetailShell` + `IssueSessionDataSource`, rewire the issue-scoped routes through it, and assert existing session-page tests stay green. Then add `GenericAgentSessionDataSource` + the `agent-sessions/:sessionId` route.
3. **Workbench pages** (D1, D9): `AgentListPage`, `AgentDetailPage`, profile editor; register routes + sidebar/mobile nav + page titles.
4. **Composer & launch** (D6): `AgentSessionComposerPage` wiring launch → detail navigation.
5. **Quick entries** (D10): "Ask Agent" on issue/epic/dashboard.
6. **Empty/error states** throughout.
7. Final full `typecheck` + `test:run` for `packages/web`.

**Rollback:** the change is isolated to new routes/pages/widgets and a `SessionPage` refactor gated behind a data-source interface. Reverting the web commits restores the prior issue-scoped session page and removes the new nav entries; there is no server-side state to roll back. Feature is behind no flag (per the project's "active development, no version compat" stance) and ships as the default Agent surface.

## Open Questions

- **Generic `compact`/`reset`**: should a follow-up backend task add generic-session compaction endpoints so the recovery actions can be uniform across sources? (D8 currently omits them.)
- **Context-ref picker scope**: is manual/`?ref` pre-fill (D6 v1) sufficient, or do we need an autocomplete that searches issues/epics/repos in the composer?
- **"Project" context ref**: the launch body carries `issueNumber/epicNumber/repository/workspacePath` but no explicit `project` (the project is the URL scope). The "Ask Agent from a project" entry therefore has no ref to attach — acceptable, or should the composer show an explicit "current project" chip?
- **Association surfacing**: the #130 issue/epic association endpoints (`AgentSessionContextAssociationDto[]`) exist — should issue/epic pages additionally render a "Related agent sessions" list now, or is the agent-scoped history on the detail page enough for this scope?
