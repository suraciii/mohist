# Architecture

Mohist separates interaction, decision, and execution planes. Server owns
product state and decisions. Runner performs user-project work and reports
facts. Detailed domain contracts are indexed by [`domain-analysis.md`](domain-analysis.md).

## Core Decisions

- Server is the state authority. Adapters and Runner do not keep competing
  business state.
- Runner reports execution facts. Workflow interprets those facts and decides
  production-line state.
- One aggregate transaction writes one aggregate and its events. Cross-aggregate
  work uses durable events and idempotent commands.
- Official clients and provider adapters enter through Server APIs. They do not
  bypass the API through CLI, database, grains, Runner, or Runtime protocols.
- One Server daemon is the current control-plane deployment. State uses the
  actor model; the architecture does not require distributed actors.
- User-project shell, process, git, and Agent execution run only in Runner.
- The durable dispatcher notifies. It never executes tasks or calls Runner.

## System Boundary

```text diagram
+--------------------------+   +------------+
| Web, CLI, External Agent |   | Slack user |
+-------------+------------+   +------+-----+
              +----+              +---+
                   v              v
                +-----+   +---------------+
                | API +---| Slack adapter |---+
                +-----+   +-------+-------+   |
                          +-------+           |
                          v                   |
               +---------------------+        |
               | Connection boundary |        |
               +----------+----------+        |
                          |                   |
                          v                   |
                    +-----------+             |
                    | Agent API |<------------+
                    +-----+-----+
                          |
                          v
                  +---------------+
                  | Control Plane |
                  +-------+-------+
                          |
                          v
                 +-----------------+
                 | Execution Plane |
                 +--------+--------+
                          |
                          v
                  +--------------+
                  | User Project |
                  +--------------+
```

- Web, CLI, and External Agents own presentation and local interaction.
  External Agents keep their own conversations and use Mohist Skills plus `mo`.
- Slack enters through the Server Connection boundary. The
  `mohist-slack` adapter translates Socket Mode and owns no persistent product
  state. See [`slack.md`](slack.md).
- The Agent API owns unified Agent launch, continuation, observation, and stop.
  Agent, Session, Workflow, and Connection contracts remain on the Server side.
- The Control Plane owns product state and decisions. The Execution Plane
  materializes Workspaces, runs user-project commands, and reports facts.
- The User Project is the only location for project shell, process, git, and
  Agent execution side effects.

## Ownership Rules

- External presentation and provider protocol translation belong to Web, CLI,
  and adapters, not Agent or Session. Web is the fallback for observation,
  manual operations, and takeover.
- Agent transcript, SessionInput, AgentTurn, Activity, and audit belong to the
  Session context. They do not live in adapter or Web local state.
- Agent identity, Instructions, configuration, Skills, and AgentJobs belong to
  Agent. Agent Connection binding and access policy are separate from Agent
  definition.
- Runner registration, presence, and capacity belong to Server. Web and CLI do
  not arbitrate them.
- Workspace preparation and cleanup belong to Runner. Workspace identity and
  lifecycle belong to Workspace.
- Mohist daemon self-management belongs to Server. Runner manages only the user
  project workspace, git, shell, and Agent execution.
- Slack credentials, durable provider ingress, conversation mappings, and
  delivery state belong to Server infrastructure. The Slack adapter owns only
  protocol state. Slack App enrollment and Managed Agent App lifecycle are
  separate Server integration aggregates, not adapter or Agent state.
- Skill installation belongs to CLI. Skills and External Agent exploration do
  not access the Mohist database.

## Facts and Decisions

Runner produces facts and never interprets them. Workflow interprets facts and
never produces Runner facts.

Runner may report completion, failure, verification, output, or a failure
classification such as `retry-safe`. Runner may not advance state, mark work
Done, bypass approval, or authorize retry. Every in-flight work item has an
owner. Stale reports are rejected instead of merged.

```text diagram
+--------+    +-------------+    +-------------+    +----------+    +-------+
| Effect +--->| Fact report +--->| Owner check +--->| Decision +--->| State |
+--------+    +-------------+    +-------------+    +----------+    +-------+
```

## Events and Runtime Guarantees

Domain reaction events use durable at-least-once delivery to advance
cross-aggregate state. UI push events are best effort and only update screens.
State and domain events append in one aggregate transaction. The durable
dispatcher is the sole notifier. The UI reconciles after disconnect; Workflow
progress never depends on UI push.

- Logs, metrics, traces, notifications, and status pages are not business
  authorities. Core work continues if they fail.
- Background tasks, queues, and diagnostic data have resource limits. Supporting
  capabilities degrade before resources required for business work.
- Polling and status-query cost grows with current relevant data, not unrelated
  history.
- Health checks expose latency, resource pressure, and degraded capabilities,
  not only process liveness.

See [`observability.md`](observability.md) for detailed observability rules.

## Aggregates and Transactions

An aggregate is both a strong-consistency boundary and a database transaction
boundary.

- One transaction saves one aggregate and the domain events caused by that
  change. A join table, repository, or handler must not bypass the boundary.
- Aggregates in one bounded context may reference, query, and command one
  another. Each synchronous call chain has one direction and no callback cycle.
- A source aggregate commits state and events. A durable handler sends an
  idempotent command to the target aggregate. Failure causes redelivery or
  retry. It does not roll back another committed aggregate.
- Each business fact has one write authority. Other aggregates store only the
  minimum context or read model needed for their decisions. Copies are
  eventually consistent and never write the source fact.
- A cross-aggregate query may select a candidate or assemble a command. The
  target aggregate validates its invariants again. Stale data may cause
  rejection, retry, or reselection, but not corruption.

### Durable application process manager

A durable application process manager, also called a coordinator grain, is a
narrow application-layer component for a cross-aggregate command that needs
Project-level serialization and recovery after retry, lost activation, or
network interruption. It is not a business authority.

- Persist only the uncertain command-delivery fence. Clear it after a definite
  `applied` or `rejected` result. Do not cache a business result. After the
  fence clears, a retry reads the participant by stable target identity before
  recomputing race-sensitive input. Matching request identity returns the
  persisted result; conflicting identity is rejected.
- Write at most one participant aggregate in one synchronous call chain. A
  two-aggregate operation uses separate transactions and durable events.
- Call participants through narrow interfaces. Participants must not reference
  or synchronously call the coordinator. Durable event handlers may reenter it.
  Other contexts continue to use participant interfaces directly.
- Store no participant business facts. Technical fence fields may include
  command identity, command kind, canonical parameters, and expected revision.
  Reusing a pending identity is valid only when kind and parameters match.
- Never create a synchronous callback cycle.

The current coordinators serialize by Project key. The Issue Repository
coordinator handles Issue creation, target Repository reassignment, reopening a
cancelled Issue, and Repository deletion. A separate Project coordinator
handles Workflow Profile mutation, Project default writes, Profile deletion,
and WorkflowRun start binding.

A duplicate Run start returns its identical persisted binding. Conflicting
startup facts are rejected. Profile deletion that races Issue selection ends in
a block or retryable conflict, never a dangling reference. The coordinators do
not call each other or share a transaction. They sequence existing validators
and one participant commit. They do not reimplement invariant validation or
handle eventual progress across aggregates, UI pushes, or Session and Runtime
binding.

`Completed` and `Stopped` WorkflowRuns are immutable terminal aggregates.
Workflow rejects retry, rerun, rerun-from-stage, and resume for them. Repeating
Issue work creates a new WorkflowRun and a new start-binding coordination.

## Persistence

- Product and Workflow state is durable.
- Runner Workspace directories are rebuildable.
- Artifacts persist as an audit trail.
- Authority grains are not `[Reentrant]`.

## Interaction Surfaces

A Mohist Agent launched by Web, CLI, an Agent Connection, an event, or a
mention follows one Agent API path. Mohist owns its Agent definition, AgentJob,
AgentSession, SessionInput, AgentTurn, transcript, Activity, result, and
evidence. Server infrastructure owns provider conversation mapping and
delivery state.

A third-party External Agent keeps its conversation in its own host and uses
Mohist Skills plus `mo` for domain commands. Causing Mohist work does not turn
that conversation into an AgentSession. If it launches a Mohist Agent, the
launch follows the first path.

Slack is therefore neither a Runtime nor an Agent. The stateless adapter enters
through the Connection boundary and Agent API. It never reads Mohist storage,
parses Runner logs, invokes CLI as a hidden path, or stores shadow Agent
configuration. See [`agent-api.md`](agent-api.md) and [`slack.md`](slack.md).

## Non-Goals

- The architecture does not distribute the control plane across Servers.
- Runner does not become a workflow decision authority or a second state store.
- Adapters do not gain client-specific Agent execution protocols.
- Cross-aggregate operations do not become one multi-aggregate transaction.

## Status

The current architecture uses one Server control plane and one Runner
execution plane. Provider adapters are stateless protocol boundaries. Durable
application process managers are limited to the Project-scoped command classes
defined above.
