# Domain Analysis

Where does a change belong? First: problem space (subdomains). Then: solution space (bounded contexts).

## Subdomains

### Core: Workflow

Autonomous work pipeline. Advance, schedule, dispatch, approve, repair, resume. Interpret reports, decide next state. Workflow owns TaskRun and Action contracts. Direct use of a runtime-specific Action is an Inline Agent execution, not an Agent entity.

### Supporting

| Subdomain | Problem | Ubiquitous language |
|---|---|---|
| Issue | what work is, how organized, what progress | issue, epic, sub-issue, parent issue, status, prerequisite, priority, risk, draft, done |
| Project Space | environment, isolation, config | project, repository (named resource, default), variable, base branch, prompt |
| Agent | reusable named intelligence and its execution jobs | Mohist Agent, AgentJob, AgentJobInput, WorkResult |
| Session | logical execution conversation, compression, query, audit | AgentSession, Runtime Binding, Transcript, Context, Usage, Lineage |
| Runner | execution resource availability and capacity | resource, presence, registration, capacity |
| Skill·Explore | refine vague needs into bounded issues | — |

Epic is Issue granularity (organizing facet), not a separate subdomain.
Issue 与 Epic 是同一限界上下文中的两个聚合。Issue 持有自己的当前 `EpicNumber?`；Epic
持有目标与推进策略，但不持有第二份权威成员集合。Epic 的成员、进度和候选 Issue 是
对 Issue 当前状态的查询结果。
Sub-issue/parent is also Issue-internal organization (work decomposition axis, orthogonal to Epic's goal/feeding axis); Workflow never sees it. See [`issue-breakdown.md`](issue-breakdown.md).
Prompt belongs to Project Space (only configurable layer). Builtin .prompt is loader fallback.

### Agent and Session terms

Action, Inline Agent, Mohist Agent, AgentJob, AgentSession, and Runtime Session — canonical
definitions, lifecycle ownership, invocation paths, and the full invariant list live in
[`agent-execution.md`](agent-execution.md).

### Read-side: AgentOps

Cross-domain read-only reports (activity feed, delivery cost, cross-aggregate board). Allowed to depend on all business domains. This makes Session a true leaf.

### Not subdomains

- Artifact: belongs to Workflow. No independent problem class.
- OpenSpec: external tool. Never a domain concept.
- Generic: Label, User, SystemInfo — infrastructure.
- Technical layers: Events, Api, Infrastructure — not business domains.

## Bounded contexts and relationships

DDD patterns: Customer/Supplier (C/S), Conformist (C), ACL, OHS, Published Language (PL), Shared Kernel (SK).

| # | Upstream | Downstream | Pattern | What flows |
|---|---|---|---|---|
| 1 | Workflow | Issue | C/S | profile, run creation, verdict/output |
| 2 | Workflow | Runner | OHS+PL | task dispatch, fact report |
| 3 | Project Space | Workflow | PL | project variables |
| 4 | Project Space | Issue | SK | ProjectId, repo ref |
| 5 | Issue | Skill·Explore | OHS+PL | issue body/template |
| 6 | Agent | runner process | C | AgentJob dispatch with Agent definition snapshot |
| 7 | Runner | runner process | PL | registration, poll presence |
| 8 | Server | Web | OHS+PL | API DTO |
| 9 | Server | CLI | OHS+PL | API DTO |
| 10 | Generic | Issue etc. | SK/PL | labels, user identity |
| 11 | Session | Issue/Workflow/API/AgentOps | OHS+PL | session DTO |
| 12 | Runner/Agent | Session | PL | runtime events, close events |
| 13 | Session/Issue/Workflow/Runner | AgentOps | OHS | cross-domain report assembly |

Runner process (TS) is infrastructure, not a context. It follows Workflow Action contracts
and AgentJob dispatch contracts.

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
- Session is horizontal leaf. Model evolves independently. No reverse dependencies.
- runner process is infrastructure: conforms to Workflow + Agent contracts, registers with Runner and proves presence by polling.
- ProjectId is shared identity, not a Workflow model dependency.
- Artifact belongs to Workflow, not independent.

## Judgment rules

| If it defines... | It goes in... |
|---|---|
| stages, tasks, checks, state advance, scheduling, approval | Workflow |
| work unit properties, lifecycle, deps, organization | Issue |
| repo binding, isolation, execution config, prompt library | Project Space |
| agent definition, job dispatch, report validation | Agent |
| execution recording, transcript, context, usage, query | Session |
| resource registration, presence, capacity | Runner |
| cross-domain read report assembly | AgentOps |
| labels, users, system info | Generic |
