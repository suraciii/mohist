### Requirement: A CLI connection subgroup manages the Connection lifecycle

The CLI SHALL provide an `mo agent connection` subgroup that creates, configures, claims ownership for, views, lists, edits, and deletes Slack Connections. This issue SHALL deliver `create`, `configure`, `claim-owner`, `view`, `list`, `edit`, and `delete`; credential rotation, owner transfer, and enable/disable lifecycle operations are out of scope.

#### Scenario: Creating a connection from the CLI
- **WHEN** a user runs `mo agent connection create <agent> --provider slack`
- **THEN** Mohist creates a recoverable Connection and outputs the Connection identity and the prefilled Slack App creation reference, without requiring `mohist-slack` to be online

#### Scenario: Delivered command surface
- **WHEN** a user inspects the `mo agent connection` subgroup
- **THEN** the `create`, `configure`, `claim-owner`, `view`, `list`, `edit`, and `delete` commands are present and the rotation, transfer, enable, and disable commands are absent

### Requirement: Credentials are entered and stored only through protected channels

Slack App and Bot credentials SHALL be supplied either through hidden terminal input or through a `--credentials-file` pointing at a UTF-8 JSON document containing exactly the `appToken` and `botToken` fields. The credential file MUST be a regular non-symlink file readable and writable only by the current user. Credentials MUST NOT be accepted as command-line arguments and MUST NOT appear in command echo, Agent Instructions, messages, logs, or Session transcripts.

#### Scenario: Credentials read from a protected file
- **WHEN** `mo agent connection configure <id> --credentials-file <path>` is run with a `chmod 600` regular file containing `{"appToken":"xapp-...","botToken":"xoxb-..."}`
- **THEN** the credentials are stored and never printed to the terminal or written into Agent configuration

#### Scenario: Command-line tokens are refused
- **WHEN** a credential value is passed directly as a command argument
- **THEN** the command rejects the invocation and stores no credentials

#### Scenario: Insecure credential file is refused
- **WHEN** the credentials file is a symlink or is readable by other users
- **THEN** the command refuses to read it and stores no credentials

#### Scenario: Credentials stay out of transcripts and logs
- **WHEN** a dispatch is launched through a configured Connection
- **THEN** neither the App token nor the Bot token appears in the AgentJob, AgentSession, SessionInput, transcript, or server logs

### Requirement: Setup progress is durable across service and configuration failures

Setup SHALL advance through the steps Create app & add credentials, Waiting for Slack service, Fix Slack setup, Claim owner, and Complete. Steps already confirmed SHALL be preserved when the Slack service is offline, the token is invalid, the App and Bot are inconsistent, or the bound Agent is not yet ready. The Connection SHALL surface a single current step and a single actionable next action, and MUST NOT regress to a generic Setup required state that hides completed progress.

#### Scenario: Offline service preserves prior progress
- **WHEN** `mohist-slack` is not installed or is offline after credentials have been saved
- **THEN** the Connection remains in Waiting for Slack service, retains the saved credentials and prior confirmed steps, and surfaces the service install or start action as the next step

#### Scenario: Service available advances past Waiting
- **WHEN** `mohist-slack` records a fresh heartbeat after a period offline
- **THEN** the Connection advances past Waiting for Slack service toward identity verification, without losing previously confirmed steps

#### Scenario: Invalid token surfaces a fixable step
- **WHEN** the saved token is invalid, the App and Bot do not belong to the same install, or a required scope is missing
- **THEN** the Connection enters Fix Slack setup, lists only the confirmed problems and the concrete remediation actions, and does not lose previously completed steps

#### Scenario: Agent not ready does not block setup
- **WHEN** the bound Agent is not yet ready while Slack setup otherwise advances
- **THEN** the Connection continues to advance its Slack setup steps independently of the Agent's readiness

### Requirement: mohist-slack is a CLI-managed service

The CLI SHALL manage the `mohist-slack` adapter as a service via `mo install slack`, `mo service status slack`, and `mo update slack`. The Slack target SHALL be a first-class service target alongside Server and Runner.

#### Scenario: Installing the slack service
- **WHEN** a user runs `mo install slack`
- **THEN** the `mohist-slack` adapter is installed as a managed service on the host

#### Scenario: Reporting slack service status
- **WHEN** a user runs `mo service status slack`
- **THEN** the command reports whether `mohist-slack` is installed and currently running

### Requirement: Identity is verified before owner claim becomes available

Before the Connection offers owner claim, Mohist SHALL verify that the saved credentials resolve to a consistent workspace, App, and Bot, and that the App grants the scopes required to receive DMs, look up workspace members, and post replies. A Connection whose identity cannot be verified MUST NOT advance to Claim owner and MUST NOT invite the user to wait for a DM that cannot be received.

#### Scenario: Verified identity unlocks claim
- **WHEN** credentials resolve to a single consistent workspace, App, and Bot with the required scopes
- **THEN** the Connection advances to Claim owner and exposes the claim action

#### Scenario: Inconsistent identity blocks claim
- **WHEN** the App and Bot do not belong to the same install or a required scope is missing
- **THEN** the Connection remains in Fix Slack setup and does not present the claim action

### Requirement: Owner claim uses a short-lived single-use code validated against workspace membership

Generating an owner claim code SHALL produce one short-lived, single-use code and SHALL immediately invalidate any previously generated code. A code SHALL be accepted only when it is sent in a DM to the bound Bot by a current regular (non-guest, non-bot, non-deactivated) member of the workspace. External collaborators, bots, deactivated members, and members of other workspaces MUST NOT be able to claim ownership. A successful claim SHALL establish exactly one Owner.

#### Scenario: A regular member claims ownership
- **WHEN** a current regular workspace member sends the valid one-time code in a DM to the Bot before it expires
- **THEN** that member becomes the Connection Owner

#### Scenario: Regeneration invalidates the prior code
- **WHEN** a new claim code is generated while a prior code is still valid
- **THEN** the prior code is immediately invalid and cannot be used to claim ownership

#### Scenario: Disqualified identities cannot claim
- **WHEN** a guest, a bot, a deactivated member, or a member of another workspace sends the claim code
- **THEN** the claim is rejected, no Owner is established, and no Agent resources are created

#### Scenario: An expired code cannot claim
- **WHEN** a code is used after its validity window has elapsed
- **THEN** the claim is rejected and the user is told to generate a new code

#### Scenario: A claim-code DM is treated as a claim, not a task
- **WHEN** an inbound DM's text matches a pending, unused owner-claim code
- **THEN** the DM is processed as a claim attempt and creates no AgentJob, AgentSession, or SessionInput

### Requirement: DM access is owner-only after claim

Once an Owner has been established, the Connection SHALL accept DM task dispatch only from the Owner. Any other workspace member who DMs the Bot SHALL receive an explicit rejection and Mohist MUST NOT create an AgentJob, AgentSession, SessionInput, AgentTurn, or provider inbox entry for that DM.

#### Scenario: Owner DM is processed
- **WHEN** the Owner sends a task DM to the Bot
- **THEN** the dispatch proceeds according to the DM dispatch contract

#### Scenario: Non-owner DM is rejected without side effects
- **WHEN** a workspace member who is not the Owner DMs the Bot
- **THEN** the member receives an explicit denial and no AgentJob, AgentSession, SessionInput, AgentTurn, or inbox entry is created
