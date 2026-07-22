### Requirement: Execution backend as an Agent config dimension
A Mohist Agent's configuration SHALL carry an execution-backend dimension accepting `opencode` or `pi`. When the backend is unset or absent, it SHALL resolve to `opencode`.

#### Scenario: Default backend when config omits it
- **WHEN** an Agent is launched whose config does not specify a backend
- **THEN** the resolved backend for that launch SHALL be `opencode`

#### Scenario: Pi backend read from config
- **WHEN** an Agent's config specifies backend `pi`
- **THEN** the resolved backend for launches of that Agent SHALL be `pi`

#### Scenario: Invalid backend rejected
- **WHEN** an Agent's config specifies a backend other than `opencode` or `pi`
- **THEN** the value SHALL be rejected as invalid rather than silently coerced to a default

### Requirement: Launch-time override precedence
A backend supplied as a launch-time override SHALL take precedence over the Agent's configured backend. Absent an override, the Agent's configured backend SHALL be used.

#### Scenario: Override wins over Agent config
- **WHEN** an Agent configured for `pi` is launched with a launch-time override of `opencode`
- **THEN** the resolved backend for that launch SHALL be `opencode`

#### Scenario: No override falls back to Agent config
- **WHEN** an Agent configured for `pi` is launched without a launch-time override
- **THEN** the resolved backend SHALL be `pi`

### Requirement: Backend fixed to the launch snapshot
The resolved backend SHALL be captured into the AgentJob snapshot at launch time. Subsequent edits to the Agent definition SHALL NOT alter the backend of any execution already started.

#### Scenario: In-flight config edit does not change a started execution
- **WHEN** an AgentJob is launched with resolved backend `pi` and the Agent's backend config is later changed to `opencode` while that job is still running
- **THEN** the running execution SHALL continue to use `pi`

#### Scenario: Replay reuses the snapshotted backend
- **WHEN** an AgentJob that was snapshotted with backend `pi` is recovered or re-dispatched after process loss
- **THEN** the recovered execution SHALL use `pi`, not a fresh read of the (possibly edited) Agent config

### Requirement: Launch opens the session with the resolved backend
Both the manual launch path and the event-driven (routed) launch path SHALL open the AgentSession using the resolved backend, not a fixed `opencode` literal.

#### Scenario: Manual launch opens with resolved backend
- **WHEN** an Agent whose resolved backend is `pi` is launched via the manual launch path
- **THEN** the AgentSession SHALL be opened with runtime `pi`

#### Scenario: Routed launch opens with resolved backend
- **WHEN** an event-driven routed launch resolves backend `pi`
- **THEN** the AgentSession SHALL be opened with runtime `pi`
