## Why

The backend config API (`GET/PUT /api/config`) already persists `agent.timeout`, `agent.maxConcurrent`, and `poll.interval` in SQLite, but the frontend General Settings tab is a static placeholder ("coming soon"). Users have no way to view or modify these settings from the web UI, and two of the three values (`agent.timeout`, `poll.interval`) are not actually consumed at runtime despite being stored.

## What Changes

- Wire the frontend General Settings tab to the existing `GET/PUT /api/config` endpoints
- Add frontend API client functions, TypeScript types, and React hooks for config CRUD
- Build form UI with validated inputs for agent timeout, max concurrent agents, and poll interval (with sensible units: minutes/count/seconds)
- Enhance `PUT /api/config/:key` response to return the full config object for frontend convenience

## Future Work

- Connect `agent.timeout` to ACP session creation (currently hardcoded 30 min default)
- Connect `poll.interval` to the polling loop (currently not consumed anywhere)
- Make `agent.maxConcurrent` dynamically reactive instead of read-once at server startup

## Capabilities

### New Capabilities

- `general-settings-ui`: Frontend General Settings tab with form inputs, validation, and live save for agent timeout / max concurrent / poll interval

### Modified Capabilities

- `web-ui`: Add config API client, types, and React hooks to the frontend API layer
- `http-api`: Ensure `PUT /api/config/:key` returns the updated value for optimistic UI updates

## Impact

- **Frontend**: `web/src/components/SettingsPage.tsx` (General tab), `web/src/lib/api.ts` (new config methods), `web/src/lib/types.ts` (Config type), `web/src/hooks/` (new useConfig hook)
- **Backend API**: `src/api/config.ts` (PUT response shape enhancement)
- **Tests**: `web/tests/SettingsPage.test.tsx` (update from placeholder test to real form tests)
- **No breaking changes**: PUT response gains additional fields, existing consumers unaffected
