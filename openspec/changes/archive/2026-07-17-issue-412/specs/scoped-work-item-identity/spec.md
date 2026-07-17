### Requirement: Issue and Epic have one Project-scoped identity

An Issue SHALL be identified only by (`ProjectId`, `IssueNumber`) and an Epic SHALL be identified
only by (`ProjectId`, `EpicNumber`). IssueNumber and EpicNumber SHALL be permanent within their
Project. The domain model and persisted current state SHALL NOT retain a second random IssueId or
EpicId.

#### Scenario: The same number in different Projects identifies different Issues

- **GIVEN** Project A and Project B each contain Issue number 42
- **WHEN** either Issue is addressed
- **THEN** (`ProjectId`, `IssueNumber`) resolves exactly one Issue in that Project
- **AND** no global Issue id lookup is needed

#### Scenario: Issue and Epic creation return their canonical identity

- **WHEN** an Issue or Epic is created
- **THEN** the response, domain event, and resource link identify it by Project and allocated number
- **AND** no random IssueId or EpicId is generated or returned

### Requirement: Actor and resource keys derive from domain identity

Issue and Epic GrainKeys SHALL be produced by one lossless typed codec from their Project-scoped
identity. HTTP resources and CloudEvents sources SHALL use Project plus number. Call sites SHALL NOT
construct ad hoc `projectId:number` strings or resolve number aliases to hidden ids.

#### Scenario: Issue GrainKey round trips

- **WHEN** `IssueKey(projectId, issueNumber)` is encoded and decoded
- **THEN** the decoded key equals both original components
- **AND** the same codec is used by command and query entry points

#### Scenario: Resource paths carry the canonical identity

- **WHEN** Issue 42 and Epic 7 in a Project are represented as resources or event sources
- **THEN** their paths contain the Project identity and number
- **AND** no separate IssueId or EpicId path is exposed

### Requirement: References use the owning resource's canonical identity

Persisted relationships to Issue or Epic SHALL use Project plus number. This includes comments,
prerequisites, profiles, inbox items, session/workflow origin metadata, read projections, and any
other current-state reference. A reference SHALL NOT keep a random id as a parallel identity.

#### Scenario: Related records survive identity migration

- **GIVEN** an existing Issue has comments, prerequisites, a WorkflowRun, sessions, and projections
- **WHEN** current state is migrated to Project-scoped identity
- **THEN** every related record still resolves to the same Issue by Project and number
- **AND** no dangling random-id foreign reference remains

### Requirement: Current state migrates without a compatibility model

The migration SHALL preserve current Issue/Epic state and references, SHALL be idempotent when a
deployment is retried, and SHALL remove old id columns/resolvers after cutover. The final model SHALL
NOT dual-read or dual-write id and number.

#### Scenario: Migration is retried

- **GIVEN** current rows have already been converted to Project-scoped identities
- **WHEN** the migration or startup convergence runs again
- **THEN** it leaves the same canonical rows and references
- **AND** it does not create duplicates or require the old identity columns
