# Runner Guide

Runner is Mohist's execution backend. Server decides what work means and
whether it may proceed. Runner performs the assigned work on a host.

Host environment capture, refresh, and diagnostics are specified in
[`Runner Execution Environment`](../environment/spec.md).

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

## Checking Runner State

```bash
mo runner status
# Reads the Server-global Runner projection: presence, control, admission,
# Runtime readiness/catalogs, capacity, active owners, drain, and next actions.
mo service status runner
# Reads only the local service-manager unit; it performs no HTTP request.
```

The Web UI reads the same Server-global projection on the dashboard headline,
board warning, sidebar, `/runners`, `/runners/<runnerId>`, and Activity evidence.
These surfaces use admission, presence, control, drain, capacity, and active-work
facts independently. They do not infer `idle`, `busy`, or healthy readiness from
zero active work.

### Runner status projection

The status read is application-scoped, not Project-scoped. `mo runner status`,
`/runners`, and `/runners/<runnerId>` read the Server projection assembled from
known Runner definitions, current presence and control observations, credential
state, drain fences, and active Workflow and AgentJob owner ledgers. A known
offline Runner remains visible with its configured slots; unknown live details
are shown as unavailable rather than guessed.

The projection keeps these facts separate:

- presence: `online`, `stale`, or `offline`;
- control: `connected` or `disconnected`;
- admission: `ready` or `blocked`, with ordered stable reason codes;
- drain: inactive or active, including update identity when known;
- capacity: used and configured total slots, with used nullable when the active
  owner snapshot is unavailable;
- active work: distinct Workflow and AgentJob owner rows;
- Runtime readiness and catalog capability, which are not interchangeable.

For the distinction between retained ownership and current execution, see
[Runner work confirmation](../work-confirmation/spec.md).

Server-owned admission reason codes include `presence-offline`,
`presence-stale`, `credential-revoked`, `credential-missing`,
`control-disconnected`, `draining`, `admission-observation-missing`, and
`capacity-full`. Runner-local blockers retain their stable codes, including
`provider-policy-invalid` and `runtime-event-queue-unavailable`.

Recovery guidance is also Server-owned. It can request installation, start or
re-enrollment, waiting for drain or capacity, waiting for Runtime readiness, or
a code-specific local correction. A status read never invents a command.
`mo service status runner` is different: it reads only the local service-manager
unit and does not query this projection.

Every list or detail response has one `observedAt` timestamp captured at read
start. The response is an observational snapshot: definitions, leases, claims,
and ledgers may change while it is assembled, and a later claim remains the
final authority. Status rendering must not mutate work or treat the snapshot as
a reservation.

Fleet-wide display and aggregation follow
[AgentOps activity](../../agent-ops/activity/spec.md#fleet-summary).

## Concurrent Capacity

Server gives each Runner one shared execution slot by default. At most one
Workflow task or AgentJob executes on that Runner at once. Additional work waits
for capacity after acceptance.

Change slots on the Runner detail page in the Web UI. Server owns this limit, so
the next dispatch observes a change without restarting Runner. Increase capacity
only after observing host and provider limits because each AgentSession consumes
CPU and memory, and excess concurrency can hit model limits or Git locks.

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

## Manager Execution

The Slack Manager executes `mo` commands on a Runner without ever holding the
Manager credential. A credential broker started for one Manager execution
admits a request only from the launcher process that execution generated, and
the execution environment receives only the non-secret broker locator.

- **Local authentication fails closed.** The broker refuses whenever it cannot
  establish that the connecting process is the generated launcher: an
  unsupported host platform, an unavailable socket handle, a failed or
  abnormal socket-table inspection, or a peer that is not that launcher. An
  inspection that ends abnormally is not evidence even when it already
  contained a matching row, and a partially observed table never admits a
  peer.
- **Failures stay distinguishable.** A refusal, a request that could not reach
  or start the broker, and a command terminated by a signal report different
  outcomes, so a Manager sees a named reason instead of an unexplained exit
  and can tell a broker refusal from a lost request from a killed command.
- **Diagnostics carry no credentials.** Refusal reasons and broker diagnostics
  name a category only: no bearer values, credential files, socket paths, or
  process identifiers.

## Runner Failure

- Workflow work that has not begun waits for an eligible Runner.
- Executing Workflow work and AgentJobs fail with `runner-lost`.
- Pausing keeps the executing Action running, so paused work still belongs to
  the Runner process generation that owns it and fails with `runner-lost` when
  a replacement process generation is admitted.
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
names a Mohist Agent through `mohist/agent`; the Agent definition selects the
Runtime. `mohist/agent` remains the only Workflow Agent Action; the named Agent
definition supplies the model configuration. See
[Action Contracts](../../workflow/actions/spec.md).

`ENABLED_AGENT_RUNTIMES` controls which Agent Runtime processes this Runner may
create. It is a comma-separated list containing `pi`, `opencode`, or both. The
default is `pi`; OpenCode is an explicit opt-in. For a managed Runner, set the
selection when installing it:

```bash
mo install runner --enabled-agent-runtimes pi,opencode
```

The installer stores an explicit selection in
`~/.config/mohist/runner.env`, readable only by the current user, and the
systemd unit reads that stable EnvironmentFile. `mo update runner` and a later
install without the flag preserve the existing file. When the file does not
exist, the Runner's default remains Pi.

Managed installation also persists the selected Server URL and Runner root in
`~/.config/mohist/runner-managed.env`. The one-time enrollment token is
written with mode `0600` under the Runner root; Runner exchanges it for its
machine credential on first start and then removes the token file. Neither
credential is written to the systemd unit or command output.

For a foreground development Runner, set the environment directly:

```bash
ENABLED_AGENT_RUNTIMES=pi,opencode npm start
```

The setting must contain at least one known Runtime. Runner refuses to start for
an empty list or an unknown value. A disabled Runtime is absent from the
Runner's catalogs and readiness report, so Server leaves matching work queued.
Follow-up, cancel, compact, and reset requests for a disabled Runtime return
Runner wire error `runtime-unavailable` and API code `runtime_unavailable`
rather than switching the Session to another Runtime. A Runtime that is enabled
but temporarily not ready continues to use `unavailable` on the Runner wire.
Cancel, compact, and reset expose that transient state as
`runner_unavailable`; follow-up remains accepted and queued for retry.

Runtime replacement and shutdown use two host-local millisecond settings in
service-manager configuration: `QUARANTINE_DRAIN_TIMEOUT_MS` (default 60 seconds)
bounds a quarantined Runtime generation, and `RUNTIME_SHUTDOWN_TIMEOUT_MS`
(default 30 seconds) bounds graceful shutdown. See [Runner design](design.md)
for the drain and shutdown protocol.

## Implementation Gaps

- Original-outcome recovery is not uniform for Follow-up, Stop, Session
  commands, and Workspace removal when the connection drops after delivery.
- Workflow terminal status reconciles after a lost notification, but other live
  Runner operations can still return unavailable. An unavailable response does
  not prove that a local effect did not happen.

---

Implementation source: `packages/runner/` and
`packages/server/src/Mohist.Server/Runner/`.
