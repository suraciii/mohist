# Domain Analysis

This document assigns Mohist behavior to problem-space subdomains and
solution-space bounded contexts. The relationship map is normative. Detailed
component contracts live in the linked context specifications.

## Design Drivers

- A business fact has one owner and one business language.
- A bounded context may publish a contract without exposing its internal model.
- A read-side report may depend on all business contexts but must not become a
  write authority.
- The relationship map describes domain direction. It does not describe static
  source-code dependency direction.

## Model

### Subdomains

**Core: Workflow.** Workflow is the autonomous production line. It advances,
schedules, approves, repairs, and resumes work. It owns Project-scoped
Workflow Profiles and Workflow Runs. An Agent-backed task uses `mohist/agent`.
Workflow supplies input and interprets the AgentJob result. It does not own
Agent execution, Runtime selection, or Runner dispatch. Mechanical Actions
remain Workflow orchestration.

**Supporting subdomains:**

- **Issue:** work units, organization, prerequisites, priority, risk, and
  progress. Its language includes Issue, Epic, parent, child, status, Draft,
  and Done.
- **Project Space:** Project-scoped repositories, variables, prompts, and
  defaults. A Project is the only configurable scope. A builtin `.prompt` is a
  loader fallback, not a Prompt resource.
- **Workspace:** the named execution environment. An Issue gets a dedicated
  Workspace. An interaction origin such as a Slack channel or Web conversation
  may reuse one. Explicit creation starts another. Workspace lifecycle is
  independent of AgentSession and WorkflowRun.
- **Agent:** reusable Mohist Agents, AgentJobs, Runner dispatch, and external
  Connections. Web, CLI, Workflow, events, mentions, and Agent Connections use
  the same Agent launch boundary.
- **Session:** logical execution conversations, input delivery, turns,
  compression, query, transcript, context, usage, and audit.
- **Runner:** execution resource registration, presence, and capacity.
- **External surfaces:** persistent enrollment, binding, projection, and
  delivery facts for collaboration providers. Adapters hold only transient
  protocol state.

External-surface bounded contexts are:

- **Slack:** workspace Mohist App enrollment and managed Agent App lifecycle,
  including App creation, installation approval, manifests, Socket readiness,
  operation fences, and unknown outcomes.
- **GitHub:** Repository-level Issue mirroring, GitHub comment commands,
  progress projection, and external identity.
- **Notification:** notification policy, wording, and suggested action for
  Issue and Workflow events. Hermes is only the delivery adapter.
- **Outbound Webhook:** Project-scoped `WebhookSubscription` and the external
  OHS/PL over CloudEvents.

Epic is an Issue organizing facet, not a separate subdomain. Issue and Epic are
aggregates in one bounded context. Issue owns its optional `EpicNumber`.
Epic owns the goal and advancement policy, but no independent membership set.
Membership, progress, and candidates are queries over current Issue state.
Parent and child are also Issue-internal organization. They express work
decomposition, not Epic feeding. Workflow does not inspect that relationship.
See [`composite-issues.md`](composite-issues.md).

Repository belongs to Project Space. Issue stores only the target Repository
name. WorkflowRun stores the Project and Issue identity needed to resolve it.
An unfinished Issue prevents changes to its Repository execution attributes.
See [`repositories.md`](repositories.md).

Workspace is separate from Project Space configuration. Issue and interaction
origins resolve to Workspace through Origin. Runner materializes it as a
directory. Workflow uses Workspace routing facts and Runner capacity to choose
dispatch. See [`workspaces.md`](workspaces.md).

See [`../CONTEXT.md`](../CONTEXT.md) for shared Agent terms and
[`agent-execution.md`](agent-execution.md) for lifecycle ownership and
invariants.

**Read-side: AgentOps.** AgentOps assembles cross-domain, read-only reports
such as activity feeds, delivery cost, and cross-aggregate boards. Session is a
leaf of the business model.

### Not subdomains

- Artifact belongs to Workflow and has no independent problem class.
- External Agent hosts, CLI, Web UI, Slack, and `mohist-slack` are adapters.
  Agent Connection remains in Agent because its binding, access policy, and
  lifecycle are persistent product behavior. Slack protocol state is not.
- Slack workspace enrollment and Managed Agent App lifecycle form a Server-side
  supporting context. They are separate from the stateless Slack protocol
  adapter and from Agent state.
- Skills describe capabilities. The catalog, distribution, and installation
  are CLI-carried product content. Selected Skills are Agent configuration.
  Requirement refinement is a usage scenario that produces a ready Issue, not
  a subdomain.
- Label, User, and SystemInfo are Generic infrastructure concepts.
- Events, API, and Infrastructure are technical layers, not business domains.

### Bounded contexts and relationships

The following list is the complete relationship map. Each entry names the DDD
upstream, downstream, relationship pattern, and published flow. C/S means
Customer/Supplier, C means Conformist, ACL means Anti-Corruption Layer, OHS
means Open Host Service, PL means Published Language, and SK means Shared
Kernel.

Workflow is the DDD upstream of Issue in entry 1. This does not change the
static `Issue -> Workflow` dependency described under Dependency invariants.

Core context map:

```text diagram
 +---------------+   +-----------+   +---------+
 | Project Space +---| Workspace +---| Generic +----+++
 +---------------+   +-----+-----+   +---------+    |||
                +----------+                        |||
                v                                   |||
            +-------+                               |||
            | Agent +-------------------------------++++
            +---+---+                               ||||
                |                                   ||||
                v                                   ||||
           +--------+                               ||||
           | Runner +-------------------------------+++++
           +----+---+                               |||||
          +-----+----------+                        |||||
          v                v                        |||||
 +----------------+   +---------+                   ||||
 | runner process |<--| Session +<------------------+++++
 +----------------+   +----+----+                   |||||
                  +--------+                        |||||
                  v                                 |||||
            +----------+                            |||||
            | Workflow +<---------------------------++++|
            +-----+----+                             ||||
                  +------+                           ||||
                         v                           ||||
                 +--------------+                    ||||
                 | Issue + Epic |<-------------------+|||
                 +-------+------+                     |||
           +-------------+------------+               ||||
           v                          v               ||||
+--------------------+   +------------------------+   |||
| AgentOps read side |<--| Repository coordinator |<--+++
+--------------------+   +------------------------+
```

Entry and adapter map:

```text diagram
+--------------------+    +-------------------+
| Server application |    | Agent and Session |
+----------+---------+    +---------+---------+
           +-+------------+---------+-------+
             +------------+---------+       |
             v9, 10 OHS + vL: API DTO       v17 OHS + PL
        +--------+   +--------+   +-------------------+
        | Web UI |   | CLI mo |   | provider adapters |
        +--------+   +--------+   +-------------------+
```

1. Workflow -> Issue (C/S): Workflow Profile, run creation, verdict, and
   output.
2. Agent -> Workflow (OHS + PL): launch contract, readiness, and AgentJob
   result.
3. Project Space -> Workflow (PL): default Profile, Repository, Project
   Variables, and Prompt.
4. Project Space -> Issue (SK): ProjectId and Repository reference.
5. Workspace -> Workflow and Agent (PL): Workspace identity, Origin resolution,
   and routing facts.
6. Agent -> Runner (OHS + PL): scheduling, dispatch, capacity, and reports.
7. Agent -> runner process (C): AgentJob dispatch with the Agent snapshot.
8. Runner -> runner process (PL): registration and polling presence.
9. Server -> Web (OHS + PL): API DTO.
10. Server -> CLI (OHS + PL): API DTO.
11. Generic -> Issue and related contexts (SK + PL): labels, users, and system
    information.
12. Session -> Issue, Workflow, API, and AgentOps (OHS + PL): session DTO.
13. Runner and Agent -> Session (PL): SessionInput, Activity, and Runtime
    observations.
14. Issue, Workflow, Agent, and Runner -> AgentOps (OHS): report facts.
15. Issue -> IssueRepositoryCoordinator (C): create, reassign, and reopen
    participant commands.
16. Project Space -> IssueRepositoryCoordinator (C): Repository removal
    commands.
17. Agent and Session -> Web, CLI, and provider adapters (OHS + PL):
    management, launch, results, Session Input, Turn, and events.

The TypeScript Runner process is infrastructure. It follows Agent Action and
AgentJob contracts and reports presence through Runner. The
`IssueRepositoryCoordinator` is a Project-scoped application process manager,
not a business context. It stores no Issue, Project, or Repository facts. It
provides serialization and redelivery safety for the narrow command class in
entries 15 and 16. Participants do not call it synchronously.

## Semantics

### Dependency invariants

- Aggregates in one bounded context may collaborate. Each transaction saves one
  aggregate and its events. A synchronous call chain must have one direction
  and must not form a callback cycle.
- Issue alone writes current Epic membership. Epic queries Issue state, then
  sends an advancement command that Issue validates in its own transaction.
- Do not add a generic `OwnerRef`, controller aggregate, or relationship
  aggregate. `EpicNumber?` is the complete Issue membership relation.
- Workflow uses Agent only through the launch and result Published Language. It
  does not read Agent configuration, select a Runtime, snapshot definitions,
  dispatch Runner work, or own execution retry and recovery.
- The static `Issue -> Workflow` dependency remains one way. Issue supplies
  WorkflowRun with `ProjectId`, `IssueNumber`, and `EpicNumber?` as association
  data. An Issue-side handler consumes Workflow result events.
- AgentJob is the only top-level Agent execution owner. Every `mohist/agent`
  Workflow task and direct Agent launch uses the same launch boundary.
  Mechanical Action attempts remain Workflow orchestration. `TaskRun`, Inline
  Agent, and Agent Definition Reference are not domain concepts. See
  [`agent-execution.md`](agent-execution.md).
- Agent consumes narrow Runner scheduling facts and Session association facts.
  Session has no reverse dependency on Agent behavior.
- Agent owns Agent Connection as a separate resource. The Connection references
  one Agent and never copies or changes its execution definition.
- Agent Connection owns external binding, lifecycle, and access policy.
  Provider ingress, Slack conversation mapping, and pending delivery are
  Server integration records. The adapter holds transient protocol state only.
- Slack has two independent supporting aggregates: `SlackWorkspaceEnrollment`
  owns the workspace Mohist App; `ManagedSlackAgentApp` owns one external Agent
  App. AgentApp binds to Connection through a durable fact and idempotent
  command, not a cross-aggregate transaction.
- Agent Connection staged binding fixes `AgentId + WorkspaceTeamId` at create.
  `AppId + BotUserId` move atomically from both empty to both set exactly once.
  Partial binding and rebinding are rejected. Removing a Connection does not
  delete a retained Agent App.
- SessionInput and AgentTurn are AgentSession-owned child records. They do not
  replace AgentJob or create a second top-level work unit.
- Session is a horizontal read and execution leaf. It has no reverse business
  dependency.
- The Runner process is infrastructure. It conforms to Agent contracts and
  proves presence by polling Runner.
- ProjectId is shared identity, not a Workflow model dependency.
- Artifact belongs to Workflow.
- The IssueRepositoryCoordinator is used only when Project-level serialization
  and redelivery safety protect a non-terminal Issue Repository binding. It
  persists only an uncertain-command fence. It must not write multiple
  aggregates, callback synchronously, or duplicate business facts. See
  [`architecture.md`](architecture.md#durable-application-process-manager).

### Judgment rules

- Stages, task order, checks, mechanical Actions, state advance, approval, and
  Agent launch timing belong to Workflow.
- Work properties, lifecycle, dependencies, and organization belong to Issue.
- Repository binding, isolation, execution configuration, and prompts belong to
  Project Space.
- Work location, Origin, materialization, and archive belong to Workspace.
- Agent configuration, execution jobs, Runner dispatch, retry, recovery, and
  report validation belong to Agent.
- External Agent binding, provider identity, access policy, and Connection
  lifecycle belong to Agent.
- Execution records, transcript, context, usage, and query belong to Session.
- Registration, presence, and capacity belong to Runner.
- Cross-domain read reports belong to AgentOps.
- Labels, users, and system information belong to Generic.

## Status

The current design uses Workflow as the core domain, with Issue, Project Space,
Workspace, Agent, Session, Runner, external surfaces, and AgentOps as supporting
contexts. The context map contains seventeen published relationships and the
IssueRepositoryCoordinator is the only named application process manager.
