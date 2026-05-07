## MODIFIED Requirements

### Requirement: AgentSession runtime boundary is workflow-visibility independent

`AgentSession` SHALL represent an observable AI developer working session without importing or constructing workflow visibility adapters. Runtime session options SHALL contain only session/runtime configuration and `SessionObserver[]`; EventBus and DB repository dependencies SHALL be owned by workflow/service observers outside `agent-runtime`.

#### Scenario: Runtime options exclude visibility dependencies
- **WHEN** `AgentSessionOptions` is inspected
- **THEN** it contains no `EventBus`, `WorkflowLogRepo`, `SessionStreamLogRepo`, or `CoderSessionRepo` fields
- **AND** it accepts `observers?: SessionObserver[]`

#### Scenario: Agent runtime has no workflow visibility imports
- **WHEN** files under `packages/cli/src/agent-runtime/` are inspected
- **THEN** they do not import workflow visibility modules, EventBus, or DB repo types
- **AND** `agent-session.ts` does not import `WorkflowSessionObserver`

#### Scenario: Session events are published through observers
- **WHEN** an ACP session starts, emits text chunks, emits tool calls, receives raw notifications, changes state, or closes
- **THEN** `AgentSession` notifies the supplied `SessionObserver[]`
- **AND** observer failures are logged without stopping the session flow

### Requirement: AgentSession lifecycle behavior is preserved

`AgentSession` and `withSession` SHALL preserve existing lifecycle semantics while the boundary is cleaned.

#### Scenario: withSession guarantees cleanup
- **WHEN** `withSession` creates a session and execution succeeds or fails
- **THEN** `close()` is invoked from a `finally` path

#### Scenario: Abort performs cancellation and cleanup
- **WHEN** the session abort signal fires during prompt execution
- **THEN** ACP cancel is attempted
- **AND** optional `onBeforeKill` is invoked
- **AND** the ACP process is cleaned up
- **AND** the returned result is a user-visible failure

#### Scenario: Timeout performs cancellation and cleanup
- **WHEN** prompt execution exceeds the configured timeout
- **THEN** ACP cancel is attempted
- **AND** optional `onBeforeKill` is invoked
- **AND** the ACP process is cleaned up
- **AND** observers receive a terminal timeout state change
- **AND** the returned result is a user-visible timeout failure

#### Scenario: Model override remains degraded behavior on failure
- **WHEN** a model override is configured and ACP session creation succeeds
- **THEN** the model is applied through ACP session configuration
- **AND** if applying the model fails, a warning/degraded event is produced without aborting session creation

### Requirement: ACP driver remains runtime-only if extracted

If ACP SDK communication is extracted, the adapter SHALL hide real ACP implementation complexity without owning workflow or visibility concepts.

#### Scenario: ACP driver has runtime-only dependencies
- **WHEN** an ACP driver/helper is introduced
- **THEN** it owns ACP SDK connection setup and calls only
- **AND** it imports no issue, workflow, EventBus, DB repo, stream-log, or coder-session visibility modules

#### Scenario: No shallow protocol layer is required
- **WHEN** extracting an ACP helper would only mirror the SDK methods without reducing `AgentSession` responsibility
- **THEN** the implementation may keep ACP SDK calls in `AgentSession`
- **AND** `AgentSession` must still satisfy the workflow-visibility independence requirement
