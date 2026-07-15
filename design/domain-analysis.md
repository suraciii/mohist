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
Sub-issue/parent is also Issue-internal organization (work decomposition axis, orthogonal to Epic's goal/feeding axis); Workflow never sees it. See [`issue-breakdown.md`](issue-breakdown.md).
Prompt belongs to Project Space (only configurable layer). Builtin .prompt is loader fallback.

### Agent and Session terms

- **Action**: a work execution interface selected by `uses`. `mohist/opencode` describes how one work turn executes; it has no Agent identity.
- **Inline Agent**: a usage mode where a Workflow TaskRun directly supplies a runtime Action's input. It is not persisted and has no Agent ID.
- **Mohist Agent (Named Agent)**: a reusable, project-scoped Agent definition with stable identity, instructions, config, skills, subscriptions, and status.
- **AgentJob**: one execution of a Mohist Agent. It snapshots the resolved Agent definition and owns pending/running/terminal state and result.
- **AgentSession**: Mohist's stable logical conversation and audit record. It records runtime facts but does not own TaskRun or AgentJob completion.
- **Runtime Session**: one physical conversation owned by an external runtime. One AgentSession may have several Runtime Sessions in its lineage, but only one current binding.

Full lifecycle ownership and invocation paths: [`agent-execution.md`](agent-execution.md).

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
and AgentJob dispatch contracts. Shared runtime implementation does not couple Workflow to
the Agent context.

## Dependency invariants

```
Workflow (TaskRun) ──dispatch──┐
                              v
                         runner process ──runtime facts──> Session (AgentSession)
                              ^
Agent (AgentJob) ───dispatch───┘
```

- Workflow depends on zero business contexts. This is not style — it enables autonomy.
- Issue → Workflow only. Workflow never knows "issue."
- `mohist/opencode` never resolves or depends on a Mohist Agent. Its direct Workflow use is Inline Agent execution.
- Mohist Agent launch snapshots the Agent definition into AgentJob. Editing the Agent cannot mutate an in-flight job.
- TaskRun owns Workflow work completion; AgentJob owns Mohist Agent work completion; AgentSession owns neither.
- Every AgentSession has one origin: Workflow or Agent launch. Equal prompts, models, or runtime config never merge origins.
- One Mohist Agent may create many AgentSessions. An Inline Agent has no Agent identity to own or be referenced by a Session.
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
