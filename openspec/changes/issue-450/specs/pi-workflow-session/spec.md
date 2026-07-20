### Requirement: Workflow Session names identify stable logical conversations

A Pi Workflow Action SHALL resolve its logical AgentSession by project, WorkflowRun, and session name. Tasks in the same WorkflowRun with the same explicit session name SHALL resolve to the same logical AgentSession; different names or different WorkflowRuns SHALL remain isolated even when prompt, model, variant, and working directory are identical. When `session` is omitted, the Action SHALL use the current Work ID as the session name.

#### Scenario: Same name shares one logical conversation

- **WHEN** two tasks in the same WorkflowRun use the same Pi session name
- **THEN** both SHALL resolve to the same logical AgentSession
- **AND** the second task SHALL receive the first task's conversation context

#### Scenario: Omitted name isolates unrelated work

- **WHEN** two tasks omit `session` and have different Work IDs
- **THEN** each SHALL use its own Work ID as the session name
- **AND** their logical AgentSessions SHALL remain separate

### Requirement: The physical Pi binding is persisted before the first prompt

For an unbound Pi logical AgentSession, the Runner SHALL create a physical Pi Session in the authoritative working directory and SHALL persist its binding before submitting the first prompt. The binding SHALL record runtime `pi`, the owning Runner, immutable working directory, and the absolute Pi session-file path as `runtimeSessionId`; the Pi internal Session UUID SHALL remain diagnostic only. If physical creation does not yield a session-file path, the turn SHALL fail as `incompatible-runtime`. If binding persistence fails, the turn SHALL fail as `session-binding-failed` and MUST NOT submit the prompt.

#### Scenario: Binding precedes first prompt admission

- **WHEN** a Pi task targets an AgentSession with no physical binding
- **THEN** the Runner SHALL create the physical Pi Session and persist its absolute session-file path first
- **AND** it SHALL submit the first prompt only after persistence succeeds

#### Scenario: Failed binding persistence prevents execution

- **WHEN** the physical Pi Session is created but its logical binding cannot be persisted
- **THEN** the Action SHALL fail with `session-binding-failed`
- **AND** the first prompt MUST NOT be submitted

### Requirement: Current Pi bindings are reused without model-driven rotation

The current Pi binding SHALL be reused for the same logical AgentSession across Workflow tasks, task retries, cleanup turns, and Runner restarts. A model or variant change SHALL be applied to the existing physical Session and MUST NOT replace the session-file path, append lineage, or discard conversation context. After Runner restart, the runtime SHALL lazily restore the physical Session from the persisted absolute session-file path rather than creating a replacement. A Workflow change between Pi and another registered runtime in either direction SHALL preserve the logical AgentSession identity, establish a new physical binding under an expected-current guard, and append the new binding to lineage without migrating the previous runtime's context.

#### Scenario: Retry reuses the physical Session

- **WHEN** a Pi task is retried with the same logical session name
- **THEN** it SHALL use the current persisted Pi session-file path
- **AND** it MUST NOT create a new physical Session for the retry

#### Scenario: Model change preserves context and binding

- **WHEN** a later task on the same logical AgentSession changes `options.model` or `options.variant`
- **THEN** the runtime SHALL apply the selection to the current physical Pi Session
- **AND** it MUST NOT replace the binding or append lineage

#### Scenario: Runner restart restores from the binding

- **WHEN** a Runner restarts before a later task uses an already-bound Pi AgentSession
- **THEN** the runtime SHALL restore that Session from the persisted session-file path
- **AND** the later task SHALL continue with the prior conversation context

#### Scenario: Runtime change appends physical lineage

- **WHEN** a Workflow task selects Pi for a logical AgentSession whose current binding belongs to another runtime
- **THEN** Mohist SHALL keep the same logical AgentSession identity and establish a new Pi binding
- **AND** it SHALL append the Pi session-file path and runtime to the Session lineage without migrating old context

#### Scenario: Switching from Pi to another runtime uses the same guard

- **WHEN** a Workflow task selects another registered runtime for a logical AgentSession currently bound to Pi
- **THEN** Mohist SHALL keep the same logical AgentSession identity and establish the replacement binding only when the expected Pi binding is still current
- **AND** it SHALL append the replacement runtime binding to lineage without migrating Pi context

### Requirement: Invalid persisted bindings fail without silent replacement

Before submitting a prompt, the Runner SHALL verify that the authoritative working directory matches the logical AgentSession's immutable bound directory. A mismatch SHALL fail with `session-workspace-mismatch`. When a current Pi binding exists but its session file is missing, unreadable, corrupt, or otherwise cannot be restored, the task SHALL fail with `runtime-session-missing` and an explicit Reset instruction. The runtime MUST NOT create a replacement physical Session, change lineage, or submit the prompt in either case. A binding created before Pi wrote its first assistant message SHALL follow the same missing-file rule after a Runner restart.

#### Scenario: Missing session file requires Reset

- **WHEN** a task resolves a Pi binding whose session file no longer exists
- **THEN** the task SHALL fail with `runtime-session-missing` and an explicit Reset instruction
- **AND** the runtime MUST NOT create a replacement Session

#### Scenario: Workspace mismatch rejects before prompt

- **WHEN** a Pi task's authoritative working directory differs from the logical AgentSession's bound directory
- **THEN** the task SHALL fail with `session-workspace-mismatch`
- **AND** no prompt SHALL be submitted and no binding SHALL be replaced

#### Scenario: Pre-persistence crash follows missing-file semantics

- **WHEN** the first turn persisted a session-file binding but the Runner stopped before Pi created that file
- **THEN** a later restore SHALL fail with `runtime-session-missing` and a Reset instruction
- **AND** the runtime MUST NOT infer an empty replacement conversation

### Requirement: One Workflow work turn executes per logical AgentSession

Mohist SHALL admit at most one Workflow-initiated work turn at a time for a logical AgentSession. Another work turn targeting that Session SHALL remain serialized until the current work turn reaches a terminal outcome and the runtime confirms the physical execution stopped. An interruption-unconfirmed outcome SHALL quarantine that physical Session and later work MUST NOT start on it until stop is observed or Runner process restart makes prior in-process execution impossible. Different logical AgentSessions SHALL remain independently executable.

#### Scenario: Concurrent tasks on one Session are serialized

- **WHEN** two Workflow tasks concurrently target the same logical Pi AgentSession
- **THEN** Mohist SHALL execute at most one of their work turns at a time
- **AND** the second turn MUST NOT overlap the first on the physical Pi Session

#### Scenario: Separate Sessions remain independent

- **WHEN** concurrent Pi tasks target different logical AgentSessions
- **THEN** serialization of one Session SHALL NOT serialize the other Session

#### Scenario: Unconfirmed stop prevents overlap

- **WHEN** a turn ends with interruption unconfirmed and another Workflow task targets the same logical AgentSession
- **THEN** the later task SHALL fail as runtime unavailable rather than start on the quarantined physical Session
- **AND** no two Pi Prompts SHALL overlap on that physical Session

### Requirement: Pi turn facts populate the existing Session audit record

For every Pi Workflow turn, the Runner SHALL durably report the submitted prompt and normalized Pi events to the current logical AgentSession. The projection SHALL include assistant text, reasoning when present, tool-call lifecycle and result facts, resolved model observations, and token usage including input, output, cache, and thought tokens when Pi provides them. Before prompt admission, `session.input` SHALL be durably accepted by the Session authority. After admission, each normalized fact SHALL be written to a Runner-local durable outbox before the Action returns; delivery SHALL retry across transport loss and Runner restart without replaying the Prompt. Each binding SHALL use one stable event stream identity and monotonically increasing sequence. The Session authority SHALL atomically ignore an already-applied sequence before updating transcript, usage, or model state, reject a gap, and reject facts for a stale physical binding. A runtime change MUST NOT replace the binding until the previous binding's durable outbox is drained. Pi message IDs and tool-call IDs SHALL also suppress duplicate SDK projections before enqueue. Unknown Pi events SHALL be diagnostic only and MUST NOT change Workflow or AgentSession state. AgentSession events SHALL record execution facts and MUST NOT decide TaskRun completion or Workflow advancement.

#### Scenario: Session view shows a completed Pi turn

- **WHEN** a Pi turn emits assistant text, tool execution, resolved model, and usage facts
- **THEN** the AgentSession transcript and usage read model SHALL expose those facts for the Session view
- **AND** the facts SHALL remain associated with the current Pi session-file binding

#### Scenario: Duplicate Pi events are idempotent

- **WHEN** the same Pi message or tool event is delivered more than once
- **THEN** the Session projection SHALL use its message ID or tool-call ID to avoid duplicate transcript or usage facts

#### Scenario: Post-admission transport failure is retried durably

- **WHEN** a Pi event cannot reach the Server after the Prompt has been admitted
- **THEN** the Runner SHALL retain the ordered event in its durable outbox and preserve the Action result
- **AND** it SHALL retry delivery without replaying the Prompt, including after Runner restart

#### Scenario: Ambiguous delivery does not duplicate usage or transcript

- **WHEN** the Server applies an event but the acknowledgement is lost and the Runner sends the same stream sequence again
- **THEN** the Session authority SHALL acknowledge the repeated sequence without applying it again
- **AND** transcript text, tool facts, and usage totals SHALL remain unchanged by the duplicate

#### Scenario: Event sequence gaps are rejected

- **WHEN** the Session authority receives a current-binding event whose sequence skips an unapplied predecessor
- **THEN** it SHALL reject that event without changing Session state
- **AND** the Runner SHALL retain and resend the missing ordered outbox entries

#### Scenario: Stale binding events are rejected

- **WHEN** a runtime event identifies a physical Session that is no longer the logical AgentSession's current binding
- **THEN** the Session authority SHALL reject that event
- **AND** it MUST NOT alter the current transcript, usage, or Workflow state

#### Scenario: Session facts do not complete Workflow work

- **WHEN** AgentSession receives a Pi end event or final assistant text
- **THEN** it SHALL record the facts for audit and display
- **AND** it MUST NOT complete the TaskRun or advance the Workflow
