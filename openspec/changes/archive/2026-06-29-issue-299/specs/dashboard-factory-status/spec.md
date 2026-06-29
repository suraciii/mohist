## MODIFIED Requirements

### Requirement: Headline surfaces runner-online, in-flight, awaiting-approval, and today-shipped fields

The headline SHALL surface exactly these status fields, each derived from existing read-only frontend sources so that no new backend API endpoint is introduced: (1) **runner online** state derived from `agentStatus.runnerAvailable`; (2) **in-flight issue count** derived from the active issue query (`useIssues`) as the count of issues where `status === 'in_progress'` AND `health !== 'done'` AND `health !== 'cancelled'`; (3) **awaiting-approval count** derived from `useIssues` as the count of issues where `approvalState?.status === 'awaiting'`; (4) **issues shipped today** derived from `useIssues` as the count of issues where `status === 'done'` AND `completedAt` falls within the current calendar day. The headline SHALL present all four fields together so a user can read factory state in one pass.

#### Scenario: Each field is computed from its documented source

- **WHEN** the factory status headline renders
- **THEN** the runner-online field SHALL reflect `agentStatus.runnerAvailable`
- **AND** the in-flight count SHALL equal the number of issues with `status === 'in_progress'`, `health !== 'done'`, and `health !== 'cancelled'`
- **AND** the awaiting-approval count SHALL equal the number of issues with `approvalState?.status === 'awaiting'`
- **AND** the today-shipped count SHALL equal the number of issues with `status === 'done'` whose `completedAt` is within the current calendar day

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

The today-shipped field SHALL be computed from `status === 'done'` issues whose `completedAt` — the issue's persisted completion time — falls within the current calendar day. `completedAt` is the single source of "today", replacing the former `updatedAt` approximation; the forward-compatibility placeholder is now resolved. An issue whose `updatedAt` changes after completion (for example a post-completion comment, title edit, or label change) SHALL NOT be counted as shipped today, because the count is driven by `completedAt`, not by `updatedAt`.

#### Scenario: Today-shipped counts only done issues completed today

- **WHEN** there are `done` issues whose `completedAt` is today and `done` issues whose `completedAt` is a prior day
- **THEN** the today-shipped count SHALL include only the `done` issues completed today
- **AND** `done` issues completed on prior days SHALL NOT be counted

#### Scenario: Post-completion edit does not re-count a done issue as shipped today

- **WHEN** a `done` issue was completed on a prior day
- **AND** the issue's `updatedAt` is bumped to the current day by a post-completion edit
- **THEN** the issue SHALL NOT be counted in today-shipped
- **AND** the today-shipped count SHALL be driven by `completedAt`, not by `updatedAt`
