# issue-detail-reading-flow Specification

## Requirements

### Requirement: Attention-Ordered Block Sequence

The reading flow SHALL own an attention-ordered sequence of content blocks: workflow progress and outputs first, then changes/diff, then commits, then the description, then comments. This order SHALL hold whenever the relevant blocks are present.

#### Scenario: All content blocks are present

- **WHEN** the reading flow renders with workflow progress, outputs, diff, commits, description, and comments all present
- **THEN** the workflow progress and outputs block precedes the changes/diff block
- **AND** the changes/diff block precedes the commits block
- **AND** the commits block precedes the description block
- **AND** the description block precedes the comments block

#### Scenario: Workflow progress precedes description and comments

- **WHEN** the reading flow renders workflow progress alongside the description and comments
- **THEN** the workflow progress appears before the description
- **AND** the description appears before the comments

### Requirement: Maximum-Width Main Column

On desktop the reading flow SHALL be the maximum-width body column, so the primary work content has the most room and the peripheral reference rail is constrained to a narrower column.

#### Scenario: Desktop layout column widths

- **WHEN** the detail page renders on a desktop viewport
- **THEN** the reading flow column is wider than the reference rail column
- **AND** the reading flow occupies the largest share of the body width

### Requirement: Lightest Container Treatment

The reading flow SHALL present its blocks with the lightest container chrome — content-forward and free of heavy card borders or fills — so attention rests on the content rather than on containers.

#### Scenario: Reading-flow blocks avoid heavy chrome

- **WHEN** the reading flow renders its content blocks
- **THEN** those blocks do not carry heavier card chrome than the reference-rail items
- **AND** the content is presented directly rather than wrapped in heavy bordered cards

### Requirement: Medium Visual-Weight Tier

The reading flow SHALL be the medium visual-weight tier: lighter than the status headline but heavier than the reference rail.

#### Scenario: Tier weight between header and rail

- **WHEN** the three tiers render together
- **THEN** the reading flow is visually lighter than the status headline
- **AND** the reading flow is visually heavier than the reference rail

### Requirement: Collapsible Long Blocks Preserve Key Signal

Long reading-flow blocks that are collapsible — including the description and the change/diff list — SHALL remain readable when collapsed by preserving a key signal of their content, rather than disappearing entirely.

#### Scenario: Long description is collapsed

- **WHEN** a long description block is collapsed
- **THEN** a signal that the description exists and a hint of its content remains visible
- **AND** the block does not vanish from the reading flow

#### Scenario: Change list is collapsed

- **WHEN** the change/diff list is collapsed
- **THEN** the file, addition, and deletion counts remain visible
- **AND** the reader can still see the scale of the change without expanding it

### Requirement: Decision Surface and Reference Content Excluded

The reading flow SHALL contain only the work-content sequence above. It SHALL NOT contain the runtime decision/action surface (which belongs to the status-header tier) nor the metadata and low-frequency configuration (which belongs to the reference rail).

#### Scenario: Decision surface is not in the reading flow

- **WHEN** the reading flow renders
- **THEN** the runtime decision/action surface is not placed inside the reading flow
- **AND** the metadata, model, workflow-profile control, prerequisites, drift, and convergence blocks are not placed inside the reading flow
