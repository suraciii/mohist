## MODIFIED Requirements

### Requirement: resume-retry-rerun-reopen-contract

The issue recovery API SHALL expose distinct behaviors for `resume`, `retry`, `rerun`, and `reopen`. The API SHALL NOT provide a working `restart` recovery path.

#### Scenario: Reopen endpoint is closed-only

- **WHEN** a client requests `POST /api/issues/:number/reopen`
- **AND** the issue status is `closed`
- **THEN** the API reopens the issue to `active`
- **AND** the API does not auto-enqueue `resume-pipeline`

#### Scenario: Reopen endpoint rejects blocked recovery

- **WHEN** a client requests `POST /api/issues/:number/reopen`
- **AND** the issue status is `blocked`, `paused`, or `interrupted`
- **THEN** the API returns an error indicating reopen is only for closed issues

#### Scenario: Resume endpoint recovers paused work

- **WHEN** a client requests `POST /api/issues/:number/resume`
- **AND** the issue status is `paused`
- **THEN** the API restores the issue to `active`
- **AND** the API preserves the current stage and checkpoints
- **AND** the API enqueues resume-pipeline when runtime conditions allow recovery

#### Scenario: Resume endpoint recovers interrupted work

- **WHEN** a client requests `POST /api/issues/:number/resume`
- **AND** the issue status is `interrupted`
- **THEN** the API restores the issue to `active`
- **AND** the API preserves the current stage and checkpoints

#### Scenario: Retry endpoint no longer simulates restart

- **WHEN** a client requests `POST /api/issues/:number/retry`
- **AND** retry recovery has no usable checkpoint or retryable failure evidence
- **THEN** the API rejects the request
- **AND** the API does not reset the issue to backlog or draft as a fallback
- **AND** the error directs the client to rerun or rewind instead

#### Scenario: Restart endpoint is deprecated

- **WHEN** a client requests `POST /api/issues/:number/restart`
- **THEN** the API returns a deprecation error
- **AND** the response instructs the client to use retry, rerun, or rewind instead
- **AND** the API does not mutate issue stage, checkpoint, or status

### Requirement: start-handler-guidance-uses-current-verb-model

`POST /api/issues/:number/start` SHALL use the current recovery verb model in its error guidance.

#### Scenario: Start blocked issue

- **WHEN** a client requests `POST /api/issues/:number/start`
- **AND** the issue is in a failed or needs-action state
- **THEN** the API returns an error
- **AND** the message references retry, rerun, or rewind
- **AND** the message does not recommend restart

#### Scenario: Start closed issue

- **WHEN** a client requests `POST /api/issues/:number/start`
- **AND** the issue status is `closed`
- **THEN** the API returns an error
- **AND** the message recommends reopen
