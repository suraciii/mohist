## ADDED Requirements

### Requirement: Activity navigation entry in Header
The WebUI Header SHALL include an "Activity" navigation entry with a pulse/radar icon, linking to `/activity`.

#### Scenario: Activity link visible in Header
- **WHEN** user views any page in the WebUI
- **THEN** the Header displays an "Activity" link with a pulse or radar icon
- **AND** clicking it navigates to `/activity`

#### Scenario: Activity link highlighted on active page
- **WHEN** user is currently on `/activity`
- **THEN** the Activity link in the Header is visually highlighted as the active route

### Requirement: Activity navigation entry in MobileBottomNav
The WebUI MobileBottomNav SHALL include an "Activity" entry with a pulse/radar icon, linking to `/activity`.

#### Scenario: Activity tab visible in mobile nav
- **WHEN** user views the WebUI on a mobile viewport
- **THEN** the MobileBottomNav displays an "Activity" tab with a pulse or radar icon
- **AND** tapping it navigates to `/activity`

#### Scenario: Activity tab highlighted on active page
- **WHEN** user is on `/activity` on a mobile viewport
- **THEN** the Activity tab in MobileBottomNav is visually highlighted as active

### Requirement: Frontend API client provides getAgentSessions method
`api.ts` SHALL add `getAgentSessions` method corresponding to `GET /api/agent/sessions`.

#### Scenario: getAgentSessions call without params
- **WHEN** calling `api.getAgentSessions()`
- **THEN** sends `GET /api/agent/sessions` request
- **AND** returns array of session objects

#### Scenario: getAgentSessions call with params
- **WHEN** calling `api.getAgentSessions({ status: 'running', limit: 10 })`
- **THEN** sends `GET /api/agent/sessions?status=running&limit=10` request
- **AND** returns filtered array of session objects

### Requirement: Frontend hooks provide useAgentSessions
`useQueries.ts` SHALL add `useAgentSessions` hook that wraps `getAgentSessions` with React Query, supporting automatic refetch on SSE-driven invalidation.

#### Scenario: useAgentSessions fetches on mount
- **WHEN** a component calls `useAgentSessions()`
- **THEN** it fetches `GET /api/agent/sessions` on mount
- **AND** returns `{ data, isLoading, error }`

#### Scenario: useAgentSessions with status filter
- **WHEN** a component calls `useAgentSessions({ status: 'running' })`
- **THEN** only running sessions are fetched and returned
