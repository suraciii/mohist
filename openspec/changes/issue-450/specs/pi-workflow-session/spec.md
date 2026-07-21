### Requirement: Workflow Session names identify stable logical conversations

Workflow SHALL resolve a logical AgentSession from project, WorkflowRun, and normalized Session name. Reuse SHALL be independent of task ID, retry attempt, and selected runtime.

#### Scenario: Same name shares one logical conversation

- **WHEN** later tasks or checks in one WorkflowRun use the same explicit Session name
- **THEN** they resolve the same logical AgentSession

#### Scenario: Omitted name uses Work ID

- **WHEN** a Pi Action omits Session
- **THEN** its Work ID is used as the Session name
- **AND** unrelated work IDs do not share conversation state by default

### Requirement: The physical Pi binding is persisted before the first prompt

Workflow AgentSession open and attach SHALL accept an explicit runtime. A first Pi attach SHALL persist the absolute session-file path before prompt admission.

#### Scenario: First attach owns the prompt

- **WHEN** a logical Session has no physical binding
- **THEN** the Runner creates a Pi Session and atomically attaches its path as runtime `pi`
- **AND** only a successful attach permits input reporting and prompt submission

#### Scenario: Repeated attach is idempotent

- **WHEN** the same runtime and physical path are attached again
- **THEN** the existing binding is returned without a duplicate lineage entry

#### Scenario: Stale attach loses the race

- **WHEN** attach's expected runtime or physical ID no longer matches the state observed at open
- **THEN** the grain rejects it before replacing the current binding

### Requirement: Current Pi bindings are reused without model-driven rotation

A current Pi binding SHALL be reused by same-name tasks, checks, retries, cleanup turns, and Runner restarts. Model or variant changes SHALL apply to the bound physical Session without replacing it.

#### Scenario: Runner restart restores the path

- **WHEN** a new Runner process opens a logical Session bound to Pi
- **THEN** it restores the exact persisted session-file path through PiRuntime

#### Scenario: Model change keeps lineage stable

- **WHEN** a later same-name turn selects a different model or variant
- **THEN** the physical path remains unchanged
- **AND** no runtime lineage entry is appended

#### Scenario: Cleanup retains context

- **WHEN** task cleanup reinvokes Pi
- **THEN** it continues the same physical conversation

### Requirement: Runtime changes are guarded and preserve logical identity

A Workflow Session MAY change from OpenCode to Pi or Pi to OpenCode only through a guarded binding transition. The transition SHALL retain logical identity and source, append physical lineage, and start the target runtime's own context.

#### Scenario: Expected runtime change succeeds

- **WHEN** the current binding still matches the state observed by the caller and a different runtime is selected
- **THEN** the new physical binding replaces the current one
- **AND** one lineage entry is appended without migrating conversation context

#### Scenario: Same-runtime replacement requires Reset

- **WHEN** a caller tries to attach a different physical ID for the current runtime
- **THEN** the attach is rejected with Reset guidance

### Requirement: Invalid persisted bindings fail without silent replacement

Workflow Session work directory and physical identity SHALL be checked before prompt submission.

#### Scenario: Work directory mismatch is rejected

- **WHEN** a same-name logical Session is opened from a different work directory
- **THEN** execution fails as `session-workspace-mismatch`
- **AND** no physical Session is created, replaced, or prompted

#### Scenario: Missing Pi file requires Reset

- **WHEN** a current Pi binding points to a missing or corrupt file
- **THEN** execution fails as `runtime-session-missing` with Reset guidance
- **AND** no replacement Session is created implicitly

#### Scenario: First-prompt crash can leave a missing file

- **WHEN** Pi has exposed a path and the binding was persisted but the Runner dies before Pi materializes the file
- **THEN** the next attempt follows the same missing-file failure
- **AND** Mohist does not claim continuous context or replay the uncertain prompt

### Requirement: One Workflow work turn executes per logical AgentSession

One process-local coordinator SHALL serialize complete Workflow task and check turns by project, WorkflowRun, and Session name across OpenCode and Pi.

#### Scenario: Same-name task and check serialize

- **WHEN** a task and check concurrently target the same logical Session
- **THEN** only one enters Action execution
- **AND** the other waits until the first Action and its cleanup, if any, finish

#### Scenario: Runtime switch cannot bypass serialization

- **WHEN** concurrent work selects OpenCode and Pi for the same logical Session
- **THEN** both use the same coordinator key and do not overlap

#### Scenario: Different logical Sessions remain concurrent

- **WHEN** work targets different Session names
- **THEN** the coordinator permits their turns to execute independently

#### Scenario: Runner restart does not invent a durable lock

- **WHEN** the Runner process exits
- **THEN** coordinator state ends with the process
- **AND** recovery remains governed by current Workflow redelivery and persisted AgentSession binding rules

### Requirement: Pi turn facts populate the existing Session audit record

Pi input, assistant text, reasoning, tool lifecycle/results, model observations, status, and usage SHALL be reported through the existing Workflow AgentSession runtime-event route under the current physical binding. Usage SHALL preserve input, output, cache read, cache write, thought when supplied, cost amount, and currency as distinct facts.

#### Scenario: Successful turn is visible in Session views

- **WHEN** a Pi turn completes and required facts are accepted
- **THEN** existing Session transcript, tool, model, usage, cost, and runtime-lineage views expose those facts
- **AND** no Pi-specific Session record or view is required

#### Scenario: Cache-write remains distinct

- **WHEN** Pi reports cache-read and cache-write tokens
- **THEN** each value accumulates in its own Session usage field
- **AND** cache-write is not folded into cache-read

#### Scenario: Cost retains currency

- **WHEN** Pi reports a monetary cost
- **THEN** AgentSession usage stores the amount and currency through the existing cost contract

#### Scenario: Duplicate Pi callback is idempotent

- **WHEN** Pi emits the same message or tool identity more than once within a turn
- **THEN** the Runner projector reports the logical fact once

#### Scenario: Stale physical binding is rejected

- **WHEN** a runtime-event batch names a physical ID that is no longer current
- **THEN** AgentSession rejects the entire batch before transcript, model, or usage state changes

### Requirement: Required Session reporting completes before work success

The Runner SHALL complete required input and final-fact writes before returning Action success. Reporting has one completion point and no durable background success mode.

#### Scenario: Input failure blocks prompt admission

- **WHEN** `session.input` cannot be accepted
- **THEN** the prompt is not submitted and work fails as `session-reporting-failed`

#### Scenario: Final-fact failure blocks completion evaluation

- **WHEN** the prompt completed but a required assistant, tool, model, usage, or cost write cannot be flushed
- **THEN** work fails as `session-reporting-failed`
- **AND** promise, expectation, and artifact evaluation do not run

#### Scenario: No hidden replay protocol exists

- **WHEN** reporting fails or the Runner process dies
- **THEN** no Action stream, local outbox, cursor, inventory, checkpoint, or projector recovery is consulted
- **AND** a later normal Workflow redelivery may repeat the turn

#### Scenario: Session facts do not complete Workflow work

- **WHEN** AgentSession accepts all runtime facts
- **THEN** Workflow status remains unchanged until the ordinary Action result is reported

### Requirement: Pi registration does not enable out-of-scope Session commands

The Server SHALL recognize `pi` as a valid stored runtime while Pi Follow-up, Compact, Reset, and Cancel routing remains unavailable in issue #450.

#### Scenario: Registered Pi command is unavailable

- **WHEN** Follow-up, Compact, Reset, or Cancel admission observes a current Pi binding before its sister issue is delivered
- **THEN** command admission returns unavailable without reserving or dispatching the command
- **AND** OpenCode is not invoked and the binding is unchanged

#### Scenario: Historical Reset fallback remains intact

- **WHEN** Reset targets an unregistered historical runtime value
- **THEN** the existing fallback behavior remains unchanged
