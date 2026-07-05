### Requirement: `runCommand` accepts an optional per-command timeout

`runCommand` (`system/process.ts`) SHALL accept an optional `timeoutMs` parameter. When `timeoutMs` is omitted or non-positive, the call SHALL behave exactly as it does today — the command runs only under the caller-supplied work-level `AbortSignal`, and the resolved `CommandResult` is byte-identical to the current shape. The `timeoutMs` parameter is the single knob every caller composes with; `runCommand` itself SHALL NOT decide which commands are timed out.

#### Scenario: Omitting the timeout leaves behavior unchanged

- **WHEN** a caller invokes `runCommand` without a `timeoutMs`
- **THEN** the child process SHALL run solely under the supplied `AbortSignal`
- **AND** the resolved result SHALL remain the existing `{ exitCode, stdout, stderr }` aggregate with no timeout category

#### Scenario: A non-positive timeout is treated as no timeout

- **WHEN** a caller passes a non-positive `timeoutMs` (e.g. `0` or negative)
- **THEN** `runCommand` SHALL NOT arm any per-command timer
- **AND** SHALL behave as if `timeoutMs` were omitted

#### Scenario: The timeout does not by itself decide which commands are timed

- **WHEN** `runCommand` is inspected in isolation
- **THEN** it SHALL expose only the per-command timeout primitive
- **AND** SHALL NOT encode which command names (e.g. `git clone` vs `git rev-parse`) receive a timeout

### Requirement: A timed-out command terminates the entire subprocess tree

When the per-command timeout elapses, `runCommand` SHALL terminate the **entire subprocess tree**, not just the direct child. The child SHALL be spawned as the leader of its own process group (detached spawn) so that on expiry the whole group can be signaled; `runCommand` SHALL then signal the process group (negative-PID kill) so that helper processes the child spawned — e.g. `git-remote-http`, `git-remote-https` — are reaped together with the parent rather than orphaned. `killProcess` SHALL kill the group when the child was spawned detached.

#### Scenario: A hung direct child is killed on timeout

- **WHEN** a command is run with a `timeoutMs` and the child process neither exits nor produces a result before `timeoutMs` elapses
- **THEN** `runCommand` SHALL terminate the child process
- **AND** SHALL resolve with a structured timeout result rather than waiting for the work-level signal

#### Scenario: Helper subprocesses are reaped with the parent

- **WHEN** a timed command (e.g. `git fetch`) has spawned a helper subprocess (e.g. `git-remote-http`) and the per-command timeout elapses
- **THEN** `runCommand` SHALL kill the process group led by the direct child
- **AND** the helper subprocess SHALL be terminated alongside the direct child
- **AND** no orphaned helper process SHALL remain

#### Scenario: killProcess reaps the group for a detached child

- **WHEN** `killProcess` is invoked on a child that was spawned detached (leading its own process group)
- **THEN** it SHALL signal the entire process group, not only the direct child PID

### Requirement: A timeout result is distinguishable from a normal non-zero exit

A per-command timeout SHALL resolve to a structured result that is distinguishable from a command that ran to completion and exited non-zero. The result SHALL carry an exit/status category so a caller can tell "the command hung" from "the command ran and failed" without parsing stderr. A command that exits normally (zero or non-zero) before the timeout SHALL resolve without the timeout category.

#### Scenario: A timeout is categorically distinct from a non-zero exit

- **WHEN** a command exceeds its `timeoutMs` and is killed
- **THEN** the resolved result SHALL carry a timeout status category
- **AND** the result SHALL be distinguishable from a result produced by a command that exited with a non-zero code before the timeout

#### Scenario: A command that completes before the timeout has no timeout category

- **WHEN** a command exits (zero or non-zero) before `timeoutMs` elapses
- **THEN** the resolved result SHALL carry a normal-exit category
- **AND** SHALL NOT carry a timeout category

#### Scenario: Captured output is preserved up to the kill

- **WHEN** a timed-out command had produced stdout/stderr before being killed
- **THEN** the structured result SHALL include the output captured up to the point of termination

### Requirement: The per-command timeout is orthogonal to the work-level abort and existing action timeouts

The caller-supplied work-level `AbortSignal` SHALL continue to abort a running command with unchanged semantics; the per-command `timeoutMs` is an additional, independent knob. `core/script`'s `with.timeout` and `mohist/acp-agent`'s existing timeout behavior SHALL be preserved: they continue to layer `timeoutSignal` over `context.signal`, and SHALL NOT be altered or broken by the new `runCommand` `timeoutMs`. The two timeout mechanisms SHALL NOT interfere with one another. The `timeoutSignal` helper in `actions/registry.ts` SHALL be reused (not duplicated) when wiring the per-command timeout.

#### Scenario: Work-level abort still terminates the command

- **WHEN** a command is run with a `timeoutMs` and the work-level `AbortSignal` aborts before the per-command timeout elapses
- **THEN** the command SHALL be terminated by the work-level abort exactly as before
- **AND** the per-command timer SHALL NOT mask or delay the abort

#### Scenario: core/script `with.timeout` behavior is unchanged

- **WHEN** a `core/script` action is configured with `with.timeout`
- **THEN** it SHALL continue to apply `timeoutSignal` over `context.signal`
- **AND** its observable behavior SHALL be unchanged by the introduction of `runCommand`'s `timeoutMs`

#### Scenario: The timeout helper is reused, not duplicated

- **WHEN** the per-command timeout is wired into `runCommand`
- **THEN** the existing `timeoutSignal` helper SHALL be the single implementation of signal-layered timeout
- **AND** no parallel duplicate of that logic SHALL be introduced

### Requirement: Timeout behavior is verifiable without real network or wall-clock

Tests for the per-command timeout SHALL use fake or controlled subprocesses (via the existing `setXxxGitRunnerForTest` / `gh` runner injection seams) and an injected or fake timer to drive the timeout. Tests SHALL NOT depend on real network, real `git`/`gh` processes, or wall-clock timing.

#### Scenario: A hung subprocess is simulated under a fake timer

- **WHEN** a test simulates a hung network command via an injected runner that never resolves
- **AND** the fake timer is advanced past `timeoutMs`
- **THEN** the command SHALL resolve as a structured timeout result
- **AND** no real network or real child process SHALL be invoked

#### Scenario: The timer is driven by injection, not wall-clock

- **WHEN** timeout behavior is asserted in a test
- **THEN** the elapsed time SHALL be controlled by a fake timer
- **AND** SHALL NOT rely on real elapsed wall-clock duration
