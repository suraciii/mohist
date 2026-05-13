## MODIFIED Requirements

### Requirement: Issue detail session surfaces consume summary session payloads

The issue detail session list and compact session summary UI SHALL consume a summary-specific session contract instead of depending on full `workflowLogs` or transcript payloads from the list endpoint.

#### Scenario: Session list renders from summary metadata only

- **WHEN** the issue detail page renders the sessions list
- **THEN** list-oriented components use the summary payload shape returned by `GET /api/issues/:number/coder-sessions`
- **AND** they do not read `workflowLogs` from the list response

#### Scenario: Expensive derived counts are removed from the list surface

- **WHEN** the list response no longer includes workflow logs
- **THEN** `filesChanged` and `toolCalls` are removed or replaced by a lightweight non-log-backed presentation
- **AND** the session list and summary detail render without type errors

#### Scenario: Session page still loads full detail on demand

- **WHEN** a user opens a specific session page or drill-down view
- **THEN** that view still loads full transcript and log detail through the dedicated single-session endpoint

### Requirement: Issue-scoped session list queries reuse recent data briefly

The frontend query layer SHALL cache issue-specific coder session list results for a short stale window so brief navigation away from and back to the same issue does not immediately refetch the list.

#### Scenario: Recent list data is reused within the stale window

- **WHEN** a user leaves and returns to the same issue within about 30 seconds
- **THEN** the session list query reuses cached data for that issue
- **AND** the page does not immediately trigger a fresh list request on remount

#### Scenario: Cache keys remain issue-specific

- **WHEN** coder session lists are cached in the frontend
- **THEN** the cache key remains scoped to the issue identifier
- **AND** cached data from one issue is not shown for another issue
