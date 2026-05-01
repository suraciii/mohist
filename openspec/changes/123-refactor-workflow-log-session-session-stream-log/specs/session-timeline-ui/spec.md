## MODIFIED Requirements

### Requirement: SessionTimeline loads history from workflow_log API
When the page loads for a non-draft issue, SessionTimeline SHALL fetch historical data from `GET /api/issues/:number/coder-sessions` (which returns sessions with embedded `workflowLogs` sourced from `session_stream_log`) and reconstruct the round-based conversation structure by splitting on `user_message_chunk` events. The separate `GET /api/issues/:number/logs` call in `useIssueTimeline` SHALL be removed since `buildTimeline()` does not use its result.

#### Scenario: Page loads after plan stage completes
- **WHEN** the user navigates to an issue detail page after the plan stage has completed
- **THEN** SessionTimeline reconstructs rounds from `session.workflowLogs` (sourced from `session_stream_log` via the coder-sessions API) by grouping events between consecutive `user_message_chunk` entries

#### Scenario: No workflow_log entries exist
- **WHEN** the user views a draft issue with no agent activity
- **THEN** SessionTimeline shows "No agent activity yet" placeholder

#### Scenario: useIssueTimeline no longer fetches unused workflow logs
- **WHEN** the issue detail page loads
- **THEN** `useIssueTimeline` does NOT call `api.getWorkflowLogs()`
- **AND** timeline data comes exclusively from `useCoderSessions` (which fetches sessions with embedded logs from `session_stream_log`)
