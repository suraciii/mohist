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

### Legacy installation initialization

Installations created before the snapshot contract may have neither the active
file nor its `EnvironmentFile` directive. They enter the contract through the
explicit local command `mo runner environment initialize`.
This is a migration of the existing installation, not an environment refresh:

- The command reads `MainPID` from the effective `mohist-runner.service` and
  then reads only that process's allowlisted environment from `/proc/<pid>/environ`.
  It never reads the invoking terminal, starts a shell, contacts Server, or
  prints raw values.
- It applies the same canonicalisation and validation rules as capture. A
  stopped service, missing process environment, empty allowlist, or process
  identity change during the read fails closed.
- It writes `runner-environment.env` atomically with mode `0600` and inserts the
  fixed `EnvironmentFile=-%h/.config/mohist/runner-environment.env` directive
  into the unit exactly once. Legacy inline `Environment=` assignments for the
  fixed allowlist are removed from the effective `[Service]` fragment so a
  future candidate can actually remove a variable; unrelated assignments are
  preserved. Ambiguous continued `Environment=` directives fail closed. The
  command does not rewrite `ExecStart`, `WorkingDirectory`, `runner.env`,
  managed credentials, or drop-ins.
- It runs `systemctl --user daemon-reload` but does not restart the Runner,
  acquire an admission fence, or change current work. The running process keeps
  the environment that was measured; future process generations load the same
  snapshot.
- It re-reads the process environment and verifies the digest after the unit
  change. If writing, reload, or verification fails, it restores both the unit
  and any pre-existing active file before returning an error.

If an active snapshot and the directive already exist, initialization is an
idempotent verification and does not replace the snapshot. A successful
initialization is not activation evidence for a new environment version; a
later refresh still uses the normal candidate, fence, restart, and
process-generation confirmation protocol.

The local manager uses these fixed paths below `~/.config/mohist/`:

| Path | Owner | Meaning |
| --- | --- | --- |
| `runner-environment.env` | local manager/systemd | active snapshot |
| `runner-environment.candidate.env` | local manager | inactive candidate |
| `runner-environment.candidate.json` | local manager | candidate version, names, and capture time |
| `runner-environment.previous.env` | local manager | rollback snapshot retained through confirmation |
| `runner-environment-application.json` | local manager | update id and identity witness for recovery |

All five paths are host-local mode `0600` files. The JSON records contain no
environment values.

## Application Protocol

1. The local manager validates the candidate and records its metadata locally.
   The candidate file remains inactive until the Server application is accepted.
2. Server creates one environment application and the matching update fence;
   the CLI sends only the candidate version and identity witness.
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

The CLI's apply loop is bounded and injectable: every retry uses the current
identity response and a `Wait` seam. It never treats a stale, offline, or
disconnected response as settled. After a restart it requires a new process
generation and the target environment version before sending confirmation. On
failure it restores `previous.env`, restarts once, and confirms the previous
version; if that witness is unavailable it records `unconfirmed` and leaves the
Server fence and local application record for recovery.

## Wire and Read Model

Runner registration and heartbeat add nullable `environmentVersion` and
`environmentLoadedAt` fields. The Server stores them with the Runner identity.
The explicit observation report is a separate command from environment
application and carries only:

- optional process-generation identity and loaded environment witness;
- candidate source kind, operating-system user, capture time, version, variable
  names, and name-only diffs;
- bounded tool-check metadata: executable name, resolved path, selected
  snapshot kind/version, outcome, exit status, duration, and check time.

The local command is `mo runner environment check <executable>
[--snapshot active|candidate] [--report] [-- <argument> ...]`. The default is
the active snapshot. The manager passes an argv directly to a child process,
sets the Runner root as cwd, provides no stdin, captures no output, and cancels
after 10 seconds. It bounds the vector to 16 arguments and 4096 total bytes.
`--report` is opt-in so a local diagnostic never becomes a network operation by
surprise. Candidate capture may use the same report endpoint to publish its
source/user/time and name-only diff metadata.

The global Runner projection adds an optional `environment` object. The first
projection slice exposes the active environment version/load time and a
sanitized application summary:

- `updateId`, `phase`, `targetVersion`, and `previousVersion`;
- `baseProcessGeneration` and `baseConnectionGeneration` as identity
  witnesses;
- `requestedAt`, `completedAt`, and a bounded `failureCode`.

It is assembled from the process-local observation or the persisted Runner
state, so an offline Runner remains visible without activating a grain merely
to render status. Active work rows and the existing drain projection continue
to identify blockers. The latest explicit observation report is projected as a
separate child of `environment`; it is not inferred from the application
record. Reports are durable, replace candidate metadata only when supplied,
and retain at most eight latest tool checks. Raw snapshot values, complete
path lists, credentials, secret-bearing command arguments, and command output
are outside the wire model.

`POST /api/runner/{runnerId}/environment/observation` accepts the report for
operator or Runner scope. A report with `processGeneration` must match the
current Runner generation, otherwise the Server returns a stale-generation
conflict. A candidate-only report has no process witness and can be retained
while the Runner is offline. Server-side normalization is authoritative for
lengths, allowed outcome values, path shape, and name-only lists; malformed
reports fail closed rather than being displayed.

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
5. Add the sanitized observation-report contract, CLI read/write commands, and
   bounded local tool checks. Prove stale process reports are rejected and no
   raw values or command output cross the Server boundary.
6. Add the observation child projection and Web detail presentation. Prove an
   offline durable report renders without activating Runner lifecycle state.
7. Add the legacy installation initialization command and prove process-env
   capture, idempotent unit patching, no-restart behavior, and rollback with
   fake process/systemd seams.
8. Run focused tests, then `npm run test:fast`, then the full
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

The install snapshot and Runner environment identity fields are implemented.
The update fence now retains current-generation settlement counts even while a
draining poll cannot claim new work. Server now persists one environment
application per Runner, preserves its fence across process replacement, and
requires a current-generation target-version witness before confirmation. The
identity read model, local CLI candidate/apply/cancel transaction, explicit
observation report, bounded tool checks, sanitized projection, and legacy
installation initialization are now implemented. End-to-end Go task
verification and live-runtime validation remain separate gates.
