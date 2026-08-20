### Requirement: Explicit Manager CLI capability allowlist
The Manager Agent SHALL access management behavior only through an explicit allowlist of `mo` CLI capabilities. The allowlist SHALL be limited to workspace and Connection status, Agent and Connection listing or viewing, supported Agent creation or mounting, access-policy changes, Connection enable or disable, owner transfer, and applicable diagnostics. Unlisted CLI commands and direct management API calls MUST be unavailable to Manager execution.

#### Scenario: Supported inspection capability is invoked
- **WHEN** the Manager Agent invokes an allowlisted status, list, view, or diagnostic capability for a workspace or Slack Connection
- **THEN** the CLI executes the corresponding existing application service operation and returns its authoritative current result to the Agent

#### Scenario: Supported Agent mounting or creation capability is invoked
- **WHEN** the Manager Agent requests an Agent creation or mounting operation through the allowlisted CLI surface with a valid Project and Agent target
- **THEN** the CLI performs the supported create-or-mount operation through the existing application services and returns the actual resulting state

#### Scenario: Supported Connection management capability is invoked
- **WHEN** the Manager Agent requests an allowlisted access-policy change, enable, disable, or owner-transfer operation
- **THEN** the CLI invokes the corresponding existing management path and reports the authoritative operation result and next action

#### Scenario: Unlisted CLI capability is invoked
- **WHEN** the Manager Agent attempts a command outside the allowlist, including arbitrary Server API access
- **THEN** the command is rejected before any management side effect occurs and the rejection is returned as an actionable authorization or availability result

### Requirement: Authoritative CLI results
Manager decisions and user-facing claims about Agent, Connection, workspace, ownership, readiness, and next actions SHALL be based on the result of the allowlisted CLI capability. The Server MUST NOT maintain a second Manager-specific tool-result protocol or provide a conflicting management result to the Agent.

#### Scenario: Management operation reports a non-ready state
- **WHEN** an allowlisted CLI operation reports that an Agent App, Connection, or workspace is not ready or requires a next action
- **THEN** the Manager Agent receives that reported state and does not receive a Server-generated claim that the resource is ready

#### Scenario: Management operation changes state
- **WHEN** an allowlisted CLI mutation succeeds
- **THEN** the returned resource state is the source of truth for the Agent's response and the Server does not synthesize an acknowledgement message outside the normal Agent reply action

#### Scenario: CLI operation fails or has an unknown outcome
- **WHEN** an allowlisted CLI operation returns an authorization failure, validation failure, unavailable service, or unknown operation outcome
- **THEN** the Manager Agent receives the reported error class and next action, and no success state is inferred

### Requirement: Protected and destructive capabilities are unavailable
The Manager CLI capability surface MUST exclude secret submission, credential rotation or reads, credential addresses, permanent deletion, Connection binding removal, arbitrary management API access, and any operation that exposes protected values. Excluded operations MUST be rejected without side effects.

#### Scenario: Agent attempts to submit or read credentials
- **WHEN** the Manager Agent requests a Slack token, credential file, secret submission, credential rotation, credential address, or credential read
- **THEN** the capability is unavailable, no secret is accepted or returned, and no managed resource is changed

#### Scenario: Agent attempts destructive management
- **WHEN** the Manager Agent requests permanent deletion, binding removal, or another destructive operation outside the allowlist
- **THEN** the CLI rejects the request before mutation and reports that the operation requires an unavailable control-plane path

### Requirement: Existing authorization paths remain authoritative
Manager CLI calls SHALL use the existing application services and their current authorization rules for the authenticated actor, active Enrollment, Project, workspace, Agent, and Slack Connection. The Manager capability surface MUST preserve the behavior and authorization outcomes of existing CLI and Web management paths for their existing callers.

#### Scenario: Authorized target in the enrolled workspace is selected
- **WHEN** an authenticated Manager call targets a live Project and Slack Connection belonging to the current enrolled workspace
- **THEN** the existing authorization path evaluates the request and the operation proceeds only when that path allows it

#### Scenario: Cross-workspace or missing target is selected
- **WHEN** a Manager call targets a Connection from another workspace, a missing Project or Agent, or a deleted target
- **THEN** the call is rejected as unauthorized or not found before mutation and the targeted resource remains unchanged

#### Scenario: Existing CLI or Web caller uses the same operation
- **WHEN** an existing non-Manager CLI or Web caller invokes a supported Slack management operation
- **THEN** that caller continues to receive the existing route, authorization, validation, and result behavior
