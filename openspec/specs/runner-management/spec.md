# OpenSpec Capability: runner-management

### Requirement: Runner is a global execution resource

A Runner SHALL be a global execution resource that does not belong to any project. Runner registration, registry, and dispatch SHALL use the global path and SHALL NOT use a project-scoped registry. Dispatch SHALL select work by round-robin fair claiming across all known project backlogs, and this cross-project fair-claim behavior SHALL be preserved after globalization.

#### Scenario: Runner is not scoped to a project
- **WHEN** a runner registers
- **THEN** the runner SHALL be recorded as a global resource
- **AND** it SHALL NOT be bound to any project's scoped registry

#### Scenario: Global runner claims work across all project backlogs
- **WHEN** a global runner polls for work
- **THEN** it SHALL round-robin across all known project backlogs
- **AND** it SHALL fairly claim pending workflow runs regardless of which project owns them

### Requirement: Runner definition state is persisted

The control plane SHALL persist per-runner definition state. The definition state SHALL include at least the runner identity (`runnerId`) and the capacity contract (`slots`). Persisted definition state SHALL survive runner offline, runner process restart, and Orleans grain deactivation followed by reacquisition.

#### Scenario: Definition state survives grain deactivation
- **WHEN** a `RunnerGrain` is deactivated and later reactivated by the same runner reconnecting
- **THEN** the runner's persisted `slots` SHALL be restored unchanged
- **AND** the slots SHALL NOT reset to the default value

#### Scenario: Definition state survives runner process restart
- **WHEN** a runner process restarts and re-registers
- **THEN** its previously persisted `slots` SHALL remain in effect
- **AND** the re-registration SHALL NOT overwrite the persisted slots with a runner-reported value

### Requirement: Persisted slots are the sole authoritative source for dispatch capacity

The dispatch capacity of a runner (`slots`) SHALL be sourced exclusively from the persisted definition state. A concurrency value reported by the runner process via register or heartbeat SHALL NOT influence dispatch capacity. The dispatch endpoint SHALL enforce that the count of assigned workflow runs does not exceed the persisted `slots`.

#### Scenario: Runner-reported concurrency is ignored for dispatch
- **WHEN** a runner registers or heartbeats carrying a concurrency value (for example `MaxWorkflowSlots`)
- **THEN** that value SHALL NOT be used as the dispatch capacity
- **AND** dispatch SHALL use the persisted `slots` instead

#### Scenario: Dispatch enforces the persisted slot bound
- **WHEN** a runner whose assigned workflow run count equals its persisted `slots` polls for more work
- **THEN** the runner SHALL NOT claim additional workflow runs
- **AND** dispatch SHALL resume claiming once an assigned workflow run is released

### Requirement: Runner slots are configurable through the control plane

The control plane SHALL support updating a runner's `slots`. An update SHALL persist immediately and SHALL take effect on the next dispatch cycle without requiring the runner process to re-register or restart. A newly created runner definition SHALL default to `slots = 1`. A configured `slots` value SHALL be a positive integer; a non-positive value SHALL be rejected.

#### Scenario: Configuration takes effect without runner restart
- **WHEN** the control plane updates a runner's `slots` from 1 to 4
- **THEN** the new value SHALL be persisted
- **AND** the next dispatch cycle SHALL honor the new capacity without any runner process action

#### Scenario: New runner defaults to one slot
- **WHEN** a runner connects for the first time and no persisted definition state exists
- **THEN** a definition state SHALL be initialized with `slots = 1`
- **AND** a runner-reported concurrency value SHALL NOT pre-fill the initialized slots

#### Scenario: Non-positive slots value is rejected
- **WHEN** the control plane receives a `slots` value that is not a positive integer
- **THEN** the update SHALL be rejected
- **AND** the persisted `slots` SHALL remain unchanged

### Requirement: Runner slot capacity invariant

A runner SHALL NOT hold more assigned workflow runs than its persisted `slots`. The claim path SHALL be the single execution point that enforces this invariant, so the bound `|assignedWorkflows| ≤ slots` SHALL always hold. A runner that is offline SHALL NOT accept new workflow assignments.

#### Scenario: Claim refuses to exceed slots
- **WHEN** a runner attempts to claim a workflow run that would exceed its persisted `slots`
- **THEN** the claim SHALL be refused
- **AND** the invariant `|assignedWorkflows| ≤ slots` SHALL hold

#### Scenario: Offline runner accepts no new work
- **WHEN** a runner is offline
- **THEN** it SHALL NOT claim or be assigned new workflow runs
- **AND** polling an offline runner SHALL be rejected

### Requirement: Workflow run assignment is globally unique

A single workflow run SHALL be in at most one runner's `assignedWorkflows` at any time. Claiming a workflow run by a second runner SHALL NOT result in concurrent dual ownership of that workflow run.

#### Scenario: A workflow run is owned by at most one runner
- **WHEN** a workflow run is claimed by a runner
- **THEN** it SHALL appear in exactly that runner's `assignedWorkflows`
- **AND** it SHALL NOT concurrently appear in any other runner's `assignedWorkflows`

### Requirement: Runner executes only work it has claimed

A runner SHALL execute only work that originates from a workflow run it has claimed. The dispatch path SHALL validate that the runner owns the workflow run before executing the work.

#### Scenario: Work is runnable only for the owning runner
- **WHEN** a runner attempts to execute work for a workflow run
- **THEN** the dispatch path SHALL verify the runner is the claimed owner of that workflow run
- **AND** the work SHALL be dropped if the runner is not the owner

### Requirement: Runner lifecycle transitions

A runner SHALL transition to online when the execution end registers; registration SHALL capture access facts (`hostname`, `buildGitHash`, `capabilities`, `coderModels`). A runner SHALL transition to offline when it leaves or when the health-check mechanism judges it lost; transitioning offline SHALL clear the access facts. Heartbeat and health-check are external technical mechanisms; their loss-of-contact outcome SHALL enter the domain exclusively through the offline transition, and heartbeat timing SHALL NOT be modeled as a property of the runner entity.

#### Scenario: Registration brings a runner online with access facts
- **WHEN** the execution end registers a runner with access facts
- **THEN** the runner SHALL transition to online
- **AND** its access facts SHALL be recorded

#### Scenario: Going offline clears access facts
- **WHEN** a runner goes offline, whether by graceful leave or by health-check-judged loss
- **THEN** the runner SHALL transition to offline
- **AND** its access facts SHALL be cleared

#### Scenario: Health-check loss enters the domain via the offline transition
- **WHEN** the health-check mechanism judges a runner lost
- **THEN** the loss SHALL surface to the domain only through the offline transition
- **AND** heartbeat timing SHALL NOT be modeled as a property of the runner entity
