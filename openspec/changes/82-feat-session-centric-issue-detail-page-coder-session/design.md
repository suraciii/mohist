## Context

The Issue Detail page currently aggregates all coder sessions into a single flat round timeline via `SessionTimeline` + `useSessionTimeline`. Users see an undifferentiated stream of rounds — there's no way to tell which rounds belong to which session, what model each session used, or how long it ran.

**Current architecture:**

- `coder_session` table (v13) has: `id`, `issue_id`, `acp_session_id`, `execution_id`, `task_description`, `status`, `created_at`, `completed_at`. No model/stage/coder_type columns.
- `runAcpSession` (single-shot, Build tasks) and `createAcpConnection` (multi-round, Plan/Review) both insert `coder_session` rows but only pass `issueId`, `acpSessionId`, `executionId`, `taskDescription`.
- SSE events (`coder_text_chunk`, `coder_tool_call`) carry `acpSessionId` and `executionId` but **no `coderSessionId`** (the DB PK). Plan-stage events (`plan_round_start`, `plan_session_update`) carry no session identifiers at all.
- Frontend `useSessionTimeline` fetches all `workflow_log` rows for an issue and reconstructs rounds from event types — no session scoping.
- `GET /:number/coder-sessions` API exists but frontend never calls it.

**Key constraint:** The `coderSessionRepo` is optional in `AcpSessionOptions` — both `runAcpSession` and `createAcpConnection` guard on `if (coderSessionRepo)` before inserting. The SSE enrichment must handle the case where no `coderSessionRepo` is available (events omit `coderSessionId`).

## Goals / Non-Goals

**Goals:**

- Persist model, coder_type, stage metadata on each `coder_session` row
- Carry `coderSessionId` + `model` through SSE events so the frontend can correlate events to sessions
- Add `coder_session_started` / `coder_session_completed` lifecycle SSE events
- Replace `SessionTimeline` with `SessionList` + `SessionDetail` — sessions as the primary UI unit
- Real-time updates for running sessions (duration ticking, text streaming, auto-expand)

**Non-Goals:**

- Modifying the ACP protocol or opencode binary
- Multi-coder support beyond reserving the `coder_type` column
- Persisting rounds in the DB (rounds remain reconstructed from `workflow_log`)
- Changing the `workflow_log` table schema
- Backfilling model/stage data for historical sessions

## Decisions

### D1: migration v15 adds 3 nullable columns to coder_session

`ALTER TABLE coder_session ADD COLUMN {model|coder_type|stage} TEXT` — nullable so existing rows get NULL and no data migration is needed.

**Alternatives considered:**
- New table `coder_session_meta` with FK — adds a JOIN to every query, over-normalized for 3 columns.
- Non-nullable with defaults (e.g. `model TEXT NOT NULL DEFAULT 'unknown'`) — misleading; NULL clearly means "not recorded" vs "known to be unknown".

### D2: coderSessionId propagated via SSE, not a new correlator

We add `coderSessionId` (the `coder_session.id` UUID) to SSE event payloads alongside the existing `acpSessionId`. The frontend uses `coderSessionId` to correlate events to `CoderSessionItem` entries from the REST API. We keep `acpSessionId` in payloads for backward compatibility and because `workflow_log` entries still key on it.

**Why not reuse `acpSessionId` as the sole correlator:** The REST API returns `CoderSessionItem.id` (the DB PK). Frontend components need a stable ID for React keys, expand/collapse state, and query caching. `acpSessionId` is opaque and not the primary key the frontend already uses. Dual-carrying both IDs is a few bytes per event and avoids a mapping layer.

### D3: Plan/Review events get acpSessionId + coderSessionId via createAcpConnection

Currently `plan_round_start` and `plan_session_update` events are emitted from `WorkflowController` without any session identifier. We thread `acpSessionId` and `coderSessionId` through the `createAcpConnection` return value (extend `AcpConnection` interface) so the controller can include them when emitting Plan/Review SSE events.

**Alternatives considered:**
- Store `coderSessionId` on `WorkflowController` instance state — works but couples session identity to controller lifecycle; would break if the controller is reused across sessions.
- Emit Plan/Review events from inside `acp-session.ts` (like Build events) — would require restructuring the multi-round prompt flow since `WorkflowController` drives the prompts.

### D4: useCoderSessions hook — REST initial load + SSE live updates

Initial data: `GET /:number/coder-sessions` (already exists, already returns `CoderSessionItem[]` with nested `workflowLogs`). Live updates: `coder_session_started` inserts into the list, `coder_session_completed` updates matching session. Running sessions get a 1-second `setInterval` for duration ticking.

**Why not SSE-only:** The REST endpoint returns `workflowLogs` for each session, which is needed for historical round reconstruction. Emitting full log history via SSE would be wasteful. Hybrid approach gives fast initial render with live delta updates.

### D5: useSessionTimeline gains optional coderSessionId filter

`useSessionTimeline(issueNumber, coderSessionId?)` — when `coderSessionId` is provided, the reconstructed rounds and live SSE events are filtered to only that session. When omitted, returns all rounds (backward compatible). Filtering uses the `acpSessionId` associated with the `coderSessionId` via a lookup in the `CoderSessionItem` from `useCoderSessions`.

**Alternatives considered:**
- Separate `useSessionRounds(coderSessionId)` hook — duplicates all the reconstruction logic and SSE subscription machinery.
- Server-side filtering via `GET /:number/logs?acpSessionId=...` — requires new API endpoint; the client already has all logs from the coder-sessions response.

### D6: SessionList/SessionDetail replace SessionTimeline in-place

`SessionTimeline` is removed from `IssueDetailPage` and replaced by `SessionList` (renders session entries) + `SessionDetail` (inline-expanded round view). `PipelineStatusTimeline` and `TaskProgressPanel` are preserved as sub-components within `SessionDetail` for the appropriate sessions.

**Component tree:**
```
IssueDetailPage
  └── SessionList              ← replaces SessionTimeline
        ├── SessionHeader       ← metadata bar per session
        └── SessionDetail       ← expanded inline, per session
              └── RoundSection[] ← reused from current SessionTimeline
```

**Alternatives considered:**
- Keep `SessionTimeline` and add session grouping on top — the flat round model is fundamentally wrong for session-scoped display; retrofitting grouping adds complexity.
- Tabs for sessions (one tab per session) — loses at-a-glance overview; accordion/list is better for sequential sessions.

## Risks / Trade-offs

- **[SSE event size increase]** Adding `coderSessionId` + `model` to every `coder_text_chunk`/`coder_tool_call` adds ~80 bytes per event. During high-frequency streaming (10-20 events/sec), this is ~1.5KB/sec overhead — negligible. → Accept.
- **[Plan/Review event threading complexity]** Threading `coderSessionId` from `createAcpConnection` through `WorkflowController` to SSE events requires modifying the `AcpConnection` interface and all prompt call sites in `WorkflowController`. → Careful mapping in implementation; the `AcpConnection` return type already has `prompt()` and `close()`, we add `coderSessionId` and `acpSessionId` as properties.
- **[Missing metadata for historical sessions]** Sessions created before migration v15 will have NULL for model/coder_type/stage. The UI will show "unknown model" for these. → Accept; no backfill attempted. The spec explicitly requires NULL for existing rows.
- **[useSessionTimeline filtering by coderSessionId requires acpSessionId lookup]** To filter `workflow_log` entries (keyed by `acpSessionId`) for a specific `coderSessionId`, we need the mapping from the REST API response. → The `useCoderSessions` hook provides this mapping; `useSessionTimeline` can accept both or accept a `CoderSessionItem` directly.

## Migration Plan

1. **Migration v15** — Add 3 nullable TEXT columns to `coder_session`. Zero downtime; SQLite ALTER TABLE is instant.
2. **Backend changes** — Extend `CreateCoderSessionData`, `CoderSession` interface, `CoderSessionRepo.insert()`, and both `runAcpSession`/`createAcpConnection` call sites. Deploy before frontend changes.
3. **SSE enrichment** — Add fields to existing events (backward compatible; frontend ignores unknown fields). Add new event types to `ALL_EVENT_TYPES` in `event-bus.ts`.
4. **Frontend** — New `useCoderSessions` hook, new components, modified `useSessionTimeline`. Deploy as a single frontend bundle update.
5. **Rollback** — Frontend rollback restores `SessionTimeline`. Backend rollback is safe since new columns are nullable and new SSE fields are ignored by old frontend.

## Open Questions

- **Plan/Review round reconstruction:** Plan-stage events (`plan_round_start`, `plan_session_update`) currently carry no `acpSessionId`. After enrichment, we can correlate them. But historical Plan/Review rounds in `workflow_log` have no `acpSessionId` either — they'll all appear under "unknown session" if strict filtering is applied. Should we fall back to showing uncorrelated rounds in a "History" section, or attempt to match by timestamp proximity?
