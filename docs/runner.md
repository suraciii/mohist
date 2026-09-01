# Runner Guide

Runner is Mohist's execution backend. Server decides what work means and
whether it may proceed. Runner performs the assigned work on a host.

## Product Commitments

- Server retains workflow and execution decisions when Runner crashes,
  disconnects, or is replaced.
- Runner executes only work that Server assigns and reports facts back to Server.
- A Runner never decides workflow state, AgentJob result, Session state, or
  whether an uncertain external effect succeeded.
- Server limits Runner capacity and dispatches work only to eligible capacity.
- A named Workspace remains the product identity even when its materialization
  moves or is rebuilt on a Runner.
- Runner manages every process it starts and does not leave child processes
  affecting later work.

## Why Runner Exists

Mohist separates the control plane from the execution plane:

- **Server** maintains durable state, makes decisions, and emits events.
- **Runner** executes Actions, operates Git, writes files, and connects to
  execution backends such as OpenCode.

A Runner can crash, restart, or be replaced without losing Server state. Multiple
Runners can execute on different machines with independent capacity. Server
validates ownership instead of trusting a Runner report by itself.

## Starting Runner

```bash
mo install runner --repo-root "$PWD"  # First registration and start
# Later
mo service start runner
```

The first installation requests a one-time enrollment from the running Server.
Runner exchanges it for a machine credential and stores that credential under
its root. Later starts reuse the credential.

Runner connects to `http://localhost:3456` by default, registers its capacity and
capabilities, and waits for Server assignments. Start Runner after Server;
Runner cannot connect while Server is unavailable.

## Connection and Recovery

Runner may lose its Server connection without losing Server-owned work:

- Server-owned work remains available for redelivery.
- Results retry after connectivity returns.
- Session mutation recovery follows the operation's contract.
- New work waits until Runner reports current presence and readiness.
- A live Workspace inspection fails as unavailable instead of returning a
  guessed filesystem result.
- A mutating retry keeps its original operation identity.

A transport timeout does not prove success, failure, or a missing Runtime
Session. Retry only through the operation-specific recovery path with the
original identity.

## Checking Runner State

```bash
mo runner status
# Output includes Runner state
```

The Web UI shows the Runner-unavailable banner above the board, the Runners page,
each Runner detail page, and heartbeat events on the Activity page.

## Concurrent Capacity

Server gives each Runner one shared execution slot by default. At most one
Workflow task or AgentJob executes on that Runner at once. Additional work waits
for capacity after acceptance.

Change slots on the Runner detail page in the Web UI. Server owns this limit, so
the next dispatch observes a change without restarting Runner. Increase capacity
only after observing host and provider limits because each AgentSession consumes
CPU and memory, and excess concurrency can hit model limits or Git locks.

## Execution Ownership

Runner owns host-specific effects because they are replaceable execution state.
Server owns durable work decisions because a Runner can disappear. Unreported or
uncommitted files are not durable results.

For one task, Runner prepares an isolated Workspace, resolves the declared Action
input, invokes the execution backend, and reports facts and outputs. It validates
output expectations before reporting success and reclaims the Workspace when the
Workflow no longer needs it. The complete Action contract is in
[Action Contracts](actions/README.md).

Runner owns the complete process tree for every host command. A command result
includes output produced before exit. A leftover subprocess cannot keep the
result open or write into later work.

When a Workflow Workspace is first materialized, Runner transfers only the
repository data needed to establish its base and run branches. Later Stages can
rebase and integrate that branch. Transfers remain bounded, and failed
materialization does not publish or retain a partial Workspace.

## Workspace Location

An Issue uses a named Workspace such as `issue-42`. Runner materializes it under
its configured root and records the home Runner and path. Inspect that binding
instead of guessing an internal directory:

```bash
mo workspace view issue-42 --json home
```

The directory persists across the Issue's Stages and bound Sessions, but it is
rebuildable execution state. Commit and push work that must survive host loss.
Do not manually delete or change its branch, marker, or origin while work runs.

## Runner Failure

- Workflow work that has not begun waits for an eligible Runner.
- Executing Workflow work and AgentJobs fail with `runner-lost`.
- Mohist does not claim that an unconfirmed external effect continued safely.
- Retry or rerun blocked Workflow work explicitly after Runner returns. A later
  AgentJob is a new work intent, not an automatic replay.

Workflow state remains in Server, not Runner.

## Multiple Runners

Server registers multiple Runners and enforces each Runner's slots independently.
New work uses eligible capacity. A materialized Workspace has a home Runner so
later Sessions can reuse its files.

AgentJob scheduling may clear an offline home and rematerialize on another
Runner. A WorkflowRun remains assigned to its Runner and does not migrate
automatically; restore that Runner before retrying the Workflow. Unpushed local
files cannot move between hosts.

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

- **No runner is connected:** Run `mo service start runner`.
- **An Issue waits after starting:** Runner has no capacity. Start Runner; the
  Workflow continues automatically.
- **A task produces no output:** OpenCode may be stuck. Run
  `mo run pause --issue <number>` and inspect logs.
- **Workspace identity error:** Preserve required commits, remove the Workspace,
  and retry after a manual marker, branch, or origin change.
- **Git push failed:** Configure an SSH key or token with permission for the
  remote Repository.

`mo service start runner` preserves enrolled managed-service configuration. Use
`npm run dev:runner` only from a source checkout for development.

## Runner Configuration

Configure host-local behavior with environment variables installed by the service
manager. The configurable values are Server URL, Runner identity, root directory,
and poll, heartbeat, and cleanup intervals.

Dispatch slots are control-plane state. Change them from the Runner detail page,
not through Runner startup options.

### Execution boundaries

Runner does not impose a hidden per-work memory, RSS, wall-clock, or Turn budget.
Actions use only the timeout declared by the Action or Workflow. Every command
honors explicit cancellation by terminating its process group. Host-level service
protection remains the deployment owner's responsibility.

Runner does not select a global Runtime through `type`. A Workflow Agent task
names a Mohist Agent through `mohist/agent`; the Agent definition selects
`mohist/opencode` or `mohist/pi`. Agent Input supplies model options. See
[Action Contracts](actions/README.md).

Runtime replacement and shutdown use two host-local millisecond settings in
service-manager configuration: `QUARANTINE_DRAIN_TIMEOUT_MS` (default 60 seconds)
bounds a quarantined Runtime generation, and `RUNTIME_SHUTDOWN_TIMEOUT_MS`
(default 30 seconds) bounds graceful shutdown. See [Runner design](../design/runner.md)
for the drain and shutdown protocol.

## Self-hosting

For a long-running Runner managed as a service instead of foreground
`dev:runner`, see [Self-hosting](self-host.md).

## Implementation Gaps

- Original-outcome recovery is not uniform for Follow-up, Stop, Session
  commands, and Workspace removal when the connection drops after delivery.
- Workflow terminal status reconciles after a lost notification, but other live
  Runner operations can still return unavailable. An unavailable response does
  not prove that a local effect did not happen.

---

Implementation source: `packages/runner/` and
`packages/server/src/Mohist.Server/Runner/`.
