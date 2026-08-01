### Requirement: Read entry points deserialize canonical State directly
Every WorkflowRun State read entry point — including execution-plane report / dispatch reads, control-plane queries that load WorkflowRun State, and any reconciler that loads WorkflowRun State — SHALL deserialize the persisted State directly against the current model. Read paths SHALL NOT parse the whole document to probe for historical fields, SHALL NOT invoke the legacy converter, and SHALL NOT branch on legacy-versus-canonical shape. A read entry point MAY satisfy this through a shared canonical deserializer (e.g. the injected `IWorkflowRunDeserializer`); regardless of the entry point, no converter is applied.

#### Scenario: Canonical State is read without legacy probing
- **WHEN** any service-phase read entry point loads a WorkflowRun whose State is canonical
- **THEN** the entry point SHALL deserialize it directly with the current model
- **AND** the legacy converter SHALL NOT be invoked on that read path

#### Scenario: Legacy converter is unreachable from service-phase reads
- **WHEN** the set of call sites of the legacy WorkflowRun State converter is enumerated across the production read paths (workflow run store and querier, workflow querier, issue metrics querier, issue read-model loader, active-session reconciler)
- **THEN** the number of service-phase read-path invocations of the converter SHALL be zero

### Requirement: Read paths do not convert un-migrated legacy rows
The Server SHALL NOT enter its service phase until startup State migration has committed, so a service-phase read SHALL assume the persisted State is canonical. A read entry point SHALL NOT repair, convert, or otherwise normalize a legacy-shaped row; the read path's only obligation toward a non-canonical row is to leave it untouched (no converter invocation, no rewrite, no legacy-shape branching). Detection of un-migrated legacy rows is not a read-path responsibility — it is guaranteed upstream by startup migration blocking the service phase before any request is served.

#### Scenario: A legacy-shaped row is not converted on read
- **WHEN** a service-phase read entry point encounters a row whose State is not canonical
- **THEN** the entry point SHALL NOT invoke the legacy converter
- **AND** the entry point SHALL NOT rewrite or branch on the row's legacy shape

### Requirement: Legacy converter survives only at the cold-start migration boundary
The legacy-to-canonical converter SHALL remain available solely to the startup data upgrader. It SHALL NOT be referenced by, or reachable from, any service-phase read path. Support for upgrading from older databases is provided exclusively by the cold-start migration boundary.

#### Scenario: Converter is confined to database initialization
- **WHEN** the codebase is searched for references to the legacy WorkflowRun State converter
- **THEN** the only reachable caller SHALL be the startup data upgrader invoked during database initialization
