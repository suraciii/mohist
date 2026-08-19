# Domain Analysis

Where does a change belong? First: problem space (subdomains). Then: solution space (bounded contexts).

## Subdomains

### Core: Workflow

Autonomous work pipeline. Advance, schedule, dispatch, approve, repair, resume. Interpret reports,
decide next state. Workflow owns Project-scoped WorkflowProfile, WorkflowRun, TaskRun and Action
contracts. Direct use of a runtime-specific Action is an Inline Agent execution, not an Agent entity.

### Supporting

- Issue: what work is, how organized, what progress. Ubiquitous language: issue, epic, sub-issue,
  parent issue, status, prerequisite, priority, risk, draft, done.
- Project Space: environment, isolation, config. Ubiquitous language: project, repository (named
  resource, default, git URL, base branch), workspace (origin, materialization), variable, prompt.
- Agent: reusable named intelligence, execution jobs and external connections. Ubiquitous language:
  Mohist Agent, Agent Readiness, Agent Availability, AgentJob, Agent Connection, provider identity,
  access policy, WorkResult.
- Session: logical execution conversation, input delivery, turn execution, compression, query,
  audit. Ubiquitous language: AgentSession, SessionInput, AgentTurn, Runtime Binding, Activity,
  Transcript, Context, Usage.
- Runner: execution resource availability and capacity. Ubiquitous language: resource, presence,
  registration, capacity.
- Skill·Explore: refine vague needs into bounded issues.
- Slack integration: Server-side workspace Mohist App enrollment and managed Agent App external
  lifecycle (App create/install approval, manifest, Socket readiness, operation fence, unknown
  outcome). Ubiquitous language: Slack workspace, Mohist App, Agent App, enrollment, App lifecycle,
  authorization, manifest drift, Socket readiness.

Epic is Issue granularity (organizing facet), not a separate subdomain.
Issue and Epic are two aggregates in the same bounded context. Issue holds its current `EpicNumber?`.
Epic holds the goal and advancement policy, but it does not hold a second authoritative membership set.
Epic membership, progress, and candidate Issues are query results from the current Issue state.
Sub-issue/parent is also Issue-internal organization (work decomposition axis, orthogonal to Epic's goal/feeding axis); Workflow never sees it. See [`issue-breakdown.md`](issue-breakdown.md).
Prompt belongs to Project Space (Project is the only configurable scope). Builtin `.prompt` is
loader fallback, not another Prompt resource.

Repository belongs to Project Space. Issue stores only the target Repository name; WorkflowRun
stores only the Project/Issue identity needed to resolve that resource. An unfinished Issue prevents
changes to its Repository execution attributes, so Workflow does not need a Repository snapshot.
See [`repositories.md`](repositories.md).

Workspace belongs to Project Space. It is a first-class named execution environment whose
lifecycle is independent of any AgentSession or WorkflowRun; Issue and entry contexts resolve
to a Workspace through its Origin, and Runner materializes it as a directory. See
[`workspace.md`](workspace.md).

### Agent and Session terms

See [`../CONTEXT.md`](../CONTEXT.md) for the shared definitions of Action, Inline Agent, Mohist
Agent, AgentJob, AgentSession, and Runtime Session. See
[`agent-execution.md`](agent-execution.md) for lifecycle ownership, call paths, and the complete
invariants.

### Read-side: AgentOps

Cross-domain read-only reports (activity feed, delivery cost, cross-aggregate board). Allowed to depend on all business domains. This makes Session a true leaf.

### Not subdomains

- Artifact: belongs to Workflow. No independent problem class.
- OpenSpec: external tool. Never a domain concept.
- External Agent hosts, Skills, CLI, Web UI, Slack and the Slack adapter (`mohist-slack`) are interaction
  adapters, not business domains. Agent Connection belongs to Agent because its binding, access policy and
  lifecycle are persistent Agent-facing product behavior; Slack protocol state does not. The Server-side
  Slack integration control plane (Slack workspace enrollment and managed Agent App lifecycle) is a separate
  supporting context (see the Slack integration entry above): it holds external-App business facts that must survive restarts, and
  is distinct from the stateless protocol adapter and from the Agent domain.
- Generic: Label, User, SystemInfo — infrastructure.
- Technical layers: Events, Api, Infrastructure — not business domains.

## Bounded contexts and relationships

DDD patterns: Customer/Supplier (C/S), Conformist (C), ACL, OHS, Published Language (PL), Shared Kernel (SK).

The following list is the normative and complete relationship map. Each entry gives the DDD
upstream, the downstream, the relationship pattern, and what flows. These are DDD
upstream-to-downstream relationships, not static source-code dependencies.

In particular, entry 1 makes Workflow the DDD upstream of Issue. This does not conflict with the static
`Issue -> Workflow` code dependency defined in [Dependency invariants](#dependency-invariants).

1. Workflow -> Issue (C/S): WorkflowProfile, run creation, verdict/output.
2. Workflow -> Runner (OHS+PL): task dispatch, fact report.
3. Project Space -> Workflow (PL): default Profile ref, Repository resource, Workspace resource,
   Project Variables, Prompt key/body.
4. Project Space -> Issue (SK): ProjectId, repo ref.
5. Issue -> Skill·Explore (OHS+PL): issue body/template.
6. Agent -> runner process (C): AgentJob dispatch with Agent definition snapshot.
7. Runner -> runner process (PL): registration, poll presence.
8. Server -> Web (OHS+PL): API DTO.
9. Server -> CLI (OHS+PL): API DTO.
10. Generic -> Issue etc. (SK/PL): labels, user identity.
11. Session -> Issue/Workflow/API/AgentOps (OHS+PL): session DTO.
12. Runner/Agent -> Session (PL): Session input, activity, Runtime observations.
13. Session/Issue/Workflow/Runner -> AgentOps (OHS): cross-domain report assembly.
14. Issue -> IssueRepositoryCoordinator (C): narrow participant commands (create / reassign / reopen).
15. Project Space -> IssueRepositoryCoordinator (C): narrow participant commands (repository removal).
16. Agent/Session -> Web, CLI, provider adapters (OHS+PL): Agent and Connection management, launch,
    Job result, Session Input/Turn/transcript/events.

Runner process (TS) is infrastructure, not a context. It follows Workflow Action contracts
and AgentJob dispatch contracts.

`IssueRepositoryCoordinator` is a single-grain, Project-scoped application process manager. See
[Durable application process manager](architecture.md#durable-application-process-manager). It is not
an independent business bounded context. It holds no Issue, Project, or Repository facts and does not
participate in read projections. It provides Project-level serialization and failure-redelivery safety
only for the command class that establishes or breaks a non-terminal binding. Its two relationship
entries, 14 and 15, mean that the coordinator calls narrow Issue and Project participant interfaces in one
direction. A participant does not call the coordinator back in that synchronous call stack.

## Dependency invariants

- Aggregates in the same bounded context may depend on each other. An aggregate boundary limits a
  transaction; it does not prohibit collaboration. Issue and Epic may send commands to each other, but
  each command commits only the receiving aggregate. A synchronous call must not call back and form a cycle.
- Issue is the only write authority for its current Epic membership. Epic does not store independently
  mutable membership. It queries Issue state to select candidates and lets Issue accept or reject an
  advancement command in the Issue transaction.
- Do not introduce a generic `OwnerRef`, controller aggregate, or relationship aggregate. `EpicNumber?`
  completely expresses the one optional membership that Issue needs. Generalization would hide the business
  language and write authority.
- Workflow code depends on no business context. This is not a style preference. It preserves Workflow
  autonomy. Workflow does not reference Issue aggregates, repositories, or domain types.
- `Issue -> Workflow` is the static dependency direction. Issue gives WorkflowRun only a run context that
  contains `ProjectId`, `IssueNumber`, and `EpicNumber?`. These scalars are association information in the
  Published Language. They do not give Workflow a behavioral dependency on Issue. An Issue-side handler
  consumes Workflow result events.
- Agent/Session ownership invariants (work owner is TaskRun xor AgentJob, session origin,
  Inline Agent identity, shared runtime not coupling Workflow to Agent) are listed once in
  [`agent-execution.md`](agent-execution.md).
- Agent is a leaf (only one-way coupling to Session for association and cleanup).
- Agent context owns Agent Connection as a separate resource. Connection references one Agent but does
  not copy or modify its execution definition; adding Slack does not add provider fields to Mohist Agent.
- Agent Connection is the authority for external binding, lifecycle and access policy. Durable provider
  ingress, Slack conversation mappings and pending deliveries are integration records owned by Server
  infrastructure, not Agent Connection or Session facts. The adapter holds only transient protocol state
  and cannot become a second authority for Agent, Job or Session.
- The Slack integration supporting context owns two independent aggregates: `SlackWorkspaceEnrollment`
  (workspace-level Mohist App identity/capability/lifecycle and credential refs; key without Project by
  default) and `ManagedSlackAgentApp` (an Agent App's external lifecycle/install/manifest/Socket/fence/unknown/audit,
  referencing an `AgentConnectionId`). They are not Agent-domain and not process managers; AgentApp -> Connection
  converges via durable fact + idempotent bind, not a cross-aggregate transaction.
- Agent Connection supports staged binding for the `install-agent` path: `AgentId + WorkspaceTeamId` are fixed at
  creation, while `AppId + BotUserId` go from both-empty to both-set exactly once and atomically; half-binding,
  team re-binding and second app/bot re-binding are rejected. Removing a Connection does not delete a
  separately-retained managed Agent App; Agent App runtime secrets and the Mohist App credential are
  addressed by their owning aggregate (AgentApp / Enrollment), not by Agent Connection.
- SessionInput and AgentTurn are AgentSession-owned child records. They express ordered input and one
  continuous Runtime processing lifecycle, not new top-level work or a replacement for AgentJob result.
- Session is horizontal leaf. Model evolves independently. No reverse dependencies.
- runner process is infrastructure: conforms to Workflow + Agent contracts, registers with Runner and proves presence by polling.
- ProjectId is shared identity, not a Workflow model dependency.
- Artifact belongs to Workflow, not independent.
- `IssueRepositoryCoordinator` is a narrow special-case process manager. It is used only when the
  downstream command needs Project-level serialization and redelivery safety, and its result can break
  the invariant that a non-terminal Issue must have a declared Repository. This applies to Issue creation,
  target Repository reassignment, reopening a cancelled Issue, and Repository deletion. The coordinator
  persists only the fence for an uncertain command. It **must not** write multiple aggregates, make a
  synchronous callback, or store duplicate business facts. See
  [`architecture.md`](architecture.md#durable-application-process-manager) for the complete rules. Issue
  and Project participants do not reference the coordinator from their commands. Event routing and other
  contexts continue to use the existing interfaces.

## Judgment rules

- If it defines stages, tasks, checks, state advance, scheduling, or approval, it goes in Workflow.
- If it defines work unit properties, lifecycle, deps, or organization, it goes in Issue.
- If it defines repo binding, isolation, execution config, or the prompt library, it goes in
  Project Space.
- If it defines agent definition, job dispatch, or report validation, it goes in Agent.
- If it defines External Agent binding, provider identity, access policy, or connection lifecycle,
  it goes in Agent.
- If it defines execution recording, transcript, context, usage, or query, it goes in Session.
- If it defines resource registration, presence, or capacity, it goes in Runner.
- If it defines cross-domain read report assembly, it goes in AgentOps.
- If it defines labels, users, or system info, it goes in Generic.
