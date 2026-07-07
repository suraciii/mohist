### Requirement: Web UI exposes a Subscriptions section on the Agent detail page

The Web UI SHALL provide a Subscriptions section on the Agent detail page that lists every subscription owned by that Agent (both `active` and `archived`) and lets the user create, archive, restore, and delete subscriptions inline. The section SHALL surface each subscription's name, filter expression, response prompt (at least a preview), priority (or "default"), and status. Operations performed in this section SHALL target the same subscription store backing the API and CLI.

#### Scenario: User lists subscriptions for an Agent

- **WHEN** a user opens an Agent's detail page
- **THEN** the Subscriptions section SHALL list that Agent's subscriptions
- **AND** SHALL show each subscription's name, filter, priority, and status

#### Scenario: User creates a subscription from the detail page

- **WHEN** a user creates a subscription in the Subscriptions section with a name, filter, and response prompt
- **THEN** the subscription SHALL be persisted against the owning Agent with `Status = active`
- **AND** the new subscription SHALL appear in the list without a manual refresh

#### Scenario: User archives, restores, and deletes from the detail page

- **WHEN** a user archives an active subscription, restores an archived one, or deletes one via the Subscriptions section
- **THEN** the affected subscription's status (or existence) SHALL update accordingly
- **AND** the change SHALL be reflected in the list

### Requirement: CLI provides create, list, and delete subscription commands

The CLI SHALL provide commands to create, list, and delete Agent subscriptions, scoped to a project and addressing the owning Agent by name or id (consistent with existing `mo agent` commands). The create command SHALL accept the subscription name, filter expression, response prompt (inline, from file, or from stdin), and an optional priority. The list command SHALL enumerate subscriptions for an Agent. The delete command SHALL remove a subscription by identity.

#### Scenario: Create a subscription via CLI

- **WHEN** a user runs the subscription create command against an Agent with a name, filter, response prompt, and optional priority
- **THEN** the CLI SHALL create the subscription on that Agent and SHALL print the created subscription identity

#### Scenario: List subscriptions for an Agent via CLI

- **WHEN** a user runs the subscription list command for an Agent
- **THEN** the CLI SHALL enumerate that Agent's subscriptions with their name, filter, priority, and status

#### Scenario: Delete a subscription via CLI

- **WHEN** a user runs the subscription delete command addressing a subscription
- **THEN** the CLI SHALL delete that subscription
- **AND** subsequent triggers SHALL NOT consider it

### Requirement: Configuration surfaces share Agent resolution and scoping with existing Agent commands

Both the Web UI Subscriptions section and the CLI subscription commands SHALL resolve the owning Agent using the same project-scoped identity resolution (agent id or name) as the existing Agent CRUD commands. Subscription operations SHALL be rejected when the owning Agent does not exist, and SHALL be rejected when the owning Agent is `archived` (for creation). The configuration surfaces SHALL NOT introduce a separate identity or scoping model distinct from the Agent's.

#### Scenario: Subscription commands resolve the Agent the same way as Agent CRUD

- **WHEN** a user addresses an Agent by name or by id in a subscription command
- **THEN** the resolution rules SHALL match those of the existing Agent CRUD commands (id-prefixed → by id; otherwise by name then id)
- **AND** a non-existent Agent SHALL produce a clear not-found error

#### Scenario: Creating a subscription on an archived Agent is rejected from the configuration surfaces

- **WHEN** a user attempts to create a subscription from the Web UI or CLI against an Agent whose status is `archived`
- **THEN** the operation SHALL be rejected with a clear error indicating the Agent is archived
- **AND** no subscription SHALL be persisted
