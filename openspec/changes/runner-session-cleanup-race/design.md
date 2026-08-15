# Design: Workflow Cleanup Admission And Delivery

## Admission boundary

For cleanup attempt `n`, Runner derives a stable operation identity from the
Workflow run, task attempt, work ID, and `n`. It submits a dedicated cleanup
admission request carrying the original frozen execution identity plus the
cleanup operation identity. Server-side AgentSession admission is replay
idempotent: the exact operation returns the same cleanup input and Agent turn;
a conflicting operation or binding is rejected without creating a second
turn.

The admission checks the original Workflow binding through the Workflow grain:
the task attempt is still running, its settlement matches the original
binding, and the original Agent turn is terminal. The cleanup turn is a
Session-owned follow-up and has no new Workflow execution binding. Runtime
events after admission therefore use the Session turn identity and cannot be
misclassified as a second task result.

## Outbox scheduling

The durable outbox retains two identities:

- The delivery key remains the complete immutable execution identity for normal
  Workflow records. A wire request never mixes work attempts, Agent turns,
  Runners, or runtime Sessions.
- A logical scheduling key covers project, Workflow run, and Session name.
  It serializes records for a reused physical Session across turns.

The original terminal facts are delivered and acknowledged before cleanup
admission is selected. The cleanup admission is an isolated boundary record;
the subsequent Session-owned runtime input is also isolated. Existing durable
retry and receipt matching remain fail-closed.

## Required focused coverage

Runner tests must prove Pi/generic cleanup uses a deterministic operation and
turn identity, does not submit a normal second Workflow `session.input`, and
does not emit cleanup runtime facts before a matching cleanup receipt. Outbox
tests must hold the original terminal receipt open and prove cleanup admission
is not delivered concurrently. Server tests must prove exact replay is
idempotent, conflicting identity is rejected without a replacement turn, and
cleanup is denied after the original Workflow task is no longer eligible.
