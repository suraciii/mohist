## MODIFIED Requirements

### Requirement: REQ-WUI-209-001 Homepage is a decision-first work entry

The Issues page SHALL surface user-actionable work before the Kanban board by rendering a compact `Needs attention` summary above the board when actionable items exist. The summary SHALL derive from existing issue and agent data and use user-facing decision labels rather than raw internal state names. This behavior now lives on the Issues route that hosts the Kanban board, not on the default landing page (the Dashboard). The derivation that produces the attention list SHALL be sourced from the shared Issue-context public API (the `issue-attention-derivation` capability), and SHALL NOT be re-implemented in widget-local code. Behaviour, labels, and ordering MUST remain identical to the prior widget-local implementation.

#### Scenario: Homepage surfaces actionable issues first

- **WHEN** the Issues page contains issues awaiting approval, interrupted issues, blocked issues, integrate failures, or done issues that are not merged
- **THEN** the page shows a `Needs attention` summary above the board
- **AND** each summary item uses user-action language such as `Approval needed`, `Integration failed`, `Interrupted`, `Needs action`, or `Not merged`
- **AND** optional detail text may explain the secondary reason without replacing the primary action label

#### Scenario: Attention summary does not replace board navigation

- **WHEN** a user selects an item in the `Needs attention` summary
- **THEN** the user can open the relevant issue directly
- **AND** the Kanban board remains available below as the main browsing surface

#### Scenario: Kanban widget imports attention derivation from the shared Issue context

- **WHEN** the Kanban widget source is inspected
- **THEN** it imports `deriveAttentionItems` and `AttentionItem` from the shared Issue-context public API rather than from a widget-local model file
- **AND** the prior widget-local `homepage-attention.ts` derivation module has been removed

#### Scenario: Attention summary output is unchanged after the move

- **WHEN** the Issues page renders the `Needs attention` summary after the derivation has been relocated
- **THEN** the rendered summary items are identical (same `issueId`, `issueNumber`, `label`, and `detail`) to what the prior widget-local implementation would have produced for the same input
