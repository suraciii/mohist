### Requirement: Authority grains rely solely on Orleans turn-based serialization

`WorkflowGrain` is an authority grain that owns state. It SHALL NOT be marked `[Reentrant]`. Orleans turn-based serialization SHALL guard its owned domain and persistent-state mutations: state-mutating calls on the same activation SHALL NOT interleave. Narrow `[AlwaysInterleave]` methods MAY remain only when they do not mutate owned domain or persistent state. `RunnerGrain` reentrancy removal is deferred to a follow-up prerequisite that resolves the multi-call poll lease and reciprocal Runner↔AgentJob waits without modifying `DispatchService`.

#### Scenario: WorkflowGrain is not reentrant

- **WHEN** `WorkflowGrain` is inspected
- **THEN** it SHALL NOT carry the `[Reentrant]` attribute
- **AND** its state mutations SHALL be guarded solely by turn serialization

### Requirement: Concurrency characteristic tests cover state consistency without reentrancy

The system SHALL include characteristic tests that verify `WorkflowGrain` remains state-consistent when multiple operations are issued against the same grain activation without broad reentrancy. These tests SHALL act as a safety net proving that removing `[Reentrant]` does not introduce torn state. The tests SHALL use injectable time and fake storage; they SHALL NOT touch real Orleans clustering, real databases, or wall-clock time. Order-sensitive lifecycle operations SHALL be prepared in valid phases, and assertions SHALL accept only complete serialized outcomes without assuming scheduler order.

#### Scenario: WorkflowGrain state consistency under concurrent control calls

- **WHEN** multiple valid control operations are issued against the same `WorkflowGrain` activation without broad reentrancy from deliberately prepared lifecycle phases
- **THEN** the grain's in-memory run state and persisted snapshot SHALL remain internally consistent
- **AND** no operation SHALL observe a partially-applied state from another in-flight turn
