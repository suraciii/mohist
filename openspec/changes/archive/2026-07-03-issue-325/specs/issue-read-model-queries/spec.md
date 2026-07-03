### Requirement: Read-model query ownership

The Issue read-model query service (`IssueQuerier`) SHALL own only synchronous read-model concerns: listing a project's issues, fetching a single issue's detail, enriching read models, and the workflow-run → issue reverse lookup. It MUST NOT own any analytics/metrics aggregation concern.

#### Scenario: List query returns filtered and enriched read models

- **WHEN** a caller lists a project's issues with any combination of stage, label, priority, archived, or all filters
- **THEN** the read-model query service returns the matching read models with the same filtering, ordering, workflow/feedback projection, and enrichment as before this change

#### Scenario: Detail query returns a single enriched read model

- **WHEN** a caller requests a single issue by project and number
- **THEN** the read-model query service returns that issue's read model (or null when absent), enriched identically to before this change

#### Scenario: Workflow-run reverse lookup resolves the owning in-progress issue

- **WHEN** a caller resolves the issue bound to a workflow run id
- **THEN** the read-model query service returns the `inProgress` issue id bound to that run, or null when none is bound, identical to before this change

### Requirement: No metrics concerns in the read-model service

The read-model query service MUST NOT contain any metrics aggregation method (completion buckets, quality, approval-wait, delivery-time, stage-duration), any metrics result record, the `CompletionBucket` enum, or any private metrics accumulator/helper type. Those concerns SHALL live in the dedicated metrics service.

#### Scenario: Metrics are not addressable on the read-model service

- **WHEN** a caller attempts to invoke a metrics aggregation method or reference a metrics result type on the read-model query service
- **THEN** the member is absent; the aggregation and its result types are provided by the dedicated metrics service instead

### Requirement: Single consolidated read-model mapping

The read-model mapping surface SHALL be a single consolidated path. The near-duplicate `ToInfo` overloads MUST be merged so that the field-by-field mapping of a domain `Issue` to an `IssueInfo` is defined exactly once, rather than maintained as multiple copies.

#### Scenario: Mapping produces an identical IssueInfo

- **WHEN** an issue is mapped to its read-model shape from any call site
- **THEN** the resulting `IssueInfo` carries the same fields and values as before this change, including the resolved workflow profile id, repository resolution, and label copy

#### Scenario: Only one mapping body exists

- **WHEN** the codebase is inspected for the field-by-field `Issue` → `IssueInfo` mapping
- **THEN** the mapping body appears in exactly one location, invoked by all former call sites
