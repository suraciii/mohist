# Runner Execution Environment

The Runner process inherits a service-manager environment. An operator's
interactive `PATH` can therefore contain a usable tool that a task cannot
find. A durable design must preserve the control-plane ownership rules while
allowing the host owner to update that environment without interrupting work.

## Core Decisions

- Keep raw environment values on the Runner host; Server receives metadata and
  observations only.
- Use a separate, versioned snapshot file. Do not overload `runner.env` or
  patch one tool path into the unit file.
- Treat capture, application, and observation as different facts with
  different owners.
- Reuse the existing Runner update fence for admission, but add a wait path for
  environment applications. Keep the release updater's current refusal
  semantics unchanged.
- Confirm activation with both a new process generation and the target
  environment version.
- Never adopt work from an old process generation or replay a completed Input.

## System Boundary

The local environment manager owns capture, canonicalisation, the candidate,
the active and previous files, the systemd restart, and recovery. The Runner
owns only the environment it loaded and its process-lifetime observation. The
Server owns the update fence, work admission, sanitized environment metadata,
and the read projection. Workflow and AgentJob aggregates remain the owners of
work settlement and result acknowledgement. Web renders the projection and
can request cancellation; it cannot capture a remote terminal environment.

```text diagram
+---------------+                   +--------+                                     +--------+
| Local manager |                   | Server |                                     | Runner |
+-------+-------+                   +----+---+                                     +----+---+
        |                                |                                              |
        |   Publish candidate metadata   |                                              |
        +------------------------------->|                                              |
        |                                |                                              |
        |    Begin environment fence     |                                              |
        +------------------------------->|                                              |
        |                                |                                              |
        |Owner rows and settlement facts |                                              |
        |<...............................+                                              |
        |                                |                                              |
        |                                |       Report work and acknowledgements       |
        |                                |<---------------------------------------------+
        |                                |                                              |
        |                           Restart after settlement                            |
        +------------------------------------------------------------------------------>|
        |                                |                                              |
        |                                |Report new generation and environment version |
        |                                |<---------------------------------------------+
        |                                |                                              |
        |  Confirm or reject activation  |                                              |
        |<...............................+                                              |
        |                                |                                              |
        |     Release matching fence     |                                              |
        +------------------------------->|                                              |
        |                                |                                              |
+-------+-------+                   +----+---+                                     +----+---+
| Local manager |                   | Server |                                     | Runner |
+---------------+                   +--------+                                     +--------+
```

The diagram is a boundary map, not a second protocol definition. The rules
below are authoritative.

## Domain Model

### Environment snapshot

An environment snapshot is an immutable set of allowlisted, non-secret values
identified by a content digest. It is host-scoped and applies at Runner process
start. The active snapshot, one candidate, and one previous snapshot may exist
at the same time. A candidate never changes the active process. A captured
candidate is not an application until Server has accepted its metadata.

### Environment application

An application is a local transaction identified by an update id and a target
snapshot version. Its state is the state machine in the product guide. The
transaction does not own work rows, workflow state, Session identity, or
Runtime generation. It may hold one update fence and may remove only that fence.

### Environment observation

An observation is a report from one Runner process containing its process
generation, loaded snapshot version, load time, and optional sanitized tool
checks. It is evidence of what that process loaded, not a command to Server and
not a capability claim.

## Snapshot Rules

Capture reads the environment of the invoking user without starting a shell.
Canonicalisation is deterministic: filter the fixed allowlist, validate values,
deduplicate `PATH` while preserving first occurrence, sort variable names for
the digest, and write an atomic mode-`0600` file. The version is computed from
the canonical lines, not from file timestamps or the process environment order.

The systemd unit loads the snapshot through an optional `EnvironmentFile`.
The snapshot file is outside managed release directories, so release promotion
cannot replace it. The managed update transaction must preserve that directive
and must not copy its own `PATH` into the snapshot.

## Application Protocol

1. The local manager validates the candidate and publishes only its metadata.
   If this publish fails, the candidate remains local and cannot be applied.
2. Server creates one environment application and the matching update fence.
   A second application, a missing Runner, or a conflicting managed update is
   rejected without changing the active snapshot.
3. The local manager waits for the settled predicate:
   `owner ledgers empty AND current-generation in-flight count = 0 AND
   current-generation awaiting-ack count = 0`. The Server must use the current
   process generation. Missing, stale, or disconnected evidence is not empty.
4. The manager saves the active file, installs the candidate atomically, and
   restarts the service. The old file remains recoverable until confirmation.
5. Runner registers with a new process generation and reports the loaded
   version. Server marks the application active only when both values match the
   target. Only then may the manager remove the matching fence.

The environment application fence is durable until cancellation, confirmed
activation, or confirmed rollback. Losing the local CLI does not release it;
the next CLI invocation or an authorized Web action must inspect and resolve the
application.

The existing release updater may use the same update fence API, but its
transaction remains a release transaction and continues to reject an active
Runner. Environment waiting must not silently change release update behavior.

## Wire and Read Model

Runner registration and heartbeat add nullable `environmentVersion` and
`environmentLoadedAt` fields. The Server stores them with the Runner identity.
Candidate and application commands carry only:

- `runnerId`, `updateId`, and snapshot version;
- source kind, operating-system user, capture time, and changed variable names;
- state, failure code, and bounded tool-check metadata.

The global Runner projection adds an optional `environment` object. It is
assembled like other Runner status facts and remains visible for an offline
Runner from durable state. The projection must not activate a grain merely to
render status. Raw snapshot values, complete path lists, credentials, command
arguments that contain secrets, and command output are outside the wire model.

The latest poll observation must retain enough information to evaluate the
settled predicate for the current generation. Counts are sufficient for the
predicate; owner rows remain the source of task identity and display.

## Failure and Recovery

Validation failure leaves the active snapshot and fence unchanged. Cancellation
is accepted only from `waiting` and clears only the matching fence. A restart
or confirmation failure first attempts to restore the previous snapshot. A
confirmed rollback records `failed` and releases the matching fence. If the
rollback cannot be witnessed, the application is `unconfirmed` and admission
remains fenced until local recovery confirms a version.

If the Runner disappears during the handoff, normal process-generation closeout
settles any remaining owned work as `runner-lost`. A later process must register
with a new generation; it must not claim the old work or report a result for an
old generation. Environment application recovery never adopts a Turn, Session,
or Input from the old process.

## Tool Checks

Tool checks are local, bounded child processes launched with an argument vector,
the Runner root as working directory, and the selected snapshot. The local
manager owns execution; Server stores only the sanitized observation. There is
no generic Server-to-host shell endpoint. A check cannot change admission or
Runtime readiness and cannot prove that a project test will compile.

## Implementation Order and Gates

1. Add the product and design contracts and link them from the Runner guides.
2. Add the Go snapshot canonicaliser, atomic file writer, install integration,
   and managed-unit preservation. Prove it with fake filesystem, systemd, time,
   and command seams.
3. Add Runner identity fields and the current-generation poll settlement
   observation. Prove registration, heartbeat, restart, and stale-generation
   behavior in Server and Runner specs.
4. Add the environment application fence, wait, cancellation, rollback, and
   confirmation protocol. Prove each state transition with injected time and
   fake work ledgers.
5. Add the sanitized status projection, CLI read/write commands, tool checks,
   and Web detail presentation. Prove no raw values or command output cross the
   Server boundary.
6. Run focused tests, then `npm run test:fast`, then the full
   `npm run verify`. Live validation must first prove Runner occupancy is zero
   before a restart and must record the exact process generation and environment
   version.

## Alternatives Considered

### Patch the unit with the current `PATH`

Rejected. It creates configuration drift, has no candidate or rollback, and a
successful restart does not prove which environment the process loaded.

### Store the full environment on Server

Rejected. It leaks credentials, makes Server a host-configuration owner, and
cannot represent a terminal environment that has not been captured locally.

### Add a general remote shell endpoint for checks

Rejected. It expands the Runner control surface and makes tool diagnostics a
remote execution capability. Local, argument-vector checks provide the required
evidence without a new privileged channel.

## Status

The install snapshot and Runner environment observation fields are implemented.
The update fence now retains current-generation settlement counts even while a
draining poll cannot claim new work. Server now persists one environment
application per Runner, preserves its fence across process replacement, and
requires a current-generation target-version witness before confirmation.
Local snapshot apply/rollback, sanitized status projection, CLI refresh
commands, and tool checks remain unimplemented slices of Issue #1009.
