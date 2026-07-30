### Requirement: Enable and Disable are user-driven Desired state toggles

The CLI and API SHALL expose explicit enable and disable operations that set the Connection's Desired state. Disable SHALL be a deliberate user choice, distinct from any external health anomaly; Enable SHALL restore the Desired state to Enabled. Neither operation SHALL alter the bound Agent, credentials, Owner, or accepted work.

#### Scenario: Disabling a Connection from the CLI
- **WHEN** an operator runs `mo agent connection disable <id>`
- **THEN** the Connection's Desired state becomes Disabled and the command reports the change

#### Scenario: Enabling a previously disabled Connection from the CLI
- **WHEN** an operator runs `mo agent connection enable <id>` on a disabled Connection
- **THEN** the Connection's Desired state becomes Enabled and the command reports the change

#### Scenario: Disable is idempotent
- **WHEN** an operator disables a Connection that is already Disabled
- **THEN** the Desired state remains Disabled and the operation succeeds without error

### Requirement: A Disabled Connection immediately stops accepting Slack input and sending replies

Once a Connection is Disabled, Mohist SHALL immediately reject inbound Slack DM input at the ingress boundary and SHALL stop enqueuing new outbound replies through that Connection. The adapter SHALL stop discovering the Connection so no new Socket Mode sessions or deliveries are initiated for it. These effects SHALL be immediate upon the Desired state change, without waiting for an adapter restart or heartbeat cycle.

#### Scenario: Ingress rejected after disable
- **WHEN** a Slack DM arrives at a Disabled Connection
- **THEN** the ingress is rejected and no AgentJob, AgentSession, SessionInput, or AgentTurn is created

#### Scenario: Adapter stops discovering a disabled Connection
- **WHEN** the adapter discovery list is refreshed after a Connection is Disabled
- **THEN** the disabled Connection is absent from the discovery result and the adapter initiates no new sessions or deliveries for it

### Requirement: Disable preserves accepted work

Disabling a Connection SHALL NOT cancel, delete, or alter any AgentJob, AgentSession, SessionInput, AgentTurn, or attachment that was already accepted before the disable. Accepted execution SHALL continue to be owned and observed by Mohist; only new Slack-initiated input and new replies through that Connection are stopped.

#### Scenario: Accepted work survives disable
- **WHEN** a Connection is Disabled while an accepted AgentJob is still running
- **THEN** the running Job, its Session, Inputs, and Turns remain intact and observable, and only new Slack input is blocked

### Requirement: Enable does not replay disabled-period messages or expired progress

Enabling a previously disabled Connection SHALL NOT replay, redeliver, or reprocess Slack messages that arrived during the disabled period, and SHALL NOT restore any progress entries that expired while disabled. The Connection SHALL resume accepting only messages that arrive after the enable.

#### Scenario: No replay of messages sent while disabled
- **WHEN** Slack messages arrived while the Connection was Disabled and the Connection is later Enabled
- **THEN** those messages are not replayed or processed and only new messages arriving after enable are accepted

### Requirement: Disable and Degraded are independent and non-substituting

Disable (a user-chosen Desired state) and Degraded (an external Connection health anomaly such as backpressure) SHALL be independent facts. A Disabled Connection SHALL NOT be reported as Degraded merely because it is disabled, and a Degraded Connection SHALL NOT be reported as Disabled merely because it is unhealthy. Each state SHALL be surfaced and resolvable on its own terms.

#### Scenario: Disabled but not Degraded
- **WHEN** a Connection is Disabled while the Slack side is otherwise healthy
- **THEN** the Desired state is Disabled and the Connection health is Healthy, not Degraded

#### Scenario: Degraded but not Disabled
- **WHEN** a Connection is Degraded due to backpressure while its Desired state is Enabled
- **THEN** the Connection health is Degraded and the Desired state remains Enabled

### Requirement: Delete clears provider records but preserves the Agent and accepted work

Deleting a Connection SHALL remove the Connection's dedicated credentials, provider inbox, conversation mappings, and pending outbound delivery records. Deletion MUST NOT delete the bound Agent, any AgentJob, any AgentSession, any accepted SessionInput, any AgentTurn, or their attachments. Deletion SHALL NOT claim or imply that the Slack App has been uninstalled from the Slack workspace; the diagnostic surface SHALL be honest that the App remains installed on the Slack side until a workspace admin removes it.

#### Scenario: Deletion removes provider records only
- **WHEN** an operator deletes a Connection that has accepted inputs and pending deliveries
- **THEN** the Slack credentials, inbox, conversation mappings, and pending deliveries are removed, and the Agent, its Jobs, Sessions, and accepted inputs remain

#### Scenario: Deletion does not claim Slack App uninstall
- **WHEN** a Connection is deleted
- **THEN** the diagnostic and command output state that the Connection's Mohist-side records were removed and that the Slack App remains installed on the Slack side until manually uninstalled by a workspace admin

#### Scenario: Agent remains usable after Connection deletion
- **WHEN** a Connection to an Agent is deleted
- **THEN** the Agent can still be launched and observed from Web and CLI exactly as before the Connection existed
