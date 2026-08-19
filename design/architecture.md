# Architecture

## Boundary

```text diagram
User in Slack -> Slack Bot / mohist-slack -> Connection boundary --+
User -> Web UI (backup operation + view) -> API -------------------+
User -> direct CLI -> API -----------------------------------------+
                                                                   |
User in IDE / chat                                                 |
       |                                                           |
       v                                                           |
External Agent -> Mohist Skill -> mo CLI -> API -------------------+
                                                                   |
                                                                   v
Agent API
       |
       v
Control Plane        owns state, makes decisions
       |
       v
Execution Plane      runs commands, reports facts
       |
       v
User Project
```

## What goes where

- A third-party External Agent conversation belongs in the External Agent host, not in Mohist Web
  or Server.
- External presentation and provider protocol translation belong in Web, CLI, and `mohist-slack`,
  not in Agent or Session domains.
- Mohist Agent transcript, SessionInput, AgentTurn, and activity belong in the Session context, not
  in `mohist-slack` or Web local state.
- CLI command grammar and local interaction belong in the CLI, not in Server.
- The official client Agent invocation contract belongs in the Server Agent API, not in
  `mohist-slack` or Runner.
- Fallback observe and act belongs in Web UI + API, not in Runner.
- State authority belongs in Server, not in Runner.
- Workflow decisions belong in Server, not in Runner.
- Register/presence/capacity belong in Server, not in Web or CLI.
- Workspace prep/clean belongs in Runner, not in Server.
- User-project shell, process, and agent execution belong in Runner, not in Server.
- Git side effects belong in Runner, not in Server.
- OpenSpec side effects belong in Runner, not in Server.
- Mohist daemon self-management process execution (inspect, update, install, restart, and determine
  the status of Mohist and its managed services) belongs in Server; Runner is for user-project
  workspace, git, shell, and agent execution.
- Mohist Agent identity, instructions, config, skills, and jobs belong in the Agent context, not in
  the Slack adapter, Web, or CLI.
- Agent Connection binding and access policy belong in the Agent context, not in the Agent
  definition or Slack thread state.
- Slack credentials and service authentication belong in Server infrastructure, not in Agent or
  Session domains.
- The Slack protocol (receiving events, sending messages) belongs in `mohist-slack`, not in Server
  Agent or Session contexts.
- The Slack provider inbox, conversation mapping, and pending outbound delivery belong in Server
  infrastructure, not in `mohist-slack` local storage or Agent or Session aggregates.
- Slack workspace Mohist App enrollment and managed Agent App lifecycle (external App
  create/install approval, manifest, Socket facts, operation fence, unknown outcome) belong in the
  Server Slack integration supporting context (`SlackWorkspaceEnrollment` / `ManagedSlackAgentApp`
  aggregates), not in `mohist-slack`, Agent or Session aggregates, or pure integration records.
- The Slack Configuration access/refresh pair, Mohist App credentials, and managed Agent App
  runtime secrets (client/signing secret, app-level token, bot token) belong in the Server Slack
  integration supporting context, addressed by the owning aggregate (Enrollment or AgentApp); never
  in Agent Connection, `mohist-slack`, or plaintext in a row, DTO, audit, or log.
- The `mohist-slack` process lifecycle belongs to a CLI managed service, not to an Agent Connection
  aggregate or Runner.
- Third-party Agent exploration and delegation belong in the External Agent + Skill, not in Mohist
  Runtime or Web UI.
- A Mohist Agent conversation from any client belongs in Agent API + AgentSession, not in provider
  adapter local state.
- Skill install belongs in the CLI, not in Server.
- Product design belongs in `docs/`, not in `design/`.
- The domain model belongs in code, not in `design/`.
- Architecture rules belong in `design/architecture.md`, not in OpenSpec.
- Builtin workflow content belongs in `*.workflow.yaml`, not in `design/`.

## Facts and decisions

Runner produces facts. Never interprets them.
Workflow interprets facts. Never produces them.

Runner may report a failure classification, including `retry-safe`, as an execution fact. It does not authorize or cause a retry. Workflow is the sole authority that decides whether work fails, retries, recovers, advances, waits, or requires approval.

## Report pipeline

```text diagram
Side effect
  |
  v
Report              <- fact, not command
  |
  v
Ownership check     <- reject without proof
  |
  v
Decision            <- interpret in workflow context
  |
  v
State change        <- advance or wait
```

Runner may say: completed / failed / verification passed / output produced / failure classification reported.
Runner may not say: advance state / mark done / bypass approval / allow retry.

Every in-flight work has an owner. Stale reports get rejected, never merged.

## Events: two channels

Domain reaction events use durable at-least-once delivery; their purpose is to advance
cross-aggregate state. UI push events are best-effort; their purpose is to update the screen.

The UI self-reconciles after a disconnect. Workflow progress must never depend on the UI.

Events append in same transaction as state save. Dispatcher is the sole notifier.

## Runtime guarantees

- Logs, metrics, traces, notifications, and status pages are not business authorities. Core work
  continues if they fail.
- Background tasks, queues, and diagnostic data have resource limits. When a limit is reached, the
  system degrades supporting capabilities before it consumes resources needed for business work.
- The cost of polling and status queries grows only with the current relevant data. It must not grow
  with unrelated historical data.
- Health checks expose latency, resource pressure, and degraded supporting capabilities. They do more
  than report whether a process is alive.

See [Observability](observability.md) for the detailed rules.

## Aggregates and transactions

An aggregate is both a strong-consistency boundary and a database transaction boundary.

- One transaction can save only one aggregate's state and the domain events caused by that state change.
- A transaction must not modify two aggregates. A join table, repository, or handler must not bypass an
  aggregate boundary to perform a cross-aggregate write.
- Aggregates in the same bounded context may reference, query, and send commands to each other. A
  transaction boundary does not decide whether a dependency is allowed. Each synchronous call chain must
  have one explicit direction. It must not form a cycle through a synchronous callback.
- A cross-aggregate process advances as follows: the source aggregate commits state and events, a durable
  handler receives an event, and the target aggregate receives an idempotent command. If a step fails,
  event redelivery or command retry continues the process. It does not roll back another aggregate that
  already committed.
- Each business fact has one write authority. When another aggregate needs that fact, it stores only the
  minimum context or read model required for its own decision. These copies are eventually consistent.
  They do not validate or write the original fact.
- A cross-aggregate query may select a candidate or assemble a command. The target aggregate must validate
  its own invariants again. A stale query result may cause rejection, retry, or reselection. It must not
  corrupt the target aggregate state.

Therefore, "state and events in one transaction" means the state and events of one aggregate. It does not
mean that all aggregates involved in one business operation share a transaction.

### Durable application process manager

A durable application-layer process manager, also called a coordinator grain, may be introduced when a
cross-aggregate command must be serialized across participants and its result must remain recoverable after
a retry, lost activation, or network interruption. It is **not** a new business authority. It is a narrow
special case of the established source-aggregate commit, durable-handler delivery, and idempotent
target-aggregate command pattern. It sends a group of race-prone commands through one coordination point
and makes every step safe for redelivery.

All of these constraints apply:

- **Persist only uncertain command-delivery state.** The coordinator stores only the fence for the
  command that is in progress and clears the fence as soon as the command has a definite `applied`
  or `rejected` result. It does not cache a business result. When the participant's aggregate is the
  durable result, a retry after the fence was cleared reads that aggregate by the command's stable
  target identity before it recomputes any race-sensitive input. It returns the persisted result for
  matching request identity and rejects conflicting identity. A lost response must not make the
  retry resolve newer configuration for an already applied command.
- **Write at most one participant aggregate per command.** One synchronous coordinator call chain
  enters only one participant transaction. The coordinator must not write across aggregates or use a
  join table or repository to bypass aggregate boundaries. If one business operation affects two
  aggregates, each aggregate uses its own transaction and durable events carry the process forward.
  The coordinator only provides serialization and idempotency.
- **Remain downstream of participant interfaces.** The coordinator sends commands in one direction
  through narrow participant interfaces. A participant must not call the coordinator back in the
  same synchronous call stack or hold a coordinator reference. Participant aggregates do not know
  that the coordinator exists. Event routing, handlers, and commands from other contexts continue to
  use the participant's own interface directly.
- **Do not store duplicate business facts.** Coordinator persistence contains no participant
  business state. Those facts remain authoritative only in their aggregates. The coordinator may
  store technical fence fields such as the command identity, the command kind, a canonical command
  parameter snapshot, and the expected revision. Reuse of a pending command identity is a replay
  only when the command kind and canonical parameter snapshot are identical; a different payload is
  rejected.
- **Do not create a synchronous callback cycle.** The coordinator calls a participant, the
  participant commits, a durable event is published, and a handler reenters the coordinator. Reentry
  must use durable dispatch. The participant must not call the coordinator synchronously from inside
  the command.

Each coordinator serializes by Project key. Today one coordinator serializes the commands that
establish or break a non-terminal Issue's Repository binding: Issue creation, target Repository
reassignment, reopening a cancelled Issue, and Repository deletion. Another coordinator serializes
Workflow Profile mutation, Project default and Agent Action override writes, Profile deletion, and
WorkflowRun start binding, so a Run observes the complete configuration before or after a mutation,
never an intermediate version. A duplicate Run start returns the identical persisted binding, while
conflicting startup facts are rejected. A Profile deletion that races an Issue selection ends in a
block or a retryable conflict; it never leaves a dangling reference. The coordinators do not call
each other and do not share a transaction. They do not reimplement invariant validation; they
sequence the existing validators and one participant commit under the fence. Coordinators do not
handle eventually consistent progress across aggregates, UI pushes, or Session and Runtime binding.

`Completed` and `Stopped` WorkflowRuns are immutable terminal aggregates. Workflow control rejects
retry, rerun, rerun-from-stage, and resume for them. Re-executing Issue work creates a new
WorkflowRun and enters the same start-binding coordination; a terminal Run never regains an active
Profile reference.

## Persistence

- Product state: persist.
- Workflow state: persist.
- Runner workspace: rebuildable.
- Artifact: persist (audit trail).
- Authority grains: no `[Reentrant]`.

## Interaction surfaces and Agent ownership

Mohist does not require the user to move daily collaboration into its Web UI. The presentation surface
may be Slack, an IDE, a terminal, or Web, but that does not decide which Agent owns the work.

There are two distinct paths:

1. A Mohist Agent is launched through Web, CLI, an Agent Connection, an event, or a mention. Every path
   reaches the same Agent API; provider adapters first enter through the Server Connection boundary.
   Mohist owns the Agent definition, AgentJob, AgentSession, SessionInput,
   AgentTurn, durable transcript, activity, result, and evidence. Server infrastructure owns durable
   provider conversation mapping and delivery state; the external surface owns only presentation and
   transient protocol translation.
2. A third-party External Agent keeps its own conversation in its host and uses Mohist Skills + `mo` to
   issue domain commands. That external conversation does not become an AgentSession merely because it
   caused Mohist work. If it explicitly launches a Mohist Agent, the launched work follows path 1.

Slack Bot therefore is not a Runtime or another Agent. One `mohist-slack` service operates the Slack
Connections for one Server. It exists as a separate process because Slack's first-class client lives in
Node, not because it is a separate state boundary: it is stateless, enters through the Connection boundary
and reaches Agent API, and never reads Mohist storage, shells out to `mo`, parses Runner logs, persists
provider inbox, thread mappings or pending deliveries, or stores a shadow copy of Agent
instructions/config/skills. Detailed contracts: [`agent-api.md`](agent-api.md) and
[`slack.md`](slack.md).

External skills read projects, call `mo` CLI, and may write ordinary files. They never touch the Mohist
database. Runner may adapt OpenCode or another runtime for Workflow TaskRun and AgentJob work.
Agent/Session ownership invariants: [`agent-execution.md`](agent-execution.md).

## Constraints

- CLI never merges into Server.
- Official Agent clients use Agent API; provider adapters enter through the Server Connection boundary,
  which invokes Agent API and cannot bypass it through CLI, database, grain, Runner, or Runtime protocols.
- Provider credentials, durable ingress, conversation mappings and delivery state live in Server
  infrastructure, outside Agent/Session domains; Agent Connection owns only the external binding, access
  policy and lifecycle.
- The Slack control plane (workspace enrollment and managed Agent App lifecycle) is a Server-side
  Slack integration supporting context of independent aggregates, not the `mohist-slack` adapter and not
  Agent-domain state. It owns the external App lifecycle facts (create/install/manifest/Socket/fence/unknown);
  Agent Connection remains the authority for binding, access policy and enable/disable. Managed Agent App
  runtime secrets and the Mohist App credential are addressed by their owning aggregate (AgentApp / Enrollment),
  not by Agent Connection, so removing a Connection does not delete a separately-retained Slack App's secrets.
  Production code reaches Slack create/delete only through a narrow app-management port.
- All shell/agent/git/OpenSpec execution goes to Runner.
- Single state authority. `mohist-slack` is a stateless managed adapter process; anything that must
  survive a restart lives in Server.
- Single control-plane daemon today. Actor model for state, not distribution.
- Durable dispatcher notifies. Never executes tasks or calls runner.
- OpenSpec is not architecture authority.
