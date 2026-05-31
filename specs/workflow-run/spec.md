## ADDED Requirements

### Requirement: Workflow recovery resolves durable project identity
Workflow run recovery SHALL restore backlog membership using durable project identity from authoritative workflow data. If workflow metadata annotations or indexed metadata project fields are absent, recovery MUST resolve the project from persisted workflow variables or another durable source bound to the workflow run before falling back to any default backlog.

#### Scenario: Recovery uses workflow variables when metadata is empty
- **WHEN** backlog recovery processes a non-terminal workflow run whose metadata annotations do not contain a project id
- **AND** persisted workflow variables contain the workflow's project id
- **THEN** recovery SHALL register the workflow in that project backlog
- **AND** it SHALL NOT register the workflow in the `default` backlog

#### Scenario: Recovery avoids claiming actively leased workflows
- **WHEN** backlog recovery processes a workflow run that has a valid active lease
- **THEN** recovery SHALL preserve the lease owner as the active executor
- **AND** it SHALL NOT create a backlog claim or dispatch opportunity that can assign the same work item to another runner

#### Scenario: Missing project identity is explicit
- **WHEN** recovery cannot resolve project identity from metadata, workflow variables, issue binding, or another authoritative durable source
- **THEN** recovery SHALL record an explicit recovery diagnostic for the workflow
- **AND** it SHALL NOT silently claim the workflow as part of the `default` project backlog
