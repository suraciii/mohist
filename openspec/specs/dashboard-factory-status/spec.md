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

The headline SHALL surface a **today-cost** field positioned alongside the runner-online, in-flight, awaiting-approval, and today-shipped fields. The field SHALL be populated from the project's agent cost rollup endpoint (`agent-cost-metrics` `todayCost`) - the slot that previously shipped empty pending that endpoint is now connected to the rollup value. The headline SHALL source the value from the rollup endpoint rather than recomputing it over the local session set. The empty/zero-sample case (the rollup returning no sessions with usage for the current day) SHALL render in a way that is visibly distinct from a literal zero-cost value, so a missing or empty rollup is not mistaken for free operation; a genuine `todayCost` of zero produced by sessions with usage that summed to zero SHALL render as a real numeric zero, distinct from the empty case.

#### Scenario: Today-cost field is populated from the rollup endpoint

- **WHEN** the factory status headline renders and the agent cost rollup endpoint returns a `todayCost` value with a non-empty sample
- **THEN** the today-cost field SHALL display that numeric `todayCost` value
- **AND** the value SHALL come from the rollup endpoint rather than being recomputed locally over the session set
- **AND** the runner-online, in-flight, awaiting-approval, and today-shipped fields SHALL continue to render their real values

#### Scenario: Empty today-cost is distinct from a zero value

- **WHEN** the agent cost rollup returns the empty/zero-sample result for `todayCost` (no sessions with usage for the current day)
- **THEN** the today-cost field SHALL render an empty/no-data placeholder
- **AND** the slot SHALL NOT display a numeric zero that could be mistaken for an actual computed cost

#### Scenario: Genuine zero today-cost renders as a real zero

- **WHEN** the agent cost rollup returns a `todayCost` of zero produced by sessions with usage that summed to zero
- **THEN** the today-cost field SHALL render a numeric zero
- **AND** it SHALL be distinguishable from the empty/zero-sample placeholder
