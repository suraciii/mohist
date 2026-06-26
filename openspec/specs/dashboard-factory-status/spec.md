### Requirement: Factory status headline is pinned full-width atop the dashboard

The Dashboard SHALL render a full-width **factory status headline** as the topmost first-screen element, spanning the full content width above every zone and above the Attention Hero. The headline MUST be the first dashboard element visible without scrolling so a user can judge factory health ("can I walk away / do I need to step in now") in a single glance. The headline SHALL render whenever the Dashboard renders for a project that has at least one project; it SHALL NOT be hidden by empty data.

#### Scenario: Headline renders above all zones full-width

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the page SHALL render the factory status headline as the topmost dashboard element
- **AND** the headline SHALL span the full content width
- **AND** the headline SHALL appear above the Attention Hero and above the `Pulse`, `Productivity`, and `Digest` zones

#### Scenario: Headline renders even with no activity

- **WHEN** the Dashboard page renders and there are no in-flight issues, no awaiting-approval issues, and nothing shipped today
- **THEN** the factory status headline SHALL still render at the top of the dashboard
- **AND** its field values SHALL reflect zero counts rather than the headline being omitted

### Requirement: Headline surfaces runner-online, in-flight, awaiting-approval, and today-shipped fields

The headline SHALL surface exactly these status fields, each derived from existing read-only frontend sources so that no new backend API endpoint is introduced: (1) **runner online** state derived from `agentStatus.runnerAvailable`; (2) **in-flight issue count** derived from the active issue query (`useIssues`) as the count of issues where `status === 'in_progress'` AND `health !== 'done'` AND `health !== 'cancelled'`; (3) **awaiting-approval count** derived from `useIssues` as the count of issues where `approvalState?.status === 'awaiting'`; (4) **issues shipped today** derived from `useIssues` as the count of issues where `status === 'done'` AND `updatedAt` falls within the current calendar day. The headline SHALL present all four fields together so a user can read factory state in one pass.

#### Scenario: Each field is computed from its documented source

- **WHEN** the factory status headline renders
- **THEN** the runner-online field SHALL reflect `agentStatus.runnerAvailable`
- **AND** the in-flight count SHALL equal the number of issues with `status === 'in_progress'`, `health !== 'done'`, and `health !== 'cancelled'`
- **AND** the awaiting-approval count SHALL equal the number of issues with `approvalState?.status === 'awaiting'`
- **AND** the today-shipped count SHALL equal the number of issues with `status === 'done'` whose `updatedAt` is within the current calendar day

#### Scenario: Runner online state is surfaced from agent status

- **WHEN** `agentStatus.runnerAvailable` is `true`
- **THEN** the runner-online field SHALL indicate the runner is available
- **WHEN** `agentStatus.runnerAvailable` is `false`
- **THEN** the runner-online field SHALL indicate the runner is unavailable

#### Scenario: Headline is read-only with respect to domain state

- **WHEN** the factory status headline renders and refreshes its data
- **THEN** the headline SHALL consume only existing read-only query sources
- **AND** the headline SHALL NOT introduce any new backend API endpoint
- **AND** the headline SHALL NOT perform any write or mutation against issue, activity, or agent domain state

### Requirement: Today-shipped count uses updatedAt today and is forward-compatible with completedAt

The today-shipped field SHALL be computed from `status === 'done'` issues whose `updatedAt` falls within the current calendar day. Because a dedicated `completedAt` timestamp is not yet available, `updatedAt` SHALL be used as the source of "today" until that field lands; the derivation SHALL be isolated so that migrating from `updatedAt` to `completedAt` is a source change that does not alter the field's user-visible contract.

#### Scenario: Today-shipped counts only done issues updated today

- **WHEN** there are `done` issues whose `updatedAt` is today and `done` issues whose `updatedAt` is a prior day
- **THEN** the today-shipped count SHALL include only the `done` issues updated today
- **AND** `done` issues updated on prior days SHALL NOT be counted

### Requirement: Headline reserves a today-cost field slot that ships empty

The headline SHALL reserve a **today-cost** field slot positioned alongside the other status fields. This slot SHALL ship empty in this change because the cost rollup endpoint (epic issue #262) is not yet available; the empty slot SHALL NOT block delivery of the other headline fields. The slot SHALL be wired so that once the rollup endpoint lands, the today-cost value can be connected without restructuring the headline layout. The empty slot SHALL render in a way that is visibly distinct from a zero-cost value.

#### Scenario: Today-cost slot ships empty without blocking other fields

- **WHEN** the factory status headline renders in this change
- **THEN** the runner-online, in-flight, awaiting-approval, and today-shipped fields SHALL render with real values
- **AND** the today-cost slot SHALL render empty (or as a reserved placeholder) rather than a numeric value

#### Scenario: Empty today-cost is distinct from a zero value

- **WHEN** the today-cost rollup endpoint is not yet available
- **THEN** the today-cost slot SHALL render an empty/reserved placeholder
- **AND** the slot SHALL NOT display a numeric zero that could be mistaken for an actual computed cost

#### Scenario: Today-cost slot is ready to receive the rollup value later

- **WHEN** the cost rollup endpoint (issue #262) becomes available
- **THEN** the today-cost slot SHALL be connectable to that endpoint without changing the headline layout or the other fields
