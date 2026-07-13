### Requirement: Authority grains rely solely on Orleans turn-based serialization

`WorkflowGrain` and `RunnerGrain` are authority grains that own state. They SHALL NOT be marked `[Reentrant]`. Orleans turn-based serialization SHALL be the sole guarantee of state safety for these grains: each grain method call executes as a single serialized turn, and concurrent calls on the same grain activation SHALL NOT interleave. No other concurrency mechanism (reentrancy, manual write gates, internal semaphores guarding persistent state) SHALL be layered on top of the turn model.

#### Scenario: WorkflowGrain is not reentrant

- **WHEN** `WorkflowGrain` is inspected
- **THEN** it SHALL NOT carry the `[Reentrant]` attribute
- **AND** its state mutations SHALL be guarded solely by turn serialization

#### Scenario: RunnerGrain is not reentrant

- **WHEN** `RunnerGrain` is inspected
- **THEN** it SHALL NOT carry the `[Reentrant]` attribute
- **AND** its state mutations SHALL be guarded solely by turn serialization

### Requirement: RunnerGrain persistent-state writes are unguarded by a manual gate

`RunnerGrain` SHALL NOT maintain a write-gate semaphore (e.g. `_worksStateWriteGate`) around `IPersistentState.WriteStateAsync`. The `PersistAsync` path SHALL call `_worksState.WriteStateAsync()` directly, relying on turn serialization to prevent overlapping writes from the same grain activation. Removing the gate SHALL NOT change the persistence contract: writes remain durable and a failed write still propagates the exception to the caller.

#### Scenario: No write gate field exists on RunnerGrain

- **WHEN** `RunnerGrain` is inspected
- **THEN** no `SemaphoreSlim` dedicated to serializing persistent-state writes SHALL exist
- **AND** `PersistAsync` SHALL invoke `WriteStateAsync` without acquiring any such gate

#### Scenario: Concurrent grain calls do not interleave writes

- **WHEN** two grain method calls on the same `RunnerGrain` activation both attempt to persist state
- **THEN** turn serialization SHALL ensure their state mutations and writes do not interleave
- **AND** the persisted state SHALL reflect one complete turn followed by the other, never a torn mix

### Requirement: Concurrency characteristic tests cover state consistency without reentrancy

The system SHALL include characteristic tests that verify `WorkflowGrain` and `RunnerGrain` remain state-consistent when multiple operations are issued against the same grain activation without reentrancy. These tests SHALL act as a safety net proving that removing `[Reentrant]` and the write gate does not introduce torn state. The tests SHALL use injectable time and fake storage; they SHALL NOT touch real Orleans clustering, real databases, or wall-clock time.

#### Scenario: WorkflowGrain state consistency under concurrent control calls

- **WHEN** multiple control operations (start/pause/resume/retry/rerun) are issued against the same `WorkflowGrain` activation without reentrancy
- **THEN** the grain's in-memory run state and persisted snapshot SHALL remain internally consistent
- **AND** no operation SHALL observe a partially-applied state from another in-flight turn

#### Scenario: RunnerGrain state consistency under concurrent work mutations

- **WHEN** multiple work-mutating operations (assign agent-job work, report result, reconcile agent-jobs, closeout on presence loss) are issued against the same `RunnerGrain` activation without reentrancy and without a write gate
- **THEN** the runner's works ledger and persisted state SHALL remain internally consistent
- **AND** no operation SHALL observe a partially-applied state from another in-flight turn
