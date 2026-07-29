### Requirement: Quality stage set derives from the Definition
Quality metric windows SHALL derive the set of stages from the WorkflowDefinition bound to the Run(s) being aggregated, not from a hardcoded builtin stage-name constant. A stage that does not appear in the relevant Definition SHALL NOT be present in the quality window output.

#### Scenario: Custom profile stages appear without builtin ghosts
- **WHEN** a delivered issue ran under a custom WorkflowDefinition whose stages are `explore`, `implement`, `review`
- **AND** the quality window aggregates that issue
- **THEN** the window SHALL contain exactly `explore`, `implement`, `review`
- **AND** SHALL NOT contain `plan`, `build`, `check`, or `integrate` as entered=0 phantom rows

#### Scenario: Builtin profile metrics are unchanged
- **WHEN** a delivered issue ran under the builtin profile whose stages are `plan`, `build`, `check`, `integrate`
- **THEN** the quality window SHALL list those four stages in that order with the same entered and rework counts as before this change

### Requirement: Stage order follows Definition order
Quality metric windows SHALL present stages in the order defined by the Definition's `Stages` list. The output SHALL NOT fall back to alphabetical ordering for stages whose Definition position is known.

#### Scenario: Custom stage order follows the Definition, not alphabetical
- **WHEN** a delivered issue ran under a custom Definition with stages declared as `zebra`, `alpha`, `mid`
- **AND** all three stages were entered during the run
- **THEN** the quality window SHALL list the stages as `zebra`, `alpha`, `mid`
- **AND** SHALL NOT reorder them alphabetically as `alpha`, `mid`, `zebra`

### Requirement: Cross-profile aggregation groups by stage id in Definition order
When a quality window aggregates delivered issues that ran under different Definitions, the stage set SHALL be the union of stage ids actually entered across those issues. Each stage id SHALL appear once, ordered by its position in the project's effective profile Definition; stages absent from that Definition SHALL be appended after the Definition-ordered stages.

#### Scenario: Window spans issues with different stage sets
- **WHEN** a quality window aggregates one issue whose Definition has stages `plan`, `build`, `check` and another whose Definition has stages `plan`, `build`, `verify`
- **AND** all four distinct stages were entered
- **THEN** the window SHALL list each distinct stage id once
- **AND** stages present in the effective profile Definition SHALL appear in their Definition order
- **AND** the remaining observed stages SHALL appear after them
