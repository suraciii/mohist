### Requirement: Read entry points deserialize canonical State directly
Every WorkflowRun State read entry point — including execution-plane report / dispatch reads, control-plane status queries, and any reconciler that loads WorkflowRun State — SHALL deserialize the persisted State directly against the current model. Read paths SHALL NOT parse the whole document to probe for historical fields, SHALL NOT invoke the legacy converter, and SHALL NOT branch on legacy-versus-canonical shape.

#### Scenario: Canonical State is read without legacy probing
- **WHEN** any service-phase read entry point loads a WorkflowRun whose State is canonical
- **THEN** the entry point SHALL deserialize it directly with the current model
- **AND** the legacy converter SHALL NOT be invoked on that read path

#### Scenario: Legacy converter is unreachable from service-phase reads
- **WHEN** the set of call sites of the legacy WorkflowRun State converter is enumerated across the production read paths (workflow run store and querier, workflow querier, issue metrics querier, issue read-model loader, active-session reconciler)
- **THEN** the number of service-phase read-path invocations of the converter SHALL be zero

### Requirement: Read paths do not mask un-migrated legacy rows
The Server SHALL NOT enter its service phase until startup State migration has committed, so a service-phase read SHALL assume the persisted State is canonical. A read entry point SHALL NOT silently repair or convert a legacy-shaped row; encountering one in the service phase is a defect signaled by failed deserialization, not a condition the read path normalizes.

#### Scenario: A legacy-shaped row is not silently fixed on read
- **WHEN** a service-phase read entry point encounters a row whose State is not canonical
- **THEN** the entry point SHALL NOT convert or rewrite it
- **AND** deserialization against the current model SHALL surface the inconsistency rather than mask it

### Requirement: Legacy converter survives only at the cold-start migration boundary
The legacy-to-canonical converter SHALL remain available solely to the startup data upgrader. It SHALL NOT be referenced by, or reachable from, any service-phase read path. Support for upgrading from older databases is provided exclusively by the cold-start migration boundary.

#### Scenario: Converter is confined to database initialization
- **WHEN** the codebase is searched for references to the legacy WorkflowRun State converter
- **THEN** the only reachable caller SHALL be the startup data upgrader invoked during database initialization
