## ADDED Requirements

### Requirement: Context exhaustion is classified as a distinct failure category

The system SHALL define `context_exhaustion` as a distinct failure category in the failure classification enum. This category SHALL be used when a task or session failure is determined to be caused by context window exhaustion rather than a logic error, tool failure, or infrastructure issue.

#### Scenario: Failure classified as context_exhaustion
- **WHEN** a session fails near-concurrently with context window usage exceeding 95%
- **AND** the failure was not due to a tool error or explicit exception
- **THEN** the failure SHALL be classified as `context_exhaustion`
- **AND** the failure evidence SHALL include the context usage percentage at time of failure

#### Scenario: Normal tool failure is not misclassified
- **WHEN** a session fails due to a tool execution error (e.g., bash command returns non-zero)
- **AND** context usage is at 70%
- **THEN** the failure SHALL NOT be classified as `context_exhaustion`
- **AND** the failure SHALL use the appropriate tool or task failure category

### Requirement: Context exhaustion detection uses context window usage data

The system SHALL detect potential context exhaustion by examining context window usage data from session notifications. A failure SHALL be flagged as context exhaustion when: (1) the session's `contextWindowUsed` / `contextWindowSize` ratio exceeds 90% at or near the time of failure, or (2) the session completes in an anomalously short time without producing expected output, with context usage above 85%.

#### Scenario: Exhaustion detected from high usage ratio
- **WHEN** a session fails and context usage was 96% at the time of failure
- **THEN** the system SHALL classify the failure as `context_exhaustion`
- **AND** the error SHALL include the usage percentage

#### Scenario: Exhaustion detected from rapid completion pattern
- **WHEN** a session completes in under 10 seconds without producing expected artifacts
- **AND** context usage was 88% at the start of the session
- **THEN** the system SHALL flag the result as suspected context exhaustion
- **AND** the error SHALL indicate that context may have been exhausted

#### Scenario: Normal rapid completion is not flagged
- **WHEN** a session completes in under 10 seconds with all expected artifacts produced
- **AND** context usage was 30%
- **THEN** the system SHALL NOT flag it as context exhaustion
- **AND** the result SHALL be treated as a normal completion

### Requirement: Error messages indicate context exhaustion and suggest recovery

When a failure is classified as `context_exhaustion`, the error message SHALL clearly indicate that context window exhaustion occurred. The message SHALL suggest recovery actions: compact the session or reset it. The message SHALL NOT mislead users into thinking the failure was caused by missing artifacts or tool errors.

#### Scenario: Error message for context exhaustion
- **WHEN** a task fails with `context_exhaustion` classification
- **THEN** the error message SHALL include text like "Context window exhausted (96% used)"
- **AND** the message SHALL suggest "Compact the session to free context, or Reset to start fresh"

#### Scenario: Error message does not mask root cause
- **WHEN** a task fails due to context exhaustion but a secondary effect was a missing artifact
- **THEN** the primary error SHALL indicate context exhaustion
- **AND** missing artifact information SHALL be presented as secondary detail, not the root cause

### Requirement: Workflow tasks are marked with clear failure reason on exhaustion

When a workflow task fails due to context exhaustion, the task's failure reason SHALL be recorded as `context_exhaustion` in the WorkflowRun. The task detail SHALL show the context usage percentage at failure time. The workflow stage SHALL not automatically retry on context exhaustion.

#### Scenario: Task records context exhaustion failure
- **WHEN** a Build task fails with detected context exhaustion
- **THEN** the TaskRun SHALL record failureReason as "Context window exhausted (94%)"
- **AND** the failure category SHALL be `context_exhaustion`

#### Scenario: Context exhaustion does not trigger auto-retry
- **WHEN** a task fails with `context_exhaustion`
- **THEN** the workflow SHALL NOT automatically retry the task
- **AND** the stage SHALL present the failure for user intervention (compact/reset then manual retry)

### Requirement: Context exhaustion data is available to error display surfaces

The system SHALL make context exhaustion detection data available to all error display surfaces (Web UI, CLI output, stage-state API). The data SHALL include the failure category, context usage at failure, and suggested recovery actions.

#### Scenario: Web UI shows context exhaustion error with recovery actions
- **WHEN** a user views a failed task in the Web UI
- **AND** the failure is classified as `context_exhaustion`
- **THEN** the error display SHALL show "Context window exhausted"
- **AND** the display SHALL show the usage percentage at failure
- **AND** the display SHALL include Compact and Reset as suggested actions

#### Scenario: CLI shows context exhaustion in task output
- **WHEN** a CLI command displays task status
- **AND** the task failed with `context_exhaustion`
- **THEN** the output SHALL indicate context exhaustion as the failure reason
- **AND** the output SHALL suggest running compact or reset commands (if available)
