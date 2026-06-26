## ADDED Requirements

### Requirement: Server exposes workspace cleanup policy to the runner

The server SHALL expose a workspace cleanup policy that the runner can read. The policy SHALL include the retention window (or an explicit unlimited/disabled sentinel) and the storage budget with a target watermark. The server MAY deliver this policy via the runner poll response or a dedicated runner-facing config read. The server MUST NOT scan the runner filesystem, maintain a cleanup queue, or perform runner filesystem deletion; cleanup execution is exclusively a runner-side responsibility.

#### Scenario: Runner reads cleanup policy

- **WHEN** the runner polls or reads its configuration from the server
- **THEN** the response SHALL include a cleanup policy containing a retention window and a storage budget with target watermark
- **AND** the server SHALL NOT instruct or perform any filesystem deletion on the runner

#### Scenario: Server does not scan or schedule runner deletion

- **WHEN** workspace cleanup policy is in effect
- **THEN** the server SHALL NOT enumerate runner workspace directories
- **AND** the server SHALL NOT maintain a per-workspace cleanup queue
- **AND** the server SHALL NOT invoke runner filesystem deletion outside the existing manual cleanup path

### Requirement: Workflow run terminal status is reachable by the owning runner

The server SHALL ensure that a workflow run reaching a terminal state (`completed`, `stopped`, `failed`) is observable by the runner that owns the workspace, both by delivering a workflow run lifecycle event to that runner and by remaining queryable so the runner can converge on missed events. The server is the source of workflow run lifecycle facts; the runner's local marker expresses only workspace identity.

#### Scenario: Terminal event is delivered to the owning runner

- **WHEN** a workflow run reaches a terminal state
- **AND** the owning runner is connected
- **THEN** the server SHALL deliver a workflow run lifecycle event for that run to the owning runner

#### Scenario: Terminal status remains queryable for convergence

- **WHEN** the runner queries the server for the status of an active registry entry's workflow run
- **THEN** the server SHALL return the current lifecycle state of that workflow run
- **AND** the response SHALL distinguish terminal states (`completed`, `stopped`, `failed`) from non-terminal states (`running`, `paused`, `awaiting approval`)
