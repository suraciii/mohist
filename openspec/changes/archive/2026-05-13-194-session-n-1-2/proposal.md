## Why

Opening an issue with many past coder runs currently blocks on an overloaded sessions list API that does per-session log queries and log parsing, pushing a core issue-detail screen past two minutes to become usable. This needs to be fixed now because the sessions list is the user's only quick view into agent execution history, and its current latency makes high-activity issues effectively unreadable.

## What Changes

- Redefine `GET /api/issues/:number/coder-sessions` as a lightweight list endpoint that returns only session metadata needed for the issue detail sidebar, without embedding transcript or workflow log payloads.
- Preserve full transcript and log loading on `GET /api/issues/:number/coder-sessions/:sessionId`, so detailed session inspection continues to happen only after a user opens a specific session.
- Remove list-surface dependence on per-session `workflowLogs` payloads and update the Web UI session list/detail summary to use lightweight fields or omit expensive derived counts until a cheaper source exists.
- Add frontend caching for coder session lists so navigating away from and back to an issue does not immediately trigger another full list fetch.
- Align session stream and workflow log persistence on millisecond-precision ISO timestamps while keeping deterministic fallback ordering for older second-precision rows.
- Introduce batch-oriented repository/query support where session-associated logs still need to be loaded in groups, avoiding repeated per-session round trips.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `http-api`
- `agent-session-ui`
- `coder-session-tracking`

## Impact

- **Backend API**: `packages/cli/src/api/issues.ts` must split session list and session detail responsibilities cleanly so the list endpoint no longer loads or serializes per-session logs.
- **Repositories and queries**: `packages/cli/src/db/coder-session-repo.ts`, `packages/cli/src/db/session-stream-log-repo.ts`, and `packages/cli/src/db/workflow-log-repo.ts` are affected by list-query performance, batch loading support, and timestamp ordering behavior.
- **Session persistence**: log inserts in `packages/cli/src/db/session-stream-log-repo.ts` and `packages/cli/src/db/workflow-log-repo.ts` need consistent millisecond-resolution timestamps without regressing ordering for existing data.
- **Web UI data contract**: `packages/cli/web/src/lib/types.ts`, `packages/cli/web/src/lib/api.ts`, `packages/cli/web/src/hooks/useCoderSessions.ts`, `packages/cli/web/src/components/SessionList.tsx`, `packages/cli/web/src/components/SessionDetail.tsx`, and `packages/cli/web/src/components/SessionPage.tsx` must stop assuming list responses contain full `workflowLogs` payloads and should cache list results across short page switches.
- **Session detail behavior**: `GET /api/issues/:number/coder-sessions/:sessionId` and the dedicated session page remain the source of truth for transcript reconstruction, full logs, and deep inspection.
- **Validation**: performance and regression coverage should verify that issues with 50+ sessions load the list within the target budget, that the dedicated session page still renders correctly, and that old second-precision rows continue to sort deterministically.
