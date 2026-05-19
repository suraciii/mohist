## MODIFIED Requirements

### Requirement: REQ-WR-001 workflow run records structured convergence evidence

Workflow runs SHALL preserve structured task, check, and reaction outputs as runtime evidence for convergence decisions.

#### Scenario: Failed check context is assembled from structured outputs

- **WHEN** a check fails with structured items
- **THEN** the workflow run SHALL build failed-check context containing check identity, parsed verdict, blocking items, non-blocking items, source artifact references, snapshot metadata, and relevant prior task outputs
- **AND** reaction tasks SHALL receive this bounded context instead of scraping unstructured prose

#### Scenario: Reaction outputs drive verification rechecks

- **WHEN** a reaction task completes
- **THEN** the workflow run SHALL record attempted, resolved, unresolved, and newly observed item IDs
- **AND** the configured task/check path SHALL re-run in verification mode before the failed check can become passed
