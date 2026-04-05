## MODIFIED Requirements

### Requirement: HTTP API routes
The API SHALL include all existing endpoints plus new endpoints for Web UI support. New endpoints:

- `GET /api/events` — SSE real-time event stream (optional `?projectId=xxx` for project filtering)
- `POST /api/issues/:number/approve` — Approve an issue at a gate stage and start new agent session
- `GET /api/issues/:number/diff` — Get git diff summary for an issue's worktree branch
- `GET /api/agent/status` — Get current Agent status (running/idle, which issue)

#### Scenario: SSE connection
- **WHEN** client requests `GET /api/events` with `Accept: text/event-stream`
- **THEN** server responds with SSE stream

#### Scenario: SSE connection with project filter
- **WHEN** client requests `GET /api/events?projectId=abc123`
- **THEN** server responds with SSE stream filtered to events for that project only

#### Scenario: Approve issue at gate
- **WHEN** `POST /api/issues/:number/approve` is called for an issue at a gate stage
- **THEN** server extracts context from latest agent comment, starts new agent session, and issue advances

#### Scenario: Approve non-gate issue
- **WHEN** `POST /api/issues/:number/approve` is called for an issue not at a gate
- **THEN** server returns 400 error

#### Scenario: Approve while agent running
- **WHEN** `POST /api/issues/:number/approve` is called while another agent is running
- **THEN** server returns 400 error indicating agent is busy

#### Scenario: Get agent status
- **WHEN** `GET /api/agent/status` is called while Agent is running
- **THEN** server returns `{ running: true, issueId: "...", issueNumber: 1 }`

#### Scenario: Get agent status idle
- **WHEN** `GET /api/agent/status` is called while no Agent is running
- **THEN** server returns `{ running: false }`

#### Scenario: Get diff summary
- **WHEN** `GET /api/issues/:number/diff` is called for an issue in build stage with a worktree
- **THEN** server returns list of changed files with add/remove line counts using WorktreeManager
