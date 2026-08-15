### Requirement: Durable handoff admission freezes one execution per work attempt

For each new Agent-backed Workflow task attempt, the system SHALL persist one
durable handoff command keyed by the command, project, WorkflowRun, and TaskRun
identity with a canonical request fingerprint over the rendered input. The
first prepare SHALL resolve the referenced Agent exactly once and freeze its
generic execution definition — instructions, runtime, model, variant, and
ordered Skills — together with the minted AgentJob, AgentSession, SessionInput,
and AgentTurn identifiers. A replay of the same command and fingerprint SHALL
return the persisted disposition without re-reading mutable Agent
configuration, and a definitive preflight failure SHALL be persisted as a
rejection that no replay can overturn.

#### Scenario: A rendered handoff is replayed after response loss

- **WHEN** the same handoff command is prepared again after activation loss or a retry
- **THEN** the Server returns the original frozen invocation and disposition
- **AND** it does not re-read mutable Agent configuration

#### Scenario: A preflight failure becomes definitive

- **WHEN** the referenced Agent cannot be resolved, lacks a usable runtime, or the rendered input is invalid during the first preflight
- **THEN** the Server persists the matching rejection (`agent_not_found`, `agent_runtime_unavailable`, or the invalid-input code)
- **AND** a later replay remains rejected even if the Agent or its configuration changes

#### Scenario: Conflicting rendered input cannot alter a stored plan

- **WHEN** a prepare with the same command identity arrives with a different request fingerprint
- **THEN** the Server rejects it as a conflict
- **AND** the persisted invocation, rejection, and acceptance remain unchanged

#### Scenario: Acceptance is a durable receipt, not execution

- **WHEN** the Workflow submits the matching command identity and fingerprint for a prepared handoff
- **THEN** the Server persists an acceptance receipt, and a replayed acceptance reuses that receipt
- **AND** the receipt alone creates no AgentJob, AgentSession, SessionInput, AgentTurn, or Runner work

### Requirement: Idempotent activation materializes the reserved lineage

The system SHALL materialize the reserved AgentJob, AgentSession, first
SessionInput, and first AgentTurn for a handoff only from a durable accepted
receipt. Activation SHALL use exactly the minted identifiers and the frozen
execution definition and MUST NOT re-read mutable Agent configuration.
Repeated activation of the same receipt SHALL reuse the existing participants
without creating duplicates, replacement work, or a second execution.

#### Scenario: An accepted handoff activates as a real AgentJob

- **WHEN** activation runs for an accepted receipt
- **THEN** the AgentJob, AgentSession, first SessionInput, and first AgentTurn exist under the identifiers minted by the frozen invocation

#### Scenario: Activation replays without duplication

- **WHEN** activation runs again for the same accepted receipt after a crash, retry, or grain reactivation
- **THEN** the same AgentJob, AgentSession, SessionInput, and AgentTurn are reused
- **AND** no duplicate or replacement execution is created

#### Scenario: Agent edits after acceptance do not affect the invocation

- **WHEN** the Agent definition is edited after the handoff was accepted
- **THEN** the activated execution continues to use the frozen execution definition
- **AND** only later, newly dispatched invocations resolve the edited definition

#### Scenario: Unaccepted handoffs never materialize

- **WHEN** a handoff is only prepared, or is rejected
- **THEN** no AgentJob, AgentSession, SessionInput, or AgentTurn is created from it

### Requirement: Shared AgentJob admission and scheduling

A workflow-originated AgentJob SHALL execute through the same admission and
scheduling boundary as a direct Agent launch: shared Agent readiness, workspace
resolution, per-Agent concurrency permits, and Runner claim through the
existing AgentJob claim path. The change MUST NOT introduce a second queue,
scheduler, or direct Runner-process control for Workflow handoffs.

#### Scenario: Concurrency is shared with direct launches

- **WHEN** the referenced Agent is at its concurrency limit from other launches
- **THEN** the workflow-originated AgentJob waits for a permit under the same per-Agent concurrency gate
- **AND** a later permit grant admits it through the same path as a direct launch

#### Scenario: No matching Runner is online

- **WHEN** no eligible Runner is online for the Agent's runtime
- **THEN** the AgentJob remains admitted-but-waiting with its waiting reason
- **AND** the Workflow task is not independently failed by the wait

#### Scenario: A Runner claims the job through the existing claim path

- **WHEN** an eligible Runner polls for AgentJob work
- **THEN** it claims the workflow-originated job through the same claim path as a direct Agent launch
- **AND** the dispatch carries the AgentJob owner kind together with the Workflow lineage

### Requirement: Typed terminal transport

The Workflow handoff and the AgentJob participants SHALL exchange invocation
state through a typed transport that carries the invocation identity, status,
and terminal result, including output, failure reason, and artifact upload
references. The AgentJob terminal SHALL reach the Workflow side through this
transport, and the Agent execution MUST NOT use the Workflow task-report
endpoint as an Agent transport channel.

#### Scenario: The AgentJob terminal arrives typed

- **WHEN** the AgentJob reaches a terminal status
- **THEN** the typed transport delivers the terminal with the invocation identity and final result to the Workflow finalizer
- **AND** Agent execution facts are not encoded as a Workflow task report payload

#### Scenario: Transport delivery is replayable

- **WHEN** a terminal delivery is lost or duplicated
- **THEN** redelivery resolves against the same invocation identity without creating a second task outcome

### Requirement: Workflow-owned completion finalization with receipts

A Workflow-owned finalizer SHALL consume the AgentJob terminal and apply the
task completion effects exactly once: success or failure settlement, `expect`
evaluation, `artifacts` binding, `setVars` application, and recovery decisions.
Each applied effect SHALL be recorded in a durable completion-effect receipt,
and later duplicate or stale terminals for the same invocation SHALL be
acknowledged without reapplying effects. Workflow advancement SHALL remain
Workflow-owned; the AgentJob SHALL own the Agent execution lifecycle and its
result.

#### Scenario: A completed AgentJob completes the Workflow task once

- **WHEN** the finalizer consumes a completed AgentJob terminal
- **THEN** the Workflow task is completed with the job's output, `expect` is evaluated, artifacts are bound and recorded, and `setVars` are applied
- **AND** Workflow advancement happens exactly once for that task attempt

#### Scenario: A failed AgentJob follows normal failure and recovery semantics

- **WHEN** the finalizer consumes a failed AgentJob terminal
- **THEN** the task follows the normal Workflow failure semantics
- **AND** a matching recovery handler decision is applied under the remaining recovery budget

#### Scenario: Duplicate terminal delivery does not reapply effects

- **WHEN** the same AgentJob terminal is delivered again after the finalizer applied its effects
- **THEN** the finalizer acknowledges it as already applied using the completion-effect receipt
- **AND** artifacts, variables, task outcome, and advancement are not applied a second time

#### Scenario: Finalizer interruption resumes exactly once

- **WHEN** the finalizer is interrupted after persisting a receipt but before all effects are applied
- **THEN** resumption continues from the recorded receipts and completes the remaining effects
- **AND** no effect is applied twice

### Requirement: `mohist/agent` dispatch cutover with an unchanged input contract

After the activation participants, typed transport, and finalizer exist, every
new `mohist/agent` dispatch SHALL use the handoff path. The task input contract
SHALL remain `name`, `prompt`, `session`, and `timeout` with the existing
resolution and validation semantics, and `mohist/agent` SHALL remain valid for
tasks only. `mohist/agent` MUST NOT remain an inline TaskRun-owned execution:
consumers SHALL adopt the AgentJob/AgentSession identifiers and the stable
status and result contract.

#### Scenario: A new mohist/agent task dispatches through the handoff

- **WHEN** a Workflow dispatches a new task that uses `mohist/agent`
- **THEN** the dispatch goes through durable handoff admission and AgentJob activation
- **AND** the task is no longer rewritten to an inline `mohist/opencode` or `mohist/pi` TaskRun execution

#### Scenario: Task input stays unchanged

- **WHEN** a task declares `name`, `prompt`, `session`, and `timeout`
- **THEN** each field keeps its existing meaning: `name` resolves the Agent by the shared resolution order, `prompt` is the execution input, `session` names the logical session and defaults to the work id, and `timeout` bounds the execution
- **AND** profile save and validation keep requiring only `name` and `prompt` without checking whether the Agent exists

#### Scenario: Invalid input is still rejected

- **WHEN** a `mohist/agent` task lacks a non-empty `name` or `prompt`, supplies a non-positive `timeout`, or is used for a check
- **THEN** dispatch is rejected with the existing invalid-input and task-only errors

#### Scenario: No unowned handoff work is dispatched

- **WHEN** any of the activation participants, typed transport, or finalizer is unavailable
- **THEN** the system does not switch new dispatches onto the handoff path
- **AND** no handoff-owned work is dispatched to a Runner without an owner for its completion effects

### Requirement: Stable invocation status and cross-surface lineage

The system SHALL expose one stable invocation status per workflow Agent
invocation with the values `queued`, `executing`, `completed`, `failed`,
`cancelled`, and `recovering`, together with the minted AgentJob, AgentSession,
SessionInput, and AgentTurn identifiers and the final result. Workflow read
surfaces and Agent/Session read surfaces SHALL each locate the same execution
from the other side through this linkage without parsing runtime transcript
content.

#### Scenario: Status follows the execution lifecycle

- **WHEN** the invocation is admitted but not claimed, claimed and executing, terminal, or undergoing a recovery decision
- **THEN** the exposed status is `queued`, `executing`, the matching terminal value, or `recovering` respectively

#### Scenario: The Workflow surface shows the Agent execution

- **WHEN** a Workflow read surface reads a handoff-executed task
- **THEN** it exposes the invocation status together with the AgentJob, AgentSession, SessionInput, and AgentTurn identifiers
- **AND** it exposes the final result once terminal without reading the runtime transcript

#### Scenario: The Session surface shows the Workflow origin

- **WHEN** an Agent or Session read surface reads the AgentSession of a workflow invocation
- **THEN** it locates the owning WorkflowRun and TaskRun through the same linkage
- **AND** both surfaces resolve to one execution identity

### Requirement: Sibling invocation paths remain unchanged

The handoff path SHALL apply only to `mohist/agent` Workflow tasks. Direct
Agent launches, other Workflow Actions including inline `mohist/opencode` and
`mohist/pi` tasks, runtime adapters, and the Slack Bot and external Agent API
surfaces MUST keep their existing execution semantics, and the shared admission
boundary MUST NOT change their behavior.

#### Scenario: A direct Agent launch is unaffected

- **WHEN** an Agent is launched directly from the Web UI, CLI, event routing, or a connection
- **THEN** it uses the existing launch path, admission, and identifiers without a Workflow handoff

#### Scenario: Inline runtime actions are unaffected

- **WHEN** a Workflow task uses `mohist/opencode` or `mohist/pi` directly
- **THEN** the existing inline TaskRun-owned dispatch and result semantics remain in force
