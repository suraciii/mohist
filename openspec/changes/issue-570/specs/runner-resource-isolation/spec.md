### Requirement: Per-work resource bounding on the Runner

Each work item executing on a Runner SHALL run inside a bounded resource envelope (memory, and process count where the platform supports it) that is separate from the Runner process's own envelope. The bounding SHALL be enforced by the execution environment (systemd slice/cgroup or an equivalent deployment-declared control), so that a single work item exhausting its envelope is reclaimed by the kernel/enforcement layer without affecting the Runner process or other work. The Runner control plane's own memory footprint (poll state, reported set, task-log buffers, outboxes) MUST be bounded independently of the size of any single work item's payloads.

#### Scenario: A work item exceeds its memory envelope

- **WHEN** a work item's processes allocate beyond the work item's memory ceiling
- **THEN** the enforcement layer SHALL reclaim or kill only that work item's processes
- **AND** the Runner process and all other in-flight work items SHALL continue running

#### Scenario: Large task output on a long task

- **WHEN** a long Agent task produces very large in-memory state
- **THEN** the per-work envelope SHALL contain the growth to that work item
- **AND** the Runner's control-plane channels (poll, report, heartbeat) SHALL remain operational

### Requirement: Runaway work is contained and terminable without cascade

A runaway work item (memory exhaustion, unbounded process spawning, or a wedged process tree) SHALL be contained and terminable: its processes are killed, its execution slot is freed, and its outcome is recorded — without killing the Runner process, without taking down a shared runtime generation that hosts other sessions' work, and without failing sibling work on the same Runner. Containment SHALL complete within a bounded time via an escalation path, and the contained work SHALL receive a terminal state with a resource-containment reason (not `runner-lost`, and not a silent loss), so the outcome is factual rather than an interruption of unknown cause.

#### Scenario: One long task runs away while others execute

- **WHEN** one work item becomes a resource runaway while other work items execute on the same Runner
- **THEN** the runaway work item SHALL be terminated and its slot freed within a bounded time
- **AND** sibling work items SHALL continue executing and the Runner SHALL keep polling and reporting

#### Scenario: Contained work receives a factual terminal state

- **WHEN** a work item is terminated by resource containment
- **THEN** its owner SHALL receive a terminal result identifying the resource-containment reason
- **AND** the result MUST NOT be recorded as `runner-lost` or as an unknown interruption

### Requirement: Deployment declares the isolation controls

Runner deployment configuration (systemd unit/cgroup hierarchy or equivalent) SHALL declare the per-work resource controls backing the isolation: a mechanism that places each work item's execution (including its spawned process trees) under its own bounded resource envelope beneath the Runner service, with the Runner control plane outside the per-work envelopes. The configuration SHALL be the deployment-authoritative source for the envelopes so isolation is effective in production rather than advisory in code.

#### Scenario: Runner service runs with per-work slices

- **WHEN** the Runner service is deployed with its resource configuration
- **THEN** each work item's execution and its descendant processes SHALL be placed under a per-work bounded envelope
- **AND** the Runner service process itself SHALL not be inside any single work item's envelope

#### Scenario: Containment survives process-tree depth

- **WHEN** a contained work item spawns nested descendant processes
- **THEN** the envelope SHALL cover the whole process tree of that work item
- **AND** containment SHALL terminate the descendants along with the direct processes
