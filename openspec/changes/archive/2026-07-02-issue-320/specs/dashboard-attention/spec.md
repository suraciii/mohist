## ADDED Requirements

### Requirement: Attention All-clear state excludes the stale productivity-preview placeholder

When the Attention Hero renders its All-clear state (no attention items and the runner is available), the state SHALL render only the all-clear message and the `ApprovalWaitSummary`. The All-clear state SHALL NOT render a productivity-preview placeholder block (e.g. a "Productivity preview will appear here once it ships." block) or any other transitional placeholder advertising future Productivity content; Productivity is already rendered as its own zone directly beneath the Hero, so such a placeholder is stale and SHALL be treated as an anti-regression guard.

#### Scenario: All-clear renders message and approval summary only

- **WHEN** the Attention Hero renders the All-clear state with resolved issue data, no attention items, and `agentStatus.runnerAvailable !== false`
- **THEN** the Hero SHALL render the all-clear message and the `ApprovalWaitSummary`
- **AND** the Hero SHALL NOT render a productivity-preview placeholder block

#### Scenario: All-clear does not advertise upcoming Productivity content

- **WHEN** the Attention Hero renders the All-clear state
- **THEN** the state SHALL NOT render any text or block advertising that a Productivity preview will appear in the future
- **AND** the Productivity zone beneath the Hero remains the sole surface for Productivity content
