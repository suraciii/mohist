## Context

The backend config stack is fully implemented: `ConfigRepo` → `ConfigService` → `GET/PUT /api/config`. The frontend `SettingsPage` has a General tab that is a static placeholder ("coming soon"). The frontend API layer (`api.ts`, `types.ts`, `useQueries.ts`) has no config-related methods, types, or hooks yet.

The frontend uses React Query (TanStack Query) consistently across all data fetching. The `useSaveProvider` hook in `useQueries.ts:187` demonstrates the established optimistic update pattern (`onMutate` → `onError` rollback → `onSettled` invalidate).

## Goals / Non-Goals

**Goals:**
- Wire General Settings tab to the existing backend config API
- Follow established frontend patterns (React Query, `api.ts` request helper, optimistic updates)
- Provide unit-converting form inputs (ms ↔ min/sec) with validation
- Enhance `PUT /api/config/:key` response to return full config for frontend convenience

**Non-Goals:**
- Runtime consumption of `agent.timeout` and `poll.interval` in backend services (separate change)
- Dynamic reactivity of `agent.maxConcurrent` without server restart (separate change)
- Adding new config keys beyond the three existing ones
- WebSocket/SSE push for config changes

## Decisions

### D1: PUT response returns full config object

Change `PUT /api/config/:key` response from `{ key, value }` to the same shape as `GET /api/config` (`{ agentTimeout, maxConcurrentAgents, pollInterval }`). This lets the frontend use a single cache shape and avoids a follow-up GET after each mutation.

**Alternatives considered:** Keep `{ key, value }` response and refetch after mutation — adds a round-trip, less efficient for the multi-field form.

### D2: Extract GeneralSettingsSection as a standalone component

Create `GeneralSettingsSection` component (in `SettingsPage.tsx` or a sibling file) that receives config data and update functions from hooks. The SettingsPage General tab renders `<GeneralSettingsSection />`. This mirrors the existing `CustomProvidersSection` pattern and keeps the already-large `SettingsPage.tsx` manageable.

**Alternatives considered:** Inline all General tab logic into SettingsPage — file is already 430 lines, would grow too large.

### D3: Use React Query optimistic update pattern

Follow the `useSaveProvider` pattern: `useUpdateConfig` mutation with `onMutate` to optimistically update the `['config']` query cache, `onError` to rollback, `onSettled` to invalidate. Single-field save triggers one PUT, resets triggers three sequential PUTs with Promise.all.

**Alternatives considered:** Simple mutation + refetch without optimistic update — simpler but causes visible flicker on each save.

### D4: Per-field save with inline Save button

Each config field has its own inline Save button (or auto-save on blur). This matches the per-key PUT API design and avoids batching complexity. The "Reset to Defaults" button at the bottom triggers all three PUTs.

**Alternatives considered:** Single "Save All" button for the whole form — requires dirty-tracking across fields, more complex state management.

### D5: Frontend stores display units, converts to ms on API call

Local component state holds values in user-friendly units (minutes for timeout, seconds for poll interval, count for maxConcurrent). Convert to milliseconds on save. Show the unit label next to each input.

**Alternatives considered:** Store raw ms values in state and convert only for display — confusing for developers, easy to mix up which direction the conversion goes.

## Risks / Trade-offs

- [PUT response shape change is a minor breaking change for any CLI consumer] → The CLI does not currently use `PUT /api/config/:key` for automated workflows, so impact is negligible. The response still has `success: true` and `data` field.
- [Reset to Defaults fires 3 sequential PUTs] → Acceptable for an infrequent user action. If it fails partway, the cache rollback on each mutation handles partial states. Could batch later with a `POST /api/config/reset` endpoint if needed.

## Migration Plan

1. Deploy backend `PUT` response change first (backward-compatible: adds more fields to `data`)
2. Deploy frontend changes (api methods, types, hooks, GeneralSettingsSection)
3. Update `SettingsPage.test.tsx` from placeholder assertion to real form tests

No rollback complexity — if frontend has issues, the General tab simply shows loading/error states.

## Open Questions

None.
