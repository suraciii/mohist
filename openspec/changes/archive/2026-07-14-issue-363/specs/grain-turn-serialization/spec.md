### Requirement: Authority grains rely solely on Orleans turn-based serialization

`WorkflowGrain` and `RunnerGrain` are authority grains that own state. They SHALL NOT be marked `[Reentrant]`. Orleans turn-based serialization SHALL guard their owned domain and persistent-state mutations: state-mutating calls on the same activation SHALL NOT interleave. Narrow `[AlwaysInterleave]` methods MAY remain only when they do not mutate owned domain or persistent state, or when they protect their own mutations with an explicit gate (`_lifecycleGate`) to remain safe under interleaving.

#### Scenario: WorkflowGrain is not reentrant

- **WHEN** `WorkflowGrain` is inspected
- **THEN** it SHALL NOT carry the `[Reentrant]` attribute
- **AND** its state mutations SHALL be guarded solely by turn serialization

#### Scenario: RunnerGrain is not reentrant

- **WHEN** `RunnerGrain` is inspected
- **THEN** it SHALL NOT carry the `[Reentrant]` attribute
- **AND** its state mutations SHALL be guarded solely by turn serialization, except for `[AlwaysInterleave]` methods that protect their own mutations with `_lifecycleGate`

### Requirement: RunnerGrain write gate is removed

`RunnerGrain._worksStateWriteGate` SHALL NOT exist. `WriteStateAsync` calls SHALL be serialized by turn-based execution alone. The persistence contract is unchanged: writes remain durable and a failed `WriteStateAsync` still propagates the exception to the caller.

#### Scenario: No write gate semaphore

- **WHEN** `RunnerGrain` is inspected
- **THEN** no `SemaphoreSlim` dedicated to serializing `WriteStateAsync` SHALL exist
- **AND** `PersistAsync` SHALL call `WriteStateAsync` directly without an additional lock

### Requirement: RunnerGrain poll admission gate is removed

`RunnerGrain._pollAdmissionGate` SHALL NOT exist. The `_pollAdmitted` boolean flag SHALL remain as a business-logic check: `AssignAgentJobAsync` SHALL reject with "runner-reconciling" when a poll is in progress. `TryBeginPollAsync` SHALL set `_pollAdmitted` and return admission; `EndPollAsync` SHALL clear it. No method SHALL block waiting for a poll-admission semaphore. The poll sequence (owned by `DispatchService`) is unchanged: the same methods are called in the same order.

#### Scenario: Poll admission uses flag, not semaphore

- **WHEN** `TryBeginPollAsync` is called and no poll is in progress
- **THEN** it SHALL set `_pollAdmitted = true` and return admission with capacity
- **AND** it SHALL NOT acquire any semaphore

#### Scenario: Unregister and Update do not block on poll admission

- **WHEN** `UnregisterAsync` or `UpdateAsync` is called while a poll is in progress
- **THEN** the method SHALL proceed without blocking on a poll-admission semaphore
- **AND** the method SHALL acquire `_lifecycleGate` for its state mutations as normal

#### Scenario: DispatchService poll order is unchanged

- **WHEN** the DispatchService poll sequence is inspected
- **THEN** it SHALL call `TryBeginPollAsync`, `TouchPresenceAsync`, `GetInfoAsync`, `ReconcileAgentJobsAsync`, and `EndPollAsync` in the same order as before
- **AND** every fresh workflow claim SHALL revalidate live Runner registration and capacity before assigning work

### Requirement: RunnerGrain interleavable methods prevent reciprocal deadlock with AgentJobGrain

`GetRuntimeStateAsync`, `GetSlotsAsync`, and `AssignAgentJobAsync` on `IRunnerGrain` SHALL be marked `[AlwaysInterleave]`. This prevents a reciprocal deadlock when `AgentJobGrain.TryAssignToRunnerAsync` calls RunnerGrain while RunnerGrain's turn is held by `HandleTimeoutAsync` or `UnregisterAsync` calling `AgentJobGrain.ReportResultAsync`/`FailAsync` during closeout. `AssignAgentJobAsync` SHALL protect its state mutations with `_lifecycleGate`; `GetRuntimeStateAsync` and `GetSlotsAsync` are read-only and need no gate. `HandleTimeoutAsync` SHALL move its `CloseoutLostAsync` call outside `_lifecycleGate` so that `AssignAgentJobAsync` can acquire the gate during closeout.

#### Scenario: Interleavable methods on IRunnerGrain

- **WHEN** `IRunnerGrain` is inspected
- **THEN** `GetRuntimeStateAsync`, `GetSlotsAsync`, and `AssignAgentJobAsync` SHALL carry `[AlwaysInterleave]`
- **AND** `AssignAgentJobAsync` SHALL acquire `_lifecycleGate` before mutating state

#### Scenario: Closeout does not hold lifecycle gate

- **WHEN** `HandleTimeoutAsync` detects a presence timeout
- **THEN** it SHALL set the runner offline and unregister from the registry inside `_lifecycleGate`
- **AND** it SHALL call `CloseoutLostAsync` after releasing `_lifecycleGate`
- **AND** `CloseoutLostAsync` SHALL NOT hold `_lifecycleGate` during cross-grain calls to `IAgentJobGrain`

#### Scenario: No reciprocal deadlock during closeout

- **GIVEN** `HandleTimeoutAsync` is executing and calling `CloseoutLostAsync` (RunnerGrain turn held)
- **WHEN** `AgentJobGrain.TryAssignToRunnerAsync` calls `RunnerGrain.AssignAgentJobAsync`
- **THEN** `AssignAgentJobAsync` SHALL execute as an interleaved call (not blocked by the held turn)
- **AND** it SHALL acquire `_lifecycleGate` (which is free because closeout runs outside it)
- **AND** it SHALL reject if the runner is offline, completing `TryAssignToRunnerAsync` and freeing AgentJobGrain to process the closeout's cross-grain call

### Requirement: Concurrency characteristic tests cover state consistency without reentrancy

The system SHALL include characteristic tests that verify `WorkflowGrain` and `RunnerGrain` remain state-consistent when multiple operations are issued against the same grain activation without broad reentrancy. These tests SHALL act as a safety net proving that removing `[Reentrant]` does not introduce torn state. The tests SHALL use injectable time and fake storage; they SHALL NOT touch real Orleans clustering, real databases, or wall-clock time. Order-sensitive lifecycle operations SHALL be prepared in valid phases, and assertions SHALL accept only complete serialized outcomes without assuming scheduler order.

#### Scenario: WorkflowGrain state consistency under concurrent control calls

- **WHEN** multiple valid control operations are issued against the same `WorkflowGrain` activation without broad reentrancy from deliberately prepared lifecycle phases
- **THEN** the grain's in-memory run state and persisted snapshot SHALL remain internally consistent
- **AND** no operation SHALL observe a partially-applied state from another in-flight turn

#### Scenario: RunnerGrain state consistency under concurrent lifecycle and poll calls

- **WHEN** multiple valid lifecycle operations (register, unregister, update, timeout) and poll operations (try-begin, touch-presence, reconcile, end) are issued against the same `RunnerGrain` activation without broad reentrancy from deliberately prepared phases
- **THEN** the grain's in-memory status, presence, and works ledger SHALL remain internally consistent
- **AND** the persisted snapshot SHALL agree with the in-memory state
- **AND** `[AlwaysInterleave]` methods SHALL not produce torn state when interleaved with turn-serialized methods
