### Requirement: Per-work resource containment on the runner

The Runner MUST bound each work item's resource consumption through
deployment-backed per-work resource configuration, and MUST terminate a work
whose execution exceeds its bounds. A work terminated by containment MUST be
reported with a definite result carrying a resource-containment reason so it
settles through the normal report path. Containment MUST NOT require killing
the runner process itself.

#### Scenario: A work exceeds its memory bound

- **WHEN** a work item's execution exceeds its configured per-work resource bound
- **THEN** the runner terminates that execution without killing the runner process
- **AND** the work is reported with a definite failed result carrying a resource-containment reason

#### Scenario: A work stays within bounds

- **WHEN** a work item executes within its configured resource bounds
- **THEN** its execution proceeds and completes without containment interference

### Requirement: Containment does not cascade to sibling work

Terminating a runaway work MUST NOT terminate, corrupt, or strand sibling
in-flight work on the same runner, nor lose awaiting-acknowledgement results.
Sibling work MUST continue executing and reporting normally through and after
the containment event.

#### Scenario: Runaway task terminated while siblings run

- **WHEN** one work item is terminated by resource containment while other work items are in flight on the same runner
- **THEN** the sibling work items continue executing and deliver their reports normally
- **AND** no sibling transitions to an error or interrupted state as a side effect of the containment

#### Scenario: Shared runtime generation torn down for quarantine

- **WHEN** a shared runtime generation is quarantined and torn down while other works hold completed results that are not yet acknowledged
- **THEN** those results are still delivered to the server under their original work identities

### Requirement: Bounded quarantined generation drain

A quarantined OpenCode runtime generation MUST drain within a bounded
deadline. When the deadline elapses, the runtime MUST forcibly release the
generation so a replacement generation can be created. A wedged generation
MUST NOT block runtime replacement indefinitely.

#### Scenario: Quarantined generation never drains on its own

- **WHEN** a quarantined generation still has active turns that do not end within the bounded drain deadline
- **THEN** the runtime forcibly releases the generation at the deadline and starts a replacement generation
- **AND** new work can execute on the replacement generation without waiting on the wedged one

#### Scenario: Quarantined generation drains normally

- **WHEN** a quarantined generation's active turns end before the bounded drain deadline
- **THEN** the generation is released promptly and replacement proceeds without waiting for the full deadline

### Requirement: Bounded runtime shutdown paths

Runtime shutdown paths — OpenCode process-tree termination and undici
dispatcher close, and the pi runtime's service close — MUST be bounded by a
deadline instead of waiting indefinitely on hung requests or unresponsive
processes. When the deadline elapses, shutdown MUST proceed by abandoning the
wait and forcing teardown, so runner shutdown, runtime replacement, and
generation teardown complete within a bounded time.

#### Scenario: Dispatcher close hangs on an in-flight request

- **WHEN** an OpenCode dispatcher close would block forever on a hung in-flight request because request timeouts are disabled
- **THEN** the close path returns within its bounded deadline
- **AND** the runner proceeds with shutdown or replacement rather than waiting on the hung request

#### Scenario: Process-tree termination hangs

- **WHEN** termination of an OpenCode server process tree does not complete within the bounded shutdown deadline
- **THEN** the shutdown path proceeds past the deadline without blocking the runner indefinitely

### Requirement: Execution plane remains operational after containment

After a contained work termination or a forced generation teardown, the
runner's execution plane MUST remain operational: execution slots are
released, shared runtimes are replaceable, and newly dispatched work executes
and reports normally without requiring a runner process restart.

#### Scenario: Next dispatch after a containment event

- **WHEN** the server dispatches new work to a runner after a runaway work was terminated by containment
- **THEN** the runner admits and executes the new work and reports its result normally

#### Scenario: Replacement generation after forced teardown

- **WHEN** a runtime generation was forcibly released at its drain deadline
- **THEN** the runtime produces a working replacement generation that serves subsequent OpenCode work
