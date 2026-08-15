# Runner Guide

Runner is Mohist's execution backend. Server decides what to do; Runner performs
the work.

## Why Runner Exists

Mohist separates the **control plane from the execution plane**.

- **Server, the control plane:** Maintains state, makes decisions, and emits
  events.
- **Runner, the execution plane:** Executes Actions, operates Git, writes files,
  and connects to execution backends such as OpenCode.

This separation matters because:

- Runner can crash, restart, or be replaced without losing Server state.
- Future Runner instances can execute concurrently on different machines with
  different capacities.
- Server validates ownership instead of trusting Runner reports implicitly.

## Starting Runner

```bash
mo install runner --repo-root "$PWD"  # First registration and start
# Later
mo service start runner
```

The first installation requests a one-time enrollment from the running Server.
Runner exchanges it for a machine credential and stores that credential under
its root. Later starts reuse the credential.

After startup, Runner:

1. Connects to local Server at `http://localhost:3456` by default.
2. Registers itself and declares its capacity and capabilities.
3. Waits for work.
4. Starts execution when Server assigns a task.

Start Runner **after Server**. It cannot connect while Server is unavailable.

## Checking Runner State

```bash
mo runner status
# Output includes Runner state
```

The Web UI also shows Runner state:

- The Runner-unavailable banner above the board.
- The Runners page and each Runner detail page.
- Runner heartbeat events on the Activity page.

## Concurrent Capacity

Server gives each Runner one shared execution slot by default:

- At most one Workflow task or AgentJob executes on that Runner at once.
- Additional work waits for capacity after it starts.

Change slots on the Runner detail page in the Web UI. Server owns this limit so
the next dispatch observes a change without restarting Runner.

Do not increase capacity without accounting for resource use:

- Each executing AgentSession consumes CPU and memory.
- Excess concurrency can trigger model API limits, overload the host, and cause
  Git lock conflicts.
- Increase from the default only after observing the host and provider limits.

## Execution Ownership

Runner owns host-specific side effects because they are replaceable execution
state. Server owns durable work decisions because a Runner can disappear at any
time. This boundary explains why an interrupted Runner can be replaced without
changing Workflow truth, and why unreported or uncommitted files cannot be
treated as a durable result.

For one task, Runner prepares an isolated workspace, resolves the declared
Action input, invokes the execution backend, and reports facts and outputs to
Server. It validates declared output expectations before reporting success and
reclaims the workspace when the Workflow no longer needs it. The complete
Action contract is in [Action Contracts](actions/README.md).

Runner owns the complete process tree for every host command it starts. A
command result includes output produced before the command exits; leftover
subprocesses cannot keep the result open or write into later work.

When a Workflow workspace is first materialized, Runner transfers only the
repository data needed to establish its base and run branches. Later stages can
still rebase and integrate that branch normally. Repository transfers remain
bounded, and a failed materialization does not publish or retain a partial
workspace.

## Workspace Location

An Issue uses a named Workspace such as `issue-42`. Runner materializes it under
its configured root and records the actual home Runner and path. Inspect that
binding instead of guessing an internal directory:

```bash
mo workspace view issue-42 --json home
```

The directory persists across the Issue's Stages and bound Sessions, but it is
rebuildable execution state. Commit and push work that must survive host loss.
Do not manually delete or change its branch, marker, or origin while work runs.

## Runner Failure

If the Runner process crashes:

- A Workflow that has not begun execution waits for an available Runner.
- Executing Workflow work and AgentJobs fail with `runner-lost`; Mohist does not
  claim that an unconfirmed external effect can continue transparently.
- After Runner returns, retry or rerun blocked Workflow work explicitly. A later
  AgentJob is a new work intent rather than an automatic replay.

Workflow state is not lost because it lives in Server, not Runner.

## Multiple Runners

Server can register multiple Runners and enforces each Runner's slots
independently. New work uses eligible capacity. A materialized Workspace has a
home Runner so later Sessions can reuse its files. AgentJob scheduling can clear
an offline home and rematerialize on another Runner. A WorkflowRun remains
assigned to its Runner and does not migrate automatically; restore that Runner
before retrying the Workflow. Unpushed local files cannot move between hosts.

## Debugging Runner

### Runner Logs

```bash
mo service logs runner          # Operational logs from service-manager
# Or inspect stdout from the Runner process directly
```

### Execution Logs for One Issue

```bash
mo issue logs <number>
mo issue events <number>             # Event stream
mo session list --issue <number>     # AgentSessions for the Issue
```

### Common Runner Problems

| Symptom | Cause | Resolution |
|---|---|---|
| The board shows "No runner is connected" | Runner is not running | Run `mo service start runner` |
| An Issue waits after starting | No Runner has capacity | Start Runner; the Workflow continues automatically |
| A task produces no output for a long time | OpenCode is stuck | Run `mo run pause --issue <number>` and inspect logs |
| Workspace identity error | Marker, branch, or origin was changed manually | Preserve required commits, remove that workspace, and retry |
| Git push failed | Remote Repository permission | Configure an SSH key or token |

`mo service start runner` preserves the enrolled managed-service configuration.
Use `npm run dev:runner` only when running Runner from a source checkout for
development.

## Runner Configuration

Configure host-local Runner behavior with environment variables installed by
the service manager. Configurable values include:

- Server URL.
- Runner identity and root directory.
- Poll, heartbeat, and cleanup intervals.

Dispatch slots are control-plane state and are changed from the Runner detail
page, not through Runner startup options.

Runner does not select a global Runtime backend through `type`. A Workflow
task's `uses` value selects an execution-backend Action such as
`mohist/opencode` or `mohist/pi`. Action Input supplies model options. See
[Action Contracts](actions/README.md).

Runtime replacement and shutdown are bounded by host-local settings:

- `QUARANTINE_DRAIN_TIMEOUT_MS` controls how long a quarantined OpenCode
  generation may drain before active turns fail with
  `generation-drain-timeout`; the default is 60 seconds.
- `RUNTIME_SHUTDOWN_TIMEOUT_MS` controls graceful OpenCode dispatcher/process
  teardown and Pi service shutdown; the default is 30 seconds. On expiry the
  runner abandons the wait, destroys the transport, and proceeds with the
  replacement or shutdown. OpenCode termination sends the graceful stop first
  and uses a best-effort process-group `SIGKILL` when a process handle is
  available.

Both settings are millisecond values and should be set in the service manager
configuration. A forced generation release only fails turns still active at
the deadline; results already waiting for acknowledgement remain journaled and
are reported under their original work identities.

## Self-hosting

For a long-running Runner managed as a service instead of foreground
`dev:runner`, see [Self-hosting](self-host.md).

---

Implementation source: `packages/runner/` and
`packages/server/src/Mohist.Server/Runner/`.
