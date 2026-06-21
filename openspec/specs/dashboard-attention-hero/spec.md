### Requirement: Attention Hero mounts in the Dashboard Attention slot

The Attention Hero SHALL be the view that renders inside the Dashboard `Attention` zone slot (defined by the `dashboard-shell` capability). The Hero SHALL be the only content rendered in the `Attention` slot; the Dashboard SHALL NOT render the generic zone placeholder for the `Attention` slot once the Hero is mounted. The `Attention` slot is the first (top-most) zone, so attention items are the first thing a returning user sees on the Dashboard.

#### Scenario: Attention slot renders the Hero instead of a placeholder

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the `Attention` slot SHALL render the Attention Hero view
- **AND** the `Attention` slot SHALL NOT render the generic empty zone placeholder

#### Scenario: Hero renders as the first zone

- **WHEN** the Dashboard page renders
- **THEN** the Attention Hero SHALL occupy the `Attention` slot, which is positioned as the first (top-most) zone
- **AND** the `Pulse`, `Productivity`, and `Digest` slots SHALL render after it

### Requirement: Hero state is derived from the shared attention derivation and agent status

The Attention Hero SHALL compute its two-state rendering exclusively from `deriveAttentionItems(issues, agentStatus)` (the shared Issue-context derivation defined by the `issue-attention-derivation` capability) together with the `useAgentStatus()` hook. The Hero SHALL NOT re-implement, override, or extend any of the four attention rules, SHALL NOT introduce new attention categories, and SHALL NOT mutate `Issue` state. Because the Hero consumes the same derivation as the Kanban widget, the set of surfaced items SHALL be identical to the items surfaced on Kanban cards for the same `(issues, agentStatus)` input.

#### Scenario: Hero consumes the shared derivation

- **WHEN** the Attention Hero source is inspected
- **THEN** its attention items SHALL come from `deriveAttentionItems` imported from the shared Issue-context module
- **AND** the Hero source SHALL NOT contain a local copy of any of the four attention rules

#### Scenario: Hero output matches Kanban for the same input

- **WHEN** the Hero and the Kanban widget are rendered with the same `(issues, agentStatus)` input
- **THEN** the set of attention items each surfaces SHALL be identical (same `issueId`, `label`, and `detail`)
- **AND** no issue SHALL appear in one surface but not the other

### Requirement: Has-attention state lists each attention item with a direct action

When `deriveAttentionItems` returns a non-empty list, the Hero SHALL render in its **has-attention** state. In this state the Hero SHALL render one entry per `AttentionItem`, in the evaluation order produced by the derivation, showing the item's `label` and `detail`. Each entry SHALL expose a direct action appropriate to the item: navigation to the corresponding issue detail view (available on every entry); an `Approve` action for items whose `label` is `Approval needed`; and a `Resume` action for items whose underlying issue accepts a resume call (for example `Interrupted`, `Needs action`, or `Integration failed` items). Activating an entry's primary action SHALL navigate to the issue detail view or invoke the corresponding endpoint (`POST /issues/{n}/resume`-class for Resume, the approval endpoint for Approve).

#### Scenario: Non-empty attention list renders has-attention state

- **WHEN** `deriveAttentionItems` returns one or more `AttentionItem` values
- **THEN** the Hero SHALL render the has-attention state
- **AND** the Hero SHALL NOT render the all-clear state

#### Scenario: Each item is listed with label and detail

- **WHEN** the Hero renders the has-attention state
- **THEN** it SHALL render one entry per `AttentionItem`
- **AND** each entry SHALL display the item's `label` and `detail`
- **AND** the entry order SHALL match the evaluation order returned by `deriveAttentionItems`

#### Scenario: Approval-needed item offers Approve

- **WHEN** the has-attention state contains an `AttentionItem` whose `label` is `Approval needed`
- **THEN** that entry SHALL expose an `Approve` action
- **AND** activating it SHALL invoke the approval endpoint for that issue

#### Scenario: Resumable item offers Resume

- **WHEN** the has-attention state contains an `AttentionItem` whose underlying issue accepts a resume call
- **THEN** that entry SHALL expose a `Resume` action
- **AND** activating it SHALL invoke the resume endpoint (`POST /issues/{n}/resume`-class) for that issue

#### Scenario: Entry navigation reaches issue detail

- **WHEN** a user activates an entry's navigation affordance
- **THEN** the application SHALL navigate to the corresponding issue detail view

### Requirement: Has-attention state surfaces a Runner-down entry when the runner is unavailable

When `agentStatus.runnerAvailable === false`, the Hero SHALL additionally render a Runner-down entry in the has-attention state, regardless of whether `deriveAttentionItems` returned issue-level items. The Runner-down entry SHALL be visually distinct from per-issue items and SHALL offer a path to runner status or diagnostics. The Hero SHALL treat `runnerAvailable` values other than `false` (including `undefined` or still-loading) as "runner available" and SHALL NOT render a Runner-down entry for them.

#### Scenario: Runner unavailable shows Runner-down entry

- **WHEN** `agentStatus.runnerAvailable === false`
- **THEN** the Hero SHALL render a Runner-down entry
- **AND** the entry SHALL be visually distinct from per-issue attention items

#### Scenario: Runner-down entry renders even with no issue items

- **WHEN** `agentStatus.runnerAvailable === false` and `deriveAttentionItems` returns an empty list
- **THEN** the Hero SHALL render the has-attention state containing the Runner-down entry
- **AND** the Hero SHALL NOT render the all-clear state

#### Scenario: Unknown or available runner does not show Runner-down

- **WHEN** `agentStatus.runnerAvailable` is `true` or `undefined` (for example still loading)
- **THEN** the Hero SHALL NOT render a Runner-down entry

### Requirement: All-clear state shows when nothing needs attention

When `deriveAttentionItems` returns an empty list AND `agentStatus.runnerAvailable` is not `false`, the Hero SHALL render its **all-clear** state. The all-clear state SHALL display an `All clear` message. The all-clear state SHALL include a placeholder affordance pointing to the Productivity preview; the placeholder SHALL render descriptive copy only and SHALL NOT implement Productivity content, which is owned by a downstream issue.

#### Scenario: Empty attention list and available runner render all-clear

- **WHEN** `deriveAttentionItems` returns an empty list and `agentStatus.runnerAvailable` is not `false`
- **THEN** the Hero SHALL render the all-clear state
- **AND** the Hero SHALL display an `All clear` message
- **AND** the Hero SHALL NOT render any per-issue entry or Runner-down entry

#### Scenario: All-clear state carries a Productivity placeholder

- **WHEN** the Hero renders the all-clear state
- **THEN** it SHALL render a placeholder affordance that points to the Productivity preview
- **AND** the placeholder SHALL NOT render live Productivity content, which remains owned by a downstream issue

### Requirement: Hero is a passive on-page surface with no notifications or pushes

The Attention Hero SHALL be a passive, on-page surface. It SHALL NOT initiate notifications, push messages, toasts, or other side effects beyond the direct actions users explicitly activate. Surfacing an attention item SHALL NOT, by itself, mutate workflow state.

#### Scenario: Surfacing an item does not trigger notifications

- **WHEN** the Hero renders an attention item
- **THEN** no notification, push, or toast SHALL be initiated solely because the item was surfaced
- **AND** workflow state SHALL NOT be mutated unless a user activates an explicit action on that item
