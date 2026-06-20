## MODIFIED Requirements

### Requirement: Workflow uses issue-aware model resolution

The effective coder agent configuration SHALL be fixed once, at issue creation, by generically merging the issue workflow profile's `Variables` from project-level and global-level `VariableBundle`s (project values win, global values fill gaps, symmetric for `vars` and each `stages.<stage>.vars`). Runtime workflow execution and issue-bound recovery sessions SHALL read that pre-merged `Variables` directly and SHALL NOT run a per-stage model fallback chain at execution time. `BuildVariables` SHALL return the pre-merged bundle (plus context variables) without recomputing agent config. Per-stage agent dispatch SHALL read the ordinary variable key `Variables.stages[stage].vars.agent`, falling back to `Variables.vars.agent` — both ordinary variable lookups with no agent-specific resolution code. A reasoning variant selected alongside a model SHALL be treated as part of that model's agent configuration: it SHALL be fixed once at issue creation by the same `Variables` merge, and per-stage agent dispatch SHALL carry the variant alongside the model when one is present.

#### Scenario: Effective agent is resolved at issue creation, not at runtime

- **WHEN** an issue is created
- **THEN** the issue workflow profile's `Variables` SHALL be populated by a generic merge of project and global `VariableBundle`s
- **AND** runtime `BuildVariables` SHALL return that pre-merged bundle (plus context variables) without recomputing agent config

#### Scenario: Stage override wins over default agent

- **WHEN** the merged `Variables.stages.build.vars.agent` defines a model
- **AND** `Variables.vars.agent` also defines a model
- **THEN** the build-stage coder session SHALL use the stage-scoped agent value
- **AND** the dispatch SHALL read it as the ordinary variable lookup `Variables.stages[stage].vars.agent`
- **AND** a variant accompanying the stage-scoped agent value SHALL be carried with it

#### Scenario: Default agent applies when no stage override exists

- **WHEN** no agent variable exists for the current stage in `Variables.stages`
- **AND** `Variables.vars.agent` defines a model
- **THEN** the coder session SHALL use `Variables.vars.agent`
- **AND** the fallback SHALL be an ordinary variable lookup with no cross-layer resolution

#### Scenario: Global configuration remains fallback through the T1 merge

- **WHEN** an agent variable is absent from the project `Variables`
- **AND** the global `Variables` provides it
- **THEN** the merged issue `Variables` SHALL contain the global value
- **AND** later changes to global config SHALL apply to newly created issues without repackaging the runtime resolution path

#### Scenario: Recovery sessions read the same pre-merged Variables

- **WHEN** conflict resolution or build-error-fix starts an issue-bound coder session
- **THEN** the session SHALL resolve its agent model from the issue's pre-merged `Variables`
- **AND** it SHALL NOT run an independent runtime fallback chain

#### Scenario: Variant is fixed at issue creation and dispatched with its model

- **WHEN** an issue is created with an agent model that carries a reasoning variant
- **THEN** the variant SHALL be captured in the issue's pre-merged `Variables` alongside the model at creation time
- **AND** per-stage coder session dispatch SHALL carry the variant alongside the model to the runner
- **AND** the variant SHALL NOT be recomputed at runtime dispatch time
