## Context

The `coder_session` table has no `title` field. The UI displays `task_description` (raw prompt first 200 chars) for each session, which is always `<mohist-task>\n\n<role>\nYou are implementi...` — useless for distinguishing sessions. The title needs to be set by each caller at session creation time and flow through DB → API → SSE → frontend.

Current data flow: caller → `AcpSessionOptions`/`AcpConnectionOptions` → `acp-session.ts` (2 sites: `runAcpSession:395`, `createAcpConnection:818`) → `coderSessionRepo.insert()` + `eventBus.emit('coder_session_started')` → API endpoints → frontend.

## Goals / Non-Goals

**Goals:**
- Add nullable `title` column to `coder_session` table
- Plumb `title` through ACP session options, DB insert, SSE events, API responses, and frontend display
- All 7+ callers pass meaningful titles
- Frontend falls back gracefully for old sessions without titles

**Non-Goals:**
- Backfilling titles for existing sessions (frontend fallback handles this)
- Session grouping/folding by task
- Changing the `taskDescription` field semantics

## Decisions

### D1: Nullable column with no data migration

Add `title TEXT` as nullable via schema migration (version 21). Existing rows get `NULL`. Frontend fallback chain (title → executionId task parse → stage → taskDescription) handles old data without a backfill migration.

**Alternatives considered:** Running an UPDATE to backfill titles from executionId parsing. Rejected — adds complexity, the fallback chain covers it at display time with zero migration risk.

### D2: title field on both AcpSessionOptions and AcpConnectionOptions

Both interfaces get `title?: string`. This mirrors existing patterns like `stage`, `issueNumber`, `model` — optional fields that flow from caller through to DB/SSE.

### D3: title passed at caller level, not derived

Each caller explicitly constructs the title string. No auto-generation from `taskDescription` or `executionId` in `acp-session.ts` — the caller has the best context (task ID + title, skill name, issue title).

### D4: coder_session_started SSE event gains title field

The `coder_session_started` event in `event-bus.ts` EventMap gains `title?: string`. Both emission sites in `acp-session.ts` (line ~405 for `runAcpSession`, line ~831 for `createAcpConnection`) pass `options.title`.

### D5: Frontend fallback chain in getSessionLabel

Replace the current `stage → taskDescription` logic in `SessionHeader.tsx:getSessionLabel` with a 4-level priority chain:
1. `session.title` (non-null)
2. Extract task ID from `executionId` via regex `/^(?:build|check)-\d+-(T-\d+)$/`
3. Capitalized `stage` name
4. `taskDescription.slice(0, 24)` (existing behavior)

### D6: Additional callers beyond the 7 listed

Two more callers use `createAcpConnection` and should pass titles:
- `conflict-resolution.ts` → `title: "Conflict resolution"`
- `server/index.ts` build fix → `title: "Auto-fix: build errors"`

## Risks / Trade-offs

- [Old sessions show fallback] → Frontend fallback chain handles this transparently. No user action needed.
- [Caller forgets to pass title] → `title` is optional (`title?: string`), so it degrades to `NULL` with existing fallback behavior. Not a crash risk.

## Migration Plan

1. Deploy backend: schema migration v21 (`ALTER TABLE coder_session ADD COLUMN title TEXT`), repo changes, ACP options, callers, API, SSE
2. Deploy frontend: type updates, fallback chain, SSE handler updates
3. No rollback concerns — `title` is nullable, absence is handled by fallback

## Open Questions

None.
