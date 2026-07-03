### Requirement: Single shared issue-loading prelude

The "load a project's issues and map them to read models" prelude (querying the project's issue rows, resolving the project default template id and disabled workflow profile ids, mapping each row through the consolidated `ToInfo`/`ToReadModel` path, and applying workflow and feedback projections) SHALL be defined exactly once as a shared helper. Every call site that needs this prelude MUST consume the shared helper rather than a private copy.

#### Scenario: Read-model list paths use the shared prelude

- **WHEN** the read-model list query or the in-progress-with-approval-gate query loads a project's issues and maps them to read models
- **THEN** it invokes the single shared prelude, not a private copy of the load-map-project block

#### Scenario: Metrics paths use the shared prelude

- **WHEN** a metrics method (quality, approval-wait, stage-duration) loads a project's issues and maps them to read models
- **THEN** it invokes the single shared prelude, not a private copy of the load-map-project block

### Requirement: Cross-service shared helper

The shared prelude MUST be a cross-service helper available to both the read-model query service and the metrics service. It MUST NOT be duplicated within each service.

#### Scenario: No duplicated load-and-map block exists

- **WHEN** the codebase is inspected for the load-issues → resolve-template/disabled-ids → map-to-read-models → apply-projections block
- **THEN** the block appears in exactly one location and is invoked by every former call site across both services

#### Scenario: Shared prelude produces identical read models

- **WHEN** the shared prelude loads and maps a project's issues
- **THEN** the resulting read models — including resolved workflow profile ids, workflow projections, and feedback — are identical to those produced by the inlined blocks before this change
