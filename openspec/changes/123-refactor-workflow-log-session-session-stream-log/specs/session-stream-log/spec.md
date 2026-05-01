## ADDED Requirements

### Requirement: session_stream_log table stores session real-time stream data
Server SHALL maintain a `session_stream_log` table that stores agent interaction data scoped to individual ACP sessions. Each record SHALL contain `id` (TEXT PK), `session_id` (TEXT NOT NULL, the ACP session ID), `issue_id` (TEXT NOT NULL, FK to `issues(id)` with CASCADE delete), `event_type` (TEXT NOT NULL), `data` (TEXT NOT NULL, default `'{}'`), and `created_at` (TEXT NOT NULL, default `datetime('now')`).

The following event types SHALL be written to `session_stream_log`:
- `agent_thought_chunk`
- `agent_message_chunk`
- `tool_call`
- `tool_call_update`
- `user_message_chunk`

#### Scenario: agent_thought_chunk written to session_stream_log
- **WHEN** an ACP sessionUpdate with type `agent_thought_chunk` is received
- **THEN** a record is inserted into `session_stream_log` with the session_id, issue_id, event_type `'agent_thought_chunk'`, and data containing the thought text

#### Scenario: agent_message_chunk written to session_stream_log
- **WHEN** an ACP sessionUpdate with type `agent_message_chunk` is received
- **THEN** a record is inserted into `session_stream_log` with the session_id, issue_id, event_type `'agent_message_chunk'`, and data containing the message text

#### Scenario: tool_call written to session_stream_log
- **WHEN** an ACP sessionUpdate with type `tool_call` is received
- **THEN** a record is inserted into `session_stream_log` with the session_id, issue_id, event_type `'tool_call'`, and data containing tool name, input, status

#### Scenario: tool_call_update written to session_stream_log
- **WHEN** an ACP sessionUpdate with type `tool_call_update` is received
- **THEN** a record is inserted into `session_stream_log` with the session_id, issue_id, event_type `'tool_call_update'`, and data containing updated status and output

### Requirement: session_stream_log indexed by session_id and issue_id
Server SHALL create two indexes on `session_stream_log`:
- `idx_session_stream_log_session` on `(session_id, created_at)` for session-scoped chronological queries
- `idx_session_stream_log_issue` on `(issue_id, created_at)` for issue-scoped chronological queries

#### Scenario: Query logs by session_id efficiently
- **WHEN** querying all stream logs for a specific session_id
- **THEN** the query uses `idx_session_stream_log_session` and returns records ordered by `created_at ASC`

#### Scenario: Query logs by issue_id efficiently
- **WHEN** querying all stream logs for a specific issue_id
- **THEN** the query uses `idx_session_stream_log_issue` and returns records ordered by `created_at ASC`

### Requirement: SessionStreamLogRepo provides CRUD operations
Server SHALL provide a `SessionStreamLogRepo` class registered on `StateManager` with methods:
- `insert(issueId, sessionId, eventType, data)` — insert a new stream log entry
- `findBySessionId(sessionId)` — return all entries for a session ordered by `created_at ASC`
- `findByIssueId(issueId)` — return all entries for an issue ordered by `created_at ASC`

#### Scenario: Insert and read back a stream log entry
- **WHEN** `sessionStreamLogRepo.insert(issueId, sessionId, 'agent_message_chunk', { text: 'hello' })` is called
- **THEN** the entry is persisted and `findBySessionId(sessionId)` returns the entry with correct event_type and parsed data

#### Scenario: findByIssueId returns all session stream logs for an issue
- **WHEN** multiple sessions have written stream logs for the same issue_id
- **THEN** `findByIssueId(issueId)` returns all entries across all sessions, ordered by `created_at ASC`

### Requirement: acp-session.ts writes session stream events to session_stream_log
In both `runAcpSession` and `createAcpConnection` modes, the `sessionUpdate` handler in `acp-session.ts` SHALL write `agent_thought_chunk`, `agent_message_chunk`, `tool_call`, `tool_call_update`, and `user_message_chunk` events to `session_stream_log` (via `SessionStreamLogRepo`) instead of `workflow_log`. All other event types SHALL continue to be written to `workflow_log` as before.

#### Scenario: Single-shot session streams to session_stream_log
- **WHEN** `runAcpSession` receives an `agent_message_chunk` sessionUpdate
- **THEN** the entry is inserted into `session_stream_log` (not `workflow_log`)
- **AND** the entry's `session_id` is set to the ACP session ID

#### Scenario: Multi-round session streams to session_stream_log
- **WHEN** `createAcpConnection` receives a `tool_call` sessionUpdate
- **THEN** the entry is inserted into `session_stream_log` (not `workflow_log`)
- **AND** the entry's `session_id` is set to the ACP session ID

#### Scenario: Non-stream events still go to workflow_log
- **WHEN** a lifecycle event such as `acp_session_start` or `acp_session_completed` occurs
- **THEN** the entry is inserted into `workflow_log` as before (unchanged)

### Requirement: DB migration creates session_stream_log table
A new schema migration SHALL create the `session_stream_log` table and its indexes. The migration SHALL be idempotent (use `IF NOT EXISTS`).

#### Scenario: Fresh database gets session_stream_log table
- **WHEN** the server starts with a fresh database
- **THEN** the migration runs and creates `session_stream_log` with both indexes

#### Scenario: Existing database upgraded
- **WHEN** the server starts with an existing database that lacks `session_stream_log`
- **THEN** the migration adds the table and indexes without affecting existing `workflow_log` data

### Requirement: No historical data migration
Historical session stream data already stored in `workflow_log` SHALL NOT be migrated to `session_stream_log`. Old data remains in `workflow_log` for backward compatibility.

#### Scenario: Old session chunk data remains in workflow_log
- **WHEN** an issue has historical `agent_message_chunk` entries in `workflow_log` from before this change
- **THEN** those entries remain in `workflow_log` untouched
- **AND** new session stream data is written to `session_stream_log`
