### Requirement: Subscription is a first-class object owned by an Agent

The system SHALL treat an `AgentSubscription` as a first-class, independently addressable object that belongs to exactly one project-scoped `Agent`. An Agent SHALL be able to own many subscriptions (1 Agent : N subscriptions). A subscription SHALL be independently created, listed, fetched, updated, named, archived, restored, and deleted without mutating the owning Agent's definition, and without affecting sibling subscriptions except through explicit per-subscription operations.

A subscription SHALL NOT be embedded as a row inside the Agent definition; it SHALL be its own addressable entity resolvable within the owning Agent's scope.

#### Scenario: One Agent owns many independent subscriptions

- **WHEN** a user creates two subscriptions on the same Agent with different names
- **THEN** both subscriptions SHALL exist as distinct, independently addressable objects
- **AND** archiving, updating, or deleting one SHALL NOT mutate or delete the other
- **AND** neither SHALL alter the owning Agent's definition

#### Scenario: Subscription is scoped to its owning Agent and project

- **WHEN** a subscription is addressed outside the scope of its owning Agent or project
- **THEN** the subscription SHALL NOT be reachable
- **AND** operations on it SHALL be rejected as not-found

### Requirement: Subscription declares name, filter, response prompt, optional priority, and status

Each `AgentSubscription` SHALL declare: a `Name` (unique within the owning Agent), a `Filter` expression over CloudEvent envelope attributes, a `ResponsePrompt` (the second-layer prompt consumed at trigger time), an optional `Priority` (used for event-level arbitration, null meaning a default), and a `Status` of `active` or `archived`. The subscription SHALL also carry creation/update timestamps and project/agent identity metadata.

A subscription SHALL NOT carry an out-of-band "scope" field; targeting a specific issue or source SHALL be expressed inside the `Filter` expression.

#### Scenario: Creating a subscription with required fields

- **WHEN** a user creates a subscription with a name, a filter expression, and a response prompt on an Agent
- **THEN** the system SHALL persist the subscription with `Status = active`
- **AND** SHALL record the owning project id, agent id, and timestamps
- **AND** SHALL return the created subscription identity

#### Scenario: Name uniqueness within an Agent

- **WHEN** a user attempts to create two subscriptions with the same name on the same Agent
- **THEN** the second creation SHALL be rejected with a clear conflict error
- **AND** the original subscription SHALL remain unchanged

#### Scenario: Priority is optional

- **WHEN** a user creates a subscription without supplying a priority
- **THEN** the subscription SHALL be accepted and SHALL participate in arbitration using a default priority

#### Scenario: Targeting a specific issue is expressed in the filter

- **WHEN** a user wants a subscription to match only events for a specific issue
- **THEN** the user SHALL express that constraint inside the `Filter` expression (for example via the event `source`)
- **AND** the subscription model SHALL NOT provide a separate scope/range field for that purpose

### Requirement: Subscriptions support independent CRUD and active/archived state transitions

The system SHALL provide operations to create, list, fetch, update (mutate name/filter/response-prompt/priority), archive, restore, and delete a subscription, each scoped to the owning Agent. A subscription's `Status` SHALL transition `active → archived` (archive) and `archived → active` (restore). Archiving SHALL be reversible; deletion SHALL remove the subscription.

#### Scenario: Update mutates subscription fields without affecting others

- **WHEN** a user updates the filter or response prompt of one subscription
- **THEN** only that subscription's fields SHALL change
- **AND** its `UpdatedAt` SHALL advance
- **AND** sibling subscriptions and the owning Agent SHALL remain unchanged

#### Scenario: Archive and restore toggle status

- **WHEN** a user archives an active subscription and later restores it
- **THEN** archiving SHALL set `Status = archived`
- **AND** restoring SHALL set `Status = active`
- **AND** the subscription's identity and fields SHALL be preserved across both transitions

### Requirement: Lifecycle invariant — archived subscriptions do not trigger

An `archived` subscription SHALL NOT participate in event dispatch. When a CloudEvent arrives, the dispatch pipeline SHALL consider only subscriptions whose `Status` is `active`.

#### Scenario: Archived subscription is skipped on event arrival

- **WHEN** a CloudEvent arrives that would match an archived subscription's filter
- **THEN** that subscription SHALL NOT be considered for matching
- **AND** SHALL NOT trigger an Agent launch
- **AND** SHALL NOT be selected by arbitration even if no other subscription matches

### Requirement: Lifecycle invariant — archived Agent blocks new subscriptions and stops its existing subscriptions

When an Agent is in `archived` status, the system SHALL reject creation of new subscriptions on that Agent. Existing subscriptions owned by an archived Agent SHALL NOT trigger, regardless of their own status. Restoring the Agent to `active` SHALL re-enable triggering for its active subscriptions and SHALL re-allow creation of new subscriptions.

#### Scenario: Creating a subscription on an archived Agent is rejected

- **WHEN** a user attempts to create a subscription on an Agent whose status is `archived`
- **THEN** the system SHALL reject the creation with a clear error indicating the Agent is archived
- **AND** no subscription SHALL be persisted

#### Scenario: Existing subscriptions stop triggering when the owning Agent is archived

- **WHEN** an Agent is archived while it owns active subscriptions
- **THEN** those subscriptions SHALL NOT trigger on subsequent matching events for as long as the Agent remains archived
- **AND** the subscriptions themselves SHALL retain their own `active` status

#### Scenario: Restoring the Agent re-enables its active subscriptions

- **WHEN** an archived Agent is restored to `active`
- **THEN** its subscriptions whose own status is `active` SHALL become eligible to trigger again
- **AND** new subscriptions SHALL be accepted on that Agent again

### Requirement: Lifecycle invariant — archiving or deleting a subscription does not disturb already-running triggered sessions

Archiving or deleting a subscription SHALL affect only future triggers. A session that was already launched by that subscription and is still running SHALL be allowed to run to completion; the system SHALL NOT cancel, fail, or alter it as a consequence of the subscription's status change or deletion.

#### Scenario: Running session survives subscription archive

- **WHEN** a subscription that has already triggered a still-running Agent session is archived
- **THEN** the running session SHALL continue unaffected
- **AND** the session SHALL NOT be cancelled or failed by the archive operation

#### Scenario: Running session survives subscription deletion

- **WHEN** a subscription that has already triggered a still-running Agent session is deleted
- **THEN** the already-launched session SHALL continue to run to completion
- **AND** only future triggers from that subscription SHALL be prevented
