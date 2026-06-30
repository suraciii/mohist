## Why

#128, #129, and #130 built the backend for direct Agent usage — project-scoped Agent profiles, generic `AgentSession` launch outside a workflow, and a visibility layer (agent-scoped lists, summaries, activity attribution). But none of it is reachable from the Web: agents can still only be triggered through a workflow tied to an issue, and a user cannot browse profiles, start or continue an ad-hoc session, or read its transcript from the UI. This blocks using Mohist agents the way Codex, OpenCode, or Hermes are used directly. With the API prerequisites now complete, the Web workbench is the missing product surface that turns that backend into something a user can actually drive.

## What Changes

- **Agent list page** (new top-level nav entry): browse Agent profiles with runtime/config summary, the most recent session, and availability status.
- **Agent detail page**: profile summary plus the agent's session history grouped by lifecycle state (recent / running / failed / ended), and an entry point to start a new session.
- **Agent profile management UI**: create / edit / archive a profile, configuring `instructions`, `agentConfig` (model + variant via the unified `ModelSelect`), and `skills` metadata. Consumes the #128 CRUD API.
- **New session composer**: pick an agent, enter a prompt, and optionally attach context references (issue / epic / project / repository / workspace path). Context refs are passed as session metadata only — they do **not** create scope, mount, or supervisor lifecycle.
- **Generic session detail / transcript**: open a direct-Agent `AgentSession` by its session id and read its transcript, status, usage, failure category, and context references. Reuses the existing transcript rendering, generalized to a session that has no owning workflow run or issue.
- **Follow-up input**: send a follow-up prompt to an active (non-terminal) generic session, reusing the existing composer affordance.
- **"Ask Agent" quick entry** on issue, epic, and project pages: opens the workbench composer with the current entity pre-filled as context — no supervisor/mount configuration introduced.
- **Empty / error states**: no agents defined, no available runner, external agent unavailable, profile archived, session running/failed/completed.

## Capabilities

### New Capabilities

- `agent-workbench`: The Web surface for direct Agent usage — Agent list & detail pages, profile create/edit/archive UI, the new-session composer (prompt + optional context references), the agent-scoped session history (recent/running/failed/ended), follow-up input for direct sessions, the generic-session detail entry, and the "Ask Agent" quick-entry points on issue/epic/project pages.

### Modified Capabilities

- `agent-session-ui`: The session page and transcript rendering generalize so a generic (non-workflow) `AgentSession` is readable by session id. The session header/breadcrumb requirement currently mandates a "workflow stage" and "a link back to the owning issue"; for a generic session the header SHALL link back to the owning Agent profile (or the referenced issue when an issue context ref exists) and SHALL omit workflow-only fields rather than fabricate them. The header-above-transcript, recovery-bar, compaction-summary, followup-composer, timestamp, syntax-highlighting, and responsive requirements SHALL apply uniformly to a generic session.

## Impact

- **packages/web (new pages & widgets)**: new Agent list, Agent detail, and generic-Agent-session pages; new widgets for the profile editor, new-session composer, agent-scoped session history list, and "Ask Agent" quick entry.
- **packages/web (routing & shell)**: new routes under the project scope (e.g. `agents`, `agents/:ref`, `agent-sessions/:id`) wired in `packages/web/src/app/App.tsx`; a new sidebar nav entry in `AppSidebar.tsx`.
- **packages/web (entities/agent)**: extend the agent API client/queries to consume the #129 launch/followup/cancel endpoints and the #130 agent-scoped list, generic-session summary, transcript, and issue/epic association endpoints (the existing CRUD client stays).
- **packages/web (session page)**: generalize `SessionPage` (`packages/web/src/pages/session/ui/SessionPage.tsx`) and the transcript/followup data sources (today keyed by issue-number + session-name) so a generic session is resolved and read by session id without an owning issue or workflow stage.
- **packages/web (issue & epic detail)**: add an "Ask Agent" entry on `IssueDetailPage` and `EpicDetailPage` that navigates to the composer with context pre-filled.
- **Reuses**: existing transcript, recovery, follow-up composer, and `ModelSelect` widgets, generalized where needed.
- **Backend**: none — purely consumes the completed #128 / #129 / #130 HTTP APIs. No new external dependencies.
