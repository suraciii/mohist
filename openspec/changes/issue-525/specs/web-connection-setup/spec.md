### Requirement: The Agent detail page exposes a Connections entry that creates a recoverable Connection

The Web SHALL surface a Connections section on the Agent detail page that lists the Agent's Connections and offers an **Add Slack** action. Choosing **Add Slack** SHALL immediately create a recoverable Slack Connection bound to that Agent, before any Slack-side work is done, so the Connection exists and can be resumed regardless of whether the page stays open.

#### Scenario: Add Slack creates a persistent Connection immediately
- **WHEN** an operator chooses Add Slack on an Agent's detail page
- **THEN** a Slack Connection bound to that Agent is created immediately and persists server-side, so the operator can resume it later even if the page is closed before any step is completed

#### Scenario: The Connections section lists the Agent's Connections
- **WHEN** an operator views an Agent's detail page that already has one or more Slack Connections
- **THEN** the Connections section lists those Connections and lets the operator open one to resume or inspect it

### Requirement: Creating a Connection presents a Bot identity preview derived from the bound Agent

Creating a Connection SHALL immediately present the Bot identity that will appear in Slack — name, short description, and avatar — derived from the bound Agent. When the Agent name does not satisfy Slack's naming rules, Mohist SHALL derive and preview a mention name with a stable suffix and SHALL NOT modify the Agent itself. A short App description SHALL be derived from the Agent description; when the description is empty, Mohist SHALL generate a non-empty generic description.

#### Scenario: Identity preview is derived from the Agent
- **WHEN** an operator creates a Connection for an Agent with a valid name and description
- **THEN** the Web shows the Bot name, App description, and avatar that will appear in Slack, all derived from the Agent

#### Scenario: Invalid Agent name yields a stable-suffix mention name without changing the Agent
- **WHEN** the bound Agent's name violates Slack's naming rules
- **THEN** Mohist previews a mention name carrying a stable suffix and does not modify the Agent

### Requirement: The create result provides an external Create in Slack entry Mohist does not perform

The create result SHALL provide a **Create in Slack** entry that leads the operator to create the private Slack App on Slack's side. Mohist SHALL NOT create the Slack App itself and SHALL NOT perform the workspace installation on the operator's behalf.

#### Scenario: Create in Slack points the operator to the external App creation
- **WHEN** a Connection is created from the Web
- **THEN** the Web presents a Create in Slack entry the operator follows to create the App on Slack, and Mohist does not create or install the App

### Requirement: Setup progress is owned by the server and is resumable across sessions and devices

The Web SHALL derive setup progress solely from the server-persisted setup state. Closing the page, refreshing, or returning on another device SHALL preserve every completed step and resume at the current step. The Web SHALL NOT maintain an independent client-side step state that can diverge from the server.

#### Scenario: Closing and reopening resumes at the current step
- **WHEN** an operator completes some steps, closes the page, and later returns to the same Connection (possibly on another device)
- **THEN** every previously completed step is preserved and the operator resumes at the current setup step rather than restarting

#### Scenario: Setup state is not held only in the browser
- **WHEN** the Connection is opened after the browser session or device changed
- **THEN** the displayed setup step reflects the server-persisted state, not a value held only in the previous browser session

### Requirement: Transient blocking conditions do not lose progress and surface a single next step

When `mohist-slack` is offline, the token is invalid, or the Agent is not yet Ready, the Web SHALL NOT lose setup progress and SHALL surface the single actionable next step. The operator SHALL be able to tell from the summary alone which boundary is blocked and what to do next.

#### Scenario: Service offline keeps progress and points to the service next step
- **WHEN** the credentials are configured but the Slack service is offline
- **THEN** the setup progress is retained and the Web surfaces the single next step to bring the service online, without restarting setup

#### Scenario: Invalid credentials keep progress and point to reconfiguration
- **WHEN** the configured token is invalid or the App and Bot do not belong to the same install
- **THEN** the setup progress is retained and the Web surfaces the single next step to replace or re-verify the credentials, without restarting setup

#### Scenario: Agent not Ready keeps Connection progress
- **WHEN** the Slack side is configured but the bound Agent is not yet Ready
- **THEN** the Connection setup progress is retained and the Web does not treat the Agent configuration gap as a setup failure that loses progress

### Requirement: The summary highlights one current state and one next action while keeping four facts readable

The Connection summary area SHALL highlight exactly one current state and exactly one next action at a time, so the operator never assembles a conclusion from raw fields. It SHALL additionally keep Setup progress, Desired state, Connection health, and Agent Readiness separately readable as independent facts, and SHALL NOT collapse them into a single covering status such as Connected.

#### Scenario: A single next action is highlighted
- **WHEN** an operator views a Connection whose setup is incomplete
- **THEN** the summary highlights the current state and the single next action, and the four independent facts remain available as detail without being merged into one status

#### Scenario: A healthy Connection with an unconfigured Agent reports both
- **WHEN** a Connection has completed setup and the Slack side is healthy but the bound Agent is not Ready
- **THEN** the summary highlights the Agent-needs-setup state as the current conclusion while still reporting the Connection's healthy setup independently, instead of a single covering status

### Requirement: The Web and the CLI operate the same Connection with one progress

The Web and the CLI SHALL be two entry points to the same Connection. A setup step completed through one entry SHALL immediately hold when the Connection is viewed through the other. The Web SHALL NOT establish a second local configuration or a second copy of the progress.

#### Scenario: A step completed in the CLI is reflected in the Web
- **WHEN** a step is completed through the CLI for a Connection that is also open in the Web
- **THEN** the Web reflects that completed step without the operator re-running it in the Web

#### Scenario: A step completed in the Web is reflected in the CLI
- **WHEN** a step is completed through the Web for a Connection
- **THEN** the CLI shows that completed step as already done for the same Connection

### Requirement: The owner claim step generates a one-time code claimed through the Bot

After the Connection identity is verified, the Web SHALL let the configurator generate a short-lived, single-use owner claim code. The code SHALL be shown once and SHALL NOT be re-displayed after the operator leaves the page; generating a new code SHALL immediately invalidate any prior unused code of the same kind. The claim itself SHALL be completed by sending the code in a direct message to the Bot, which also proves the App can receive and respond to direct messages.

#### Scenario: A claim code is shown once
- **WHEN** the configurator generates an owner claim code and then leaves the page
- **THEN** the code is not re-displayed on return, and a lost code is recovered only by generating a new one

#### Scenario: Regenerating invalidates the previous code
- **WHEN** the configurator generates a new owner claim code while a previous unused code exists
- **THEN** the previous unused code is immediately invalidated and can no longer be used to claim ownership

#### Scenario: Claim is completed through the Bot direct message
- **WHEN** the configurator sends the claim code to the Bot in a Slack direct message and the sender is an eligible workspace member
- **THEN** ownership is bound to that member and setup advances, demonstrating the App can receive direct messages
