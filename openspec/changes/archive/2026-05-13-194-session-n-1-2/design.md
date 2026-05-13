## Context

Issue detail currently depends on `GET /api/issues/:number/coder-sessions` to render the sessions card. That endpoint has drifted into a hybrid list-plus-detail API: it loads base session rows, then performs additional `session_stream_log` and `workflow_log` queries for each session, and parses log payloads so the list can show derived counts. On issues with many historical runs, this produces an N+1 query pattern and expensive server-side parsing, pushing response time past two minutes.

The existing dedicated detail endpoint, `GET /api/issues/:number/coder-sessions/:sessionId`, already owns the expensive work needed for transcript reconstruction and deep inspection. The design should restore a clear separation of responsibilities: issue detail needs a fast summary list, while full logs should only load after the user opens a single session.

Two constraints shape the implementation:

- Existing log rows in SQLite use second-precision `created_at` values from `datetime('now')`, so sorting must remain deterministic while new writes move to millisecond precision.
- Web UI components currently assume list responses include `workflowLogs`-backed derived data, so the contract change must either remove or replace those fields without breaking type safety or session detail navigation.

## Goals / Non-Goals

**Goals:**

- Make `GET /api/issues/:number/coder-sessions` a lightweight list endpoint that returns only metadata needed for the issue detail sessions card.
- Preserve full transcript and log loading on `GET /api/issues/:number/coder-sessions/:sessionId` so session drill-down behavior does not regress.
- Remove list-path dependence on per-session `workflowLogs` payloads and server-side per-row log parsing.
- Add short-lived frontend caching for session lists so page switches do not immediately refetch unchanged data.
- Move new `session_stream_log` and `workflow_log` writes to millisecond-precision ISO timestamps while preserving stable ordering for legacy rows.
- Create a repository shape that supports grouped log access where logs still need to be fetched for multiple sessions.

**Non-Goals:**

- Redesigning the session detail page or transcript rendering model.
- Solving all log parsing overhead in the detail path.
- Backfilling old rows to a new timestamp format.
- Introducing new end-user features beyond faster list loading and stable existing detail behavior.
- Completing longer-term JSON parsing and query unification work identified for a later issue.

## Decisions

### D1: Split list and detail API responsibilities explicitly

`GET /api/issues/:number/coder-sessions` will return only session summary fields already available on the session row or cheaply derivable without log scans: identifiers, status, stage, model/provider metadata, start/end timestamps, duration-related fields, and any lightweight booleans already persisted with the session. It will no longer attach `workflowLogs`, transcript fragments, or other per-session log payloads.

`GET /api/issues/:number/coder-sessions/:sessionId` remains the only endpoint that loads session stream logs and workflow logs for transcript reconstruction, tool events, and debugging details. This keeps the expensive work behind an explicit user action and matches the current `SessionPage` loading model.

This is the primary latency fix because it removes both the N+1 query pattern and the log parsing work from the hot path needed to open an issue detail screen.

**Alternatives considered:**

- Keep one endpoint and add a query flag such as `?includeLogs=false`: rejected because it preserves an overloaded contract and makes regressions easy when callers omit the flag.
- Keep embedding logs but switch to batched queries only: rejected as insufficient because parsing and response size would still scale with session count.

### D2: Introduce separate summary and detail API types in the Web UI contract

The frontend type contract will distinguish a list item from a full session detail payload instead of reusing a single shape that implies logs are always present. `useCoderSessions` and list-oriented components will consume the summary type. `SessionPage` and any session drill-down path will consume the detail type returned by the dedicated session endpoint.

This makes the API split enforceable at compile time and prevents list components from accidentally reintroducing dependence on `workflowLogs` or transcript fields.

**Alternatives considered:**

- Keep one broad TypeScript type with many optional fields: rejected because it hides the contract change and makes accidental detail-path assumptions likely.
- Transform detail payloads into list payloads on the client: rejected because it does not address the server cost and still encourages over-fetching.

### D3: Remove expensive derived counts from the list path for now

The issue detail sessions list and compact session summary UI will stop depending on per-session `workflowLogs` to compute `filesChanged` and `toolCalls`. In this change, those values should either be omitted from the list surface entirely or replaced with a static placeholder only if required for layout stability. The preferred implementation is to remove the display and related types until a cheap persisted source exists.

This choice keeps the fix minimal and avoids introducing partial aggregation logic that still needs log scans. If these counts remain important, they should later be precomputed on `coder_session` or maintained incrementally during workflow execution.

**Alternatives considered:**

- Compute counts via batched `workflow_log` queries for every list request: rejected because it still adds avoidable work to the latency-sensitive path.
- Precompute and backfill counts immediately in this issue: rejected because it expands scope into schema/data migration work that is not required for the P0 fix.

### D4: Add batch log repository APIs for detail-adjacent use cases, but do not use them in the list endpoint

`SessionStreamLogRepo` and `WorkflowLogRepo` will gain `findBySessionIds(sessionIds: string[])` methods that return rows ordered by `session_id`, `created_at`, and `rowid`. These methods are not part of the list endpoint implementation; they provide an efficient building block for any remaining grouped log access, tests, or future background aggregation without reintroducing per-session repository calls.

Keeping these APIs separate from the list endpoint preserves the design boundary while still addressing the broader repository-level N+1 pattern identified in the proposal.

**Alternatives considered:**

- Skip batch methods entirely and only optimize the list endpoint: rejected because repository callers would remain one refactor away from repeating the same pattern elsewhere.
- Replace all single-session repository methods immediately: rejected because single-session reads remain appropriate for the detail endpoint and are simpler to keep.

### D5: Write new log timestamps in JavaScript as millisecond ISO strings and preserve fallback ordering for old rows

New inserts into `session_stream_log` and `workflow_log` will set `created_at` from application code using a millisecond-precision ISO 8601 string, rather than SQLite `datetime('now')`. Reads that depend on event order will sort by `created_at`, then `rowid` as a deterministic fallback for older second-precision rows and same-timestamp collisions.

This aligns new data with the ordering precision needed for transcript reconstruction and workflow timelines without requiring an immediate migration of historical rows.

**Alternatives considered:**

- Continue using SQLite timestamps and accept second precision: rejected because it leaves known ordering ambiguity unsolved.
- Migrate old rows to a new format during this change: rejected because it adds operational risk and is unnecessary for the performance fix.

### D6: Cache session lists briefly in the frontend query layer

`useCoderSessions` will configure a non-zero `staleTime` (target: 30 seconds) so short navigations away from and back to an issue reuse the recent list response instead of triggering another immediate fetch. The cache key should remain issue-specific so updates on one issue do not leak into another.

This is a complementary optimization rather than the main fix. It reduces repeated load on the server and improves perceived responsiveness during normal UI navigation.

**Alternatives considered:**

- No caching, relying only on backend speed improvements: rejected because page switches would still create unnecessary churn.
- Longer cache duration such as several minutes: rejected for now because session state can change frequently while an issue is active.

## Risks / Trade-offs

- [List UI loses `filesChanged` / `toolCalls` visibility temporarily] → Remove only the expensive counts, keep core metadata visible, and document precomputed counts as a follow-up improvement.
- [A caller may still assume list responses contain `workflowLogs`] → Introduce separate summary/detail types and update all known list consumers in the same change.
- [Mixed timestamp precision could still create ambiguous ordering for historical rows] → Preserve `ORDER BY created_at, rowid` so old rows remain stable even when second-precision timestamps collide.
- [Frontend cache may show slightly stale session status for a short period] → Keep `staleTime` short and allow manual or automatic refetch on focus/invalidations when the issue page remains active.
- [Batch repository APIs could be misused to expand list payloads later] → Keep the list endpoint implementation intentionally metadata-only and reflect that boundary in the design and tests.

## Migration Plan

1. Update the list endpoint contract and backend query path so `GET /coder-sessions` reads only from `coder_session` and no longer serializes `workflowLogs`.
2. Add or adjust frontend types, API client code, and session list components to consume the summary payload and stop reading removed fields.
3. Keep the dedicated session detail endpoint unchanged in behavior, but verify it still loads full logs and transcripts correctly.
4. Add `findBySessionIds` methods to both log repositories and cover ordering behavior with tests.
5. Change new log inserts to write millisecond-precision ISO timestamps from application code.
6. Configure `useCoderSessions` caching with the agreed `staleTime` and verify navigation no longer triggers unnecessary refetches.
7. Validate with an issue containing 50+ sessions that the list endpoint meets the sub-second target and that the dedicated session page still renders correctly.

Rollback is straightforward because the change is additive around repository helpers and contract-focused around the list endpoint. If needed, revert the frontend summary contract and backend list serialization together. No data migration is required; mixed timestamp formats remain readable by the fallback ordering strategy.

## Open Questions

- Should the list UI remove `filesChanged` and `toolCalls` completely, or preserve their layout slots with an explicit unavailable state until precomputed counts exist?
- Is the session summary duration fully derivable from existing persisted timestamps, or does the current UI rely on any log-derived timing fields that need a lightweight replacement?
