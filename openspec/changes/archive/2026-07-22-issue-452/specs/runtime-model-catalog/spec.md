### Requirement: Runner reports per-runtime catalogs
The Runner SHALL report the Pi model catalog alongside the OpenCode catalog, each tagged by its runtime. The Pi catalog SHALL contain only models whose provider has configured credentials.

#### Scenario: Pi catalog reported alongside OpenCode
- **WHEN** a Runner with both runtimes ready registers with the Server
- **THEN** it SHALL report both the OpenCode catalog and the Pi catalog, each tagged by runtime

#### Scenario: Pi catalog excludes uncredentialed models
- **WHEN** the Pi runtime has providers that lack configured credentials
- **THEN** the reported Pi catalog SHALL contain only models from providers with configured credentials

### Requirement: Catalog API serves models by runtime
The model catalog API SHALL serve models by runtime so a caller receives the catalog for the requested backend.

#### Scenario: Query returns the runtime-specific catalog
- **WHEN** the catalog API is queried for runtime `pi`
- **THEN** it SHALL return the Pi catalog, not the OpenCode catalog

#### Scenario: Query without a runtime defaults to OpenCode
- **WHEN** the catalog API is queried without specifying a runtime
- **THEN** it SHALL return the OpenCode catalog

### Requirement: Web backend selector drives the model list
The Mohist Agent editor SHALL present an execution-backend selector that determines which runtime catalog feeds the model picker.

#### Scenario: Selecting Pi shows Pi models
- **WHEN** the editor's backend selector is set to `pi`
- **THEN** the model picker SHALL list models from the Pi catalog

#### Scenario: Selecting OpenCode shows OpenCode models
- **WHEN** the editor's backend selector is set to `opencode`
- **THEN** the model picker SHALL list models from the OpenCode catalog

### Requirement: Issue and stage model selection grouped by backend
Issue-level and per-stage model selection SHALL present models for the selected backend. The Pi group SHALL show only configured-credential models.

#### Scenario: Issue model selection reflects the selected backend
- **WHEN** the issue model selector's backend is set to `pi`
- **THEN** the listed models SHALL come from the Pi catalog and SHALL include only configured-credential models

#### Scenario: Per-stage model selection reflects the selected backend
- **WHEN** a stage's backend is set to `opencode`
- **THEN** that stage's model list SHALL come from the OpenCode catalog

### Requirement: Catalog is a configuration aid only
The catalog SHALL be a configuration aid. Whether a selected model is valid SHALL be finally validated by the execution backend at turn time, not pre-asserted solely from catalog presence.

#### Scenario: Catalog absence does not by itself reject a model
- **WHEN** a model is absent from the reported catalog but its provider has valid credentials at execution time
- **THEN** the model's legality SHALL be determined by the execution backend at turn time, not rejected solely because it was absent from the catalog
