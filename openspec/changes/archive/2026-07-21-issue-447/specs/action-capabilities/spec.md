### Requirement: Action manifests declare non-default capabilities
Every executable Action manifest SHALL declare the complete set of non-default capabilities required by its execution function. The supported capability names SHALL be `agent-turn`, `issue-fields`, `workflow-checkpoint`, `add-tasks`, and `write-vars`; an Action without a declaration SHALL have no non-default capabilities. The Runner MUST reject an Action definition with an unknown or duplicate capability name.

#### Scenario: Declare an agent-turn Action
- **WHEN** an Action manifest declares `agent-turn`
- **THEN** the Runner SHALL recognize that Action as eligible to receive the agent-turn capability
- **AND** the declaration SHALL be part of the Action contract used for execution

#### Scenario: Reject an invalid capability declaration
- **WHEN** an Action manifest declares an unknown capability or declares the same capability more than once
- **THEN** the Runner MUST reject the Action definition before it accepts work

### Requirement: Actions receive only the default host and declared capabilities
The Action execution function SHALL receive validated `with` input and a host whose default surface contains only the resolved work directory, cancellation signal, task logging, and command execution. The host MUST NOT expose workflow variables, server connections, runtime handles, recovery declarations, dispatch metadata, or identity fields. The Runner SHALL inject an additional capability only when the selected Action manifest declares it.

#### Scenario: Execute an Action without capabilities
- **WHEN** the Runner executes an Action whose manifest declares no capabilities
- **THEN** its execution host SHALL expose only the default host surface
- **AND** it MUST NOT expose an agent-turn operation, task-addition operation, variable-write operation, server connection, or runtime handle

#### Scenario: Inject only declared capabilities
- **WHEN** the Runner executes an Action declaring `agent-turn` and not `add-tasks` or `write-vars`
- **THEN** the Action SHALL receive the agent-turn capability
- **AND** it MUST NOT receive task-addition or variable-write capabilities

### Requirement: Private workflow context is available only through declared operations
An Action declaring `issue-fields` SHALL receive an operation that returns the current Issue title and body. An Action declaring `workflow-checkpoint` SHALL receive an operation that returns an opaque, stable checkpoint token for a supplied scope. The Runner SHALL resolve both operations from private dispatch context. Neither operation MUST expose project, issue, workflow-run, work, stage, title, recovery, or parent-issue identity to the Action.

#### Scenario: Resolve issue fields without dispatch identity
- **WHEN** an Action declaring `issue-fields` requests the current Issue title and body
- **THEN** the Runner SHALL return the fields for the dispatched Issue
- **AND** the Action host MUST NOT expose the project id or issue number used to resolve them

#### Scenario: Create a workflow-scoped archive checkpoint
- **WHEN** an Action declaring `workflow-checkpoint` requests a checkpoint token for the same scope across a retry of the same workflow run
- **THEN** the Runner SHALL return the same opaque token
- **AND** a different workflow run MUST receive a distinct token for that scope
- **AND** the Action host MUST NOT expose either workflow-run identity

### Requirement: Agent turns are a capability-owned operation
An Action declaring `agent-turn` SHALL receive an operation that executes an Agent turn from the Action's prompt, optional logical session, and options. The capability implementation SHALL own parent-Issue prompt composition, runtime readiness, session opening or attachment, runtime execution, runtime-event reporting, cancellation, and session closure. The Action MUST NOT receive the runtime, server, or dispatch identity implementation used to perform those responsibilities.

#### Scenario: Run an OpenCode agent turn
- **WHEN** `mohist/opencode` executes with a ready OpenCode runtime and valid turn input
- **THEN** its declared `agent-turn` capability SHALL run the turn using the existing session and runtime behavior
- **AND** the Action's public output and business error codes SHALL remain unchanged

#### Scenario: Agent runtime is unavailable
- **WHEN** an Action invokes its declared `agent-turn` capability while the required runtime is unavailable or not ready
- **THEN** the Action SHALL receive the existing runtime-unavailable failure behavior
- **AND** the Action MUST NOT receive a raw runtime handle as a fallback

### Requirement: Manifests control template rendering timing for Action inputs
Each Action input declaration SHALL use immediate template rendering unless it declares deferred rendering. The Runner SHALL render immediate inputs before Action input validation and invocation. For a deferred input, the Runner SHALL validate its declared top-level JSON kind without expanding nested templates and SHALL pass that original input value to the Action. An Action uses deferred input only through its validated `with` input; the Runner MUST NOT inject a raw dispatch field selected by Action name.

#### Scenario: Preserve OpenSpec task defaults for generated tasks
- **WHEN** `mohist/openspec-tasks` receives its deferred `task` input containing `${{ vars.agent }}` or another nested template
- **THEN** the Action SHALL receive that template unchanged in `with.task`
- **AND** generated follow-up tasks SHALL retain the template for their own dispatch-time expansion
- **AND** the Action host MUST NOT contain `rawTask` or another Action-name-specific raw input field

### Requirement: Promise output projection follows the agent-turn capability
For a successful task executed by an Action declaring `agent-turn`, the task executor SHALL evaluate the final assistant text for the task's completion contract and project public output as `null` or `{ "promise": value }`. For every Action not declaring `agent-turn`, the executor SHALL preserve the successful Action output through completion evaluation. The executor MUST NOT select either behavior from an Action name list.

#### Scenario: Project a matched agent-turn promise
- **WHEN** a successful `agent-turn` Action produces final assistant text containing a completion promise accepted by the task contract
- **THEN** the completed task output SHALL be `{ "promise": value }`
- **AND** runtime facts and assistant text MUST NOT appear in public task output

#### Scenario: Preserve a non-agent Action output
- **WHEN** a successful Action that does not declare `agent-turn` completes its task contract
- **THEN** the completed task output SHALL equal the Action's successful output
- **AND** the executor MUST NOT apply agent-turn promise projection
