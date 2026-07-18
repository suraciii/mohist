### Requirement: A work turn carries a per-turn deadline declaration

A work-owned turn SHALL carry a deadline declaration on the turn request. The Workflow Action SHALL declare the deadline for every Workflow Inline Agent turn, defaulting to 60 minutes when no explicit override is supplied; an explicit executor override SHALL take precedence over the default. The runtime SHALL NOT expose the deadline value to the prompt body, to render variables, or to any agent-visible text; the deadline is a runtime-internal scheduling fact, not an instruction. A turn request that carries no deadline declaration SHALL NOT run the two-stage wrap-up protocol; it SHALL be awaited without warning injection, and the existing signal-driven abort backstop SHALL continue to apply.

#### Scenario: The Action declares the default 60-minute deadline

- **WHEN** the Workflow Action adapter builds a turn request and the executor has not supplied an explicit deadline
- **THEN** the turn request SHALL carry a 60-minute deadline
- **AND** the runtime SHALL run the two-stage wrap-up protocol against that deadline

#### Scenario: An executor override wins over the default

- **WHEN** the executor supplies an explicit deadline for a turn
- **THEN** the turn request SHALL carry that deadline
- **AND** the runtime SHALL run the two-stage wrap-up protocol against the supplied value, not the 60-minute default

#### Scenario: The deadline value is not surfaced to the agent

- **WHEN** a turn with a declared deadline is sent to OpenCode
- **THEN** the prompt body, system variant, and any rendered variables SHALL NOT contain the deadline value or any static "N minutes remaining" phrasing
- **AND** the only time signal the agent can act on SHALL be the injected wrap-up warning

#### Scenario: A turn without a declared deadline does not run the protocol

- **WHEN** a turn request carries no deadline declaration
- **THEN** the runtime SHALL NOT inject a wrap-up warning
- **AND** SHALL continue to await the prompt and SHALL honour the existing signal-driven abort backstop

### Requirement: A single wrap-up warning is injected 5 minutes before the deadline

For each turn that carries a declared deadline, the runtime SHALL inject exactly one wrap-up warning by calling `client.session.promptAsync()` on the turn's physical Session. The warning SHALL be scheduled at deadline-minus-5-minutes. When the declared deadline is less than 5 minutes, the warning SHALL be injected at turn start rather than skipped. The warning SHALL be injected at most once per Prompt execution: retries, restarts, or subsequent prompts on the same Session SHALL each be treated as a fresh Prompt execution with their own (single) warning. The warning injection is fire-and-forget — the runtime SHALL NOT await its completion before continuing to await the prompt response.

#### Scenario: The warning fires 5 minutes before the deadline

- **WHEN** a turn with a declared deadline of at least 5 minutes is in progress and the clock reaches deadline-minus-5-minutes
- **THEN** the runtime SHALL call `client.session.promptAsync()` on the turn's physical Session exactly once
- **AND** SHALL NOT inject a second warning for the same Prompt execution even if the turn continues

#### Scenario: A short deadline warns at turn start

- **WHEN** a turn carries a declared deadline of less than 5 minutes
- **THEN** the runtime SHALL inject the wrap-up warning at turn start
- **AND** SHALL NOT inject a second warning for the same Prompt execution

#### Scenario: The warning injection is not awaited

- **WHEN** the runtime injects the wrap-up warning via `client.session.promptAsync()`
- **THEN** the runtime SHALL return to awaiting the prompt response without waiting for the warning's processing to complete

#### Scenario: A warning-injection failure does not fail the turn

- **WHEN** the `client.session.promptAsync()` call for the warning fails or its result is uncertain
- **THEN** the runtime SHALL NOT fail the turn solely on the basis of that failure
- **AND** SHALL NOT retry the injection
- **AND** SHALL continue to await the prompt response until it arrives or the deadline aborts the turn

### Requirement: The wrap-up warning text is task-agnostic

The injected warning text SHALL be fixed by the runtime and task-agnostic. It SHALL convey, in fixed order: stop starting new work; commit current changes; leave a progress record in this task's progress channel; end the turn. The warning text SHALL NOT reference any expect marker, output marker, file name, artifact path, prompt contract name, or any task-specific identifier. Each Prompt execution on the same Session SHALL receive warning text of identical wording.

#### Scenario: The warning carries no task-specific identifiers

- **WHEN** the runtime composes the wrap-up warning for any turn
- **THEN** the warning text SHALL NOT contain marker names (for example `unfinished`, `promise`), file names (for example `progress.txt`), or artifact paths
- **AND** SHALL be the same fixed wording regardless of which Workflow task or profile issued the turn

#### Scenario: The warning instructs a deterministic wrap-up sequence

- **WHEN** the runtime injects the wrap-up warning
- **THEN** the text SHALL instruct the agent, in order, to stop new work, commit current changes, leave a progress record in the task's progress channel, and end the turn
- **AND** SHALL NOT prescribe commit messages, file locations, or completion markers

### Requirement: The deadline terminates an still-running turn as interrupted

When the deadline is reached while the prompt response has not yet arrived, the runtime SHALL call `client.session.abort()` on the physical Session and return an `interrupted` result. The work SHALL fail. This behaviour SHALL be unchanged from the pre-existing deadline-abort path: the two-stage protocol adds the warning injection, not a different termination outcome.

#### Scenario: A turn still running at the deadline is aborted

- **WHEN** the declared deadline is reached while the awaited `client.session.prompt()` response has not arrived
- **THEN** the runtime SHALL call `client.session.abort()`
- **AND** SHALL return an `interrupted` result
- **AND** the work SHALL fail

### Requirement: A warned turn that ends on its own is not aborted

Once the awaited prompt response has arrived, the turn is complete and the runtime SHALL NOT issue an abort for that Prompt execution, regardless of whether the wrap-up warning has fired. A turn that the agent ends normally after receiving the warning SHALL be evaluated by the existing task completion contract; the runtime SHALL NOT synthesise an interrupt, SHALL NOT roll back or auto-commit residual state, and SHALL NOT alter worktree cleanup semantics. Whether the agent reports completion, `unfinished`, or any other contract outcome is the task's own concern — the runtime's only deadline-side obligation is to have warned once.

#### Scenario: A warned turn that completes early is not aborted

- **WHEN** the wrap-up warning has been injected and the awaited prompt response arrives before the deadline
- **THEN** the runtime SHALL NOT call `client.session.abort()` for that Prompt execution
- **AND** SHALL return the normal completion facts
- **AND** the task completion contract SHALL evaluate the result without any runtime-side override

#### Scenario: A warned turn's residual state is left as-is

- **WHEN** a warned turn ends — whether by completion or by deadline abort
- **THEN** the runtime SHALL NOT auto-commit, auto-rollback, or otherwise mutate the worktree beyond the abort call already specified
- **AND** worktree cleanup semantics SHALL be unchanged

### Requirement: The warning is visible in the transcript via the existing follow-up path

The injected warning SHALL enter the Session message stream as a user follow-up and SHALL be picked up by the running turn at its next iteration boundary — the same receive path as a user-submitted follow-up. The warning's appearance in the transcript SHALL be produced by the existing event projection with no special-case plumbing: no dedicated event type, no warning-specific transcript entry kind, and no extra UI surface. The runtime SHALL NOT treat long tool-call pickup delays as a failure; the worst case degrades to a deadline abort without the warning having been processed, and that outcome SHALL remain acceptable.

#### Scenario: The warning enters the same path as a user follow-up

- **WHEN** the runtime injects the wrap-up warning via `client.session.promptAsync()`
- **THEN** the message SHALL be written to the Session message stream as a user follow-up
- **AND** SHALL be picked up by the running turn at its next iteration boundary
- **AND** SHALL appear in the transcript through the existing event projection

#### Scenario: A long tool call delays warning pickup without failing the turn

- **WHEN** the running turn is inside a long tool call at the moment the warning is injected
- **THEN** the runtime SHALL NOT classify the delay as a failure
- **AND** the warning MAY be processed only after the current tool call completes
- **AND** if the deadline fires before pickup, the turn SHALL be aborted and that outcome SHALL be accepted

### Requirement: Deadline scheduling is driven by an injectable clock

All time-based scheduling introduced by the two-stage wrap-up protocol — when to inject the warning and when the deadline has been reached — SHALL read from a clock injected through the runtime's dependencies. The runtime's production path MAY read wall-clock time through that injection point. Tests exercising the protocol SHALL drive the clock deterministically; they MUST NOT depend on real timers, real wall-clock elapsed time, or `setTimeout`-based polling. No deadline-related test SHALL assert on elapsed real time or on a sleep/delay boundary.

#### Scenario: Tests drive the protocol through a fake clock

- **WHEN** the two-stage wrap-up protocol is exercised in tests
- **THEN** the clock used for warning scheduling and deadline detection SHALL be the injected fake clock
- **AND** advancing the fake clock to deadline-minus-5-minutes SHALL trigger the warning injection
- **AND** advancing the fake clock to the deadline SHALL trigger the abort path
- **AND** no test SHALL observe real elapsed time or real timer firing

### Requirement: Mid-turn warning pickup is verified against a real OpenCode server

The implementation SHALL produce a smoke record demonstrating, against a real OpenCode server, that a `client.session.promptAsync()` message injected while a turn is running is picked up and processed by that turn at its next iteration boundary. The smoke record SHALL be persisted under `openspec/changes/issue-439/` alongside the change artifacts and SHALL be referenced by the implementation tasks. The smoke is verification evidence only; it SHALL NOT be re-run as part of the default test suite.

#### Scenario: A smoke record captures real-OpenCode mid-turn pickup

- **WHEN** the implementation completes the two-stage wrap-up protocol
- **THEN** a smoke record SHALL exist under `openspec/changes/issue-439/` demonstrating that a mid-turn `promptAsync()` message is picked up by the running turn on a real OpenCode server
- **AND** the default test suite SHALL NOT invoke real OpenCode
