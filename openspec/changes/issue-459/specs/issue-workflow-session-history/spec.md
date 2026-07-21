### Requirement: Named workflow sessions remain readable from terminal issues
Issue-scoped workflow session metadata and transcript reads SHALL resolve a persisted session with the requested name after the issue no longer has an active workflow run. The resolved session MUST belong to the requested project and issue.

#### Scenario: Completed issue exposes a historical session
- **WHEN** a completed issue has no active workflow run and has a persisted workflow session with the requested name
- **THEN** the issue-scoped session metadata and transcript reads SHALL return that historical session's content

#### Scenario: Cancelled issue exposes a historical session
- **WHEN** a cancelled issue has no active workflow run and has a persisted workflow session with the requested name
- **THEN** the issue-scoped session metadata and transcript reads SHALL return that historical session's content

### Requirement: Active workflow session resolution retains precedence
When an issue has an active workflow run, issue-scoped named session reads SHALL resolve sessions only within that active run and MUST NOT fall back to sessions from earlier workflow runs.

#### Scenario: Active run contains the requested session
- **WHEN** an in-progress issue's active workflow run contains a session with the requested name
- **THEN** the issue-scoped session read SHALL return the session from the active workflow run

#### Scenario: Requested session exists only in an earlier run
- **WHEN** an in-progress issue has an active workflow run without the requested session and an earlier workflow run has a session with that name
- **THEN** the issue-scoped session read SHALL return "not found"

### Requirement: Missing issue session remains not found
An issue-scoped named session read SHALL return "not found" when no persisted workflow session with that name belongs to the requested project and issue.

#### Scenario: Same name does not exist for the issue
- **WHEN** the requested issue has no persisted workflow session with the requested name
- **THEN** the issue-scoped session metadata and transcript reads SHALL return "not found"

### Requirement: Runtime session transcript filtering is preserved
When a runtime session ID is supplied for an issue-scoped transcript read, the transcript SHALL contain only content associated with that runtime session ID within the resolved logical workflow session.

#### Scenario: Historical transcript is filtered by runtime session
- **WHEN** a historical logical workflow session contains content from multiple runtime sessions and the transcript is requested with one runtime session ID
- **THEN** the returned transcript SHALL contain only content associated with the requested runtime session ID
