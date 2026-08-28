# Mohist Glossary

This glossary defines the product and Agent execution language shared across
Mohist contexts. See [`design/agent-execution.md`](design/agent-execution.md) for
lifecycle, event, and module boundaries.

## Product Interfaces

**External Agent**:
An Agent with which the user interacts outside Mohist and that reads or operates
Mohist on the user's behalf. It is not a Mohist resource, and Mohist neither
schedules nor runs it.

_Avoid_: Mohist Agent

**Agent Connection**:
A persistent relationship that exposes one Mohist Agent in an external
interaction location and contains its identity, invocation scope, and
availability there. It contains no copy of the Mohist Agent's Instructions,
execution configuration, or Skills and owns no AgentSession.

**Mohist App**:
The Mohist management entry point installed once in a Slack workspace. It
establishes the workspace connection and manages Agent Connections but neither
represents a business Agent nor performs Agent App work.

**Agent App**:
The dedicated Slack App and bot for one Agent Connection, presenting a Mohist
Agent as an independent identity without copying its definition. Agent Apps for
the same Mohist Agent in different workspaces are independent external
identities.

**Slack Bot**:
The client identity that represents a Mohist Agent in Slack through an Agent
Connection. It is neither another Mohist Agent nor an External Agent.

**Web UI**:
Mohist's fallback plane for observation, visualization, manual operations, and
takeover, with direct configuration, launch, and continuation of Mohist Agents.
It is neither the user's daily collaboration workspace nor the primary
interaction entry point.

**GitHub Mirror**:
The one GitHub Issue that Mohist creates and maintains for a Mohist Issue whose
target Repository is connected to GitHub. Mirroring is automatic and passive;
title and body synchronize in both directions, while execution state projects
only from Mohist outward. A GitHub Issue without a link is not tracked by
Mohist and enters only through an explicit `/mohist` command.

_Avoid_: feed, intake label, origin snapshot

## Work Management

**Project**:
One real product's scope and execution boundary. A Project declares its
Repositories, configuration, and defaults, and its data is fully isolated from
every other Project. It is not the user's collaboration workspace.

**Repository**:
A named execution resource declared by a Project: a git URL and a base branch,
with one default per Project. Each Issue binds one target Repository that its
execution must not change after work starts.

**Issue**:
One unit of work that can enter the production line. Its identity is its
Project-scoped number; it has no second internal ID.

**Composite Issue, parent Issue, child Issue**:
An Issue with an explicit `parent` reference is a child Issue, and the
referenced parent is a composite Issue. The parent tracks the complete
requirement while each child moves through its own Workflow. Decomposition is
always an explicit owner choice, never automatic.

_Avoid_: sub-issue; the DSL surface is `--parent`, and the relationship terms
are parent and child

**Epic**:
A product goal that continuously supplies linked Issues to the production
line, advancing one ready Issue at a time. Epic membership is recorded on the
Issue, and Epic progress is a query over current Issue state, not a second
membership store.

**Workflow**:
The production line that advances a ready Issue from Plan to Done. Draft and
Backlog belong to the Issue lifecycle, outside the Workflow, so requirement
readiness and execution state cannot be confused.

**WorkflowRun**:
One execution of a Workflow for one Issue. It owns orchestration state, binds
one complete Workflow Definition when it starts, and references the AgentJobs
it launches; it owns no Agent execution lifecycle itself.

**Workflow Profile**:
A Project resource that defines the stages, tasks, checks, recovery, Approval
Points, and Feedback Tasks of a Workflow. An Issue inherits the Project default or
selects another Profile from the same Project.

**Workflow Definition**:
The YAML body of a Workflow Profile that declares its stages, tasks, checks,
Approval Points, Feedback Tasks, recovery, and template expressions.

**Runner**:
The execution-plane process that registers with a Server, claims dispatched
work, materializes Workspace directories, executes resolved Agent work, and
reports facts. It never interprets facts or decides production-line state.

**Skill**:
A reusable description of an Agent capability. An External Agent installs
Mohist-distributed Skills to operate Mohist; a Mohist Agent's Skills are part
of its configuration, fixed when work starts.

## Agent Execution

**Action**:
The input and output contract by which an AgentJob delegates one execution to a
Runner. It carries the resolved Agent execution snapshot but owns no work
lifecycle.

**Mohist Agent**:
A predefined, reusable Agent resource within a Project that has a stable
identity and can start independently or as a Workflow worker. `mohist/agent`
Workflow tasks, the Web UI, CLI, Agent Connections, event routing, and comment mentions all use
the same Agent launch boundary.

**Agent Readiness**:
Mohist's unified diagnosis of whether an Agent execution configuration is
complete, with value `ready`, `needs-setup`, or `unknown`; it is not the
`active` or `archived` lifecycle. `needs-setup` includes an actionable gap, and
an entry point cannot infer `ready` or failed from `unknown`.

**AgentJob**:
The sole top-level Agent execution unit, created whenever a `mohist/agent`
Workflow task or another entry point starts a Mohist Agent. It owns scheduling, result, retry, and
recovery and is associated with the first AgentTurn in an AgentSession. It is
neither the continuing conversation nor the arbiter of subsequent Follow-up
work.

**SessionInput**:
One ordered input with stable identity that an AgentSession has accepted. One
AgentTurn can process one or more consecutive SessionInputs.

**AgentTurn**:
One continuous Runtime processing period in an AgentSession that contains one
or more ordered SessionInputs and distinguishes waiting, execution, and result
states. It is not a new top-level unit of work.

**AgentSession**:
The stable logical session and audit record that Mohist owns, with ordered
SessionInputs, AgentTurns, replies, and execution facts. The end of one
AgentTurn does not close it.

_Avoid_: "Session completed," "Session failed," or "Session closed" for the
result of one execution

**Activity**:
Whether an AgentSession has a nonterminal AgentTurn, with value `idle`, `active`,
or `unknown`; `active` covers a queued Turn and a Runtime that is executing,
while AgentTurn state gives the phase. Activity is not the success or failure
result of one unit of work.

**Runtime Session**:
A physical conversation owned by OpenCode, Pi, or another execution backend
that can be cached, resumed, replaced, or reclaimed. It does not determine
whether an AgentSession can accept more input.

## Workflow Decisions

**Approval Point**:
A Workflow state after a Stage that waits for an Approve or Request Changes
decision before the output can continue through the pipeline.

_Avoid_: quality gate

**Approval Feedback**:
The required-change description recorded when an approver selects Request
Changes at an Approval Point.

**Feedback Tasks**:
The ordered Tasks declared in `approval.feedback.tasks` that apply Approval
Feedback before the Workflow returns to the same Approval Point.

## Authentication and Access

**Administrator (Admin)**:
Mohist's only user, with all capabilities. The system has no concept of a second
user.

**Principal**:
A caller identity that Mohist recognizes as an Administrator, service, or
Agent. Every control-plane access belongs to a Principal, whose identity
determines its capabilities.

**Credential**:
A token that proves a Principal's identity and can be issued, revoked, or
expired independently. A service Credential is a deployment-level machine
identity, such as one held by a Slack adapter, and is not a user.

**Agent Identity**:
The attribution identity of a Mohist Agent as a Principal, to which Mohist
attributes the Agent's actions. Its external delivery identity is an independent
bot and does not impersonate the Administrator.

_Avoid_: "user" for a service or Agent Principal; "login" for Runner or
integration Credential authentication

## Workspace

**Workspace**:
A named, persistent Project execution environment that holds an Origin,
repository references, and archive state and exists across AgentSessions and
WorkflowRuns. Sessions and Agents with the same Origin share one Workspace;
directory contents and git organization are outside its definition.

**Origin**:
A Workspace creation source and unique resolution key: an Issue, an interaction
context such as a Slack channel or Web conversation, or an explicit creation.
At most one active Workspace exists for the same Origin at one time.

**Materialization**:
A Workspace directory instance on one Runner, with routing facts that determine
where subsequent execution is scheduled. Its directory can be reclaimed or
lost with the Runner; rematerialization starts empty without changing Workspace
identity.

_Avoid_: worktree or Runner directory as the Workspace identity
