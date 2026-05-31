## ADDED Requirements

### Requirement: Active workflow leases survive grain activation
The workflow engine SHALL preserve a persisted active `WorkflowLease` when a workflow grain activates. A grain activation MUST NOT silently clear or ignore an active lease in a way that makes the same workflow work item immediately dispatchable to another runner.

#### Scenario: Activation restores active lease ownership
- **WHEN** a workflow grain activates for a workflow run with a persisted active `WorkflowLease`
- **THEN** the grain SHALL restore that lease as active ownership state
- **AND** the restored lease SHALL identify the leased workflow work item and owning runner

#### Scenario: Activation does not make leased work dispatchable
- **WHEN** a workflow grain has restored an active persisted lease
- **THEN** `GetWorkAsync` SHALL refuse to dispatch the leased work item to a different runner
- **AND** the workflow SHALL remain owned by the lease runner until the lease is released, abandoned, expired, or deterministically handed off

### Requirement: Workflow dispatch is blocked by valid active leases
The workflow engine SHALL enforce one active owner per workflow work item. `GetWorkAsync` MUST NOT create a new lease or start a new work attempt for a workflow that already has a valid active lease.

#### Scenario: Valid lease prevents duplicate dispatch
- **WHEN** a runner asks for work from a backlog containing a workflow whose current work item has a valid active lease owned by another runner
- **THEN** `GetWorkAsync` SHALL NOT dispatch that workflow work item to the requesting runner
- **AND** it SHALL NOT persist a replacement lease for the requesting runner
- **AND** it SHALL NOT emit a second start event for the same work item

#### Scenario: Same owner may observe its lease without duplicate start
- **WHEN** the existing lease owner polls for work while its lease is still active
- **THEN** the workflow engine SHALL NOT create a second active work assignment for the same work item
- **AND** it SHALL NOT emit a duplicate work-start event unless a new explicit attempt has been created

### Requirement: Stale workflow leases are reconciled before redispatch
The workflow engine SHALL reconcile a persisted active lease that cannot be proven live through an explicit recovery path before making the leased work item available again. Stale ownership MUST become visible as abandonment, expiration, interruption, failure, or deterministic handoff evidence before another runner can receive the work.

#### Scenario: Offline owner is abandoned before redispatch
- **WHEN** a persisted active lease is owned by a runner that is offline or timed out
- **THEN** the workflow engine SHALL run the same abandonment or timeout recovery path used for runner unregister or heartbeat timeout
- **AND** the prior lease ownership SHALL be released or marked stale before another runner can receive the work item

#### Scenario: Redispatch after stale lease is visible as recovery
- **WHEN** a stale lease has been explicitly abandoned, expired, interrupted, failed, or handed off
- **THEN** a later dispatch of the same workflow work item SHALL be visible as a retry, resumed attempt, or handoff
- **AND** durable workflow state SHALL retain evidence explaining why ownership changed

#### Scenario: Unreconciled lease remains non-dispatchable
- **WHEN** the system cannot determine whether a persisted active lease is live or stale
- **THEN** the workflow work item SHALL remain non-dispatchable to another runner
- **AND** the workflow SHALL expose a blocked recovery state instead of silently assigning duplicate ownership

### Requirement: Task artifact validation reports artifact marker failures
The workflow engine SHALL validate task artifact expectations as file existence and optional neutral artifact marker or content requirements. Missing neutral artifact markers MUST produce artifact-specific diagnostics that identify the artifact file and marker without describing the failure as a check verdict failure.

#### Scenario: Required task artifact file is missing
- **WHEN** a task completes without producing a required artifact file
- **THEN** task artifact validation SHALL fail the task
- **AND** the diagnostic SHALL identify the missing artifact file requirement

#### Scenario: Neutral task artifact marker is missing
- **WHEN** a task produces a required artifact file
- **AND** a neutral artifact marker declared for that file is missing
- **THEN** task artifact validation SHALL fail the task
- **AND** the diagnostic SHALL identify the missing artifact marker as an artifact requirement

#### Scenario: Task artifact validation ignores pass fail verdict semantics
- **WHEN** a task produces an artifact containing a parseable PASS or FAIL verdict marker
- **THEN** task artifact validation SHALL treat the file as present for artifact completion purposes
- **AND** it SHALL NOT decide task success from whether the verdict is PASS or FAIL

### Requirement: Check verdict validation reports verdict marker failures
The workflow engine SHALL validate required PASS/FAIL verdict markers only while executing checks. Missing, mismatched, or failing verdict markers MUST produce check-verdict diagnostics that identify the check and expected verdict marker rather than reporting a task artifact marker failure.

#### Scenario: Required pass verdict marker is missing
- **WHEN** a check requires a pass verdict marker from an existing artifact
- **AND** the artifact does not contain the required pass marker
- **THEN** check verdict validation SHALL fail the check
- **AND** the diagnostic SHALL identify the missing or mismatched verdict marker as check evidence

#### Scenario: Fail verdict does not become missing artifact marker
- **WHEN** `review.md` exists and contains `<promise>FAIL</promise>`
- **AND** `review-passed` requires `<promise>PASS</promise>`
- **THEN** `review-passed` SHALL fail as a check verdict failure
- **AND** the runner error message SHALL NOT report `review.md` as missing an artifact marker for the `ai-review` task

#### Scenario: Artifact marker and verdict marker tests remain separate
- **WHEN** automated tests exercise workflow validation
- **THEN** task file requirements, optional neutral task artifact markers, and check PASS marker validation SHALL be covered as separate behaviors
- **AND** a passing task artifact validation test SHALL NOT require a PASS verdict marker unless it is executing a check
