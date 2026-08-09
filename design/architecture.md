# Architecture

## Boundary

```text
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

| Concern | Belongs in | Not in |
|---|---|---|
| third-party external Agent conversation | external Agent host | Mohist Web / Server |
| external presentation and provider protocol translation | Web / CLI / `mohist-slack` | Agent / Session domains |
| Mohist Agent transcript, SessionInput, AgentTurn and activity | Session context | `mohist-slack` / Web local state |
| CLI command grammar and local interaction | CLI | Server |
| official client Agent invocation contract | Server Agent API | `mohist-slack` / Runner |
| fallback observe & act | Web UI + API | Runner |
| state authority | Server | Runner |
| decide workflow | Server | Runner |
| register/presence/capacity | Server | Web / CLI |
| workspace prep/clean | Runner | Server |
| user-project shell/process/agent execution | Runner | Server |
| git side effects | Runner | Server |
| OpenSpec side effects | Runner | Server |
| Mohist daemon self-management process execution (inspect, update, install, restart, and determine the status of Mohist and its managed services) | Server | Runner for user-project workspace, git, shell, and agent execution |
| Mohist Agent identity, instructions, config, skills, and jobs | Agent context | Slack adapter / Web / CLI |
| Agent Connection binding and access policy | Agent context | Agent definition / Slack thread state |
| Slack credentials and service authentication | Server infrastructure | Agent / Session domains |
| Slack protocol: receiving events, sending messages | `mohist-slack` | Server Agent / Session contexts |
| Slack provider inbox, conversation mapping, and pending outbound delivery | Server infrastructure | `mohist-slack` local storage / Agent or Session aggregates |
| Slack workspace Mohist App enrollment and managed Agent App lifecycle (external App create/install approval, manifest, Socket facts, operation fence, unknown outcome) | Server Slack integration supporting context (SlackWorkspaceEnrollment / ManagedSlackAgentApp aggregates) | `mohist-slack` / Agent or Session aggregates / pure integration records |
| Slack Configuration access/refresh pair, Mohist App credentials, and managed Agent App runtime secrets (client/signing secret, app-level token, bot token) | Server Slack integration supporting context, addressed by owning aggregate (Enrollment or AgentApp) | Agent Connection / `mohist-slack` / plaintext in row, DTO, audit, or log |
| `mohist-slack` process lifecycle | CLI managed service | Agent Connection aggregate / Runner |
| third-party Agent exploration and delegation | external Agent + Skill | Mohist Runtime / Web UI |
| Mohist Agent conversation from any client | Agent API + AgentSession | provider adapter local state |
| skill install | CLI | Server |
| product design | docs/ | design/ |
| domain model | code | design/ |
| architecture rules | design/architecture.md | OpenSpec |
| builtin workflow content | *.workflow.yaml | design/ |

## Facts and decisions

```text
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

Runner produces facts. Never interprets them.
Workflow interprets facts. Never produces them.

Runner may report a failure classification, including `retry-safe`, as an execution fact. It does not authorize or cause a retry. Workflow is the sole authority that decides whether work fails, retries, recovers, advances, waits, or requires approval.

## Report pipeline

```text
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

| Channel | SLA | Purpose |
|---|---|---|
| Domain reaction | durable at-least-once | advance cross-aggregate state |
| UI push | best-effort | update screen |

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

- **Persist only uncertain command-delivery state.** The coordinator grain stores only the fence for the
  command that is in progress, such as `Pending { commandId, kind, payload, expectedRevision }`. It clears
  the fence as soon as the command has a definite `applied` or `rejected` result. It does not cache a
  business result.
- **Write at most one participant aggregate per command.** One synchronous coordinator call chain enters
  only one participant transaction. The coordinator must not write across aggregates or use a join table
  or repository to bypass aggregate boundaries. If one business operation affects two aggregates, each
  aggregate uses its own transaction and durable events carry the process forward. The coordinator only
  provides serialization and idempotency.
- **Remain downstream of participant interfaces.** The coordinator sends commands in one direction through
  narrow participant interfaces. A participant must not call the coordinator back in the same synchronous
  call stack or hold a coordinator reference. Participant aggregates such as `Issue` and `Project` do not
  know that the coordinator exists. Event routing, handlers, and commands from other contexts continue to
  use the participant's own interface directly.
- **Do not store duplicate business facts.** Coordinator persistence contains no Issue, Project,
  Repository, or WorkflowRun business state. Those facts remain authoritative only in their aggregates.
  The coordinator may store technical fence fields such as `commandId`, command kind, a canonical command
  parameter snapshot, and expected revision.
- **Do not create a synchronous callback cycle.** The coordinator calls a participant, the participant
  commits, a durable event is published, and a handler reenters the coordinator. Reentry must use durable
  dispatch. The participant must not call the coordinator synchronously from inside the command.

The following uses are in scope:

- `IssueRepositoryCoordinatorGrain` serializes Issue creation, Repository reassignment, reopening a
  cancelled Issue, and Repository deletion within a Project. These commands establish or break a binding
  for a non-terminal Issue. An Issue's explicit WorkflowProfile selection, including creation, edit, and
  clearing with `--inherit-workflow-profile`, is an Issue aggregate field. Issue creation commits that field
  in the same `IIssueBindingParticipant` transaction. Before it commits, the participant validates that the
  Profile exists in the same way that it validates that the Repository exists.
- `WorkflowProfileReferenceCoordinator` serializes Profile deletion, writes to a Project's default Profile,
  and WorkflowRun start-binding writes within a Project. These commands establish or break Profile
  references. Each persisted custom Profile reference has a nullable custom-Profile backing key and a
  restrictive foreign key to `(ProjectId, ProfileId)`. Builtin references keep a null backing key because
  they cannot be deleted. This foreign key is the primary mechanism that makes concurrent deletion correct.
  `WorkflowProfileDeletionBlockerQuery` combines the Project default, **all** explicit Issue selections in
  that Project, including terminal Issues, and active Run bindings. It is the source of actionable deletion
  diagnostics and errors. `IssueRepositoryCoordinatorGrain` serializes Issue selection; selection does not
  pass through the Profile coordinator. For a deletion and selection race across coordinators, the foreign
  key either makes an already-committed reference block deletion or makes the Issue receive a retryable
  `workflow-profile-not-found` conflict. It never leaves a dangling reference.

Each coordinator serializes by Project key. They use narrow participant interfaces:
`IIssueBindingParticipant`, `IProjectBindingParticipant`, and `IWorkflowRunBindingParticipant`. An
`ArchTest` prevents production code from bypassing the coordinators. The coordinators **do not** call each
other or share a transaction. Each synchronous coordinator call chain enters only one participant aggregate.
Issue selection belongs to the Issue coordinator. Project default and Run binding belong to the Profile
coordinator.

The coordinators do not handle invariant validation inside a participant, eventually consistent progress
across aggregates, UI pushes, or Session and Runtime binding.

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
2. A third-party external Agent keeps its own conversation in its host and uses Mohist Skills + `mo` to
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
