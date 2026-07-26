### Requirement: Runner-owned workspace queries
The Server SHALL NOT register, implement, or test a local service that runs git commands or reads runner workspaces. Workspace diff, commit, status, file-content, and removal requests SHALL continue to be sent to the assigned runner through the runner workspace client.

#### Scenario: Reading an active workflow workspace
- **WHEN** a caller requests a diff, commits, status, file content, or removal for an active workflow workspace
- **THEN** the Server forwards the request to the assigned runner and returns the runner's result without executing a local git or filesystem operation

### Requirement: Daemon self-management ownership
The Server SHALL own process execution needed solely to inspect, update, install, restart, or determine the state of the Mohist daemon and its managed services. This daemon self-management responsibility MUST NOT grant the Server authority to execute user-project workspace, git, shell, or agent work.

#### Scenario: Inspecting a local-source installation
- **WHEN** SystemInfo determines service status or source and installation facts for a local-source Mohist daemon
- **THEN** the Server performs the daemon self-management operation and preserves the existing status and update behavior

### Requirement: Epic dependency enforcement
The architecture dependency guard SHALL include the Epic module and SHALL reject an Epic dependency on another Server domain unless that direction is explicitly allowed by the guard.

#### Scenario: An unapproved Epic dependency is introduced
- **WHEN** an Epic type depends on a type in a Server domain that is not an explicitly permitted dependency
- **THEN** the architecture test fails

### Requirement: Domain-owned durable reactions
Every durable CloudEvent handler that changes or coordinates a domain's state SHALL reside in that domain's module. The shared event infrastructure SHALL retain only transport, dispatch, persistence, and generic event contracts. Relocating a handler MUST preserve its event subscription, filter, invocation timing, success behavior, failure delivery semantics, and durable handler identity.

#### Scenario: Redelivering a dead letter created before handler relocation
- **WHEN** a dead-letter row stores a moved handler's pre-relocation identity
- **THEN** the dispatcher resolves the relocated handler and redelivers the recorded event

#### Scenario: A workflow stage reaches a terminal result
- **WHEN** a persisted workflow stage-completed or stage-failed event is dispatched
- **THEN** the Workflow-owned handler releases the corresponding stage lock through the existing durable dispatch path

#### Scenario: An issue lifecycle event affects Epic progress
- **WHEN** a persisted Issue lifecycle event that currently triggers Epic progress recomputation is dispatched
- **THEN** the Epic-owned handler invokes the same Epic recomputation behavior as before relocation

#### Scenario: Auditing handler ownership
- **WHEN** the Server source tree is checked after the relocation
- **THEN** every pre-existing domain subscription handler and its domain-specific helper resides in its assigned feature module, and `Events/Subscriptions` contains no domain subscription handler

#### Scenario: Hermes background delivery fails
- **WHEN** Hermes delivery fails after the notification handler has accepted an event
- **THEN** the failure remains an asynchronous best-effort delivery failure and does not enter the CloudEvent dispatcher's retry or dead-letter path

### Requirement: Runner reports facts and Workflow decides retries
The runner SHALL report failure classifications, including retry-safe classifications, as execution facts. The Workflow SHALL remain the sole authority that interprets a report and decides whether work fails, retries, recovers, advances, waits, or requires approval.

#### Scenario: Runner reports a retry-safe failure
- **WHEN** the runner reports a retry-safe failure for owned active work
- **THEN** the report is processed by Workflow using the workflow definition and current run state, and the runner does not independently retry or advance the workflow
