### Requirement: Lineage stamped on every event family at production time

Every event family SHALL stamp its full business lineage into CloudEvents envelope extensions at the moment the event is produced, using only identity the producing aggregate already holds (its own state or existing annotations/labels). The stamped attributes per family SHALL conform to the lineage matrix:

- `workflow.*` events SHALL carry `workflowrunid` and `projectid`; they SHALL carry `issueid`, `issue` (issue number), and `epicid` when that affiliation is present at emit time.
- `workflow.stage.*`, `workflow.task.*`, `workflow.check.*`, and `workflow.feedback.requested` events SHALL additionally carry `stage` (the stage name), because their domain event records are structurally stage-bearing.
- `issue.*` events SHALL carry `projectid`, `issueid`, and `issue` (issue number); they SHALL carry `epicid` when the issue belongs to an epic at emit time.
- `epic.*` events SHALL carry `projectid` and `epicid`.
- `agent-session.*` events SHALL carry `projectid` and `sessionid`; they SHALL carry `agentid` when the session originates from an agent, and `issue`, `workflowrunid`, and `stage` when the session originates from a workflow/issue.
- `runner.*` events SHALL carry `runnerid`; they SHALL carry `projectid` when present.
- The inbox-synthesized event (`com.mohist.inbox.item-persisted`) SHALL lift `issue` and `issueid` already present in its hint payload onto extensions, alongside `projectid`.

#### Scenario: Workflow run events carry run identity and issue lineage

- **WHEN** a `workflow.run.*` event is produced for a run whose metadata annotations carry a project id, issue id, and issue number
- **THEN** the emitted envelope extensions contain `projectid`, `issueid`, `issue`, and `workflowrunid`

#### Scenario: Stage, task, check, and feedback-requested events carry the stage name

- **WHEN** a `workflow.stage.*`, `workflow.task.*`, `workflow.check.*`, or `workflow.feedback.requested` event is produced
- **THEN** the emitted envelope extensions contain `stage` set to the stage name, in addition to the `workflow.*` lineage attributes

#### Scenario: Issue events carry epic lineage when affiliated

- **WHEN** an `issue.*` event is produced for an issue that belongs to an epic at emit time
- **THEN** the emitted envelope extensions contain `epicid` alongside `projectid`, `issueid`, and `issue`

#### Scenario: Agent-session events carry their origin lineage

- **WHEN** an `agent-session.*` event is produced by a session whose metadata labels carry a project id, agent id, and an agent source kind
- **THEN** the emitted envelope extensions contain `projectid`, `sessionid`, and `agentid`
- **WHEN** that session originates from a workflow run and its labels carry the issue number, workflow run id, and stage
- **THEN** the extensions additionally contain `issue`, `workflowrunid`, and `stage`

#### Scenario: Epic events carry project and epic identity only

- **WHEN** an `epic.*` event is produced
- **THEN** the emitted envelope extensions contain `projectid` and `epicid`, and SHALL NOT carry `epicno`

#### Scenario: Inbox-synthesized event lifts issue lineage

- **WHEN** the inbox projection synthesizes a `com.mohist.inbox.item-persisted` event whose hint carries a project id, issue id, and issue number
- **THEN** the emitted envelope extensions contain `projectid`, `issueid`, and `issue`

### Requirement: Absent affiliation is omitted, never empty

When a lineage affiliation does not exist at emit time, the corresponding extension attribute SHALL be omitted entirely. Producers SHALL NOT stamp an empty string, null, or placeholder value for any absent affiliation.

#### Scenario: Unaffiliated issue omits epic id

- **WHEN** an `issue.*` event is produced for an issue that does not belong to any epic
- **THEN** the envelope extensions contain no `epicid` key

#### Scenario: Workflow run without issue annotation omits issue attributes

- **WHEN** a `workflow.*` event is produced for a run whose metadata has no issue id or issue number annotation
- **THEN** the envelope extensions omit `issueid` and `issue`, while still carrying `projectid` and `workflowrunid`

### Requirement: Lineage is a production-time snapshot

Lineage attributes SHALL record affiliation as it exists at the instant the event is produced. Later relationship changes (an issue moving to a different epic, a workflow run's issue annotation changing) SHALL NOT rewrite the attributes already stamped on historical events. No backfill of historical events SHALL occur.

#### Scenario: Moving an issue to another epic does not change past events

- **WHEN** an issue that previously emitted events while unaffiliated is later linked to an epic
- **THEN** the previously emitted events retain their original extensions (no `epicid`), and only events produced after the linking carry `epicid`

### Requirement: Stamping uses only identity the aggregate already holds

Producers SHALL NOT issue cross-aggregate queries to gather lineage for stamping. Lineage SHALL be derived solely from the producing aggregate's own state or from annotations/labels already attached to it.

#### Scenario: Workflow store stamps from run annotations without loading the issue

- **WHEN** the workflow run store produces an event for a run
- **THEN** it derives `projectid`, `issueid`, and `issue` from the run's own metadata annotations, and does not load the issue aggregate to stamp them

#### Scenario: Issue store stamps epicid from its own state, not a membership lookup

- **WHEN** the issue store produces an event for an issue whose own state carries an `EpicId`
- **THEN** it stamps `epicid` from that own state, and issues no query against the epic-issue membership table (or any other aggregate) to gather it

### Requirement: User-visible identity uses short names; internal ids carry the id suffix

Lineage attribute names SHALL be stable and conform to the protocol naming: the user-visible issue number is `issue` (not `issueno`), and internal identifiers carry the `id` suffix (`issueid`, `epicid`, `workflowrunid`, `agentid`, `sessionid`, `runnerid`, `projectid`). The legacy `issueno` attribute name SHALL be replaced by `issue`, and `epicno` SHALL be removed.

#### Scenario: Issue events use issue, not issueno

- **WHEN** an `issue.*` event is produced
- **THEN** the issue number appears under the extension key `issue`, and no `issueno` key is present

#### Scenario: Epic events no longer carry epicno

- **WHEN** an `epic.*` event is produced
- **THEN** the extensions contain `epicid` and contain no `epicno` key

#### Scenario: Consumers reading the renamed attribute are reconciled

- **WHEN** a server handler, the inbox projection, or the web envelope reader resolves an issue number from extensions
- **THEN** it reads the `issue` key rather than `issueno`, so that lineage routing remains consistent with the stamped name
