## ADDED Requirements

### Requirement: A returned result survives a transient journal write failure in the live process

When a runner has an exact `WorkItemResult` returned for a dispatch whose
durable `started` fence exists, a temporary failure to persist the completion
MUST retain that exact result in process memory.  The runner MUST mark work
admission unavailable and MUST NOT report the result until a durable completion
record exists.

#### Scenario: Completion persistence is temporarily unavailable

- **WHEN** execution returns a result and writing the completed journal entry fails
- **THEN** the runner retains that exact work and result in memory, leaves the
  work held against re-execution, and emits no owner result report
- **AND THEN** a later successful control-plane poll retries the retained
  persistence and reports it only after the completed journal record is durable

### Requirement: A crash with only a started entry remains unresolved

The runner MUST treat a persisted `started` journal entry without a durable
completed result as an unresolved physical-execution fence.

#### Scenario: The process exits before pending persistence recovers

- **WHEN** the process exits while an in-memory completion has not been
  durably written
- **THEN** a subsequent runner instance refuses to replay that dispatch
- **AND THEN** it MUST NOT construct a result from runtime binding, runtime
  event, session idleness, completed turn state, or logs
