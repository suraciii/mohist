## ADDED Requirements

### Requirement: Decision surface uses a restrained background with a colored edge accent

The decision surface SHALL use a neutral (white/paper) background instead of a full-surface colored background. It SHALL convey the current runtime state using a colored accent on one edge (for example a left colored border) together with the status text and action buttons. Each runtime state (`running`, `queued`, `approval required`, `blocked`, `failed`, `done`) SHALL remain visually distinguishable from the others through the combination of edge accent and status text, without relying on a full-surface colored fill. The surface SHALL NOT stack multiple full-surface colored blocks that compete for the visual center of the first screen.

#### Scenario: Decision surface uses a neutral background with an edge accent

- **WHEN** Issue Detail renders the decision surface for any runtime state
- **THEN** the surface SHALL render a neutral background rather than a full-surface colored fill
- **AND** the surface SHALL render a colored accent on one edge to convey the state

#### Scenario: Each runtime state remains visually distinguishable

- **WHEN** Issue Detail renders the decision surface across `running`, `queued`, `approval required`, `blocked`, `failed`, and `done` states
- **THEN** each state SHALL be visually distinguishable from the others via its edge accent and status text
- **AND** distinguishability SHALL NOT depend on a full-surface colored background

#### Scenario: First screen avoids competing full-surface colored blocks

- **WHEN** Issue Detail renders the decision surface alongside other first-screen regions (for example convergence or interrupted indicators)
- **THEN** the surface SHALL NOT render as a full-surface colored block that competes for the visual center
- **AND** the first screen SHALL avoid stacking multiple full-surface colored blocks
