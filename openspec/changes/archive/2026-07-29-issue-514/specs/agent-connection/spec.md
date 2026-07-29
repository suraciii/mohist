### Requirement: A Connection binds one Agent, one workspace, and one Bot identity immutably

An AgentConnection SHALL be a Project-scoped sub-resource that binds exactly one active Mohist Agent, exactly one Slack workspace, and exactly one independent Slack App/Bot identity. The Agent, workspace, and Bot identity bindings SHALL be immutable after creation; changing any of them MUST be expressed as creating a new Connection, not as editing the existing one.

#### Scenario: Rebinding is rejected
- **WHEN** an owner attempts to point an existing Connection at a different Agent or Slack workspace
- **THEN** the attempt is rejected and the original binding is unchanged

#### Scenario: A Connection is created only for an active Agent
- **WHEN** a Connection is created for an archived Agent
- **THEN** creation is rejected and no Connection is established

### Requirement: At most one non-deleted Connection per Agent and workspace

For one Project, one Agent, and one Slack workspace, Mohist SHALL allow at most one non-deleted Connection. A duplicate creation attempt against the same Agent and workspace MUST NOT overwrite or silently replace the existing Connection.

#### Scenario: Duplicate connection creation is refused
- **WHEN** a second Connection is created for the same Agent in the same Slack workspace while a non-deleted Connection already exists
- **THEN** Mohist refuses to create a duplicate, preserves the existing Connection, and points the caller to the existing one

#### Scenario: Distinct workspaces allow distinct connections
- **WHEN** a Connection exists for an Agent in one workspace and another Connection is created for the same Agent in a different workspace
- **THEN** both Connections coexist independently

### Requirement: A Connection does not own Agent execution definition

An AgentConnection SHALL NOT store or override the bound Agent's Instructions, Runtime, Model, Variant, Skills, or concurrency limits. The Connection owns only the external binding, access policy, and lifecycle facts; the execution definition remains the single copy held by the Agent.

#### Scenario: Editing the Agent updates future launches only
- **WHEN** the bound Agent's execution definition is edited after a Connection exists
- **THEN** subsequent dispatches through the Connection use the Agent's new snapshot and the Connection holds no stale copy of the prior definition

### Requirement: Setup, desired state, health, and readiness are independent facts

An AgentConnection SHALL persist and present four mutually independent facts: Setup progress (whether the external install is complete), Desired state (Enabled or Disabled), Connection health (whether the Slack side is currently reachable and consistent), and Agent Readiness (whether the bound Agent's execution configuration is known to be executable, known to be missing, or unknown). None of these facts SHALL be collapsed into a single `Connected` value; a Connection MAY be healthy while its Agent needs setup, and an Agent MAY be ready while the Slack side is temporarily unreachable.

#### Scenario: Healthy connection with an unconfigured Agent
- **WHEN** a Connection has completed setup and Slack is reachable but the bound Agent lacks a required runtime configuration
- **THEN** the Connection reports healthy setup and health while separately reporting the Agent as needing setup

#### Scenario: Ready Agent with an unreachable Slack side
- **WHEN** the bound Agent is ready but the Slack side is temporarily unreachable
- **THEN** the Connection reports the Agent as ready and the Slack side as unhealthy, without presenting a single healthy status

### Requirement: Readiness gaps produce honest dispatch decisions

Agent Readiness gaps SHALL NOT be hidden behind Connection health. When the bound Agent needs setup, a new dispatch through the Connection MUST be explicitly rejected while the Connection itself remains healthy. When Readiness is Unknown, a new dispatch SHALL be accepted and await Runner verification. When no Runner is online or capacity is full, the dispatch SHALL be explicitly queued rather than reported as a failure.

#### Scenario: Needs setup rejects new dispatch
- **WHEN** the bound Agent needs setup and a new DM task arrives
- **THEN** the Connection rejects the dispatch with an actionable reason and remains healthy

#### Scenario: Unknown readiness accepts and waits
- **WHEN** the bound Agent's readiness is unknown and a new DM task arrives
- **THEN** the dispatch is accepted and awaits Runner verification rather than being rejected

#### Scenario: No capacity queues rather than fails
- **WHEN** a dispatch arrives and no execution slot is available
- **THEN** the dispatch is reported as queued and is not reported as an execution failure

### Requirement: Deleting a Connection preserves the Agent and accepted work

Deleting an AgentConnection SHALL remove the Slack provider integration records (credentials, provider inbox, conversation mapping, pending outbound deliveries, and temporary files not used by accepted inputs) but MUST NOT delete the bound Agent, any AgentJob, any AgentSession, any accepted SessionInput, or its attachments. After deletion, the Agent SHALL remain usable from Web and CLI with unchanged behavior and execution configuration.

#### Scenario: Deletion removes provider records only
- **WHEN** an owner deletes a Connection that has accepted inputs and pending deliveries
- **THEN** the Slack credentials, inbox, conversation mapping, and pending deliveries are removed, and the Agent, its Jobs, Sessions, and accepted inputs remain

#### Scenario: Agent remains usable after Connection deletion
- **WHEN** a Connection to an Agent is deleted
- **THEN** the Agent can still be launched and observed from Web and CLI exactly as before the Connection existed
