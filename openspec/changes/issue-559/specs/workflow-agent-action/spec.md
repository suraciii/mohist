## ADDED Requirements

### Requirement: Durable Workflow Agent Handoff Fence

For a new Agent-backed Workflow task attempt, the system SHALL persist one
typed handoff command with a canonical request fingerprint. A replay with the
same fingerprint SHALL return the original disposition. A conflicting
fingerprint SHALL not create or alter an invocation.

#### Scenario: A rendered handoff is replayed after response loss

- **WHEN** the same command is prepared again after activation loss
- **THEN** the Server returns the original frozen invocation or rejection
- **AND** it does not re-read mutable Agent configuration

#### Scenario: A preflight failure becomes definitive

- **WHEN** the Agent cannot be resolved during the first preflight
- **THEN** the Server stores a rejection for that command
- **AND** a later replay remains rejected even if the Agent becomes available

### Requirement: Acceptance Is Not Execution

The Server SHALL persist an immutable invocation and matching Workflow
acceptance receipt before a later activation slice may create execution
participants. The handoff foundation SHALL NOT materialize an AgentJob,
AgentSession, Input, Turn, or Runner work.

#### Scenario: A prepared handoff is accepted

- **WHEN** the Workflow submits the matching command id and fingerprint
- **THEN** the Server persists an accepted receipt
- **AND** no Job, Session, or Runner claim exists

### Requirement: Generic Runtime Boundary

The handoff preflight SHALL resolve the immutable generic Agent execution
definition. It SHALL NOT depend on a particular runtime adapter or use the
Workflow task-report endpoint as a transport channel.

### Requirement: No Partial Cutover

The system SHALL retain the existing `mohist/agent` dispatch translation while
typed handoff transport and the Workflow finalizer are absent.

#### Scenario: Only the handoff foundation is available

- **WHEN** a Workflow task uses `mohist/agent`
- **THEN** no unowned handoff work is dispatched to a Runner
- **AND** the existing inline execution path remains authoritative

### Requirement: Activation and terminal settlement form one dispatch contract

An accepted handoff MAY gain dark, Server-side activation support, but the
Workflow action SHALL NOT use that support until a typed AgentJob terminal
delivery and a Workflow-owned finalizer are both available. Activation SHALL
use only the frozen handoff plan and reserved identifiers. The terminal
delivery SHALL carry the invocation, WorkflowRun, TaskRun, work, Job, Session,
Input, and Turn identities, Agent terminal facts, and completion evaluation.
The Workflow finalizer SHALL record idempotent effect receipts before applying
the matching task outcome, expectation, artifacts, variables, recovery, or
advancement.

#### Scenario: Dark activation support has no production effect

- **WHEN** activation participants exist but no typed terminal finalizer is registered
- **THEN** no `mohist/agent` Workflow dispatch calls activation
- **AND** an accepted handoff continues to create no Runner work

#### Scenario: A typed terminal settles one task attempt

- **WHEN** a workflow-originated AgentJob reaches a terminal state
- **THEN** its stable terminal delivery identifies exactly one frozen invocation and task attempt
- **AND** the Workflow finalizer applies each completion effect at most once

#### Scenario: Terminal delivery replays after acknowledgement loss

- **WHEN** a terminal delivery or finalizer acknowledgement is lost and later replayed
- **THEN** the same invocation and terminal identity are retried
- **AND** no replacement AgentJob, duplicate task outcome, duplicate artifact binding, or duplicate variable write is created
