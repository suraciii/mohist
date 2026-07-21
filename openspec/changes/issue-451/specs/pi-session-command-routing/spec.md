### Requirement: The Server admits session commands for Pi-bound AgentSessions

For an AgentSession whose current Runtime Session binding has `runtime` equal to `pi`, the Server SHALL admit Compact, Reset, Follow-up, and Cancel under the same contract that governs OpenCode-bound sessions. The Server MUST NOT reject a command solely because the bound runtime is `pi`. The stable-identity, expected-binding, idle-concurrency, and recovery semantics already established for session commands SHALL apply identically to `pi`-bound sessions.

#### Scenario: Compact admitted on an idle Pi-bound session

- **WHEN** Compact is requested on an idle AgentSession currently bound to runtime `pi`
- **THEN** the Server SHALL dispatch the command through the same compact routing used for OpenCode-bound sessions
- **AND** SHALL NOT reject it on the basis of the bound runtime

#### Scenario: Reset admitted on an idle Pi-bound session

- **WHEN** Reset is requested on an idle AgentSession currently bound to runtime `pi` with an expected binding that matches the persisted current binding
- **THEN** the Server SHALL dispatch the command through the same reset routing used for OpenCode-bound sessions
- **AND** SHALL NOT reject it on the basis of the bound runtime

#### Scenario: Follow-up and Cancel admitted on a Pi-bound session

- **WHEN** Follow-up or Cancel is requested on an AgentSession currently bound to runtime `pi`
- **THEN** the Server SHALL dispatch it to the bound Runner through the same channel used for OpenCode-bound sessions
- **AND** the wire payload SHALL carry `runtime: "pi"` taken from the persisted binding

### Requirement: Runner command handlers route by the bound runtime

The Runner's Follow-up, Cancel, and SessionCommand handlers SHALL select the execution backend from the `runtime` field carried on the command's session binding. A command whose binding carries `runtime: "pi"` SHALL be dispatched to the Pi runtime; a command whose binding carries `runtime: "opencode"` SHALL be dispatched to the OpenCode runtime. The handlers MUST NOT hardcode a single runtime independent of the binding.

#### Scenario: Follow-up on a Pi binding dispatches to the Pi runtime

- **WHEN** the Runner receives a Follow-up whose session binding carries `runtime: "pi"`
- **THEN** the Runner SHALL invoke the Pi runtime's Follow-up operation for that binding
- **AND** SHALL NOT invoke the OpenCode runtime for the Follow-up

#### Scenario: Follow-up on an OpenCode binding dispatches to the OpenCode runtime

- **WHEN** the Runner receives a Follow-up whose session binding carries `runtime: "opencode"`
- **THEN** the Runner SHALL invoke the OpenCode runtime's Follow-up operation
- **AND** SHALL NOT invoke the Pi runtime for the Follow-up

#### Scenario: Cancel routes by the bound runtime

- **WHEN** the Runner receives a Cancel whose session binding carries `runtime: "pi"` / `runtime: "opencode"`
- **THEN** the Runner SHALL invoke the matching runtime's Cancel operation

#### Scenario: Compact and Reset route by the bound runtime

- **WHEN** the Runner receives a SessionCommand (Compact or Reset) whose binding carries `runtime: "pi"`
- **THEN** the Runner SHALL invoke the Pi runtime's Compact or Reset operation
- **AND** for a binding carrying `runtime: "opencode"` SHALL preserve the existing OpenCode Compact/Reset behavior

### Requirement: OpenCode session command behavior is unchanged

Admitting the Pi runtime SHALL NOT alter the behavior of any session command on an OpenCode-bound AgentSession. OpenCode Follow-up, Cancel, Compact, and Reset SHALL observe the same request shape, result shape, error vocabulary, and side effects as before this change, and SHALL NOT route through any Pi runtime code path.

#### Scenario: OpenCode Follow-up is unchanged

- **WHEN** Follow-up is issued on an OpenCode-bound session
- **THEN** the request and result SHALL match the pre-change OpenCode Follow-up behavior
- **AND** no Pi runtime code path SHALL be invoked

#### Scenario: OpenCode Compact and Reset are unchanged

- **WHEN** Compact or Reset is issued on an OpenCode-bound session
- **THEN** the operation SHALL behave identically to before this change
- **AND** SHALL NOT route through any Pi runtime path

### Requirement: Routing selects the runtime from the persisted binding, not from a cached or hardcoded source

The runtime selected for a command SHALL be the `runtime` of the AgentSession's current persisted Runtime Session binding. An in-memory cache of the physical session instance MAY be used as an optimization, but the dispatch decision MUST be driven by the binding on the command, so that a binding replaced by Reset or by a backend change is honoured immediately.

#### Scenario: Dispatch follows a replaced binding

- **WHEN** a command is issued after the AgentSession's binding was replaced (for example by a prior Reset)
- **THEN** the Runner SHALL dispatch the command to the runtime named by the new persisted binding
- **AND** SHALL NOT dispatch it to the runtime of the superseded binding

### Requirement: Unknown or unavailable runtimes report through the existing error vocabulary

When a command targets a runtime that the Runner cannot dispatch (an unrecognized runtime name, or a recognized runtime whose backend is not ready), the Runner SHALL report it through the existing command error vocabulary (`unavailable` / `notStarted`) and SHALL NOT silently drop the command or synthesize a success result.

#### Scenario: Unrecognized runtime is reported, not silently dropped

- **WHEN** a command targets a session binding whose `runtime` is neither `pi` nor `opencode`
- **THEN** the Runner SHALL return an error result using the existing vocabulary
- **AND** SHALL NOT execute the command against any runtime
