### Requirement: Purpose-aware model recommendations during authoring

When a user chooses a model during Agent creation or edit, the authoring surface SHALL present understandable model recommendations keyed to the task's purpose. Each recommendation SHALL be presented in task language — what the model is good at and when to choose it — rather than as a bare `provider/model` identifier, and the recommendation set SHALL reflect the runtime selected for the Agent.

#### Scenario: Recommendations explain their task fit

- **WHEN** a user reaches model selection while creating or editing an Agent in the Web editor or through the CLI model surface
- **THEN** the surface SHALL present recommended models, each with a plain-language description of its task fit
- **AND** the recommendations SHALL NOT be limited to bare `provider/model` identifiers

#### Scenario: Recommendations are keyed to the stated purpose

- **WHEN** the user has stated the Agent's task purpose
- **THEN** the recommendations presented SHALL be those matched to that purpose
- **AND** the surface SHALL make the match understandable without requiring model-catalog knowledge

#### Scenario: Recommendations follow the selected runtime

- **WHEN** the user switches the Agent's execution runtime during authoring
- **THEN** the recommendations SHALL refresh to models usable by the newly selected runtime
- **AND** a recommendation for the previous runtime MUST NOT remain offered as a recommendation

### Requirement: The full model catalog remains reachable

Model guidance SHALL NOT restrict choice. The full model catalog MUST remain reachable from Agent creation and edit — the full model picker in the Web editor and `mo agent model list` in the CLI — and the user SHALL be able to select any catalog model, including models that carry no recommendation.

#### Scenario: Choosing outside the recommendations

- **WHEN** a user selects a catalog model that is not among the presented recommendations
- **THEN** the selection SHALL be accepted and persisted as the Agent's model

#### Scenario: The catalog entry stays available

- **WHEN** a user runs `mo agent model list` or opens the full model picker in the Web editor
- **THEN** the full model catalog SHALL be listed for the selected runtime
- **AND** a model reachable only through the catalog SHALL remain selectable for the definition

### Requirement: Guidance is advisory, not validation

Model recommendations are advice, not acceptance criteria. The Server MUST NOT reject or flag an Agent definition because its model is unrecommended, and the user SHALL be able to save an Agent without choosing a model. A missing model SHALL surface through the Agent's executability diagnosis — not through the save operation.

#### Scenario: Saving without a model

- **WHEN** a user saves a new Agent without selecting a model
- **THEN** the save SHALL succeed
- **AND** the Agent's executability SHALL be `not-configured` with a model gap and its next action

#### Scenario: An unrecommended model is not an error

- **WHEN** a user saves an Agent whose model is a valid catalog model without a recommendation
- **THEN** the Server SHALL accept the definition
- **AND** the executability derivation MUST NOT treat the unrecommended choice as a gap
