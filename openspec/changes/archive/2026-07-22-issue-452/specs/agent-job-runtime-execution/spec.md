### Requirement: Work dispatch carries the snapshot runtime
The AgentJob work dispatch SHALL carry the snapshot-resolved runtime so the runner executes the turn on the selected backend.

#### Scenario: Runtime present on the dispatch envelope
- **WHEN** an AgentJob snapshot was fixed to backend `pi`
- **THEN** the dispatch payload delivered to the runner SHALL carry runtime `pi`

### Requirement: Executor selects runtime from the dispatch
The runner AgentJob executor SHALL select `PiRuntime` or `OpenCodeRuntime` based on the runtime carried by the dispatch. It SHALL NOT hardcode a single runtime.

#### Scenario: Dispatch runtime selects PiRuntime
- **WHEN** the executor receives an agent-job dispatch carrying runtime `pi` and the Pi runtime is ready
- **THEN** the turn SHALL execute on `PiRuntime`

#### Scenario: Dispatch runtime selects OpenCodeRuntime
- **WHEN** the executor receives an agent-job dispatch carrying runtime `opencode` and the OpenCode runtime is ready
- **THEN** the turn SHALL execute on `OpenCodeRuntime`

#### Scenario: Chosen runtime not ready fails with actionable error
- **WHEN** the executor receives an agent-job dispatch carrying runtime `pi` and the Pi runtime is not ready
- **THEN** the turn SHALL fail with an actionable runtime-unavailable error rather than silently falling back to OpenCode

### Requirement: Runtime-aware generic session open and attach
The runner's generic agent-session open and attach SHALL use the runtime recorded on the session snapshot rather than a hardcoded `opencode`.

#### Scenario: Open records the session runtime
- **WHEN** the runner opens an agent-session whose snapshot runtime is `pi`
- **THEN** the open SHALL record runtime `pi`

#### Scenario: Attach binds the session runtime
- **WHEN** the runner attaches a physical session to an agent-session whose snapshot runtime is `pi`
- **THEN** the attach SHALL bind runtime `pi`

### Requirement: Shared session infrastructure across backends
A Pi-executed AgentJob SHALL produce transcript, tool activity, usage, cost, compaction, and lineage through the same AgentSession infrastructure and views as an OpenCode-executed AgentJob. Session commands (Follow-up, Cancel, Compact, Reset) SHALL route by the session's current binding runtime.

#### Scenario: Pi run facts render in the shared session views
- **WHEN** an AgentJob executes a turn on `pi` to completion
- **THEN** its transcript, tool activity, usage, cost, and model SHALL be visible in the same AgentSession views used for OpenCode runs

#### Scenario: Session commands route by binding runtime
- **WHEN** a Follow-up, Cancel, Compact, or Reset is issued against an agent-session whose current binding runtime is `pi`
- **THEN** the command SHALL be routed to the Pi runtime

### Requirement: Actionable failure for an uncredentialed model
Launching an AgentJob whose selected model's provider has no configured credentials SHALL fail with an actionable error. The execution backend SHALL be the final validator of model legality.

#### Scenario: Uncredentialed model fails with an actionable error
- **WHEN** an AgentJob is launched with a selected model whose provider has no configured credentials
- **THEN** the execution SHALL fail with an error identifying the credential problem, not a generic or silent failure
