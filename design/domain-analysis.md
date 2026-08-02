# Domain Analysis

Where does a change belong? First: problem space (subdomains). Then: solution space (bounded contexts).

## Subdomains

### Core: Workflow

Autonomous work pipeline. Advance, schedule, dispatch, approve, repair, resume. Interpret reports,
decide next state. Workflow owns Project-scoped WorkflowProfile, WorkflowRun, TaskRun and Action
contracts. Direct use of a runtime-specific Action is an Inline Agent execution, not an Agent entity.

### Supporting

| Subdomain | Problem | Ubiquitous language |
|---|---|---|
| Issue | what work is, how organized, what progress | issue, epic, sub-issue, parent issue, status, prerequisite, priority, risk, draft, done |
| Project Space | environment, isolation, config | project, repository (named resource, default, git URL, base branch), variable, prompt |
| Agent | reusable named intelligence, execution jobs and external connections | Mohist Agent, Agent Readiness, Agent Availability, AgentJob, Agent Connection, provider identity, access policy, WorkResult |
| Session | logical execution conversation, input delivery, turn execution, compression, query, audit | AgentSession, SessionInput, AgentTurn, Runtime Binding, Activity, Transcript, Context, Usage |
| Runner | execution resource availability and capacity | resource, presence, registration, capacity |
| Skill·Explore | refine vague needs into bounded issues | — |
| Slack integration | Server-side workspace Manager enrollment and managed child App external lifecycle (App create/OAuth/approval, manifest, transport readiness, operation fence, unknown outcome) | Slack workspace, Manager, managed child App, enrollment, App lifecycle, authorization, manifest drift, transport readiness |

Epic is Issue granularity (organizing facet), not a separate subdomain.
Issue 与 Epic 是同一限界上下文中的两个聚合。Issue 持有自己的当前 `EpicNumber?`；Epic
持有目标与推进策略，但不持有第二份权威成员集合。Epic 的成员、进度和候选 Issue 是
对 Issue 当前状态的查询结果。
Sub-issue/parent is also Issue-internal organization (work decomposition axis, orthogonal to Epic's goal/feeding axis); Workflow never sees it. See [`issue-breakdown.md`](issue-breakdown.md).
Prompt belongs to Project Space (Project is the only configurable scope). Builtin `.prompt` is
loader fallback, not another Prompt resource.

Repository belongs to Project Space. Issue stores only the target Repository name; WorkflowRun
stores only the Project/Issue identity needed to resolve that resource. An unfinished Issue prevents
changes to its Repository execution attributes, so Workflow does not need a Repository snapshot.
See [`repositories.md`](repositories.md).

### Agent and Session terms

Action、Inline Agent、Mohist Agent、AgentJob、AgentSession 与 Runtime Session 的
统一定义见 [`../CONTEXT.md`](../CONTEXT.md)；生命周期所有权、调用路径和完整不变量见
[`agent-execution.md`](agent-execution.md)。

### Read-side: AgentOps

Cross-domain read-only reports (activity feed, delivery cost, cross-aggregate board). Allowed to depend on all business domains. This makes Session a true leaf.

### Not subdomains

- Artifact: belongs to Workflow. No independent problem class.
- OpenSpec: external tool. Never a domain concept.
- External Agent hosts, Skills, CLI, Web UI, Slack and the Slack adapter (`mohist-slack`) are interaction
  adapters, not business domains. Agent Connection belongs to Agent because its binding, access policy and
  lifecycle are persistent Agent-facing product behavior; Slack protocol state does not. The Server-side
  Slack integration control plane (Slack workspace enrollment and managed child App lifecycle) is a separate
  supporting context (see table above): it holds external-App business facts that must survive restarts, and
  is distinct from the stateless protocol adapter and from the Agent domain.
- Generic: Label, User, SystemInfo — infrastructure.
- Technical layers: Events, Api, Infrastructure — not business domains.

## Bounded contexts and relationships

DDD patterns: Customer/Supplier (C/S), Conformist (C), ACL, OHS, Published Language (PL), Shared Kernel (SK).

| # | Upstream | Downstream | Pattern | What flows |
|---|---|---|---|---|
| 1 | Workflow | Issue | C/S | WorkflowProfile, run creation, verdict/output |
| 2 | Workflow | Runner | OHS+PL | task dispatch, fact report |
| 3 | Project Space | Workflow | PL | default Profile ref, Repository resource, Project Variables, Prompt key/body |
| 4 | Project Space | Issue | SK | ProjectId, repo ref |
| 5 | Issue | Skill·Explore | OHS+PL | issue body/template |
| 6 | Agent | runner process | C | AgentJob dispatch with Agent definition snapshot |
| 7 | Runner | runner process | PL | registration, poll presence |
| 8 | Server | Web | OHS+PL | API DTO |
| 9 | Server | CLI | OHS+PL | API DTO |
| 10 | Generic | Issue etc. | SK/PL | labels, user identity |
| 11 | Session | Issue/Workflow/API/AgentOps | OHS+PL | session DTO |
| 12 | Runner/Agent | Session | PL | Session input, activity, Runtime observations |
| 13 | Session/Issue/Workflow/Runner | AgentOps | OHS | cross-domain report assembly |
| 14 | Issue | IssueRepositoryCoordinator | C | narrow participant commands (create / reassign / reopen) |
| 15 | Project Space | IssueRepositoryCoordinator | C | narrow participant commands (repository removal) |
| 16 | Agent/Session | Web, CLI, provider adapters | OHS+PL | Agent and Connection management, launch, Job result, Session Input/Turn/transcript/events |

Runner process (TS) is infrastructure, not a context. It follows Workflow Action contracts
and AgentJob dispatch contracts.

`IssueRepositoryCoordinator` is a single-grain, Project-scoped 应用层 process manager
（见 [`architecture.md`](architecture.md) 的「持久化应用协调者」节）。它不是独立的业务
限界上下文——不持有 Issue、Project、仓库的事实，也不参与读取投影——只为 issue 417
引入的「建立或破坏非终态绑定」一类命令提供 Project 级串行化与失败重投安全。它的
两条关系行（14、15）表示协调者单向调用 Issue 与 Project 的窄 participant 接口；
参与者不在该同步调用栈中回调协调者。

## Dependency invariants

- 同一限界上下文允许聚合相互依赖；聚合边界限制事务，不禁止协作。Issue 与 Epic 可以
  相互发送命令，但任一命令只提交接收方聚合，且同步调用过程中不回调形成环。
- Issue 是当前 Epic 归属的唯一写入权威。Epic 不保存可被独立修改的 membership；它
  通过查询 Issue 状态选择候选，并让 Issue 在自己的事务中接受或拒绝推进命令。
- 不引入通用 `OwnerRef`、controller aggregate 或关系聚合。`EpicNumber?` 已完整表达
  Issue 所需的单一可选归属，泛化只会隐藏业务语言和写入权威。
- Workflow 代码依赖零个业务上下文。这不是风格要求，而是为了保持自治；它不引用 Issue
  聚合、repository 或领域类型。
- Issue → Workflow 是静态依赖方向。Issue 向 WorkflowRun 提供只含
  `ProjectId`、`IssueNumber`、`EpicNumber?` 的运行上下文；这些标量是 Published
  Language 中的关联信息，不让 Workflow 获得 Issue 行为依赖。Workflow 的结果事件由
  Issue 侧 handler 消费。
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
  (workspace-level Manager identity/capability/lifecycle and Manager credential refs; key without Project by
  default) and `ManagedSlackChildApp` (a child App's external lifecycle/OAuth/manifest/transport/fence/unknown/audit,
  referencing an `AgentConnectionId`). They are not Agent-domain and not process managers; ChildApp → Connection
  converges via durable fact + idempotent bind, not a cross-aggregate transaction.
- Agent Connection supports staged binding for the Manager path: `AgentId + WorkspaceTeamId` are fixed at
  creation, while `AppId + BotUserId` go from both-empty to both-set exactly once and atomically; half-binding,
  team re-binding and second app/bot re-binding are rejected. Removing a Connection does not delete a
  separately-retained managed child App; managed child App runtime secrets and the Manager credential are
  addressed by their owning aggregate (ChildApp / Enrollment), not by Agent Connection.
- SessionInput and AgentTurn are AgentSession-owned child records. They express ordered input and one
  continuous Runtime processing lifecycle, not new top-level work or a replacement for AgentJob result.
- Session is horizontal leaf. Model evolves independently. No reverse dependencies.
- runner process is infrastructure: conforms to Workflow + Agent contracts, registers with Runner and proves presence by polling.
- ProjectId is shared identity, not a Workflow model dependency.
- Artifact belongs to Workflow, not independent.
- `IssueRepositoryCoordinator` 是窄化特例 process manager：仅当下游是一条需要
  Project 级串行化、重投安全、且结果会破坏「非终态 Issue 必须有声明中的仓库」不变量的
  命令（Issue 创建、目标仓库重新指派、cancelled Issue reopen、仓库删除）时才进入。它
  只持久化不确定命令的 fence，**不得**写多聚合、不得同步回调、不得保存重复业务
  事实；完整规则见 [`architecture.md`](architecture.md)。Issue 与 Project 参与者不
  在自己的命令中反向引用协调者，事件路由与其它上下文继续走原本的接口。

## Judgment rules

| If it defines... | It goes in... |
|---|---|
| stages, tasks, checks, state advance, scheduling, approval | Workflow |
| work unit properties, lifecycle, deps, organization | Issue |
| repo binding, isolation, execution config, prompt library | Project Space |
| agent definition, job dispatch, report validation | Agent |
| external Agent binding, provider identity, access policy, connection lifecycle | Agent |
| execution recording, transcript, context, usage, query | Session |
| resource registration, presence, capacity | Runner |
| cross-domain read report assembly | AgentOps |
| labels, users, system info | Generic |
