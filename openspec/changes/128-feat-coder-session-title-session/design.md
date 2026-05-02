## Context

`coder_session` table (v13, migration v15) has `task_description` but no `title`. All session labels are derived from truncated raw prompts, producing unreadable text like `<mohist-task>\n\n<role>\nYou are implementi...`. Each session's caller already has structured context (task ID, stage name, skill name, issue title) available at creation time — the field just doesn't exist to carry it.

Current schema version is **v20**. Next migration is **v21**.

Two code paths create sessions: `runAcpSession` (single-shot, ~line 393) and `createAcpConnection` (multi-round, ~line 816). Both emit `coder_session_started` SSE events. Frontend displays labels via `getSessionLabel()` in `SessionHeader.tsx:20`.

## Goals / Non-Goals

**Goals:**
- Every new coder session gets a human-readable `title` supplied by its caller
- Frontend uses `title` as the primary display label, with fallback chain for old sessions
- Backward compatible — no data migration needed

**Non-Goals:**
- Session grouping/folding (same task, multiple attempts)
- Backfilling titles for existing sessions
- Changing the `task_description` field behavior

## Decisions

### D1: Nullable `title TEXT` column with no default

Add `title TEXT` (nullable, no default) via `ALTER TABLE coder_session ADD COLUMN title TEXT` in migration v21. Old rows remain `NULL`, handled by frontend fallback chain.

**Alternatives considered:**
- `DEFAULT ''` — hides the distinction between "no title" and "empty title", complicates fallback logic
- Backfill migration — unnecessary complexity; frontend fallback chain covers it

### D2: `title` flows through options interfaces, not a separate parameter

Add `title?: string` to `AcpSessionOptions` and `AcpConnectionOptions`. Both `runAcpSession` and `createAcpConnection` pass it to `coderSessionRepo.insert()` and the `coder_session_started` SSE event.

**Alternatives considered:**
- Separate parameter on insert only — breaks the established pattern where all session metadata flows through options
- Derive title from existing fields — no reliable derivation possible for all callers

### D3: Caller-supplied static titles for stage runners

Plan/Check/auto-fix callers use hardcoded strings (`"Plan stage"`, `"Auto-fix: compilation errors"`) because each stage creates exactly one session. RalphExecutor uses dynamic `${task.id}: ${task.title}`.

**Alternatives considered:**
- Auto-derive from first LLM output — adds latency, unreliable, over-engineered for this scope

### D4: Frontend fallback chain in `getSessionLabel`

Priority: `session.title` → taskId from `executionId` → `stage` name → first 24 chars of `taskDescription`. This matches the spec and handles both new sessions (with title) and legacy sessions (without).

**Alternatives considered:**
- Server-side default in API response — violates single source of truth; DB stores NULL, display logic is a frontend concern

### D5: Additional callers discovered during exploration

`conflict-resolution.ts` also creates sessions via `createAcpConnection` — should receive `title: "Conflict resolution"`. `skill-service.ts` does NOT pass `coderSessionRepo`, so it doesn't create `coder_session` rows — no title needed there.

## Risks / Trade-offs

- **[Old sessions show fallback labels forever]** → Acceptable; fallback chain provides reasonable labels from executionId/stage. No user complaint expected since these are historical.
- **[Skill sessions don't get titles]** → `skill-service.ts` doesn't create `coder_session` rows (no `coderSessionRepo` passed), so no change needed. If skills later get session tracking, `title` should be added then.
- **[conflict-resolution.ts missed in original scope]** → Add it with `title: "Conflict resolution"` — same pattern, minimal cost.

## Migration Plan

1. Add migration v21 (`ALTER TABLE coder_session ADD COLUMN title TEXT`)
2. Deploy backend changes (repo, acp-session, callers, API) — all additive, no breaking changes
3. Deploy frontend changes — `title` is nullable, fallback chain handles absence
4. No rollback needed — column is nullable, frontend ignores missing field

## Open Questions

None.
