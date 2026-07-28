### Requirement: No dead event-dispatch path in WorkflowGrain production code

The WorkflowGrain production code MUST NOT carry a domain-event dispatch path whose every branch is a no-op. Specifically, the `IWorkflowGrainContext.DispatchEvent(WorkflowEvent)` declaration, the `WorkflowGrain.On(WorkflowEvent, string?)` implementation (whose 19 event-type branches all return `Task.CompletedTask`), the `WorkflowWorkLifecycle` loop that feeds events into it, and the `reason` parameter threaded through `CommitAsync` solely to serve that loop SHALL be removed. The codebase MUST NOT present a surface that reads as a load-bearing event-distribution pipeline when no such pipeline exists.

#### Scenario: DispatchEvent declaration is gone

- **WHEN** the server production source is searched for `DispatchEvent` under `src/`
- **THEN** the `IWorkflowGrainContext.DispatchEvent` declaration and the `WorkflowGrain` explicit-interface implementation MUST NOT appear
- **AND** the unrelated test-side `DispatchEventsAsync()` helper is out of scope and MAY remain

#### Scenario: The On dispatch method and its reason thread are gone

- **WHEN** `WorkflowGrain` is inspected for the `On(WorkflowEvent e, string? reason)` method
- **THEN** the method MUST NOT exist
- **AND** the `reason` parameter that existed only to feed `On` through `CommitAsync` MUST NOT be threaded through `CommitAsync`

#### Scenario: The WorkflowWorkLifecycle dispatch loop is gone

- **WHEN** `WorkflowWorkLifecycle` appends stage/task events after a save
- **THEN** it MUST NOT enumerate those events into a per-event dispatch call
- **AND** no replacement dispatch loop SHALL be introduced, because the dispatched method produced no effect

#### Scenario: Workflow run behavior is unchanged after removal

- **WHEN** a workflow run starts, advances a stage, marks a task running, requests approval, completes, or fails
- **THEN** the observable run state, emitted events, and persisted transitions MUST be identical to before the dead path was removed

### Requirement: No production test-backdoor that bypasses the profile coordinator

`WorkflowGrain` MUST NOT expose a settable delegate that lets production code resolve a profile binding without going through `IWorkflowProfileReferenceCoordinatorGrain`. The `BindProfileForTest` field (a `Func<string, string, Task<WorkflowProfileReferenceResult>>?`) SHALL be removed. The ArchTest promise recorded in `architecture.md` — that production code cannot bypass the profile coordinator — MUST hold without exception, because the seam that previously defeated it no longer exists.

#### Scenario: BindProfileForTest is removed

- **WHEN** the server production source is searched for `BindProfileForTest`
- **THEN** no declaration of a settable binding delegate on `WorkflowGrain` or `IWorkflowGrainContext` MUST appear

#### Scenario: Profile binding always routes through the coordinator in production

- **WHEN** `WorkflowGrain` persists a profile binding for a run
- **THEN** it MUST obtain the `WorkflowProfileReferenceResult` by calling `IWorkflowProfileReferenceCoordinatorGrain.BindWorkflowRunAsync`
- **AND** no production code path SHALL be able to substitute that call with an inline delegate

#### Scenario: Former test consumer uses a fake coordinator grain

- **WHEN** a spec that previously set `BindProfileForTest` needs an applied binding result
- **THEN** the test MUST register a fake `IWorkflowProfileReferenceCoordinatorGrain` in the test cluster that returns the desired result
- **AND** MUST NOT rely on any production-side override hook

### Requirement: Profile-resolution failure classified by exception type, not message text

Profile resolution failures MUST be communicated to `WorkflowGrain.CommitAsync` via typed exceptions that carry a decidable failure reason, NOT by `InvalidOperationException` whose message text is matched with `Contains`. `CommitAsync` SHALL branch on the exception **type** (or a typed discriminator it exposes) to decide whether to fail the run's definition resolution. Changing the wording of an exception message MUST NOT silently alter control flow, because no production code SHALL inspect exception message text to make a control-flow decision.

#### Scenario: CommitAsync branches on exception type

- **WHEN** stage initialization raises a profile/definition resolution failure inside `CommitAsync`
- **THEN** `CommitAsync` MUST select the fail-definition-resolution branch by catching the typed exception
- **AND** MUST NOT match on substrings such as `"no current definition"` or `"no definition for stage"`

#### Scenario: Message wording change does not change control flow

- **WHEN** the message text of the profile-resolution exception is edited (reworded, translated, or punctuated differently)
- **THEN** the run's failure handling in `CommitAsync` MUST behave identically to before the edit

#### Scenario: User-facing resolution-failure message is preserved

- **WHEN** a profile or stage definition cannot be resolved
- **THEN** the user-visible error information surfaced by the run MUST remain equivalent to today's behavior
- **AND** the failure reason MUST remain machine-decidable for the consuming branch
